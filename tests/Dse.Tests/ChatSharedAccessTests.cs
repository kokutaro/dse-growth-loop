using System.Text.Json;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class ChatSharedAccessTests
{
    [Fact]
    public async Task Every_participant_must_read_sources_before_AI_and_membership_invalidates_cached_answers()
    {
        const string tenant = "11111111-1111-1111-1111-111111111111";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Data:Mode"] = "Azure", ["Auth:TenantId"] = tenant, ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false", ["AiUsage:MaxOutputTokens"] = "3000",
            ["Auth:Bindings:0:TenantId"] = tenant, ["Auth:Bindings:0:Oid"] = tenant, ["Auth:Bindings:0:PrincipalId"] = "owner", ["Auth:Bindings:0:PersonId"] = "demo-person-01", ["Auth:Bindings:0:Enabled"] = "true", ["Auth:Bindings:0:Roles:0"] = "member",
            ["Auth:Bindings:1:TenantId"] = tenant, ["Auth:Bindings:1:Oid"] = "22222222-2222-2222-2222-222222222222", ["Auth:Bindings:1:PrincipalId"] = "guest", ["Auth:Bindings:1:PersonId"] = "demo-person-02", ["Auth:Bindings:1:Enabled"] = "true", ["Auth:Bindings:1:Roles:0"] = "member",
            ["Auth:Grants:0:PrincipalId"] = "owner", ["Auth:Grants:0:ScopeIds:0"] = "common", ["Auth:Grants:0:ScopeIds:1"] = "private",
            ["Auth:Grants:1:PrincipalId"] = "guest", ["Auth:Grants:1:ScopeIds:0"] = "common"
        }).Build();
        var store = new MemoryRecordStore();
        await AuthorityFixture.InitializeAsync(store,config,tenant);
        foreach (var row in DemoRecords.Create(tenant))
        {
            row["scopeIds"] = RecordJson.Array(["common"]);
            if (row.Text("kind") == "knowledge") row["authorityToken"] = AuthorityProvider.Token((await store.ReadAsync(RecordPartition.Catalog(tenant),AuthorityProvider.ControlId,default))!.Value);
            await store.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Create(row)], default);
        }
        var repo = new PrimaryRepository(store); var identities = new IdentityResolver(config); var owner = identities.ResolvePerson(tenant, "demo-person-01")!;
        var chat = new ConversationService(repo, new(), identities, TimeProvider.System, new(repo));
        var sid = (await chat.CreateAsync(owner, "challenge-shared-0001", new("架空", "相談"), default)).Text("sessionId");
        var session = await chat.SessionAsync(owner, sid, default);
        await chat.UpdateAsync(owner, sid, "enable-shared-0001", session.ETag, new("enabled", "open"), default);
        session = await chat.SessionAsync(owner, sid, default);
        const string text = "引き継ぎで困っています。事例を教えてください。";
        var m = await chat.SendAsync(owner, sid, "send-shared-0001", session.ETag, new("client-shared-0001", text), default);
        var context = new AiContext(owner, "chat-test", "search-rerank-v1", Retriever.TaxonomyVersion, 1, [], [], sid)
        { ChatMembershipVersion = 1, ChatLastSequence = 1, ConversationEvidenceIds = [m.Text("id")] };
        var units = (await new MemoryProjectionRepository(store).FindAsync(new([], [], ["引き継ぎ"]), owner.Search, default)).Where(u => u.EntityType == "knowledge").ToArray();
        context = context with { Candidates = units };
        var guard = new SourceGuard(repo);
        using var ai = new AiService(config, repo, guard, new(repo), new(store, TimeProvider.System), MockAiTransport.Instance, TimeProvider.System);
        Assert.Equal(AiStatus.Succeeded, (await ai.RerankAsync(context, text, default)).Status);
        Assert.True((await ai.RerankAsync(context, text, default)).Cached);
        session = await chat.SessionAsync(owner, sid, default);
        await chat.InviteAsync(owner, sid, "invite-shared-0001", session.ETag, new("demo-person-02"), false, default);
        var guest = identities.ResolvePerson(tenant, "demo-person-02")!;
        var invitation = (await chat.ParticipationsAsync(guest, sid, default)).Items.Single();
        await chat.DecideAsync(guest, sid, "accept-shared-0001", invitation.ETag, new("accepted"), default);
        await Assert.ThrowsAsync<DomainException>(() => ai.RerankAsync(context, text, default));
        context = context with { ChatMembershipVersion = 2 };
        Assert.False((await ai.RerankAsync(context, text, default)).Cached);
        var source = (await repo.ReadAsync(RecordPartition.Catalog(tenant), units[0].EntityId, default))!;
        source.Value["scopeIds"] = RecordJson.Array(["private"]);
        await repo.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Replace(source.Value, source.ETag)], default);
        var before = (await repo.QueryAsync(RecordPartition.Conversation(tenant, sid), "aiRun", null, null, 100, null, default)).Items.Length;
        await Assert.ThrowsAsync<DomainException>(() => ai.RerankAsync(context, text, default));
        Assert.Equal(before, (await repo.QueryAsync(RecordPartition.Conversation(tenant, sid), "aiRun", null, null, 100, null, default)).Items.Length);
        Assert.Empty(await new ChatAccess(repo, guard, identities, config).FilterAsync(context, units, default));
    }
}
