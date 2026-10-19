using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Admission/idempotency state only: no prices, cumulative calls/tokens, daily/weekly periods or budgets.
// Share the active gate with legacy accounting so mixed-version deployments cannot send concurrently.
// An uncertain provider execution stays pending until explicit offline recovery proves quiescence.
public sealed class AiExecutionGuard(IRecordStore store, TimeProvider clock)
{
    public async Task<AiReservation> BeginAsync(string tenant, string runId, AiLimits limits, long input, long output, CancellationToken ct)
    {
        if (limits.Stopped || !limits.LiveOptIn) throw new AiException(AiFailure.Stopped);
        if (limits.AccountingEnabled || limits.Attempts != 1 || limits.Concurrency != 1) throw new AiException(AiFailure.Configuration);
        if (!RecordJson.IsId(runId) || input < 1 || input > limits.InputTokens || output < 1 || output > limits.OutputTokens) throw new AiException(AiFailure.InputLimit);
        var p = RecordPartition.Catalog(tenant); var id = "ai-exec-" + ConversationService.Hash(runId);
        for (var conflict = 0; conflict < 20; conflict++)
        {
            if (await store.ReadAsync(p, id, ct) is not null) throw new AiException(AiFailure.Busy);
            var saved = await store.ReadAsync(p, "system-ai-gate", ct);
            var gate = saved?.Value.Copy() ?? RecordJson.New("aiUsageGate", "system-ai-gate", tenant, "system", ["system"], clock.GetUtcNow());
            if (gate["blocked"]?.GetValue<bool>() == true) throw new AiException(AiFailure.OutputLimit);
            if (gate.Strings("active").Length > 0) throw new AiException(AiFailure.Busy);
            gate["active"] = RecordJson.Array([id]);
            var execution = RecordJson.New("aiExecution", id, tenant, "system", ["system"], clock.GetUtcNow());
            execution["runId"] = runId; execution["state"] = "pending"; execution["accountingMode"] = "Disabled";
            try
            {
                await store.CommitAsync(p, [new(gate, saved?.ETag), RecordWrite.Create(execution)], ct);
                return new(id, "", tenant, input, output, 0) { AccountingDisabled = true };
            }
            catch (StoreConflictException) { }
        }
        throw new AiException(AiFailure.Busy);
    }
    public async Task CompleteAsync(AiReservation handle, AiUsage? usage, CancellationToken ct)
    {
        var p = RecordPartition.Catalog(handle.Tenant);
        for (var conflict = 0; conflict < 20; conflict++)
        {
            var execution = await store.ReadAsync(p, handle.Id, ct) ?? throw new InvalidOperationException("Missing AI execution");
            if (execution.Value.Text("kind") != "aiExecution") throw new InvalidOperationException("Invalid AI execution");
            if (execution.Value.Text("state") != "pending") return;
            var saved = await store.ReadAsync(p, "system-ai-gate", ct) ?? throw new InvalidOperationException("Missing AI execution gate");
            var gate = saved.Value.Copy(); gate["active"] = RecordJson.Array(gate.Strings("active").Where(id => id != handle.Id));
            if (usage is not null && (usage.Input > handle.Input || usage.Output > handle.Output)) gate["blocked"] = true;
            execution.Value["state"] = "completed";
            try { await store.CommitAsync(p, [RecordWrite.Replace(execution.Value, execution.ETag), RecordWrite.Replace(gate, saved.ETag)], ct); return; }
            catch (StoreConflictException) { }
        }
        throw new AiException(AiFailure.Busy);
    }
}
