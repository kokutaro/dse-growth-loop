using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Explicit, bounded live validation. Never seeds, edits fixtures, or exports identities/prompts.
public static class ChatSmoke
{
    public static readonly string[] Unnecessary =
    [
        "おはようございます。今日はよろしくお願いします。", "ありがとうございます。助かりました。",
        "昼食は何にしましょうか。", "本日の会議は15時からです。",
        "資料を受け取りました。", "今日はよい天気ですね。",
        "引き継ぎの課題は解決済みです。追加の支援や事例は不要です。",
        "担当変更の対策は実施済みで、確認漏れもなくなりました。相談はありません。",
        "改善報告を提出しました。これは完了の連絡だけです。",
        "引き継ぎ会議の予定は明日10時です。事例の提示は不要です。",
        "困っていた件は自分で解決しました。追加提案は不要です。",
        "事例資料のファイル名はhandover.pdfです。受領の連絡だけです。",
        "相談の予約をキャンセルしました。相談内容はありません。",
        "課題という単語の文字数は2文字です。業務相談ではありません。",
        "helpという文字列のテストです。業務の問題はありません。",
        "引き継ぎチェックリストを読み終えました。疑問や困り事はありません。",
        "解決済みの案件を閉じました。事例は必要ありません。",
        "改善会議は中止です。これは予定変更のお知らせだけです。",
        "対策済みで良好に運用できています。新たな課題はありません。",
        "『どうすればよいか』は例文です。実際の相談ではなく、支援不要です。"
    ];
    public const string Useful = "営業案件の担当変更時に引き継ぎの確認漏れが起きて困っています。窓口と未完了事項を明確にする改善事例を教えてください。";

    public static void Validate(IConfiguration c)
    {
        var l = AiLimits.Read(c);
        if (c["Data:Mode"] != "Azure" || c["Ai:Mode"] != "Live" || l.Stopped || !l.LiveOptIn ||
            c["ChatSmoke:Approved"] != "true" || c["ChatSmoke:DummyOnly"] != "true" ||
            c["ChatSmoke:OperatorPersonId"] != "demo-person-01" ||
            !RecordJson.IsId(c["ChatSmoke:RunId"] ?? "") || string.IsNullOrWhiteSpace(c["ChatSmoke:ApprovalReference"]) ||
            !Guid.TryParse(c["AZURE_CLIENT_ID"], out _) || !Guid.TryParse(c["Auth:TenantId"], out _) ||
            c["ChatSmoke:Suite"] is not ("quality" or "queue" or "probe") ||
            !decimal.TryParse(c["APPROVED_MONTHLY_BUDGET_JPY"], out var budget) || budget <= 0 ||
            l.Attempts != 1 || l.Concurrency != 1 || l.InputTokens > 8000 || l.OutputTokens > 3000 ||
            l.DailyCalls is < 1 or > 80 || l.DailyTokens is < 1 or > 1000000 || l.DailyJpy is <= 0 or > 100)
            throw new InvalidOperationException("Chat smoke requires explicit bounded Azure/live/dummy approval");
        _ = AiModel.Read(c, "Jev", l.OutputTokens); _ = AiModel.Read(c, "Foundry", l.OutputTokens);
    }

    public static async Task<bool> RunAsync(IConfiguration c, IdentityResolver identities, IPrimaryRepository repository,
        ConversationService conversations, ChatAssistanceService assistance, IIngestionQueue queue, IProjectionRepository projections,
        IIngestionBlobs blobs, CancellationToken ct)
    {
        Validate(c);
        var tenant = c["Auth:TenantId"]!;
        var actor = identities.ResolvePerson(tenant, "demo-person-01") ?? throw new DomainException(403, "FORBIDDEN");
        var fixture = (await projections.FindAsync(new([], [], ["引き継ぎ"]), actor.Search, ct))
            .SingleOrDefault(x => x.Id == "demo-knowledge-01-projection-1") ?? throw new DomainException(409, "SMOKE_FIXTURE_REQUIRED");
        var validated = await new SourceGuard(repository).RequireAsync(actor, fixture, ct);
        if (!validated.Unit.SourceRefs.SequenceEqual(DemoData.Create(tenant).Single(x => x.EntityId == "demo-knowledge-01").SourceRefs))
            throw new DomainException(409, "SMOKE_FIXTURE_REQUIRED");
        var run = c["ChatSmoke:RunId"]!; var suite = c["ChatSmoke:Suite"]!;
        var credential = new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(c["AZURE_CLIENT_ID"]!));
        var client = new Azure.Storage.Queues.QueueClient(new Uri(c["Storage:QueueEndpoint"]!.TrimEnd('/') + "/ingestion"), credential);
        var queueCount = (await client.GetPropertiesAsync(ct)).Value.ApproximateMessagesCount;
        if (queueCount != 0) throw new DomainException(409, "SMOKE_QUEUE_NOT_EMPTY");
        var audit = RecordJson.New("audit", "chat-smoke-" + run + "-" + suite, tenant, actor.PrincipalId, ["system"], DateTimeOffset.UtcNow);
        audit["approvalReference"] = c["ChatSmoke:ApprovalReference"]; audit["suite"] = suite;
        audit["fixtureVersion"] = "issue13-chat-smoke-v1"; audit["dailyCallLimit"] = AiLimits.Read(c).DailyCalls;
        await repository.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Create(audit)], ct);
        var report = new JsonObject { ["runId"] = run, ["suite"] = suite, ["fixtureVersion"] = "issue13-chat-smoke-v1",
            ["startedAt"] = DateTimeOffset.UtcNow, ["initialQueueMessages"] = queueCount, ["rules"] = JsonSerializer.SerializeToNode(assistance.Rules()), ["cases"] = new JsonArray() };
        var cases = report["cases"]!.AsArray();
        async Task Case(string id, string text, string mode, bool expected)
        {
            var key = run + "-" + suite + "-" + id;
            var challenge = await conversations.CreateAsync(actor, key, new("ダミー評価 " + id, "Issue 13の承認済み架空会話評価"), ct);
            var sid = challenge.Text("sessionId");
            var session = await conversations.SessionAsync(actor, sid, ct);
            await conversations.UpdateAsync(actor, sid, key + "-enable", session.ETag, new("enabled", "open"), ct);
            session = await conversations.SessionAsync(actor, sid, ct);
            var human = await conversations.SendAsync(actor, sid, key + "-send", session.ETag, new(key, text), ct);
            var start = Stopwatch.GetTimestamp(); var p = RecordPartition.Conversation(tenant, sid);
            var jobId = $"{sid}:committed:0000000001";
            var job = (await repository.ReadAsync(p, jobId, ct))!;
            var e = job.Value["event"]!.Deserialize<MessageCommitted>(ProjectionRules.Json)!;
            string outcome;
            if (mode == "direct")
            {
                await Task.Delay(TimeSpan.FromSeconds(assistance.Rules().DebounceSeconds), ct);
                outcome = await assistance.ProcessAsync(e, ct);
            }
            else
            {
                await queue.SendAsync(new(tenant, jobId, 1, sid), ct);
                do
                {
                    await Task.Delay(1000, ct); job = (await repository.ReadAsync(p, jobId, ct))!;
                } while (job.Value.Text("status") is not ("ready" or "failed" or "cancelled") && Stopwatch.GetElapsedTime(start).TotalSeconds < 240);
                outcome = job.Value.Text("resultId") != "" ? "posted" : job.Value.Text("assistanceOutcome");
            }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            job = (await repository.ReadAsync(p, jobId, ct))!;
            var messages = await conversations.MessagesAsync(actor, sid, 0, 100, ct);
            var posts = messages.Items.Where(m => m.Text("authorType") == "ai").ToArray();
            var row = new JsonObject { ["id"] = id, ["mode"] = mode, ["expectedPost"] = expected, ["localGate"] = ChatAssistanceService.MayNeedHelp(text),
                ["outcome"] = outcome, ["postCount"] = posts.Length, ["observedMs"] = elapsed, ["decision"] = job.Value["assistanceDecision"]?.DeepClone(),
                ["humanCommittedAt"] = human["createdAt"]?.DeepClone(), ["aiCommittedAt"] = posts.FirstOrDefault()?["createdAt"]?.DeepClone(), ["runs"] = new JsonArray() };
            if (posts.Length > 0)
            {
                var sources = await conversations.CaseSourcesAsync(actor, sid, posts[0].Text("id"), ct);
                row["sourceValidated"] = sources.ToJsonString().Contains("demo-knowledge-01", StringComparison.Ordinal);
                row["caseId"] = posts[0]["caseId"]?.DeepClone();
            }
            string? cursor = null;
            do
            {
                var page = await repository.QueryAsync(p, "aiRun", null, null, 100, cursor, ct);
                foreach (var ai in page.Items) row["runs"]!.AsArray().Add(AiSmoke.Measurement(ai.Value));
                cursor = page.ContinuationToken;
            } while (cursor is not null);
            cases.Add(row);
            Console.WriteLine("CHAT_SMOKE_CASE " + row.ToJsonString());
            // Incremental immutable artifacts retain evidence if a later scenario fails.
            await blobs.PutAsync("extracted", "validation/issue13/" + run + "/" + suite + "-" + id + ".json", JsonSerializer.SerializeToUtf8Bytes(row), ct);
        }
        // A successful positive control is mandatory: disabled AI must never look like low false positives.
        await Case("control", Useful, suite == "queue" ? "cold" : "direct", true);
        var control = cases[0]!;
        var controlOk = control["postCount"]!.GetValue<int>() == 1 && control["sourceValidated"]?.GetValue<bool>() == true;
        if (controlOk && suite == "quality")
            for (var i = 0; i < Unnecessary.Length; i++) await Case($"negative-{i + 1:D2}", Unnecessary[i], "direct", false);
        if (controlOk && suite == "queue")
            for (var i = 1; i <= 2; i++) await Case("warm-" + i, Useful, "warm", true);
        report["finishedAt"] = DateTimeOffset.UtcNow; report["controlPassed"] = controlOk;
        report["falsePosts"] = cases.Where(x => x!["expectedPost"]!.GetValue<bool>() == false).Sum(x => x!["postCount"]!.GetValue<int>());
        await blobs.PutAsync("extracted", "validation/issue13/" + run + "/" + suite + ".json", JsonSerializer.SerializeToUtf8Bytes(report), ct);
        Console.WriteLine("CHAT_SMOKE_REPORT " + report.ToJsonString());
        return controlOk && cases.Count == (suite == "quality" ? 21 : suite == "queue" ? 3 : 1);
    }
}
