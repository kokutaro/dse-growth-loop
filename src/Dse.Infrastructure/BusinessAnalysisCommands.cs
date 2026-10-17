using System.Text.RegularExpressions;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Console preflight: no repository, credential, transport or remote stop operation is resolved here.
public static class BusinessAnalysisCommands
{
    public static ActorAccess RecoveryActor(IConfiguration config, bool development, string tenant)
    {
        var person = config["Recovery:OperatorPersonId"];
        if (!RecordJson.IsId(person ?? "")) throw new ArgumentException("Recovery operator is required");
        ActorAccess? actor;
        if (config["Data:Mode"] == "Demo")
        {
            if (!development || config["Recovery:LocalOperatorConfirmed"] != "true" || tenant != "demo" ||
                string.IsNullOrWhiteSpace(config["Data:LocalPath"]))
                throw new DomainException(403, "LOCAL_RECOVERY_NOT_CONFIRMED");
            // Preflight identity selector only. Worker must resolve its current canonical authority.
            if (person == "dummy-operator") return new("demo","principal-dummy-operator","dummy-operator",["operator","analyst"],["demo"]);
            // Never used by the HTTP Demo identity.
            actor = IdentityResolver.DemoPerson(person!) is { } local ? local with { Roles = ["operator", "analyst"] } : null;
        }
        else actor = new IdentityResolver(config).ResolvePerson(tenant, person!);
        if (actor is null || !actor.HasRole("operator") || !actor.HasRole("analyst")) throw new DomainException(403, "FORBIDDEN");
        return actor;
    }
    public static void ValidateRecovery(IConfiguration config, bool development, bool apply)
    {
        if (config["Data:Mode"] is not ("Demo" or "Azure") || config["AiUsage:Stopped"] != "true" || config["AiUsage:LiveOptIn"] != "false")
            throw new DomainException(409, "RECOVERY_REQUIRES_AI_STOP");
        var tenant = config["Auth:TenantId"] ?? (config["Data:Mode"] == "Demo" ? "demo" : "");
        _ = RecoveryActor(config, development, tenant);
        if (!RecordJson.IsId(config["Recovery:OwnerPrincipalId"] ?? "") || !RecordJson.IsId(config["Recovery:RunId"] ?? ""))
            throw new ArgumentException("Explicit recovery owner and run are required");
        if (!apply) return;
        var request = RecoveryRequest(config);
        if (!request.ApiStopped || !request.WorkerStopped || !request.ProvidersStopped ||
            !RecordJson.IsId(request.ApprovalReference) || !RecordJson.IsId(request.StopEvidenceReference) ||
            !Regex.IsMatch(request.StopEvidenceSha256, "\\A[a-fA-F0-9]{64}\\z") || request.PendingReservationIds.Length > 64 ||
            request.PendingReservationIds.Distinct().Count() != request.PendingReservationIds.Length ||
            request.PendingReservationIds.Any(id => !RecordJson.IsId(id)))
            throw new ArgumentException("Reviewed stop evidence and reservation list are required");
        IdempotencyExecutor.ValidateHeaders("recovery-preflight", request.RunETag, true);
        IdempotencyExecutor.ValidateHeaders("recovery-preflight", request.SlotETag, true);
    }
    public static BusinessAnalysisRecoveryRequest RecoveryRequest(IConfiguration c) => new(
        c["Recovery:RunId"] ?? "", c["Recovery:OwnerPrincipalId"] ?? "", c["Recovery:RunETag"] ?? "", c["Recovery:SlotETag"] ?? "",
        c["Recovery:ApprovalReference"] ?? "", c["Recovery:ApiStopped"] == "true", c["Recovery:WorkerStopped"] == "true", c["Recovery:ProvidersStopped"] == "true",
        c["Recovery:StopEvidenceReference"] ?? "", c["Recovery:StopEvidenceSha256"] ?? "", c.GetSection("Recovery:PendingReservationIds").Get<string[]>() ?? []);
}
