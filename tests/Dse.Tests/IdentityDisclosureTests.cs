using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dse.Tests;

public sealed class IdentityDisclosureTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Key() => Guid.NewGuid().ToString("N");
    private sealed class UnusedAi : IKnowledgeDrafter, IDecisionProvider
    {
        public Task<AiResult<KnowledgeSummary>> DraftAsync(AiContext c, CancellationToken ct) => throw new InvalidOperationException("AI must not run for identity disclosure");
        public Task<AiResult<Decisions>> DecideAsync(AiContext c, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct) => throw new InvalidOperationException("AI must not run for identity disclosure");
    }
    private sealed class FaultStore : IRecordStore
    {
        private readonly MemoryRecordStore inner = new();
        public bool CrossTenant, LoseAcknowledgement, EditProfileAfterConsent;
        public async Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct)
        {
            var row = await inner.ReadAsync(p, id, ct);
            if (CrossTenant && row?.Value.Text("kind") == "person" && id == "demo-person-02") row.Value["tenantId"] = "outside";
            return row;
        }
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? token, CancellationToken ct)
            => inner.QueryAsync(p, kind, field, parent, size, token, ct);
        public async Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            await inner.CommitAsync(p, writes, ct);
            if (EditProfileAfterConsent && writes.Any(w => w.Value.Text("operation") == "identity-consent"))
            {
                EditProfileAfterConsent = false;
                var catalog = RecordPartition.Catalog(p.TenantId);
                var person = (await inner.ReadAsync(catalog, "demo-person-02", ct))!;
                person.Value["displayName"] = "処理中に変更された表示名";
                person.Value["sourceVersion"] = person.Value.Number("sourceVersion") + 1;
                await inner.CommitAsync(catalog, [RecordWrite.Replace(person.Value, person.ETag)], ct);
            }
            if (LoseAcknowledgement && writes.Any(w => w.Value.Text("operation") == "identity-consent"))
            { LoseAcknowledgement = false; throw new IOException("lost acknowledgement"); }
        }
    }
    private sealed record Setup(IRecordStore Store, IPrimaryRepository Repo, IConfigurationRoot Config,
        ConversationService Chat, AnonymousWorkspaceService Workspace, IdentityDisclosureService Service,
        ActorAccess Owner, ActorAccess Guest, string RoomId, string Sid);
    private static async Task<Setup> Create(bool azure = false, IRecordStore? supplied = null)
    {
        var tenant = azure ? Tenant : "demo";
        var settings = new Dictionary<string, string?> { ["Data:Mode"] = azure ? "Azure" : "Demo", ["Ai:Mode"] = "Mock" };
        for (var i = 0; i < 3; i++)
        {
            var suffix = $"{i + 1:D2}";
            settings[$"Auth:Bindings:{i}:TenantId"] = Tenant;
            settings[$"Auth:Bindings:{i}:Oid"] = $"33333333-3333-4333-8333-{i + 1:D12}";
            settings[$"Auth:Bindings:{i}:PrincipalId"] = "principal-demo-" + suffix;
            settings[$"Auth:Bindings:{i}:PersonId"] = "demo-person-" + suffix;
            settings[$"Auth:Bindings:{i}:Roles:0"] = "member";
            settings[$"Auth:Bindings:{i}:Enabled"] = "true";
            settings[$"Auth:Grants:{i}:PrincipalId"] = "principal-demo-" + suffix;
            settings[$"Auth:Grants:{i}:ScopeIds:0"] = "demo";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var store = supplied ?? new MemoryRecordStore();
        if (azure) await AuthorityFixture.InitializeAsync(store,config,tenant);
        await DemoRecords.SeedAsync(store, tenant, default);
        var repo = new PrimaryRepository(store); var fence = new AccessFence(repo); var identities = new IdentityResolver(config);
        // All fixture people have the same owner as their binding; never rely on a relaxed Demo owner check.
        for (var i = 1; i <= 3; i++)
        {
            var person = (await repo.ReadAsync(RecordPartition.Catalog(tenant), $"demo-person-{i:D2}", default))!;
            person.Value["ownerId"] = $"principal-demo-{i:D2}";
            await repo.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Replace(person.Value, person.ETag)], default);
        }
        var owner = azure ? identities.ResolvePerson(tenant, "demo-person-01")! : IdentityResolver.Demo;
        var guest = azure ? identities.ResolvePerson(tenant, "demo-person-02")! : IdentityResolver.DemoPerson("demo-person-02")!;
        var chat = new ConversationService(repo, new(), identities, TimeProvider.System, fence);
        var ai = new UnusedAi(); var knowledge = new KnowledgeService(repo, store, chat, fence, ai, ai, identities, config, TimeProvider.System);
        var workspace = new AnonymousWorkspaceService(repo, new MemoryProjectionRepository(store), new SourceGuard(repo,config), fence,
            chat, knowledge, identities, config, TimeProvider.System);
        var challenge = await workspace.CreateAsync(owner, Key(), new("架空の相談", "技術の引き継ぎを相談する"), default);
        var room = await workspace.ResolveRoomAsync(owner, challenge.RoomId!, default);
        var session = await chat.SessionAsync(owner, room.SessionId, default);
        await chat.InviteAsync(owner, room.SessionId, Key(), session.ETag, new(guest.PersonId), !azure, default);
        var invitation = (await chat.ParticipationsAsync(guest, room.SessionId, default)).Items.Single();
        await chat.DecideAsync(guest, room.SessionId, Key(), invitation.ETag, new("accepted"), default);
        var service = new IdentityDisclosureService(repo, workspace, chat, fence, config, TimeProvider.System);
        return new(store, repo, config, chat, workspace, service, owner, guest, challenge.RoomId!, room.SessionId);
    }
    private static async Task<IdentityDisclosureView> Change(Setup s, ActorAccess a, bool grant)
    {
        var current = await s.Service.ReadAsync(a, s.RoomId, default);
        return await s.Service.ChangeAsync(a, s.RoomId, Key(), current.Etag,
            new(grant, current.ReviewedVersion, current.FieldSet), default);
    }
    private static async Task Share(Setup s)
    {
        await Change(s, s.Owner, true); var shared = await Change(s, s.Guest, true);
        Assert.Equal("shared", shared.State); Assert.All(shared.Participants, p => Assert.NotNull(p.SharedName));
    }
    private static async Task EditPerson(Setup s, string id, Action<JsonObject> edit)
    {
        var p = RecordPartition.Catalog(s.Owner.TenantId); var row = (await s.Repo.ReadAsync(p, id, default))!;
        edit(row.Value); await s.Repo.CommitAsync(p, [RecordWrite.Replace(row.Value, row.ETag)], default);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_independent_mutual_current_consent_discloses_profile_display_names(bool azure)
    {
        var s = await Create(azure);
        var before = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.Equal("pending", before.State); Assert.All(before.Participants, p => Assert.Null(p.SharedName));
        var one = await Change(s, s.Owner, true);
        Assert.True(one.SelfConsent); Assert.All(one.Participants, p => Assert.Null(p.SharedName));
        await Share(s);
        // No publication/analysis state was required or granted as a side effect.
        Assert.Null((await s.Chat.SessionAsync(s.Owner, s.Sid, default)).Value["knowledgeDraft"]);
        Assert.Null(await s.Repo.ReadAsync(RecordPartition.Conversation(s.Owner.TenantId, s.Sid), KnowledgeService.Id(s.Sid), default));
        var json = JsonSerializer.Serialize(await s.Service.ReadAsync(s.Guest, s.RoomId, default), Json);
        foreach (var privateValue in new[] { s.Owner.PersonId, s.Owner.PrincipalId, s.Guest.PersonId, s.Guest.PrincipalId, s.Sid, "demo-department" })
            Assert.DoesNotContain(privateValue, json);
        var keys = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).Order().ToArray();
        Assert.Equal(new[] { "etag", "fieldSet", "participants", "reviewedVersion", "roomId", "selfConsent", "state" }, keys);
    }
    [Fact]
    public async Task Withdrawal_invalidates_everyones_grant_and_old_retry_cannot_restore_or_replay_names()
    {
        var s = await Create(); var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        var key = Key(); var input = new IdentityDisclosureConsent(true, current.ReviewedVersion, current.FieldSet);
        await s.Service.ChangeAsync(s.Owner, s.RoomId, key, current.Etag, input, default); await Change(s, s.Guest, true);
        var revoked = await Change(s, s.Guest, false);
        Assert.All(revoked.Participants, p => Assert.Null(p.SharedName));
        var retry = await s.Service.ChangeAsync(s.Owner, s.RoomId, key, current.Etag, input, default);
        Assert.False(retry.SelfConsent); Assert.All(retry.Participants, p => Assert.Null(p.SharedName));
        var oneAgain = await Change(s, s.Owner, true); Assert.All(oneAgain.Participants, p => Assert.Null(p.SharedName));
        await Change(s, s.Guest, true); Assert.Equal("shared", (await s.Service.ReadAsync(s.Owner, s.RoomId, default)).State);
    }
    [Theory]
    [InlineData("name")]
    [InlineData("version")]
    [InlineData("available")]
    [InlineData("withdrawn")]
    [InlineData("unknown_state")]
    [InlineData("scope")]
    [InlineData("owner")]
    public async Task Changed_profile_source_or_access_invalidates_all_previously_reviewed_consent(string change)
    {
        var s = await Create(); await Share(s); var old = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        await EditPerson(s, s.Guest.PersonId, p =>
        {
            switch (change)
            {
                case "name": p["displayName"] = "新しい架空表示名"; break;
                case "version": p["sourceVersion"] = p.Number("sourceVersion") + 1; break;
                case "available": p["available"] = false; break;
                case "withdrawn": p["publicationState"] = "withdrawn"; break;
                case "unknown_state": p["publicationState"] = "invalid-state"; break;
                case "scope": p["scopeIds"] = RecordJson.Array(["outside"]); break;
                case "owner": p["ownerId"] = "other-owner"; break;
            }
        });
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.NotEqual(old.ReviewedVersion, current.ReviewedVersion); Assert.False(current.SelfConsent);
        Assert.All(current.Participants, p => Assert.Null(p.SharedName));
        Assert.Equal(412, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(s.Owner, s.RoomId, Key(), old.Etag,
            new(true, old.ReviewedVersion, old.FieldSet), default))).Status);
        if (change == "name")
        {
            var one = await Change(s, s.Owner, true); Assert.All(one.Participants, p => Assert.Null(p.SharedName));
            var renewed = await Change(s, s.Guest, true);
            Assert.Contains(renewed.Participants, p => p.SharedName == "新しい架空表示名");
        }
        if (current.State == "unavailable")
        {
            Assert.Equal("IDENTITY_UNAVAILABLE", (await Assert.ThrowsAsync<DomainException>(() => Change(s, s.Owner, true))).Code);
            Assert.All((await Change(s, s.Owner, false)).Participants, p => Assert.Null(p.SharedName));
        }
    }
    [Theory]
    [InlineData("binding")]
    [InlineData("scope")]
    [InlineData("role")]
    public async Task Azure_binding_and_grant_changes_are_rechecked_locally_and_disable_disclosure(string change)
    {
        var s = await Create(true); await Share(s);
        if (change == "binding") s.Config["Auth:Bindings:1:Enabled"] = "false";
        if (change == "scope") s.Config["Auth:Grants:1:ScopeIds:0"] = "outside";
        if (change == "role") s.Config["Auth:Bindings:1:Roles:0"] = "analyst";
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.Equal("unavailable", current.State); Assert.All(current.Participants, p => Assert.Null(p.SharedName));
        await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadAsync(s.Guest, s.RoomId, default));
    }
    [Fact]
    public async Task Wrong_tenant_profile_is_never_disclosed_even_when_the_store_returns_a_corrupt_row()
    {
        var store = new FaultStore(); var s = await Create(true, store); await Share(s);
        store.CrossTenant = true;
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.Equal("unavailable", current.State); Assert.All(current.Participants, p => Assert.Null(p.SharedName));
    }
    [Fact]
    public async Task Azure_oid_rebinding_invalidates_old_consent_even_when_logical_person_and_grant_remain()
    {
        var s = await Create(true); await Share(s);
        s.Config["Auth:Bindings:1:Oid"] = "44444444-4444-4444-8444-444444444444";
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        // Config-only rebinding is now a deployment mismatch; canonical authority must be changed offline.
        Assert.Equal("unavailable", current.State); Assert.False(current.SelfConsent);
        Assert.All(current.Participants, p => Assert.Null(p.SharedName));
    }
    [Fact]
    public async Task Observed_binding_disable_and_restore_never_resurrects_old_grants()
    {
        var s = await Create(true); await Share(s);
        s.Config["Auth:Bindings:1:Enabled"] = "false";
        Assert.Equal("unavailable", (await s.Service.ReadAsync(s.Owner, s.RoomId, default)).State);
        s.Config["Auth:Bindings:1:Enabled"] = "true";
        var restored = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.Equal("pending", restored.State); Assert.False(restored.SelfConsent);
        Assert.All(restored.Participants, p => Assert.Null(p.SharedName));
        var first = await Change(s, s.Guest, true); Assert.All(first.Participants, p => Assert.Null(p.SharedName));
        await Change(s, s.Owner, true); Assert.Equal("shared", (await s.Service.ReadAsync(s.Owner, s.RoomId, default)).State);
    }
    [Fact]
    public async Task Lost_acknowledgement_keeps_durable_fence_closed_and_offline_recovery_preserves_receipt()
    {
        var store = new FaultStore(); var s = await Create(false, store);
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        var key = Key(); var input = new IdentityDisclosureConsent(true, current.ReviewedVersion, current.FieldSet);
        store.LoseAcknowledgement = true;
        await Assert.ThrowsAsync<IOException>(() => s.Service.ChangeAsync(s.Owner, s.RoomId, key, current.Etag, input, default));
        Assert.Equal("ACCESS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadAsync(s.Owner, s.RoomId, default))).Code);
        var held = (await store.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default))!;
        await new FenceRecovery(store).RecoverAsync("demo", held.ETag, [s.Sid], default);
        var retry = await s.Service.ChangeAsync(s.Owner, s.RoomId, key, current.Etag, input, default);
        Assert.True(retry.SelfConsent); Assert.All(retry.Participants, p => Assert.Null(p.SharedName));
    }
    [Fact]
    public async Task Profile_changed_after_consent_commit_is_rechecked_before_any_name_response()
    {
        var store = new FaultStore(); var s = await Create(false, store);
        await Change(s, s.Owner, true); store.EditProfileAfterConsent = true;
        var response = await Change(s, s.Guest, true);
        Assert.False(response.SelfConsent); Assert.All(response.Participants, p => Assert.Null(p.SharedName));
        await Share(s);
        Assert.Contains((await s.Service.ReadAsync(s.Owner, s.RoomId, default)).Participants, p => p.SharedName == "処理中に変更された表示名");
    }
    [Fact]
    public async Task Another_room_has_distinct_aliases_and_does_not_inherit_shared_names_or_consent()
    {
        var s = await Create(); await Share(s);
        var original = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        var other = await s.Workspace.CreateAsync(s.Owner, Key(), new("別の架空相談", "異なる活動"), default);
        var room = await s.Workspace.ResolveRoomAsync(s.Owner, other.RoomId!, default);
        var session = await s.Chat.SessionAsync(s.Owner, room.SessionId, default);
        await s.Chat.InviteAsync(s.Owner, room.SessionId, Key(), session.ETag, new(s.Guest.PersonId), true, default);
        var invitation = (await s.Chat.ParticipationsAsync(s.Guest, room.SessionId, default)).Items.Single();
        await s.Chat.DecideAsync(s.Guest, room.SessionId, Key(), invitation.ETag, new("accepted"), default);
        var separate = await s.Service.ReadAsync(s.Owner, other.RoomId!, default);
        Assert.False(separate.SelfConsent); Assert.All(separate.Participants, p => Assert.Null(p.SharedName));
        Assert.Empty(original.Participants.Select(p => p.AliasId).Intersect(separate.Participants.Select(p => p.AliasId)));
    }
    [Fact]
    public async Task Nonparticipant_wrong_tenant_scope_role_and_principal_cannot_read_or_write()
    {
        var s = await Create();
        foreach (var a in new[] { IdentityResolver.DemoPerson("demo-person-03")!, s.Owner with { TenantId = "outside" },
            s.Owner with { ScopeIds = ["outside"] }, s.Owner with { Roles = ["analyst"] }, s.Owner with { PrincipalId = "other" } })
        {
            await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadAsync(a, s.RoomId, default));
            await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(a, s.RoomId, Key(), "\"old\"",
                new(true, new string('a', 64), IdentityDisclosureConsent.DisplayNameFieldSet), default));
        }
    }
    [Fact]
    public async Task Leaving_and_new_membership_do_not_inherit_previous_identity_consent()
    {
        var s = await Create(); await Share(s);
        var participation = (await s.Chat.ParticipationsAsync(s.Guest, s.Sid, default)).Items.Single();
        await s.Chat.DecideAsync(s.Guest, s.Sid, Key(), participation.ETag, new("left"), default);
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.False(current.SelfConsent); Assert.All(current.Participants, p => Assert.Null(p.SharedName));
        await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadAsync(s.Guest, s.RoomId, default));
    }
    [Fact]
    public async Task Added_third_participant_requires_new_consent_from_every_current_participant()
    {
        var s = await Create(); await Share(s); var third = IdentityResolver.DemoPerson("demo-person-03")!;
        var session = await s.Chat.SessionAsync(s.Owner, s.Sid, default);
        await s.Chat.InviteAsync(s.Owner, s.Sid, Key(), session.ETag, new(third.PersonId), true, default);
        await Assert.ThrowsAsync<DomainException>(() => s.Service.ReadAsync(third, s.RoomId, default));
        var invitation = (await s.Chat.ParticipationsAsync(third, s.Sid, default)).Items.Single();
        await s.Chat.DecideAsync(third, s.Sid, Key(), invitation.ETag, new("accepted"), default);
        var current = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        Assert.False(current.SelfConsent); Assert.Equal(3, current.Participants.Length);
        Assert.All(current.Participants, p => Assert.Null(p.SharedName));
        await Change(s, s.Owner, true); var two = await Change(s, s.Guest, true);
        Assert.All(two.Participants, p => Assert.Null(p.SharedName));
        var all = await Change(s, third, true); Assert.Equal("shared", all.State);
        Assert.All(all.Participants, p => Assert.NotNull(p.SharedName));
    }
    [Fact]
    public async Task Stale_review_etag_and_idempotency_conflicts_require_explicit_reconfirmation()
    {
        var s = await Create(); var old = await s.Service.ReadAsync(s.Owner, s.RoomId, default);
        var key = Key(); var input = new IdentityDisclosureConsent(true, old.ReviewedVersion, old.FieldSet);
        var first = await s.Service.ChangeAsync(s.Owner, s.RoomId, key, old.Etag, input, default);
        var replay = await s.Service.ChangeAsync(s.Owner, s.RoomId, key, old.Etag, input, default);
        Assert.Equal(first.Etag, replay.Etag);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(s.Owner, s.RoomId,
            key, old.Etag, input with { Granted = false }, default))).Code);
        Assert.Equal(412, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(s.Guest, s.RoomId,
            Key(), old.Etag, input, default))).Status);
        Assert.Equal("STALE_IDENTITY_REVIEW", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(s.Guest, s.RoomId,
            Key(), first.Etag, input with { ReviewedVersion = new string('b', 64) }, default))).Code);
    }
    [Fact]
    public async Task Serialization_callback_holds_fence_and_room_lock_until_names_have_been_consumed()
    {
        var s = await Create(); await Share(s);
        Task<bool>? waiting = null;
        await s.Service.ConsumeAsync(s.Owner, s.RoomId, async (view, token) =>
        {
            Assert.Equal("shared", view.State);
            var profiles = new ProfileService(s.Repo, new(s.Repo), new(s.Repo, TimeProvider.System));
            var person = (await s.Repo.ReadAsync(RecordPartition.Catalog("demo"), s.Owner.PersonId, default))!;
            Assert.Equal("ACCESS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => profiles.UpdateAsync(s.Owner, s.Owner.PersonId,
                Key(), person.ETag, new("別の表示名", true, "correct", []), default))).Code);
            var invite = (await s.Chat.ParticipationsAsync(s.Guest, s.Sid, default)).Items.Single();
            Assert.Equal("ACCESS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => s.Chat.DecideAsync(s.Guest, s.Sid,
                Key(), invite.ETag, new("left"), default))).Code);
            Assert.Equal("ACCESS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(s.Guest, s.RoomId,
                Key(), view.Etag, new(false, view.ReviewedVersion, view.FieldSet), default))).Code);
            waiting = s.Chat.LockedAsync(s.Owner, s.Sid, () => Task.FromResult(true), default);
            Assert.False(waiting.IsCompleted); // do not wait for this lock until the serializer has returned
            return JsonSerializer.Serialize(view, Json);
        }, default);
        Assert.True(await waiting!);
        var revoked = await Change(s, s.Guest, false);
        Assert.All(revoked.Participants, p => Assert.Null(p.SharedName));
    }
    [Fact]
    public void Consent_rejects_other_targets_missing_fields_and_unsupported_field_sets()
    {
        var valid = "{\"granted\":true,\"reviewedVersion\":\"" + new string('a', 64) + "\",\"fieldSet\":\"profile-display-name-v1\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdentityDisclosureConsent>(valid[..^1] + ",\"personId\":\"other\"}", Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IdentityDisclosureConsent>("{\"granted\":true}", Json));
        Assert.Throws<ArgumentException>(() => new IdentityDisclosureConsent(true, new string('a', 64), "email").Validate());
    }
    private static (WebApplicationFactory<Program> Factory, string Token) ProductionFixture()
    {
        const string clientId = "22222222-2222-4222-8222-222222222222", oid = "33333333-3333-4333-8333-333333333333";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("identity-test-only-signing-material-0123456789abcdef"));
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            foreach (var item in new Dictionary<string, string>
            {
                ["Data:Mode"] = "Azure", ["Ai:Mode"] = "Mock", ["Cosmos:Endpoint"] = "https://unit-test.documents.azure.com/",
                ["Workspace:LegacyApisRestricted"] = "true",
                ["Auth:TenantId"] = Tenant, ["Auth:ClientId"] = clientId, ["Auth:AllowedObjectIds:0"] = oid,
                ["Auth:Bindings:0:TenantId"] = Tenant, ["Auth:Bindings:0:Oid"] = oid,
                ["Auth:Bindings:0:PrincipalId"] = "principal-demo-01", ["Auth:Bindings:0:PersonId"] = "demo-person-01",
                ["Auth:Bindings:0:Roles:0"] = "member", ["Auth:Bindings:0:Enabled"] = "true",
                ["Auth:Grants:0:PrincipalId"] = "principal-demo-01", ["Auth:Grants:0:ScopeIds:0"] = "demo"
            }) b.UseSetting(item.Key, item.Value);
            b.ConfigureServices(s =>
            {
                var memory = new MemoryRecordStore();
                AuthorityProvider.InitializeAsync(memory,Tenant,
                    [new(Tenant,oid,"principal-demo-01","demo-person-01",["member"],true)],
                    [new("principal-demo-01",["demo"])],default).GetAwaiter().GetResult();
                DemoRecords.SeedAsync(memory, Tenant, default).GetAwaiter().GetResult();
                var profile = memory.ReadAsync(RecordPartition.Catalog(Tenant), "demo-person-01", default).GetAwaiter().GetResult()!;
                profile.Value["displayName"] = "private-fixture-name-only"; profile.Value["departmentId"] = "private-fixture-department";
                memory.CommitAsync(RecordPartition.Catalog(Tenant), [RecordWrite.Replace(profile.Value, profile.ETag)], default).GetAwaiter().GetResult();
                s.AddSingleton<IRecordStore>(memory); s.AddSingleton<IPrimaryRepository>(new PrimaryRepository(memory));
                s.AddSingleton<IProjectionRepository>(new MemoryProjectionRepository(memory));
                var ai = new UnusedAi(); s.AddSingleton<IKnowledgeDrafter>(ai); s.AddSingleton<IDecisionProvider>(ai);
                s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0" };
                    config.SigningKeys.Add(key); o.Configuration = config;
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
            });
        });
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken($"https://login.microsoftonline.com/{Tenant}/v2.0", clientId,
            [new Claim("tid", Tenant), new Claim("oid", oid), new Claim("scp", "access_as_user")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
        return (factory, token);
    }
    [Fact]
    public async Task Azure_api_resolves_current_jwt_binding_and_rejects_old_oid_after_rebinding_without_database_calls()
    {
        var fixture = ProductionFixture(); using var factory = fixture.Factory; using var client = factory.CreateClient();
        var token = fixture.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var path = "/api/v1/workspace/rooms/awr-" + Key() + "/identity-consent";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        factory.Services.GetRequiredService<IConfiguration>()["Auth:Bindings:0:Oid"] = "44444444-4444-4444-8444-444444444444";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task Production_azure_routes_with_local_jwt_create_anonymous_room_and_block_legacy_records()
    {
        var fixture = ProductionFixture(); using var factory = fixture.Factory; using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/workspace/capabilities")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        var capabilityResponse = await client.GetAsync("/api/v1/workspace/capabilities"); capabilityResponse.EnsureSuccessStatusCode();
        var capabilities = (await capabilityResponse.Content.ReadFromJsonAsync<AnonymousWorkspaceCapabilities>())!;
        Assert.Equal("anonymous-workspace-v1", capabilities.ContractVersion); Assert.True(capabilities.Available);
        Assert.True(capabilities.StructuredIdentifiersRemoved); Assert.True(capabilities.RoomLocalAliases);
        Assert.True(capabilities.OpaqueOperationIds); Assert.False(capabilities.FreeTextAnonymized);
        Assert.Equal("operator-or-development", capabilities.LegacyApiIsolation); Assert.Equal("server", capabilities.ProcessingMode);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/workspace/challenges")
        { Content = JsonContent.Create(new AnonymousWorkspaceCreate("架空の業務相談", "引き継ぎを相談する")) };
        create.Headers.Add("Idempotency-Key", Key());
        var created = await client.SendAsync(create); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var challenge = (await created.Content.ReadFromJsonAsync<AnonymousWorkspaceChallenge>())!;
        Assert.StartsWith("awc-", challenge.Id); Assert.True(Guid.TryParseExact(challenge.Id[4..], "N", out _));
        Assert.NotNull(challenge.RoomId); Assert.StartsWith("awr-", challenge.RoomId);
        Assert.True(Guid.TryParseExact(challenge.RoomId![4..], "N", out _));
        var roomResponse = await client.GetAsync("/api/v1/workspace/rooms/" + challenge.RoomId); roomResponse.EnsureSuccessStatusCode();
        Assert.Equal("no-store", roomResponse.Headers.CacheControl!.ToString());
        var raw = await roomResponse.Content.ReadAsStringAsync();
        foreach (var privateValue in new[] { "principal-demo-01", "demo-person-01", Tenant, "ownerId", "participantIds", "createdBy",
            "private-fixture-name-only", "private-fixture-department", "departmentId", "displayName" }) Assert.DoesNotContain(privateValue, raw);
        var room = JsonSerializer.Deserialize<AnonymousWorkspaceRoom>(raw, Json)!;
        Assert.Equal(challenge.RoomId, room.Id); Assert.Single(room.Participants);
        Assert.StartsWith("awa-", room.Participants[0].AliasId);
        var repo = factory.Services.GetRequiredService<IPrimaryRepository>();
        var internalChallenge = (await repo.QueryAsync(RecordPartition.Catalog(Tenant), "challenge", null, null, 100, null, default)).Items.Single();
        Assert.DoesNotContain(internalChallenge.Value.Text("id"), raw); Assert.DoesNotContain(internalChallenge.Value.Text("sessionId"), raw);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/sessions/" + internalChallenge.Value.Text("sessionId"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/conversation-context")).StatusCode);
    }
}
