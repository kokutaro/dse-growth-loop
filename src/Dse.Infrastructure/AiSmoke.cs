using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public static class AiSmoke
{
    public static void Validate(IConfiguration config)
    {
        var limits = AiLimits.Read(config);
        if (config["Data:Mode"] != "Azure" || config["Ai:Mode"] != "Live" || limits.Stopped || !limits.LiveOptIn ||
            !decimal.TryParse(config["APPROVED_MONTHLY_BUDGET_JPY"], out var approved) || approved <= 0 ||
            config["AiSmoke:Approved"] != "true" || config["AiSmoke:DummyOnly"] != "true" ||
            string.IsNullOrWhiteSpace(config["AiSmoke:OperatorPersonId"]) || string.IsNullOrWhiteSpace(config["AiSmoke:ApprovalReference"]) ||
            config["AiSmoke:Count"] != "2" || limits.Attempts != 1 || limits.InputTokens > 8000 || limits.OutputTokens > 1000 ||
            config["AiSmoke:MaxInputTokens"] != limits.InputTokens.ToString() || config["AiSmoke:MaxOutputTokens"] != limits.OutputTokens.ToString())
            throw new InvalidOperationException("AI smoke requires Azure, explicit approval/operator/dummy-only/count=2/token limits and MaxAttempts=1");
        _ = AiModel.Read(config, "Jev", limits.OutputTokens);
        _ = AiModel.Read(config, "Foundry", limits.OutputTokens);
    }

    public static async Task<bool> RunAsync(IConfiguration config, IdentityResolver identities, IPrimaryRepository repository,
        IDecisionProvider decisions, ITextStructurer structurer, CancellationToken ct)
    {
        Validate(config);
        var tenant = config["Auth:TenantId"]!;
        var actor = identities.ResolvePerson(tenant, config["AiSmoke:OperatorPersonId"]!) ?? throw new DomainException(403, "FORBIDDEN");
        var id = "ai-smoke-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var job = RecordJson.New("job", id, tenant, actor.PrincipalId, actor.ScopeIds, now);
        job["container"] = "catalog"; job["sessionId"] = null; job["jobType"] = "projection";
        job["aggregateId"] = actor.PersonId; job["aggregateVersion"] = 1; job["payloadVersion"] = "1.0";
        job["dedupKey"] = id; job["status"] = "processing"; job["attempt"] = 1; job["availableAt"] = now;
        job["leaseUntil"] = null; job["notificationState"] = "sent"; job["lastErrorCode"] = null; job["supersedesJobId"] = null; job["resultId"] = null;
        var audit = RecordJson.New("audit", id + "-approval", tenant, actor.PrincipalId, ["system"], now);
        audit["approvalReference"] = config["AiSmoke:ApprovalReference"]; audit["operatorPersonId"] = actor.PersonId;
        audit["count"] = 2; audit["maxInputTokens"] = AiLimits.Read(config).InputTokens; audit["maxOutputTokens"] = AiLimits.Read(config).OutputTokens;
        await repository.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Create(job), RecordWrite.Create(audit)], ct);
        var context = new AiContext(actor, id, "smoke-v1", "taxonomy-v1", 1, [], []);
        var jev = await decisions.DecideAsync(context, "A fictional customer requests a dummy invoice.",
            [new("department", "Select the department.", new Dictionary<string, string> { ["billing"] = "Invoice", ["technical"] = "Technical support" })], [], ct);
        var foundry = await structurer.StructureAsync(context, "Fictional project: a dummy inventory report. Extract one fact; no evidence IDs are supplied.", ct);
        var ok = jev.Status == AiStatus.Succeeded && foundry.Status == AiStatus.Succeeded;
        var stored = (await repository.ReadAsync(RecordPartition.Catalog(tenant), id, ct))!;
        job["status"] = ok ? "ready" : "failed"; job["resultId"] = foundry.RunId; job["lastErrorCode"] = ok ? null : "AI_FAILED";
        await repository.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Replace(job, stored.ETag)], ct);
        Console.WriteLine($"Jev {jev.Status} {jev.Failure} run={jev.RunId}; Foundry {foundry.Status} {foundry.Failure} run={foundry.RunId}");
        return ok;
    }
}
