using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
using Microsoft.AspNetCore.Hosting;

namespace Dse.Tests;

public class KnowledgeTests
{
    private static readonly ActorAccess Owner = IdentityResolver.Demo with { Roles = ["member", "publisher", "analyst"] };
    private static readonly ActorAccess Guest = IdentityResolver.DemoPerson("demo-person-02")!;
    private static string Key() => Guid.NewGuid().ToString("N");
    private sealed class Fake : IKnowledgeDrafter, IDecisionProvider
    {
        public bool Invent;
        public Task<AiResult<KnowledgeSummary>> DraftAsync(AiContext c, CancellationToken ct) => Task.FromResult(new AiResult<KnowledgeSummary>(AiStatus.Succeeded,
            new(KnowledgeSummary.Fields.Select(f => new SummarySection(f, "ソフト 引き継ぎを試した", "participant_report", [Invent ? "invented" : c.ConversationEvidenceIds![0]])).ToArray()), "fixture-run", null));
        public Task<AiResult<Decisions>> DecideAsync(AiContext c, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct) => Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded,
            new(choices.ToDictionary(q => q.Id, q => new ChoiceAnswer("software", q.Options.ToDictionary(x => x.Key, x => x.Key == "software" ? 1d : 0d), .7)),
                nouls.ToDictionary(q => q.Id, _ => new NoulAnswer(NoulState.Unknown, null))), "fixture-classify", null));
    }
    private sealed record Setup(IRecordStore Store, IPrimaryRepository Repo, ConversationService Chat, KnowledgeService Service, Fake Ai, string Sid, IConfiguration Config);
    private static async Task<Setup> Create(IRecordStore? supplied = null)
    {
        var store = supplied ?? new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new PrimaryRepository(store); var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock" }).Build();
        var chat = new ConversationService(repo, new(), new(config), TimeProvider.System); var ai = new Fake();
        var service = new KnowledgeService(repo, store, chat, new(repo), ai, ai, new(config), config, TimeProvider.System);
        var challenge = await chat.CreateAsync(Owner, Key(), new("架空相談", "ソフト 引き継ぎ"), default); var sid = challenge.Text("sessionId");
        var session = await chat.SessionAsync(Owner, sid, default);
        await chat.InviteAsync(Owner, sid, Key(), session.ETag, new(Guest.PersonId), true, default);
        var invite = (await chat.ParticipationsAsync(Guest, sid, default)).Items.Single();
        await chat.DecideAsync(Guest, sid, Key(), invite.ETag, new("accepted"), default);
        session = await chat.SessionAsync(Owner, sid, default);
        await chat.SendAsync(Owner, sid, Key(), session.ETag, new(Key(), "ソフト 引き継ぎを試した"), default);
        return new(store, repo, chat, service, ai, sid, config);
    }
    private sealed class FaultStore : IRecordStore
    {
        private readonly MemoryRecordStore inner = new();
        public bool Fail, AfterCommit;
        public Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct) => inner.ReadAsync(p, id, ct);
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) => inner.QueryAsync(p, kind, field, parent, size, token, ct);
        public async Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            var fail = Fail && p.Container == "catalog" && writes.Any(x => x.Value.Text("kind") == "knowledge");
            if (fail && !AfterCommit) throw new IOException("offline");
            await inner.CommitAsync(p, writes, ct);
            if (fail) throw new IOException("lost acknowledgement");
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_database_failure_and_lost_acknowledgement_recover_from_the_durable_job(bool afterCommit)
    {
        var store = new FaultStore(); var s = await Create(store); var state = await Review(s); var version = state.Number("sourceVersion");
        foreach (var actor in new[] { Owner, Guest }) await Change(s, actor, "consent", new { version, publication = true, analysis = false });
        await Change(s, Owner, "approve", new { version });
        store.Fail = true; store.AfterCommit = afterCommit;
        var error = await Assert.ThrowsAsync<IOException>(() => s.Service.ProcessPublicationAsync("demo", s.Sid, version, default));
        await s.Service.RecordPublicationFailureAsync("demo", s.Sid, version, error, default);
        var pending = (await s.Repo.ReadAsync(RecordPartition.Conversation("demo", s.Sid), KnowledgeService.JobId(s.Sid, version), default))!;
        Assert.Equal("queued", pending.Value.Text("status")); Assert.Equal("pending", pending.Value.Text("notificationState"));
        store.Fail = false;
        // Unknown commit leaves the shared durable fence closed. Recovery is offline, never a lease expiry.
        var held = (await store.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default))!;
        await new FenceRecovery(store).RecoverAsync("demo", held.ETag, [s.Sid], default);
        Assert.Equal(1, await s.Service.RecoverAsync("demo", s.Sid, default));
        Assert.Single((await store.QueryAsync(RecordPartition.Catalog("demo"), "searchUnit", "entityId", KnowledgeService.Id(s.Sid), 100, null, default)).Items);
    }
    private static async Task<JsonObject> Change(Setup s, ActorAccess a, string op, object input)
    {
        var state = await s.Service.StateAsync(a,s.Sid,default);
        var body = JsonSerializer.SerializeToNode(input,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (op is "request-review" or "consent" or "approve") body["reviewedVersion"] = state.Text("reviewedVersion");
        return await s.Service.ChangeAsync(a,s.Sid,op,Key(),state.Text("etag"),body,default);
    }
    private static async Task<JsonObject> Review(Setup s)
    {
        await Change(s, Owner, "outcome", new { resolution = "unresolved", result = "原因は未解決" });
        await Change(s, Guest, "feedback", new { value = (int?)null, comment = "未評価" });
        await s.Service.GenerateAsync(Owner, s.Sid, Key(), (await s.Chat.SessionAsync(Owner, s.Sid, default)).ETag, default);
        var state = await s.Service.StateAsync(Owner, s.Sid, default);
        await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = new[] { "demo" } });
        return await Change(s, Owner, "request-review", new { version = (await s.Service.StateAsync(Owner, s.Sid, default)).Number("sourceVersion") });
    }
    private static async Task Publish(Setup s, object? analysisMetadata = null)
    {
        var state = await Review(s);
        if (analysisMetadata is not null)
        {
            state = await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = state.Strings("scopeIds"), analysisMetadata });
            state = await Change(s, Owner, "request-review", new { version = state.Number("sourceVersion") });
        }
        var version = state.Number("sourceVersion");
        foreach (var actor in new[] { Owner, Guest }) await Change(s, actor, "consent", new { version, publication = true, analysis = true });
        await Change(s, Owner, "approve", new { version });
        await s.Service.ProcessPublicationAsync("demo", s.Sid, version, default);
    }
    [Fact]
    public async Task Outcome_null_feedback_review_publication_and_withdrawal_are_authoritative()
    {
        var s = await Create(); await Publish(s);
        var feedback = await s.Repo.QueryAsync(RecordPartition.Conversation("demo", s.Sid), "feedback", null, null, 100, null, default);
        Assert.Null(feedback.Items.Single().Value["value"]); Assert.Equal("usefulness-v1", feedback.Items.Single().Value.Text("scaleVersion"));
        var projections = new MemoryProjectionRepository(s.Store); var guard = new SourceGuard(s.Repo,s.Config);
        var units = await projections.FindAsync(new([], [], ["ソフト"], .1, .5, 20), Owner.Search, default);
        Assert.Single(await guard.FilterAsync(Guest, units, default), x => x.EntityId == KnowledgeService.Id(s.Sid));
        Assert.Single(await s.Service.AnalysisInputsAsync(Owner, default));
        await Change(s, Guest, "withdraw", new { });
        Assert.DoesNotContain(await guard.FilterAsync(Guest, units, default), x => x.EntityId == KnowledgeService.Id(s.Sid));
        Assert.Empty(await s.Service.AnalysisInputsAsync(Owner, default));
        await s.Service.ProcessPublicationAsync("demo", s.Sid, 2, default);
        Assert.DoesNotContain(await guard.FilterAsync(Guest, units, default), x => x.EntityId == KnowledgeService.Id(s.Sid));
    }
    [Fact]
    public async Task Editing_and_scope_changes_require_all_participants_to_confirm_the_new_version()
    {
        var s = await Create(); var state = await Review(s); var version = state.Number("sourceVersion");
        await Change(s, Owner, "consent", new { version, publication = true, analysis = false });
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Owner, "approve", new { version }));
        await Change(s, Guest, "consent", new { version, publication = true, analysis = false });
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Guest, "approve", new { version }));
        state = await s.Service.StateAsync(Owner, s.Sid, default);
        await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = new[] { "demo" } });
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Owner, "approve", new { version }));
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Owner with { ScopeIds = ["outside"] }, "consent", new { version, publication = true, analysis = true }));
        await Assert.ThrowsAsync<DomainException>(() => s.Service.StateAsync(IdentityResolver.DemoPerson("demo-person-03")!, s.Sid, default));
    }
    [Fact]
    public async Task Duplicate_approval_republication_old_jobs_and_analysis_without_consent_do_not_leak()
    {
        var s = await Create(); await Publish(s); var state = await s.Service.StateAsync(Owner, s.Sid, default); var version = state.Number("sourceVersion");
        await Change(s, Owner, "approve", new { version });
        await s.Service.ProcessPublicationAsync("demo", s.Sid, version, default);
        var count = (await s.Store.QueryAsync(RecordPartition.Catalog("demo"), "searchUnit", "entityId", KnowledgeService.Id(s.Sid), 100, null, default)).Items.Length;
        Assert.Equal(1, count);
        await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = new[] { "demo" } });
        await s.Service.ProcessPublicationAsync("demo", s.Sid, version, default);
        Assert.Empty(await s.Service.AnalysisInputsAsync(Owner, default));
        await Change(s, Owner, "request-review", new { version = version + 1 });
        foreach (var actor in new[] { Owner, Guest }) await Change(s, actor, "consent", new { version = version + 1, publication = true, analysis = false });
        await Change(s, Owner, "approve", new { version = version + 1 });
        await s.Service.ProcessPublicationAsync("demo", s.Sid, version + 1, default);
        var guard = new SourceGuard(s.Repo,s.Config);
        var units = await new MemoryProjectionRepository(s.Store).FindAsync(new([], [], ["ソフト"], .1, .5, 20), Guest.Search, default);
        Assert.Single(await guard.FilterAsync(Guest, units, default), x => x.EntityId == KnowledgeService.Id(s.Sid));
        Assert.Empty(await s.Service.AnalysisInputsAsync(Owner, default));
    }
    [Fact]
    public async Task Invented_evidence_is_rejected_and_unknown_feedback_is_not_zero()
    {
        var s = await Create(); s.Ai.Invent = true;
        await Change(s, Owner, "outcome", new { resolution = "partial", result = "部分解決" });
        await Assert.ThrowsAsync<DomainException>(() => s.Service.GenerateAsync(Owner, s.Sid, Key(), (s.Chat.SessionAsync(Owner, s.Sid, default).Result).ETag, default));
        foreach (var bad in new[] { 0, 6 }) await Assert.ThrowsAsync<ArgumentException>(() => Change(s, Guest, "feedback", new { value = bad }));
    }
    [Fact]
    public async Task Consent_revocation_and_membership_changes_block_AI_cache_and_existing_citations()
    {
        var s = await Create(); await Publish(s);
        var guard = new SourceGuard(s.Repo,s.Config); var sourceUse = new SourceUseService(s.Repo, guard, new(s.Repo), new(new ConfigurationBuilder().Build()));
        var units = await new MemoryProjectionRepository(s.Store).FindAsync(new([], [], ["引き継ぎ"]), Guest.Search, default);
        var source = await guard.RequireAsync(Guest, units.Single(x => x.EntityId == KnowledgeService.Id(s.Sid)), default);
        // A persisted AI citation in the same session must be hidden on the next authoritative history read.
        var session = await s.Chat.SessionAsync(Owner, s.Sid, default);
        var message = RecordJson.New("message", ConversationService.MessageId(s.Sid, 2), "demo", Owner.PrincipalId, ["demo"], DateTimeOffset.UtcNow);
        message["sessionId"] = s.Sid; message["sequence"] = 2; message["authorType"] = "ai"; message["body"] = "公開済み知識の引用";
        message["sourceSnapshots"] = System.Text.Json.JsonSerializer.SerializeToNode(source.Snapshots, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        session.Value["lastSequence"] = 2;
        await s.Repo.CommitAsync(RecordPartition.Conversation("demo", s.Sid), [RecordWrite.Create(message), RecordWrite.Replace(session.Value, session.ETag)], default);
        await Change(s, Guest, "consent", new { version = source.Unit.SourceVersion, publication = false, analysis = false });
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => sourceUse.DisplayAsync(Guest, source.Snapshots, _ => Task.FromResult(true), default))).Code);
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => sourceUse.UseAsync(Guest, source.Unit, (_, _) => Task.FromResult(true), default))).Code);
        var history = await s.Chat.MessagesAsync(Owner, s.Sid, 0, 100, default);
        Assert.Equal("revoked", history.Items[1].Text("citationState")); Assert.DoesNotContain("公開済み知識の引用", history.Items[1].Text("body"));
        Assert.Equal("公開済み知識の引用", (await s.Repo.ReadAsync(RecordPartition.Conversation("demo", s.Sid), message.Text("id"), default))!.Value.Text("body"));
    }
    [Fact]
    public async Task Participant_who_left_cannot_confirm_and_unapproved_summary_is_not_an_analysis_input()
    {
        var s = await Create(); var state = await Review(s); var version = state.Number("sourceVersion");
        foreach (var actor in new[] { Owner, Guest }) await Change(s, actor, "consent", new { version, publication = true, analysis = true });
        Assert.Empty(await s.Service.AnalysisInputsAsync(Owner, default));
        var invite = (await s.Chat.ParticipationsAsync(Guest, s.Sid, default)).Items.Single();
        await s.Chat.DecideAsync(Guest, s.Sid, Key(), invite.ETag, new("left"), default);
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Guest, "consent", new { version, publication = true, analysis = true }));
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Owner, "approve", new { version }));
    }
    [Fact]
    public async Task Legacy_session_without_original_participant_field_still_requires_departed_participant_confirmation()
    {
        var s = await Create(); var partition = RecordPartition.Conversation("demo", s.Sid);
        var session = await s.Chat.SessionAsync(Owner, s.Sid, default); session.Value.Remove("originalParticipantIds");
        await s.Repo.CommitAsync(partition, [RecordWrite.Replace(session.Value, session.ETag)], default);
        var invite = (await s.Chat.ParticipationsAsync(Guest, s.Sid, default)).Items.Single();
        await s.Chat.DecideAsync(Guest, s.Sid, Key(), invite.ETag, new("left"), default);
        await Change(s, Owner, "outcome", new { resolution = "unresolved", result = "未解決" });
        await s.Service.GenerateAsync(Owner, s.Sid, Key(), (await s.Chat.SessionAsync(Owner, s.Sid, default)).ETag, default);
        var state = await s.Service.StateAsync(Owner, s.Sid, default);
        state = await Change(s, Owner, "request-review", new { version = state.Number("sourceVersion") });
        Assert.Contains(Guest.PersonId, state.Strings("participantIds"));
        await Change(s, Owner, "consent", new { version = state.Number("sourceVersion"), publication = true, analysis = false });
        await Assert.ThrowsAsync<DomainException>(() => Change(s, Owner, "approve", new { version = state.Number("sourceVersion") }));
    }
    [Fact]
    public async Task Same_request_replays_and_payload_changes_or_stale_ETag_are_rejected()
    {
        var s = await Create(); await Review(s); var state = await s.Service.StateAsync(Owner, s.Sid, default);
        var key = Key(); var input = new JsonObject { ["reviewedVersion"] = state.Text("reviewedVersion"), ["version"] = state.Number("sourceVersion"), ["publication"] = true, ["analysis"] = false };
        var first = await s.Service.ChangeAsync(Owner, s.Sid, "consent", key, state.Text("etag"), input, default);
        var replay = await s.Service.ChangeAsync(Owner, s.Sid, "consent", key, state.Text("etag"), input, default);
        Assert.Equal(first.Text("etag"), replay.Text("etag"));
        input["analysis"] = true;
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(Owner, s.Sid, "consent", key, state.Text("etag"), input, default))).Status);
        Assert.Equal(412, (await Assert.ThrowsAsync<DomainException>(() => s.Service.ChangeAsync(Owner, s.Sid, "consent", Key(), state.Text("etag"), input, default))).Status);
    }
    [Fact]
    public async Task Publication_recovers_a_committed_catalog_write_with_missing_completion_without_duplicates()
    {
        var s = await Create(); await Publish(s); var state = await s.Service.StateAsync(Owner, s.Sid, default); var version = state.Number("sourceVersion");
        var partition = RecordPartition.Conversation("demo", s.Sid); var job = (await s.Repo.ReadAsync(partition, KnowledgeService.JobId(s.Sid, version), default))!;
        job.Value["status"] = "queued"; job.Value["notificationState"] = "pending";
        await s.Repo.CommitAsync(partition, [RecordWrite.Replace(job.Value, job.ETag)], default);
        Assert.Equal(1, await s.Service.RecoverAsync("demo", s.Sid, default));
        Assert.Equal("ready", (await s.Repo.ReadAsync(partition, job.Value.Text("id"), default))!.Value.Text("status"));
        Assert.Single((await s.Repo.QueryAsync(RecordPartition.Catalog("demo"), "classificationResult", "entityId", KnowledgeService.Id(s.Sid), 100, null, default)).Items);
        Assert.Equal(0, await s.Service.RecoverAsync("demo", s.Sid, default));
    }
    [Fact]
    public async Task Export_knowledge_records_for_shared_contract_validation()
    {
        var s = await Create(); await Publish(s, new { departmentId = "public-demo-department", resolution = "unresolved", reportedHours = 2.25m, recurrences = 2, feedback = (int?)null }); List<object> rows = [];
        foreach (var (partition, kinds) in new[] {
            (RecordPartition.Catalog("demo"), new[] { "knowledge", "classificationResult", "searchUnit" }),
            (RecordPartition.Conversation("demo", s.Sid), new[] { "knowledgeDraft", "outcome", "feedback", "job" }) })
        foreach (var kind in kinds)
        {
            var page = await s.Store.QueryAsync(partition, kind, null, null, 100, null, default);
            rows.AddRange(page.Items.Where(x => partition.Container == "conversations" || x.Value.Text("entityId") == KnowledgeService.Id(s.Sid) || x.Value.Text("id") == KnowledgeService.Id(s.Sid)).Select(x => new { schema = RecordJson.ContractKinds[kind], value = x.Value }));
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DseGrowthLoop.slnx"))) root = root.Parent;
        var output = Path.Combine(root!.FullName, "tests/Dse.Tests/TestResults"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "knowledge-contract-fixture.json"), System.Text.Json.JsonSerializer.Serialize(rows));
    }
    [Fact]
    public async Task Api_requires_membership_ETag_valid_fields_and_all_confirmations_then_searches_the_approved_summary()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Data:Mode", "Demo"); b.UseSetting("Ai:Mode", "Mock"); b.UseSetting("AiUsage:Stopped", "false"); b.UseSetting("AiUsage:MaxOutputTokens", "3000");
            b.UseEnvironment("Development");
        });
        using var owner = factory.CreateClient(); using var guest = factory.CreateClient(); using var other = factory.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Demo-Person", Guest.PersonId); other.DefaultRequestHeaders.Add("X-Demo-Person", "demo-person-03");
        async Task<System.Net.Http.HttpResponseMessage> Post(HttpClient client, string path, object? body, string? etag = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(body ?? new { }) };
            request.Headers.Add("Idempotency-Key", Key()); if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(request);
        }
        async Task<JsonObject> Get(string path) => (await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonObject>(owner, path))!;
        async Task<JsonObject> Body(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<JsonObject>(response.Content))!; }
        var challenge = await Body(await Post(owner, "/api/v1/challenges", new { title = "架空", summary = "ソフト 引き継ぎ" }));
        var path = "/api/v1/sessions/" + challenge.Text("sessionId");
        var session = await Get(path); await Body(await Post(owner, path + "/participations", new { personId = Guest.PersonId }, session.Text("etag")));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await guest.GetAsync(path + "/knowledge")).StatusCode);
        var invites = (await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonObject>(guest, path + "/participations"))!;
        var decision = new HttpRequestMessage(HttpMethod.Patch, path + "/participation") { Content = System.Net.Http.Json.JsonContent.Create(new { decision = "accepted" }) };
        decision.Headers.Add("Idempotency-Key", Key()); decision.Headers.TryAddWithoutValidation("If-Match", invites["items"]![0]!["etag"]!.GetValue<string>());
        (await guest.SendAsync(decision)).EnsureSuccessStatusCode();
        session = await Get(path);
        await Body(await Post(owner, path + "/messages", new { clientMessageId = Key(), body = "ソフト 引き継ぎを試した" }, session.Text("etag")));
        var state = await Get(path + "/knowledge");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Post(owner, path + "/knowledge/outcome", new { resolution = "resolved", result = "解決" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Post(owner, path + "/knowledge/outcome", new { resolution = "resolved", result = "解決", tenantId = "other" }, state.Text("etag"))).StatusCode);
        state = await Body(await Post(owner, path + "/knowledge/outcome", new { resolution = "resolved", result = "解決" }, state.Text("etag")));
        var draft = await Body(await Post(owner, path + "/knowledge/draft", new { }, state.Text("sessionEtag")));
        Assert.Equal("fixture", draft.Text("processingMode")); Assert.Equal(6, draft["sections"]!.AsArray().Count);
        state = await Get(path + "/knowledge");
        await Body(await Post(owner, path + "/knowledge/request-review", new { version = 1, reviewedVersion = state.Text("reviewedVersion") }, state.Text("etag")));
        state = await Get(path + "/knowledge");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await Post(owner, path + "/knowledge/approve", new { version = 1, reviewedVersion = state.Text("reviewedVersion") }, state.Text("etag"))).StatusCode);
        foreach (var actor in new[] { owner, guest })
        {
            state = await Get(path + "/knowledge");
            await Body(await Post(actor, path + "/knowledge/consent", new { version = 1, reviewedVersion = state.Text("reviewedVersion"), publication = true, analysis = false }, state.Text("etag")));
        }
        state = await Get(path + "/knowledge");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Post(guest, path + "/knowledge/approve", new { version = 1, reviewedVersion = state.Text("reviewedVersion") }, state.Text("etag"))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Post(other, path + "/knowledge/approve", new { version = 1, reviewedVersion = state.Text("reviewedVersion") }, state.Text("etag"))).StatusCode);
        await Body(await Post(owner, path + "/knowledge/approve", new { version = 1, reviewedVersion = state.Text("reviewedVersion") }, state.Text("etag")));
        var diagnostic = await Get(path + "/knowledge");
        Assert.True(diagnostic["publicationJob"]!["status"]!.GetValue<string>() == "ready", diagnostic["publicationJob"]!.ToJsonString());
        await Body(await Post(owner, path + "/knowledge-recovery", new { }));
        var found = await Body(await Post(owner, "/api/v2/search", new { query = "ソフト 引き継ぎ", conditions = new { categories = new[] { new { axis = "domain", categoryId = "software" } }, signalIds = Array.Empty<string>(), terms = new[] { "ソフトウェア" } } }));
        var id = KnowledgeService.Id(challenge.Text("sessionId"));
        Assert.Contains(found["primary"]!["candidates"]!.AsArray(), x => x!["entityId"]!.GetValue<string>() == id);
        Assert.DoesNotContain("participantIds", found.ToJsonString());
        state = await Get(path + "/knowledge");
        await Body(await Post(guest, path + "/knowledge/withdraw", new { }, state.Text("etag")));
        var after = await Body(await Post(owner, "/api/search", new { categories = Array.Empty<object>(), signalIds = Array.Empty<string>(), terms = new[] { "ソフトウェア" } }));
        Assert.DoesNotContain(after["candidates"]!.AsArray(), x => x!["entityId"]!.GetValue<string>() == id);
    }
    [Fact]
    public async Task Public_analysis_metadata_is_versioned_and_metadata_only_edit_revokes_all_prior_consents()
    {
        var s = await Create(); await Publish(s); var state = await s.Service.StateAsync(Owner, s.Sid, default); var previous = state.Number("sourceVersion");
        var oldUnit = (await new MemoryProjectionRepository(s.Store).FindAsync(new([], [], ["ソフト"]), Owner.Search, default)).Single(x => x.EntityId == KnowledgeService.Id(s.Sid));
        var metadata = new { departmentId = "public-demo-department", resolution = "unresolved", reportedHours = 2.25m, recurrences = 2, feedback = 2 };
        state = await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = state.Strings("scopeIds"), analysisMetadata = metadata });
        Assert.Equal(previous + 1, state.Number("sourceVersion")); Assert.Equal("draft", state.Text("status"));
        Assert.Null(state["activeVersion"]); Assert.Null(state["approvedBy"]); Assert.Empty(state["consents"]!.AsObject()); Assert.Equal("not_granted", state.Text("analysisConsent"));
        Assert.Empty(await s.Service.AnalysisInputsAsync(Owner, default));
        Assert.DoesNotContain(await new SourceGuard(s.Repo,s.Config).FilterAsync(Owner, [oldUnit], default), x => x.EntityId == oldUnit.EntityId);
        await Change(s, Owner, "request-review", new { version = state.Number("sourceVersion") });
        foreach (var actor in new[] { Owner, Guest }) await Change(s, actor, "consent", new { version = state.Number("sourceVersion"), publication = true, analysis = true });
        await Change(s, Owner, "approve", new { version = state.Number("sourceVersion") });
        await s.Service.ProcessPublicationAsync("demo", s.Sid, state.Number("sourceVersion"), default);
        var source = await KnowledgeService.PublishedAsync(s.Repo, "demo", s.Sid, state.Number("sourceVersion"), default);
        var catalog = (await s.Repo.ReadAsync(RecordPartition.Catalog("demo"), KnowledgeService.Id(s.Sid), default))!.Value;
        Assert.True(JsonNode.DeepEquals(source!["analysisMetadata"], catalog["analysisMetadata"])); Assert.Equal(source.Number("sourceVersion"), catalog.Number("sourceVersion"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false", ["BusinessAnalysis:MinimumIndependentChallenges"] = "1", ["BusinessAnalysis:MinimumDepartments"] = "1" }).Build();
        var analysis = new BusinessAnalysisService(s.Repo, s.Store, new(s.Repo), new BusinessAnalysisDrafter(config), config, TimeProvider.System);
        var request = new BusinessAnalysisRequest(Key(), DateTimeOffset.UtcNow, BusinessAnalysisService.ConfigVersion);
        var result = await analysis.StartAsync(Owner, request, request.RunId, default);
        Assert.Equal(2.25m, result["groups"]![0]!["metrics"]!["reportedHours"]!.GetValue<decimal>());
        state = await s.Service.StateAsync(Owner, s.Sid, default);
        await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = state.Strings("scopeIds"), analysisMetadata = (object?)null });
        result = await analysis.GetAsync(Owner, request.RunId, default); Assert.Equal("invalidated", result.Text("status")); Assert.False(result.ContainsKey("sourceCount"));
    }
    [Theory]
    [InlineData("{\"departmentId\":\"private person name\"}")]
    [InlineData("{\"reportedHours\":-1}")]
    [InlineData("{\"reportedHours\":100000.01}")]
    [InlineData("{\"reportedHours\":0.001}")]
    [InlineData("{\"reportedHours\":\"2\"}")]
    [InlineData("{\"recurrences\":0}")]
    [InlineData("{\"recurrences\":\"2\"}")]
    [InlineData("{\"feedback\":\"2\"}")]
    [InlineData("{\"recurrences\":10001}")]
    [InlineData("{\"feedback\":0}")]
    [InlineData("{\"feedback\":6}")]
    [InlineData("{\"resolution\":\"automatic_market_success\"}")]
    [InlineData("{\"personId\":\"demo-person-01\"}")]
    [InlineData("[]")]
    public void Public_analysis_metadata_rejects_private_fields_wrong_types_and_unbounded_values(string metadata)
    {
        var sections = KnowledgeSummary.Fields.Select(f => new SummarySection(f, "ダミー", "unknown", [])).ToArray();
        var input = new JsonObject { ["sections"] = System.Text.Json.JsonSerializer.SerializeToNode(sections, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            ["scopeIds"] = RecordJson.Array(["demo"]), ["analysisMetadata"] = JsonNode.Parse(metadata) };
        Assert.Throws<ArgumentException>(() => KnowledgeService.ValidateInput("edit", input));
    }
    [Fact]
    public async Task Public_analysis_metadata_is_nullable_and_omission_preserves_values_but_still_revokes_consent()
    {
        var s = await Create(); await Publish(s, new { departmentId = "public-demo-department", reportedHours = (decimal?)null, resolution = (string?)null });
        var state = await s.Service.StateAsync(Owner, s.Sid, default); var original = state["analysisMetadata"]!.DeepClone();
        state = await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = state.Strings("scopeIds") });
        Assert.True(JsonNode.DeepEquals(original, state["analysisMetadata"])); Assert.Equal("not_granted", state.Text("analysisConsent")); Assert.Null(state["activeVersion"]);
        state = await Change(s, Owner, "edit", new { sections = state["sections"]!.DeepClone(), scopeIds = state.Strings("scopeIds"), analysisMetadata = (object?)null });
        Assert.Null(state["analysisMetadata"]);
    }

}
