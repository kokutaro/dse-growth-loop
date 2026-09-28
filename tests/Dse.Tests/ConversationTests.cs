using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class ConversationTests
{
    private static readonly ActorAccess Owner = IdentityResolver.Demo;
    private static readonly ActorAccess Guest = IdentityResolver.DemoPerson("demo-person-02")!;
    private static readonly ActorAccess Other = IdentityResolver.DemoPerson("demo-person-03")!;
    private sealed class FaultStore(IRecordStore inner) : IPrimaryRepository
    {
        public string? FailKind { get; set; }
        public bool AfterCommit { get; set; }
        public Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct) => inner.ReadAsync(p, id, ct);
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? cursor, CancellationToken ct)
            => inner.QueryAsync(p, kind, field, parent, size, cursor, ct);
        public async Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            var fail = writes.Any(x => x.Value.Text("kind") == FailKind);
            if (fail && !AfterCommit) throw new IOException("offline");
            await inner.CommitAsync(p, writes, ct);
            if (fail) throw new IOException("lost acknowledgment");
        }
    }
    private static ConversationService Service(IPrimaryRepository repo) => new(repo, new(), new IdentityResolver(new ConfigurationBuilder().Build()), TimeProvider.System);
    private static async Task<(FaultStore Repo, ConversationService Service, string Sid)> Setup()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = new FaultStore(store); var service = Service(repo);
        var challenge = await service.CreateAsync(Owner, "create-challenge-0001", new("架空課題", "ダミー相談"), default);
        return (repo, service, challenge.Text("sessionId"));
    }
    private static async Task<StoredRecord> Invite(ConversationService service, string sid)
    {
        var session = await service.SessionAsync(Owner, sid, default);
        await service.InviteAsync(Owner, sid, "invite-person-0001", session.ETag, new(Guest.PersonId), true, default);
        return (await service.ParticipationsAsync(Guest, sid, default)).Items.Single();
    }
    private static async Task Accept(ConversationService service, string sid)
    {
        var invite = await Invite(service, sid);
        await service.DecideAsync(Guest, sid, "accept-person-0001", invite.ETag, new("accepted"), default);
    }
    private static async Task Denied(Func<Task> action, int status = 404) => Assert.Equal(status, (await Assert.ThrowsAsync<DomainException>(action)).Status);

    [Fact]
    public async Task Consent_tenant_scope_and_leave_are_checked_before_reads_writes_and_replay()
    {
        var (repo, service, sid) = await Setup();
        await Denied(() => service.CreateAsync(Owner with { ScopeIds = ["other"] }, "create-challenge-0001", new("架空課題", "ダミー相談"), default));
        var invite = await Invite(service, sid);
        await Denied(() => service.SessionAsync(Guest, sid, default));
        await Denied(() => service.MessagesAsync(Other, sid, 0, 20, default));
        await Denied(() => service.DecideAsync(Other, sid, "forged-decision-01", invite.ETag, new("accepted"), default));
        await service.DecideAsync(Guest, sid, "accept-person-0001", invite.ETag, new("accepted"), default);
        var session = await service.SessionAsync(Guest, sid, default);
        var message = new MessageCreate("client-message-0001", "架空の会話");
        await service.SendAsync(Guest, sid, "send-message-0001", session.ETag, message, default);
        await Denied(() => service.SessionAsync(Guest with { TenantId = "another" }, sid, default));
        await Denied(() => service.SessionAsync(Guest with { ScopeIds = ["another"] }, sid, default));
        var participation = (await service.ParticipationsAsync(Guest, sid, default)).Items.Single();
        await service.DecideAsync(Guest, sid, "leave-person-0001", participation.ETag, new("left"), default);
        await Denied(() => service.MessagesAsync(Guest, sid, 0, 20, default));
        await Denied(() => service.SendAsync(Guest, sid, "send-message-0001", session.ETag, message, default));
        var saved = await repo.ReadAsync(RecordPartition.Conversation("demo", sid), ConversationService.ParticipationId(Guest.PersonId), default);
        Assert.Equal("left", saved!.Value.Text("status"));
        Assert.Single((await service.MessagesAsync(Owner, sid, 0, 20, default)).Items);
    }
    [Fact]
    public async Task Decline_never_grants_membership_and_AI_candidates_do_not_join()
    {
        var (_, service, sid) = await Setup(); var invite = await Invite(service, sid);
        Assert.Equal(new[] { Owner.PersonId }, (await service.SessionAsync(Owner, sid, default)).Value.Strings("participantIds"));
        await service.DecideAsync(Guest, sid, "decline-person-01", invite.ETag, new("declined"), default);
        await Denied(() => service.SessionAsync(Guest, sid, default));
    }
    [Fact]
    public async Task Concurrent_sends_duplicate_sends_and_replay_preserve_contiguous_sequence_and_outbox()
    {
        var (repo, service, sid) = await Setup(); await Accept(service, sid);
        var before = await service.SessionAsync(Owner, sid, default);
        var input = new MessageCreate("client-message-0001", "同時送信");
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.SendAsync(Owner, sid, "send-message-0001", before.ETag, input, default)));
        Assert.All(duplicates, x => Assert.Equal(1, x.Number("sequence")));
        await Denied(() => service.SendAsync(Owner, sid, "send-message-0001", before.ETag, input with { Body = "改ざん" }, default), 409);
        await Denied(() => service.SendAsync(Guest, sid, "send-message-0002", before.ETag, input, default), 412);
        var fresh = await service.SessionAsync(Guest, sid, default);
        await service.SendAsync(Guest, sid, "send-message-0002", fresh.ETag, input, default);
        var history = await service.MessagesAsync(Owner, sid, 0, 1, default);
        Assert.True(history.More); Assert.Equal(1, history.Next);
        var rest = await service.MessagesAsync(Owner, sid, history.Next, 1, default);
        Assert.False(rest.More); Assert.Equal(2, rest.Items.Single().Number("sequence"));
        var jobs = await repo.QueryAsync(RecordPartition.Conversation("demo", sid), "job", null, null, 100, null, default);
        Assert.Equal(2, jobs.Items.Length);
        Assert.All(jobs.Items, x => { Assert.Equal("messageCommitted", x.Value.Text("jobType")); Assert.NotNull(x.Value["event"]); });
    }
    [Fact]
    public async Task Delimiters_in_client_ids_cannot_alias_a_different_request()
    {
        var (_, service, sid) = await Setup(); var session = await service.SessionAsync(Owner, sid, default);
        await service.SendAsync(Owner, sid, "send-message-0001", session.ETag, new("a:b", "c"), default);
        await Denied(() => service.SendAsync(Owner, sid, "send-message-0001", session.ETag, new("a", "b:c"), default), 409);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_failure_or_crash_after_commit_can_retry_same_message_without_duplicates(bool afterCommit)
    {
        var (repo, service, sid) = await Setup();
        var before = await service.SessionAsync(Owner, sid, default);
        repo.FailKind = "message"; repo.AfterCommit = afterCommit;
        var input = new MessageCreate("client-message-0001", "未保存または結果不明");
        var error = await Assert.ThrowsAsync<DomainException>(() => service.SendAsync(Owner, sid, "send-message-0001", before.ETag, input, default));
        Assert.Equal("SAVE_FAILED", error.Code);
        repo.FailKind = null;
        // New service instance represents losing all in-memory connections and locks before notification.
        var restarted = Service(repo);
        var message = await restarted.SendAsync(Owner, sid, "send-message-0001", before.ETag, input, default);
        Assert.Equal(1, message.Number("sequence"));
        Assert.Single((await restarted.MessagesAsync(Owner, sid, 0, 20, default)).Items);
        Assert.Equal(1, (await restarted.SessionAsync(Owner, sid, default)).Value.Number("lastSequence"));
    }
    [Fact]
    public async Task Initialization_recovers_between_catalog_and_conversations_without_resetting_history()
    {
        var store = new MemoryRecordStore(); var repo = new FaultStore(store) { FailKind = "session" }; var service = Service(repo);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(Owner, "create-challenge-0001", new("架空", "相談"), default));
        repo.FailKind = null;
        var resumed = Service(repo);
        var challenge = (await resumed.ChallengesAsync(Owner, 20, null, default)).Items.Single();
        var sid = challenge.Value.Text("sessionId");
        var session = await resumed.SessionAsync(Owner, sid, default);
        await resumed.SendAsync(Owner, sid, "send-message-0001", session.ETag, new("message-0001", "復旧後"), default);
        await resumed.CreateAsync(Owner, "create-challenge-0001", new("架空", "相談"), default);
        Assert.Single((await resumed.MessagesAsync(Owner, sid, 0, 20, default)).Items);
    }
    [Fact]
    public async Task AI_stopped_and_closed_session_behaviors_are_independent()
    {
        var (_, service, sid) = await Setup(); await Accept(service, sid);
        var session = await service.SessionAsync(Owner, sid, default);
        Assert.Equal("stopped", session.Value.Text("aiAssistance"));
        await service.SendAsync(Owner, sid, "send-message-0001", session.ETag, new("message-0001", "AIなし"), default);
        session = await service.SessionAsync(Owner, sid, default);
        await Denied(() => service.UpdateAsync(Guest, sid, "close-session-001", session.ETag, new("stopped", "closed"), default), 403);
        await service.UpdateAsync(Owner, sid, "close-session-001", session.ETag, new("stopped", "closed"), default);
        session = await service.SessionAsync(Owner, sid, default);
        await Denied(() => service.SendAsync(Guest, sid, "send-message-0002", session.ETag, new("message-0002", "終了後"), default), 409);
        Assert.Single((await service.MessagesAsync(Guest, sid, 0, 20, default)).Items);
    }
    [Fact]
    public async Task Local_file_adapter_retains_history_across_store_instances()
    {
        var root = Path.Combine(Path.GetTempPath(), "dse-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new PrimaryRepository(new LocalRecordStore(new LocalIngestionFiles(root)));
            var service = Service(repository);
            var challenge = await service.CreateAsync(Owner, "create-challenge-0001", new("架空", "永続化"), default);
            var sid = challenge.Text("sessionId"); var session = await service.SessionAsync(Owner, sid, default);
            await service.SendAsync(Owner, sid, "send-message-0001", session.ETag, new("message-0001", "再起動"), default);
            var restarted = Service(new PrimaryRepository(new LocalRecordStore(new LocalIngestionFiles(root))));
            Assert.Equal("再起動", (await restarted.MessagesAsync(Owner, sid, 0, 20, default)).Items.Single().Text("body"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Export_conversation_records_for_shared_schema_validation()
    {
        var (repo, service, sid) = await Setup(); await Accept(service, sid);
        var session = await service.SessionAsync(Owner, sid, default);
        await service.SendAsync(Owner, sid, "send-message-0001", session.ETag, new("message-0001", "契約確認"), default);
        List<object> rows = [];
        foreach (var (partition, kinds) in new[] {
            (RecordPartition.Catalog("demo"), new[] { "challenge", "job" }),
            (RecordPartition.Conversation("demo", sid), new[] { "session", "participation", "message", "job" }) })
        foreach (var kind in kinds)
        {
            var page = await repo.QueryAsync(partition, kind, null, null, 100, null, default);
            rows.AddRange(page.Items.Select(x => new { schema = RecordJson.ContractKinds[kind], value = x.Value }));
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DseGrowthLoop.slnx"))) root = root.Parent;
        var output = Path.Combine(root!.FullName, "tests/Dse.Tests/TestResults"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "conversation-contract-fixture.json"), System.Text.Json.JsonSerializer.Serialize(rows));
    }
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    { b.UseEnvironment("Development"); b.UseSetting("Data:Mode", "Demo"); });
    private static async Task<JsonObject> Json(HttpResponseMessage response)
    { response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonObject>())!; }
    private static Task<HttpResponseMessage> Write(HttpClient client, string path, object body, string? etag = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }
    private static async Task<string> Hub(HttpClient client, string sid)
    {
        var negotiation = await Json(await client.PostAsync("/api/hubs/conversations/negotiate?negotiateVersion=1", null));
        var path = "/api/hubs/conversations?id=" + Uri.EscapeDataString(negotiation.Text("connectionToken"));
        (await client.GetAsync(path)).EnsureSuccessStatusCode();
        (await client.PostAsync(path, new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e", Encoding.UTF8, "text/plain"))).EnsureSuccessStatusCode();
        await client.GetAsync(path);
        var invocation = new JsonObject { ["type"] = 1, ["invocationId"] = "1", ["target"] = "Join", ["arguments"] = new JsonArray(sid) };
        (await client.PostAsync(path, new StringContent(invocation.ToJsonString() + "\u001e", Encoding.UTF8, "text/plain"))).EnsureSuccessStatusCode();
        var result = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        await client.DeleteAsync(path);
        return result;
    }
    [Fact]
    public async Task Hub_transport_requests_do_not_exhaust_business_write_budget()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        for (var i = 0; i < 31; i++)
            (await client.PostAsync("/api/hubs/conversations/negotiate?negotiateVersion=1", null)).EnsureSuccessStatusCode();
        (await Write(client, "/api/v1/challenges", new { title = "架空", summary = "接続維持後も登録可能" })).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Api_and_Hub_reject_before_acceptance_and_after_leave_and_bind_pagination_to_actor()
    {
        using var factory = Factory(); using var owner = factory.CreateClient(); using var guest = factory.CreateClient(); using var other = factory.CreateClient();
        guest.DefaultRequestHeaders.Add("X-Demo-Person", Guest.PersonId); other.DefaultRequestHeaders.Add("X-Demo-Person", Other.PersonId);
        var challenge = await Json(await Write(owner, "/api/v1/challenges", new { title = "架空", summary = "API会話" }));
        var sid = challenge.Text("sessionId"); var path = "/api/v1/sessions/" + sid;
        var session = await Json(await owner.GetAsync(path));
        await Json(await Write(owner, path + "/participations", new { personId = Guest.PersonId }, session.Text("etag")));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path + "/messages")).StatusCode);
        Assert.Contains("NOT_FOUND", await Hub(guest, sid));
        Assert.Contains("NOT_FOUND", await Hub(other, sid));
        var invitations = await Json(await guest.GetAsync(path + "/participations"));
        var etag = invitations["items"]![0]!["etag"]!.GetValue<string>();
        await Json(await Write(guest, path + "/participation", new { decision = "accepted" }, etag, HttpMethod.Patch));
        Assert.DoesNotContain("error", await Hub(guest, sid));
        for (var i = 0; i < 2; i++)
        {
            session = await Json(await owner.GetAsync(path));
            await Json(await Write(owner, path + "/messages", new { clientMessageId = "message-" + i, body = "保存" }, session.Text("etag")));
        }
        var page = await Json(await owner.GetAsync(path + "/messages?pageSize=1"));
        var cursor = Uri.EscapeDataString(page.Text("continuationToken"));
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.GetAsync(path + "/messages?pageSize=1&continuationToken=" + cursor)).StatusCode);
        invitations = await Json(await guest.GetAsync(path + "/participations")); etag = invitations["items"]![0]!["etag"]!.GetValue<string>();
        await Json(await Write(guest, path + "/participation", new { decision = "left" }, etag, HttpMethod.Patch));
        Assert.Contains("NOT_FOUND", await Hub(guest, sid));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/hubs/conversations?access_token=dummy")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(owner, "/api/v1/challenges", new { title = "forged", summary = "test", tenantId = "another" })).StatusCode);
    }
}
