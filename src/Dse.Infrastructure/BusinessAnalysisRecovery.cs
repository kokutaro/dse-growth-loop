using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// No HTTP endpoint. Operator attestations refer to independently reviewed stop evidence; they do not stop or prove a remote process.
public sealed record BusinessAnalysisRecoveryRequest(string RunId, string OwnerPrincipalId, string RunETag, string SlotETag,
    string ApprovalReference, bool ApiStopped, bool WorkerStopped, bool ProvidersStopped,
    string StopEvidenceReference, string StopEvidenceSha256, string[] PendingReservationIds);

public sealed class BusinessAnalysisRecovery(IRecordStore store, IConfiguration config, TimeProvider clock)
{
    private void Role(ActorAccess actor)
    {
        if (!actor.HasRole("operator") || !actor.HasRole("analyst") || (config["Data:Mode"] == "Azure" && actor.TenantId != config["Auth:TenantId"])) throw new DomainException(403, "FORBIDDEN");
    }
    private void Stopped()
    {
        if (config["Data:Mode"] is not ("Demo" or "Azure") || config["AiUsage:Stopped"] != "true" || config["AiUsage:LiveOptIn"] != "false")
            throw new DomainException(409, "RECOVERY_REQUIRES_AI_STOP");
    }
    private static string StorageId(string owner, string runId) => BusinessAnalysisService.StorageId(owner, runId);
    private sealed record PendingLedger(StoredRecord[] Reservations, StoredRecord? Gate);
    private static DomainException Inconsistent() => new(503, "RECOVERY_LEDGER_INCONSISTENT");
    private async Task<PendingLedger> PendingAsync(string tenant, string jobId, bool fixture, CancellationToken ct)
    {
        var p = RecordPartition.Catalog(tenant); Dictionary<string, StoredRecord> aiRuns = []; string? cursor = null; var pages = 0;
        do
        {
            if (++pages > 10) throw new DomainException(422, "RECOVERY_SCAN_LIMIT");
            var page = await store.QueryAsync(p, "aiRun", "jobId", jobId, 100, cursor, ct);
            foreach (var row in page.Items.Where(r => r.Value.Text("operation") == BusinessAnalysisLiveDrafter.PromptVersion))
            {
                if (row.Value.Text("tenantId") != tenant || row.Value.Text("jobId") != jobId || !RecordJson.IsId(row.Value.Text("id")) || !aiRuns.TryAdd(row.Value.Text("id"), row)) throw Inconsistent();
            }
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        // Reserve/settle maintains active and reservation rows atomically. Preserve this exact gate ETag for recovery's batch.
        var gate = await store.ReadAsync(p, "system-ai-gate", ct);
        if (gate is null)
        {
            if (!fixture || aiRuns.Count != 0) throw Inconsistent();
            return new([], null); // Fixture-only runs have no provider reservations.
        }
        if (gate.Value.Text("kind") != "aiUsageGate" || gate.Value.Text("tenantId") != tenant || gate.Value["active"] is not JsonArray) throw Inconsistent();
        string[] active;
        try { active = gate.Value.Strings("active"); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException) { throw Inconsistent(); }
        if (active.Length > 64 || active.Distinct().Count() != active.Length || active.Any(id => !RecordJson.IsId(id))) throw Inconsistent();
        Dictionary<string, StoredRecord> activeRows = []; HashSet<string> ownActive = [];
        foreach (var id in active)
        {
            var reservation = await store.ReadAsync(p, id, ct);
            if (reservation is null || reservation.Value.Text("id") != id || reservation.Value.Text("kind") != "aiReservation" || reservation.Value.Text("tenantId") != tenant || reservation.Value.Text("state") != "pending" || !RecordJson.IsId(reservation.Value.Text("runId"))) throw Inconsistent();
            var run = await store.ReadAsync(p, reservation.Value.Text("runId"), ct);
            if (run is null || run.Value.Text("kind") != "aiRun" || run.Value.Text("tenantId") != tenant) throw Inconsistent();
            if (run.Value.Text("jobId") == jobId && run.Value.Text("operation") == BusinessAnalysisLiveDrafter.PromptVersion)
            {
                if (!aiRuns.ContainsKey(run.Value.Text("id"))) throw Inconsistent();
                ownActive.Add(id);
            }
            activeRows.Add(id, reservation);
        }
        List<StoredRecord> pending = []; var examined = 0;
        foreach (var run in aiRuns.Values)
        {
            // Exact runId queries detect orphaned target pending rows, including rows omitted from gate.active.
            // Tenant-wide reservation history is never paged; one business drafter sends once per group.
            var page = await store.QueryAsync(p, "aiReservation", "runId", run.Value.Text("id"), 100, null, ct);
            if (page.ContinuationToken is not null || (examined += page.Items.Length) > 1000) throw new DomainException(422, "RECOVERY_SCAN_LIMIT");
            if (run.Value["trace"]?["attemptCount"] is not JsonValue attempts || !attempts.TryGetValue<int>(out var count) || count is < 0 or > 1 || (count > 0 && page.Items.Length == 0)) throw Inconsistent();
            foreach (var row in page.Items)
            {
                if (row.Value.Text("kind") != "aiReservation" || row.Value.Text("tenantId") != tenant || row.Value.Text("runId") != run.Value.Text("id") || row.Value.Text("state") is not ("pending" or "settled" or "uncertain")) throw Inconsistent();
                if (row.Value.Text("state") != "pending") continue;
                if (!activeRows.TryGetValue(row.Value.Text("id"), out var activeRow) || activeRow.ETag != row.ETag) throw Inconsistent();
                pending.Add(row);
                if (pending.Count > 64) throw new DomainException(422, "RECOVERY_RESERVATION_LIMIT");
            }
        }
        if (!ownActive.SetEquals(pending.Select(r => r.Value.Text("id")))) throw Inconsistent();
        return new(pending.OrderBy(r => r.Value.Text("id"), StringComparer.Ordinal).ToArray(), gate);
    }
    public async Task<JsonObject> InspectAsync(ActorAccess actor, string ownerPrincipalId, string runId, CancellationToken ct)
    {
        Role(actor); Stopped();
        if (!RecordJson.IsId(ownerPrincipalId) || !RecordJson.IsId(runId)) throw new ArgumentException("Invalid recovery identifier");
        var p = RecordPartition.Catalog(actor.TenantId); var id = StorageId(ownerPrincipalId, runId);
        var run = await store.ReadAsync(p, id, ct) ?? throw new DomainException(404, "NOT_FOUND");
        if (run.Value.Text("jobType") != "businessAnalysis" || run.Value.Text("ownerId") != ownerPrincipalId || !AccessPolicy.InScope(actor, run.Value)) throw new DomainException(404, "NOT_FOUND");
        var slot = await store.ReadAsync(p, "business-analysis-slot", ct) ?? throw new DomainException(404, "NOT_FOUND");
        var ledger = await PendingAsync(actor.TenantId, id, run.Value.Text("processingMode") == "fixture", ct);
        var pending = ledger.Reservations;
        return new JsonObject { ["runId"] = runId, ["runEtag"] = run.ETag, ["slotEtag"] = slot.ETag, ["runStatus"] = run.Value.Text("analysisStatus"),
            ["slotStatus"] = slot.Value.Text("status"), ["slotMatchesRun"] = slot.Value.Text("analysisRunId") == id,
            ["pendingReservationIds"] = RecordJson.Array(pending.Select(r => r.Value.Text("id"))) };
    }
    public async Task<JsonObject> RecoverAsync(ActorAccess actor, BusinessAnalysisRecoveryRequest request, CancellationToken ct)
    {
        Role(actor); Stopped();
        if (!request.ApiStopped || !request.WorkerStopped || !request.ProvidersStopped || !RecordJson.IsId(request.RunId) || !RecordJson.IsId(request.OwnerPrincipalId) ||
            !RecordJson.IsId(request.ApprovalReference) || !RecordJson.IsId(request.StopEvidenceReference) ||
            !Regex.IsMatch(request.StopEvidenceSha256 ?? "", "^[a-fA-F0-9]{64}$") || request.PendingReservationIds is null || request.PendingReservationIds.Length > 64 ||
            request.PendingReservationIds.Distinct().Count() != request.PendingReservationIds.Length || request.PendingReservationIds.Any(x => !RecordJson.IsId(x)))
            throw new ArgumentException("Reviewed stop evidence and explicit approval are required");
        IdempotencyExecutor.ValidateHeaders("recovery-validation", request.RunETag, true);
        IdempotencyExecutor.ValidateHeaders("recovery-validation", request.SlotETag, true);
        var p = RecordPartition.Catalog(actor.TenantId); var id = StorageId(request.OwnerPrincipalId, request.RunId);
        var receiptId = "analysis-recovery-" + ConversationService.Hash(id + ":" + request.ApprovalReference);
        var fingerprint = ConversationService.Hash(RecordJson.Canonical(AiCodecs.Node(request with { PendingReservationIds = request.PendingReservationIds.Order().ToArray() })));
        var prior = await store.ReadAsync(p, receiptId, ct);
        if (prior is not null)
        {
            if (!AccessPolicy.InScope(actor, prior.Value)) throw new DomainException(404, "NOT_FOUND");
            if (prior.Value.Text("fingerprint") != fingerprint) throw new DomainException(409, "RECOVERY_REQUEST_CONFLICT");
            return prior.Value["result"]!.DeepClone().AsObject();
        }
        var run = await store.ReadAsync(p, id, ct) ?? throw new DomainException(404, "NOT_FOUND");
        if (run.Value.Text("jobType") != "businessAnalysis" || run.Value.Text("ownerId") != request.OwnerPrincipalId || !AccessPolicy.InScope(actor, run.Value)) throw new DomainException(404, "NOT_FOUND");
        var slot = await store.ReadAsync(p, "business-analysis-slot", ct) ?? throw new DomainException(404, "NOT_FOUND");
        if (slot.ETag != request.SlotETag || run.ETag != request.RunETag) throw new DomainException(412, "STALE_VERSION");
        if (slot.Value.Text("analysisRunId") != id || slot.Value.Text("status") is not ("processing" or "blocked")) throw new DomainException(409, "RECOVERY_SLOT_MISMATCH");
        var ledger = await PendingAsync(actor.TenantId, id, run.Value.Text("processingMode") == "fixture", ct);
        var pending = ledger.Reservations;
        if (!pending.Select(r => r.Value.Text("id")).ToHashSet().SetEquals(request.PendingReservationIds)) throw new DomainException(409, "RECOVERY_RESERVATION_MISMATCH");
        var gate = ledger.Gate;
        var fence = await store.ReadAsync(p, "system-access-fence", ct);
        if (fence?.Value.Text("holder") is { Length: > 0 }) throw new DomainException(409, "RECOVERY_REQUIRES_FENCE_RECOVERY");
        var result = new JsonObject { ["runId"] = request.RunId, ["status"] = "aborted", ["pendingReservationsMarkedUncertain"] = pending.Length,
            ["refundedJpy"] = 0m, ["approvalReference"] = request.ApprovalReference, ["stopEvidenceReference"] = request.StopEvidenceReference,
            ["stopEvidenceSha256"] = request.StopEvidenceSha256!.ToLowerInvariant(), ["receiptId"] = receiptId };
        var receipt = RecordJson.New("audit", receiptId, actor.TenantId, actor.PrincipalId, run.Value.Strings("scopeIds"), clock.GetUtcNow());
        receipt["operation"] = "businessAnalysisOfflineRecovery"; receipt["fingerprint"] = fingerprint; receipt["result"] = result.DeepClone();
        run.Value["status"] = "failed"; run.Value["analysisStatus"] = "aborted"; run.Value["groups"] = new JsonArray(); run.Value["exclusions"] = new JsonArray();
        run.Value["sourceManifest"] = new JsonArray(); run.Value["sourceCount"] = null; run.Value["duplicateCount"] = null; run.Value["inputHash"] = null;
        run.Value["decision"] = null; run.Value["recoveryRequired"] = false; run.Value["recoveryReceiptId"] = receiptId; run.Value["updatedAt"] = clock.GetUtcNow();
        slot.Value["status"] = "ready"; slot.Value["analysisRunId"] = null; slot.Value["updatedAt"] = clock.GetUtcNow();
        List<RecordWrite> writes = [RecordWrite.Replace(run.Value, run.ETag), RecordWrite.Replace(slot.Value, slot.ETag), RecordWrite.Create(receipt)];
        foreach (var reservation in pending)
        {
            reservation.Value["state"] = "uncertain"; reservation.Value["actualInput"] = reservation.Value["input"]?.DeepClone(); reservation.Value["actualOutput"] = reservation.Value["output"]?.DeepClone();
            reservation.Value["chargedEstimateJpy"] = reservation.Value["jpy"]?.DeepClone(); writes.Add(RecordWrite.Replace(reservation.Value, reservation.ETag));
        }
        if (gate is not null)
        {
            var owned = pending.Select(r => r.Value.Text("id")).ToHashSet();
            gate.Value["active"] = RecordJson.Array(gate.Value.Strings("active").Where(r => !owned.Contains(r)));
            writes.Add(RecordWrite.Replace(gate.Value, gate.ETag));
        }
        // One catalog batch: no partial refund, no other run's slot release, no automatic inference retry.
        Stopped();
        await store.CommitAsync(p, writes, ct);
        return result;
    }
}
