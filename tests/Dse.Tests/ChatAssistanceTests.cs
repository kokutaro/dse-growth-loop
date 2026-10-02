using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class ChatAssistanceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fake : IDecisionProvider, IReranker
    {
        public int Decisions, Rankings;
        public bool Useful = true, Unknown;
        public AiFailure? Failure;
        public Func<Task>? BeforeRank;
        public SearchUnit[] Inputs = [];
        public Task<AiResult<Decisions>> DecideAsync(AiContext c, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct)
        {
            Decisions++;
            return Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded,
                new(choices.ToDictionary(q => q.Id, _ => new ChoiceAnswer(Unknown ? "unknown" : "software", new Dictionary<string, double> { ["software"] = .9, ["unknown"] = .1 }, .9)),
                    nouls.ToDictionary(q => q.Id, q => new NoulAnswer(NoulState.Supported, q.Id == "assistance" && !Useful ? .01 : .9))), "fake-decide", null));
        }
        public async Task<AiResult<Ranking>> RerankAsync(AiContext c, string text, CancellationToken ct)
        {
            Rankings++; Inputs = c.Candidates;
            if (BeforeRank is not null) await BeforeRank();
            return new(Failure is null ? AiStatus.Succeeded : AiStatus.Failed,
                Failure is null ? new(c.Candidates.Select(u => new RankedCandidate(u.Id, EvidenceBudget.Trim(u.SourceRefs, c.EvidenceBytesPerCandidate, u.Id, [])[0].Text, [u.SourceRefs[0].Id])).ToArray()) : null, "fake-rank", Failure);
        }
    }
    private sealed record Rig(MemoryRecordStore Store, IPrimaryRepository Repo, ConversationService Chat, ChatAssistanceService Assist, Fake Ai, Clock Time, string Sid, IConfiguration Config);
    private static async Task<Rig> Setup(Dictionary<string, string?>? settings = null, Func<MemoryRecordStore, IPrimaryRepository>? wrap = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false"
        }).AddInMemoryCollection(settings ?? []).Build();
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var repo = wrap?.Invoke(store) ?? new PrimaryRepository(store); var clock = new Clock(); var ai = new Fake();
        var identities = new IdentityResolver(config); var fence = new AccessFence(repo); var guard = new SourceGuard(repo);
        var chat = new ConversationService(repo, new(), identities, clock, fence);
        var sid = (await chat.CreateAsync(IdentityResolver.Demo, "create-challenge-0001", new("架空課題", "引き継ぎ"), default)).Text("sessionId");
        var session = await chat.SessionAsync(IdentityResolver.Demo, sid, default);
        await chat.UpdateAsync(IdentityResolver.Demo, sid, "enable-assistance-0001", session.ETag, new("enabled", "open"), default);
        var search = new NaturalSearchService(new MemoryProjectionRepository(store), repo, guard, fence, ai, ai, config, clock);
        return new(store, repo, chat, new(repo, search, guard, fence, ai, identities, config, clock), ai, clock, sid, config);
    }
    private static async Task<MessageCommitted> Send(Rig r, string body = "引き継ぎの改善で困っています。事例を教えてください。")
    {
        var s = await r.Chat.SessionAsync(IdentityResolver.Demo, r.Sid, default); var id = Guid.NewGuid().ToString("N");
        var m = await r.Chat.SendAsync(IdentityResolver.Demo, r.Sid, id, s.ETag, new(id, body), default);
        var job = (await r.Repo.ReadAsync(RecordPartition.Conversation("demo", r.Sid), $"{r.Sid}:committed:{m.Number("sequence"):D10}", default))!;
        return job.Value["event"]!.Deserialize<MessageCommitted>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    private static async Task<JsonObject[]> History(Rig r) => (await r.Chat.MessagesAsync(IdentityResolver.Demo, r.Sid, 0, 100, default)).Items;
    [Fact]
    public async Task Debounce_duplicate_events_restart_and_replay_save_one_grounded_case_without_AI_loop()
    {
        var r = await Setup(); var e = await Send(r);
        Assert.Equal("deferred", await r.Assist.ProcessAsync(e, default)); Assert.Equal(0, r.Ai.Decisions);
        r.Time.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("posted", await r.Assist.ProcessAsync(e, default));
        for (int i = 0; i < 10; i++) await r.Assist.ProcessAsync(e, default);
        var m = (await History(r)).Single(x => x.Text("authorType") == "ai");
        Assert.Equal("ai_case", m.Text("messageKind")); Assert.NotEmpty(m["sourceSnapshots"]!.AsArray()); Assert.NotNull(m["citations"]);
        Assert.Equal(1, r.Ai.Rankings); Assert.All(r.Ai.Inputs, x => Assert.Equal("knowledge", x.EntityType));
        Assert.Single((await r.Repo.QueryAsync(RecordPartition.Conversation("demo", r.Sid), "job", null, null, 100, null, default)).Items);
        await r.Assist.ProcessAsync(e with { EventId = "forged-ai-event", MessageId = m.Text("id"), Sequence = m.Number("sequence") }, default);
        Assert.Equal(1, r.Ai.Rankings);
    }
    [Fact]
    public async Task Rapid_messages_coalesce_and_same_case_and_topic_are_suppressed()
    {
        var r = await Setup(); var first = await Send(r); var last = await Send(r);
        r.Time.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("skipped", await r.Assist.ProcessAsync(first, default));
        Assert.Equal("posted", await r.Assist.ProcessAsync(last, default));
        var again = await Send(r); r.Time.Now += TimeSpan.FromMinutes(5);
        Assert.Equal("skipped", await r.Assist.ProcessAsync(again, default));
        Assert.Single(await History(r), x => x.Text("authorType") == "ai");
    }
    [Fact]
    public async Task Twenty_unnecessary_conversations_skip_search_and_LLM()
    {
        string[] examples = ["おはよう", "ありがとう", "了解です", "承知しました", "また明日", "お疲れ様", "こんにちは", "よろしく", "休憩します", "今日は晴れ", "ランチに行きます", "戻りました", "確認しました", "メール送りました", "会議は三時", "はい", "いいえ", "予定通りです", "助かりました", "こんばんは"];
        foreach (var text in examples)
        {
            var r = await Setup(); var e = await Send(r, text); r.Time.Now += TimeSpan.FromSeconds(6);
            Assert.Equal("skipped", await r.Assist.ProcessAsync(e, default)); Assert.Equal(0, r.Ai.Decisions); Assert.Equal(0, r.Ai.Rankings);
        }
    }
    [Theory]
    [InlineData(AiFailure.Stopped)] [InlineData(AiFailure.Budget)] [InlineData(AiFailure.Unavailable)]
    public async Task Failed_reranking_never_posts_or_blocks_human_saves(AiFailure failure)
    {
        var r = await Setup(); r.Ai.Failure = failure; var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("skipped", await r.Assist.ProcessAsync(e, default)); await Send(r, "会話は続けられます");
        Assert.Equal(2, (await History(r)).Length);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Unknown_or_unnecessary_decision_skips_search_and_reranking(bool unknown)
    {
        var r = await Setup(); r.Ai.Unknown = unknown; r.Ai.Useful = unknown; var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6);
        Assert.Equal("skipped", await r.Assist.ProcessAsync(e, default)); Assert.Equal(0, r.Ai.Rankings);
    }
    [Fact]
    public async Task Withdrawal_before_input_or_before_commit_prevents_publication()
    {
        foreach (var before in new[] { true, false })
        {
            var r = await Setup(); var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6);
            async Task Withdraw() {
                var unit = DemoData.Create("demo").Single(x => x.EntityType == "knowledge");
                var row = (await r.Repo.ReadAsync(RecordPartition.Catalog("demo"), unit.EntityId, default))!;
                row.Value["status"] = "withdrawn";
                await r.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(row.Value, row.ETag)], default);
            }
            if (before) await Withdraw(); else r.Ai.BeforeRank = Withdraw;
            Assert.Equal("skipped", await r.Assist.ProcessAsync(e, default));
            if (before) Assert.Equal(0, r.Ai.Rankings);
            Assert.Single(await History(r));
        }
    }
    [Fact]
    public async Task Session_limit_daily_limit_cooldown_and_stopped_settings_skip_posting()
    {
        foreach (var settings in new Dictionary<string, string?>[] {
            new() { ["AiUsage:Stopped"] = "true" }, new() { ["ChatAssistance:EventLimit"] = "0" }, new() { ["ChatAssistance:SessionLimit"] = "1", ["ChatAssistance:CooldownSeconds"] = "0" },
            new() { ["ChatAssistance:DailyLimit"] = "1", ["ChatAssistance:CooldownSeconds"] = "0" }, new() { ["ChatAssistance:CooldownSeconds"] = "86400" } })
        {
            var r = await Setup(settings); var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6);
            var first = await r.Assist.ProcessAsync(e, default);
            if (settings.ContainsKey("AiUsage:Stopped") || settings.ContainsKey("ChatAssistance:EventLimit")) { Assert.Equal("skipped", first); Assert.Equal(0, r.Ai.Decisions); continue; }
            Assert.Equal("posted", first);
            var next = await Send(r, "別の課題の対策で困っています。"); r.Time.Now += TimeSpan.FromSeconds(6);
            Assert.Equal("skipped", await r.Assist.ProcessAsync(next, default));
            Assert.Single(await History(r), m => m.Text("authorType") == "ai");
        }
    }
    [Fact]
    public async Task Participant_change_before_commit_drops_stale_result_and_unresolved_identity_blocks_input()
    {
        var r = await Setup(); var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6);
        r.Ai.BeforeRank = async () => {
            var row = (await r.Repo.ReadAsync(RecordPartition.Conversation("demo", r.Sid), r.Sid, default))!;
            row.Value["membershipVersion"] = row.Value.Number("membershipVersion") + 1;
            row.Value["participantIds"] = RecordJson.Array(["demo-person-01", "unresolved-person"]);
            await r.Repo.CommitAsync(RecordPartition.Conversation("demo", r.Sid), [RecordWrite.Replace(row.Value, row.ETag)], default);
        };
        Assert.Equal("skipped", await r.Assist.ProcessAsync(e, default)); Assert.Single(await History(r));
        var next = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6); var calls = r.Ai.Decisions;
        Assert.Equal("skipped", await r.Assist.ProcessAsync(next, default)); Assert.Equal(calls, r.Ai.Decisions);
    }
    [Fact]
    public async Task Sources_and_feedback_are_member_only_and_revoke_historical_citations()
    {
        var r = await Setup(); var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6); await r.Assist.ProcessAsync(e, default);
        var m = (await History(r)).Single(x => x.Text("authorType") == "ai");
        var source = await r.Chat.CaseSourcesAsync(IdentityResolver.Demo, r.Sid, m.Text("id"), default);
        Assert.Equal(1, source.Number("activeVersion")); Assert.NotEmpty(source["citations"]!.AsArray());
        await Assert.ThrowsAsync<DomainException>(() => r.Chat.CaseSourcesAsync(IdentityResolver.DemoPerson("demo-person-02")!, r.Sid, m.Text("id"), default));
        foreach (var decision in new[] { "helpful", "unnecessary" })
        {
            var session = await r.Chat.SessionAsync(IdentityResolver.Demo, r.Sid, default);
            await r.Chat.FeedbackAsync(IdentityResolver.Demo, r.Sid, m.Text("id"), Guid.NewGuid().ToString("N"), session.ETag, new(decision), default);
        }
        var feedback = (await r.Repo.QueryAsync(RecordPartition.Conversation("demo", r.Sid), "feedback", null, null, 100, null, default)).Items.Single();
        Assert.Equal("unnecessary", feedback.Value.Text("decision"));
        List<object> records = [];
        foreach (var kind in new[] { "message", "feedback", "session", "job" })
            records.AddRange((await r.Repo.QueryAsync(RecordPartition.Conversation("demo", r.Sid), kind, null, null, 100, null, default)).Items.Select(x => new { schema = RecordJson.ContractKinds[kind], value = x.Value }));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DseGrowthLoop.slnx"))) dir = dir.Parent;
        await File.WriteAllTextAsync(Path.Combine(dir!.FullName, "tests/Dse.Tests/TestResults/chat-assistance-contract.json"), JsonSerializer.Serialize(records));
        var row = (await r.Repo.ReadAsync(RecordPartition.Catalog("demo"), m.Text("caseId"), default))!;
        row.Value["status"] = "withdrawn"; await r.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(row.Value, row.ETag)], default);
        await Assert.ThrowsAsync<DomainException>(() => r.Chat.CaseSourcesAsync(IdentityResolver.Demo, r.Sid, m.Text("id"), default));
        var redacted = (await History(r)).Single(x => x.Text("authorType") == "ai");
        Assert.Equal("revoked", redacted.Text("citationState")); Assert.Empty(redacted["citations"]!.AsArray()); Assert.Null(redacted["retrievalReason"]);
    }
    [Theory]
    [InlineData("DebounceSeconds", "61")] [InlineData("EventLimit", "2")] [InlineData("SessionLimit", "0")]
    [InlineData("DailyLimit", "0")] [InlineData("Attempts", "4")] [InlineData("UsefulThreshold", "NaN")]
    public async Task Invalid_rules_fail_before_model_calls(string name, string value)
    {
        var r = await Setup(new() { ["ChatAssistance:" + name] = value }); var e = await Send(r);
        await Assert.ThrowsAsync<ArgumentException>(() => r.Assist.ProcessAsync(e, default)); Assert.Equal(0, r.Ai.Decisions);
    }

    private sealed class CrashRepository(MemoryRecordStore inner) : IPrimaryRepository
    {
        public bool Fail, AfterCommit;
        public Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct) => inner.ReadAsync(p, id, ct);
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? cursor, CancellationToken ct) => inner.QueryAsync(p, kind, field, parent, size, cursor, ct);
        public async Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            var fail = Fail && writes.Any(w => w.Value.Text("messageKind") == "ai_case");
            if (fail && !AfterCommit) throw new IOException("simulated crash before DB save");
            await inner.CommitAsync(p, writes, ct);
            if (fail) throw new IOException("simulated crash after DB save");
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AI_success_before_DB_crash_and_lost_ack_recover_without_duplicate_messages(bool afterCommit)
    {
        CrashRepository? crash = null;
        var r = await Setup(new() { ["ChatAssistance:DailyLimit"] = "1" }, wrap: store => crash = new(store)); var e = await Send(r); r.Time.Now += TimeSpan.FromSeconds(6);
        crash!.Fail = true; crash.AfterCommit = afterCommit;
        await Assert.ThrowsAsync<IOException>(() => r.Assist.ProcessAsync(e, default));
        crash.Fail = false;
        // Simulate stopped processes, then the documented offline fence recovery and a fresh consumer.
        var held = (await r.Repo.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default))!;
        Assert.NotEmpty(held.Value.Text("holder"));
        await new FenceRecovery(r.Store).RecoverAsync("demo", held.ETag, [r.Sid], default);
        r.Time.Now += TimeSpan.FromMinutes(5);
        var search = new NaturalSearchService(new MemoryProjectionRepository(r.Store), r.Repo, new(r.Repo), new(r.Repo), r.Ai, r.Ai, r.Config, r.Time);
        var restarted = new ChatAssistanceService(r.Repo, search, new(r.Repo), new(r.Repo), r.Ai, new(r.Config), r.Config, r.Time);
        for (int i = 0; i < 10; i++) await restarted.ProcessAsync(e, default);
        Assert.Single(await History(r), m => m.Text("authorType") == "ai");
        Assert.Equal(2, (await r.Chat.SessionAsync(IdentityResolver.Demo, r.Sid, default)).Value.Number("lastSequence"));
        Assert.Equal(afterCommit ? 1 : 2, r.Ai.Rankings);
    }

    [Fact]
    public async Task Queue_duplicates_and_notification_failure_reconcile_from_durable_conversation_jobs()
    {
        var r = await Setup(); var e = await Send(r); var dir = Path.Combine(Path.GetTempPath(), "dse-chat-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new LocalIngestionFiles(dir); var queue = new LocalIngestionQueue(files, r.Time);
            var notifications = new JobNotifications(r.Repo, queue, r.Time);
            // No original Queue notification (a crash after the human commit). Explicit reconcile locates the outbox.
            Assert.Equal(1, await notifications.ReconcileAsync("demo", default));
            for (int i = 0; i < 10; i++) await queue.SendAsync(new("demo", e.EventId, 1, r.Sid), default);
            var options = IngestionOptions.Read(r.Config);
            var processor = new IngestionProcessor(r.Repo, new LocalIngestionBlobs(files), queue, new OfficeExtractor(options), options,
                new(r.Repo), r.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestionProcessor>.Instance, assistance: r.Assist);
            Assert.True(await processor.RunOnceAsync(default)); // debounce renews visibility, not a terminal delete
            r.Time.Now += TimeSpan.FromSeconds(6);
            for (int i = 0; i < 11; i++) Assert.True(await processor.RunOnceAsync(default));
            Assert.False(await processor.RunOnceAsync(default)); Assert.Equal(0, await notifications.ReconcileAsync("demo", default));
            Assert.Single(await History(r), m => m.Text("authorType") == "ai"); Assert.Equal(1, r.Ai.Rankings);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

}
