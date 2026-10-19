using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record AiReservation(string Id, string DayId, string Tenant, long Input, long Output, decimal Jpy, string? WeekId = null)
{
    public bool AccountingDisabled { get; init; }
}
public sealed record AiUsage(long Input, long Output, bool Estimated);
public sealed record AiExecutionRecoveryEvidence(bool ApiStopped, bool WorkerStopped, bool ProvidersStopped,
    string ApprovalReference, string StopEvidenceReference, string StopEvidenceSha256);
public enum AiValidationIssue { ResponseShape, Keys, Probability, DistributionSum, SelectedOption, SelectedMaximum }

public sealed class AiException(AiFailure failure, bool retryable = false, TimeSpan? retryAfter = null, string? requestId = null, AiValidationIssue? validationIssue = null) : Exception("AI_" + failure.ToString().ToUpperInvariant())
{
    public AiFailure Failure { get; } = failure;
    public AiValidationIssue? ValidationIssue { get; } = validationIssue;
    public bool Retryable { get; } = retryable;
    public bool ProviderRejected { get; init; }
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string? RequestId { get; } = requestId;
}

// Each attempt owns a durable reservation. CAS covers counters, slots and the reservation together.
// Pending reservations never expire: an uncertain charge must not become spendable after a restart.
public sealed partial class AiLedger(IRecordStore store, TimeProvider clock)
{
    private const string GateId = "system-ai-gate";
    private static long Count(JsonObject value, string key) => value[key]?.GetValue<long>() ?? 0;
    private static decimal Money(JsonObject value, string key = "jpy") => value[key]?.GetValue<decimal>() ?? 0;
    private JsonObject New(string kind, string id, string tenant) => RecordJson.New(kind, id, tenant, "system", ["system"], clock.GetUtcNow());

    public async Task<AiReservation> ReserveAsync(string tenant, string runId, AiModel model, AiLimits limits, long input, long output, CancellationToken ct)
    {
        if (limits.Stopped || !limits.LiveOptIn) throw new AiException(AiFailure.Stopped);
        if (!limits.AccountingEnabled) return await new AiExecutionGuard(store, clock).BeginAsync(tenant, runId, limits, input, output, ct);
        if (limits.DailyTokens <= 0 || (!limits.Weekly && (limits.DailyCalls <= 0 || limits.DailyJpy <= 0)) || (limits.Weekly && (limits.WeeklyCalls <= 0 || limits.WeeklyTotalJpy <= 0)) || limits.UsdToJpy <= 0 ||
            string.IsNullOrWhiteSpace(limits.PriceVersion) || string.IsNullOrWhiteSpace(limits.FxVersion) ||
            limits.PriceVersion == "unapproved" || limits.FxVersion == "unapproved") throw new AiException(AiFailure.Budget);
        if (input < 1 || input > limits.InputTokens || output < 1 || output > limits.OutputTokens) throw new AiException(AiFailure.InputLimit);
        var partition = RecordPartition.Catalog(tenant);
        var dayId = "ai-day-" + clock.GetUtcNow().ToString("yyyy-MM-dd");
        var weekId = limits.Weekly ? WeekId(clock.GetUtcNow()) : null;
        var cost = model.Cost(input, output, limits);
        var reservation = new AiReservation("ai-res-" + Guid.NewGuid().ToString("N"), dayId, tenant, input, output, cost, weekId);
        for (var conflict = 0; conflict < 20; conflict++)
        {
            var day = await store.ReadAsync(partition, dayId, ct);
            var gate = await store.ReadAsync(partition, GateId, ct);
            var week = weekId is null ? null : await store.ReadAsync(partition, weekId, ct);
            var w = week?.Value.Copy();
            if (limits.Weekly && (w is null || w.Text("approvalReference") == "" || limits.WeeklyTotalJpy > Money(w, "limitJpy") || limits.WeeklyCalls > Count(w, "callLimit") || limits.UsdToJpy != Money(w, "usdToJpy"))) throw new AiException(AiFailure.Budget);
            var d = day?.Value.Copy() ?? New("aiUsageDay", dayId, tenant);
            var g = gate?.Value.Copy() ?? New("aiUsageGate", GateId, tenant);
            if (g["weeklyBudgetRequired"]?.GetValue<bool>() == true && !limits.Weekly) throw new AiException(AiFailure.Budget);
            if (g["blocked"]?.GetValue<bool>() == true) throw new AiException(AiFailure.Budget);
            var active = g["active"]?.AsArray() ?? new JsonArray();
            if (active.Any(id => id?.GetValue<string>().StartsWith("ai-exec-", StringComparison.Ordinal) == true) || active.Count >= limits.Concurrency) throw new AiException(AiFailure.Busy);
            if (Count(d, "tokens") + input + output > limits.DailyTokens ||
                (!limits.Weekly && (Count(d, "calls") + 1 > limits.DailyCalls || Money(d) + cost > limits.DailyJpy)) ||
                (limits.Weekly && (Count(w!, "calls") + 1 > limits.WeeklyCalls || Money(w!, "usd") * limits.UsdToJpy + Money(w!, "reservedNonRuntimeJpy") + cost > limits.WeeklyTotalJpy)))
                throw new AiException(AiFailure.Budget);
            d["tokens"] = Count(d, "tokens") + input + output;
            d["calls"] = Count(d, "calls") + 1;
            d["jpy"] = Money(d) + cost;
            active.Add(reservation.Id); g["active"] = active.DeepClone();
            var row = New("aiReservation", reservation.Id, tenant);
            row["dayId"] = dayId; row["runId"] = runId; row["state"] = "pending";
            row["input"] = input; row["output"] = output; row["jpy"] = cost;
            row["modelId"] = model.ModelId; row["modelVersion"] = model.ModelVersion;
            row["pricingVersion"] = limits.PriceVersion; row["fxVersion"] = limits.FxVersion;
            row["usdToJpy"] = limits.UsdToJpy;
            if (w is not null) { row["weekId"] = weekId; w["calls"] = Count(w, "calls") + 1; w["usd"] = Money(w, "usd") + cost / limits.UsdToJpy; }
            try
            {
                var writes = new List<RecordWrite> { new(d, day?.ETag), new(g, gate?.ETag), RecordWrite.Create(row) };
                if (w is not null) writes.Add(RecordWrite.Replace(w, week!.ETag));
                await store.CommitAsync(partition, writes, ct);
                return reservation;
            }
            catch (StoreConflictException) { }
        }
        throw new AiException(AiFailure.Busy);
    }

    public async Task SettleAsync(AiReservation reservation, AiUsage? usage, AiModel model, AiLimits limits, CancellationToken ct)
    {
        if (reservation.AccountingDisabled)
        {
            await new AiExecutionGuard(store, clock).CompleteAsync(reservation, usage, ct);
            return;
        }
        var partition = RecordPartition.Catalog(reservation.Tenant);
        for (var conflict = 0; conflict < 20; conflict++)
        {
            var r = await store.ReadAsync(partition, reservation.Id, ct) ?? throw new InvalidOperationException("Missing AI reservation");
            if (r.Value.Text("state") != "pending") return;
            var day = (await store.ReadAsync(partition, reservation.DayId, ct))!;
            var gate = (await store.ReadAsync(partition, GateId, ct))!;
            var input = usage?.Input ?? reservation.Input;
            var output = usage?.Output ?? reservation.Output;
            var capturedFx = r.Value["usdToJpy"]!.GetValue<decimal>();
            var cost = usage is null ? reservation.Jpy : model.Cost(input, output, limits with { UsdToJpy = capturedFx });
            var week = reservation.WeekId is null ? null : await store.ReadAsync(partition, reservation.WeekId, ct);
            if (reservation.WeekId is not null && week is null) throw new InvalidOperationException("Missing AI week");
            if (week is not null) week.Value["usd"] = Money(week.Value, "usd") - reservation.Jpy / capturedFx + cost / capturedFx;
            day.Value["tokens"] = Count(day.Value, "tokens") - reservation.Input - reservation.Output + input + output;
            day.Value["jpy"] = Money(day.Value) - reservation.Jpy + cost;
            gate.Value["active"] = RecordJson.Array(gate.Value.Strings("active").Where(x => x != reservation.Id));
            // A provider exceeding the reserved ceiling invalidates the contract; stop future sends.
            if (input > reservation.Input || output > reservation.Output || cost > reservation.Jpy) gate.Value["blocked"] = true;
            r.Value["state"] = usage is null || usage.Estimated ? "uncertain" : "settled";
            r.Value["actualInput"] = input; r.Value["actualOutput"] = output; r.Value["chargedEstimateJpy"] = cost;
            try
            {
                var writes = new List<RecordWrite> { RecordWrite.Replace(day.Value, day.ETag), RecordWrite.Replace(gate.Value, gate.ETag), RecordWrite.Replace(r.Value, r.ETag) };
                if (week is not null) writes.Add(RecordWrite.Replace(week.Value, week.ETag));
                await store.CommitAsync(partition, writes, ct);
                return;
            }
            catch (StoreConflictException) { }
        }
        throw new AiException(AiFailure.Busy);
    }

    // Offline recovery releases a crashed concurrency slot but never refunds uncertain spend.
    public async Task RecoverSlotAsync(string tenant, string reservationId, bool processesStopped, CancellationToken ct, AiExecutionRecoveryEvidence? evidence = null)
    {
        if (!processesStopped) throw new ArgumentException("Stop API and Worker before AI recovery");
        var partition = RecordPartition.Catalog(tenant);
        var row = await store.ReadAsync(partition, reservationId, ct) ?? throw new ArgumentException("Unknown reservation");
        if (row.Value.Text("kind") is not ("aiReservation" or "aiExecution")) throw new ArgumentException("Invalid reservation");
        if (evidence is null || !evidence.ApiStopped || !evidence.WorkerStopped || !evidence.ProvidersStopped ||
            !RecordJson.IsId(evidence.ApprovalReference) || !RecordJson.IsId(evidence.StopEvidenceReference) ||
            !System.Text.RegularExpressions.Regex.IsMatch(evidence.StopEvidenceSha256 ?? "", "\\A[a-fA-F0-9]{64}\\z"))
            throw new ArgumentException("Execution recovery requires API/Worker/provider quiescence and reviewed stop evidence");
        if (row.Value.Text("state") != "pending") return;
        var gate = (await store.ReadAsync(partition, GateId, ct))!;
        gate.Value["active"] = RecordJson.Array(gate.Value.Strings("active").Where(x => x != reservationId));
        row.Value["state"] = "uncertain";
        row.Value["recoveryApprovalReference"] = evidence.ApprovalReference;
        row.Value["stopEvidenceReference"] = evidence.StopEvidenceReference;
        row.Value["stopEvidenceSha256"] = evidence.StopEvidenceSha256!.ToLowerInvariant();
        await store.CommitAsync(partition, [RecordWrite.Replace(row.Value, row.ETag), RecordWrite.Replace(gate.Value, gate.ETag)], ct);
    }
}
