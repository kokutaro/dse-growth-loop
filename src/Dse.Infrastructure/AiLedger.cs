using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record AiReservation(string Id, string DayId, string Tenant, long Input, long Output, decimal Jpy);
public sealed record AiUsage(long Input, long Output, bool Estimated);
public sealed class AiException(AiFailure failure, bool retryable = false, TimeSpan? retryAfter = null, string? requestId = null) : Exception("AI_" + failure.ToString().ToUpperInvariant())
{
    public AiFailure Failure { get; } = failure;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public string? RequestId { get; } = requestId;
}

// Each attempt owns a durable reservation. CAS covers counters, slots and the reservation together.
// Pending reservations never expire: an uncertain charge must not become spendable after a restart.
public sealed class AiLedger(IRecordStore store, TimeProvider clock)
{
    private const string GateId = "system-ai-gate";
    private static long Count(JsonObject value, string key) => value[key]?.GetValue<long>() ?? 0;
    private static decimal Money(JsonObject value) => value["jpy"]?.GetValue<decimal>() ?? 0;
    private JsonObject New(string kind, string id, string tenant) => RecordJson.New(kind, id, tenant, "system", ["system"], clock.GetUtcNow());

    public async Task<AiReservation> ReserveAsync(string tenant, string runId, AiModel model, AiLimits limits, long input, long output, CancellationToken ct)
    {
        if (limits.Stopped || !limits.LiveOptIn) throw new AiException(AiFailure.Stopped);
        if (limits.DailyTokens <= 0 || limits.DailyCalls <= 0 || limits.DailyJpy <= 0 || limits.UsdToJpy <= 0 ||
            string.IsNullOrWhiteSpace(limits.PriceVersion) || string.IsNullOrWhiteSpace(limits.FxVersion) ||
            limits.PriceVersion == "unapproved" || limits.FxVersion == "unapproved") throw new AiException(AiFailure.Budget);
        if (input < 1 || input > limits.InputTokens || output < 1 || output > limits.OutputTokens) throw new AiException(AiFailure.InputLimit);
        var partition = RecordPartition.Catalog(tenant);
        var dayId = "ai-day-" + clock.GetUtcNow().ToString("yyyy-MM-dd");
        var cost = model.Cost(input, output, limits);
        var reservation = new AiReservation("ai-res-" + Guid.NewGuid().ToString("N"), dayId, tenant, input, output, cost);
        for (var conflict = 0; conflict < 20; conflict++)
        {
            var day = await store.ReadAsync(partition, dayId, ct);
            var gate = await store.ReadAsync(partition, GateId, ct);
            var d = day?.Value.Copy() ?? New("aiUsageDay", dayId, tenant);
            var g = gate?.Value.Copy() ?? New("aiUsageGate", GateId, tenant);
            if (g["blocked"]?.GetValue<bool>() == true) throw new AiException(AiFailure.Budget);
            var active = g["active"]?.AsArray() ?? new JsonArray();
            if (active.Count >= limits.Concurrency) throw new AiException(AiFailure.Busy);
            if (Count(d, "tokens") + input + output > limits.DailyTokens || Count(d, "calls") + 1 > limits.DailyCalls || Money(d) + cost > limits.DailyJpy)
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
            try
            {
                await store.CommitAsync(partition, [new(d, day?.ETag), new(g, gate?.ETag), RecordWrite.Create(row)], ct);
                return reservation;
            }
            catch (StoreConflictException) { }
        }
        throw new AiException(AiFailure.Busy);
    }

    public async Task SettleAsync(AiReservation reservation, AiUsage? usage, AiModel model, AiLimits limits, CancellationToken ct)
    {
        var partition = RecordPartition.Catalog(reservation.Tenant);
        for (var conflict = 0; conflict < 20; conflict++)
        {
            var r = await store.ReadAsync(partition, reservation.Id, ct) ?? throw new InvalidOperationException("Missing AI reservation");
            if (r.Value.Text("state") != "pending") return;
            var day = (await store.ReadAsync(partition, reservation.DayId, ct))!;
            var gate = (await store.ReadAsync(partition, GateId, ct))!;
            var input = usage?.Input ?? reservation.Input;
            var output = usage?.Output ?? reservation.Output;
            var cost = usage is null ? reservation.Jpy : model.Cost(input, output, limits);
            day.Value["tokens"] = Count(day.Value, "tokens") - reservation.Input - reservation.Output + input + output;
            day.Value["jpy"] = Money(day.Value) - reservation.Jpy + cost;
            gate.Value["active"] = RecordJson.Array(gate.Value.Strings("active").Where(x => x != reservation.Id));
            // A provider exceeding the reserved ceiling invalidates the contract; stop future sends.
            if (input > reservation.Input || output > reservation.Output || cost > reservation.Jpy) gate.Value["blocked"] = true;
            r.Value["state"] = usage is null || usage.Estimated ? "uncertain" : "settled";
            r.Value["actualInput"] = input; r.Value["actualOutput"] = output; r.Value["chargedEstimateJpy"] = cost;
            try
            {
                await store.CommitAsync(partition, [RecordWrite.Replace(day.Value, day.ETag), RecordWrite.Replace(gate.Value, gate.ETag), RecordWrite.Replace(r.Value, r.ETag)], ct);
                return;
            }
            catch (StoreConflictException) { }
        }
        throw new AiException(AiFailure.Busy);
    }

    // Offline recovery releases a crashed concurrency slot but never refunds uncertain spend.
    public async Task RecoverSlotAsync(string tenant, string reservationId, bool processesStopped, CancellationToken ct)
    {
        if (!processesStopped) throw new ArgumentException("Stop API and Worker before AI recovery");
        var partition = RecordPartition.Catalog(tenant);
        var row = await store.ReadAsync(partition, reservationId, ct) ?? throw new ArgumentException("Unknown reservation");
        if (row.Value.Text("kind") != "aiReservation") throw new ArgumentException("Invalid reservation");
        if (row.Value.Text("state") != "pending") return;
        var gate = (await store.ReadAsync(partition, GateId, ct))!;
        gate.Value["active"] = RecordJson.Array(gate.Value.Strings("active").Where(x => x != reservationId));
        row.Value["state"] = "uncertain";
        await store.CommitAsync(partition, [RecordWrite.Replace(row.Value, row.ETag), RecordWrite.Replace(gate.Value, gate.ETag)], ct);
    }
}
