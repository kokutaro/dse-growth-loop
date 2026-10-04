using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;
public sealed partial class AiLedger
{
    public static DateTimeOffset WeekStartUtc(DateTimeOffset utc)
    {
        var jst = utc.ToOffset(TimeSpan.FromHours(9));
        var monday = jst.Date.AddDays(-(((int)jst.DayOfWeek + 6) % 7));
        return new DateTimeOffset(monday, TimeSpan.FromHours(9)).ToUniversalTime();
    }
    public static string WeekId(DateTimeOffset utc) => "ai-week-" + WeekStartUtc(utc).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Operator-only initialization while all inference processes are stopped. Never overwrites an existing week.
    public async Task<JsonObject> InitializeWeekAsync(string tenant, AiLimits limits, string approvalReference, bool processesStopped, CancellationToken ct)
    {
        if (!processesStopped || !limits.Stopped || !limits.Weekly || limits.UsdToJpy <= 0 || limits.WeeklyCalls < 1 ||
            limits.WeeklyTotalJpy <= 0 || limits.InfrastructureReserveJpy < 0 || limits.ExternalAiReserveJpy < 0 ||
            limits.InfrastructureReserveJpy + limits.ExternalAiReserveJpy >= limits.WeeklyTotalJpy || string.IsNullOrWhiteSpace(approvalReference))
            throw new ArgumentException("Stopped processes and approved finite weekly budget required");
        var partition = RecordPartition.Catalog(tenant); var now = clock.GetUtcNow(); var start = WeekStartUtc(now); var id = WeekId(now);
        var gate = await store.ReadAsync(partition, GateId, ct);
        if (gate?.Value.Strings("active").Length > 0) throw new InvalidOperationException("Active reservations prevent weekly import");
        var fencedGate = gate?.Value.Copy() ?? New("aiUsageGate", GateId, tenant);
        fencedGate["weeklyBudgetRequired"] = true;
        fencedGate["weeklyApprovalReference"] = approvalReference;
        var existing = await store.ReadAsync(partition, id, ct);
        if (existing is not null)
        {
            if (existing.Value.Text("approvalReference") != approvalReference) throw new ArgumentException("Existing weekly approval differs");
            // Also fence older initialized weeks without changing any counters or imported spend.
            if (gate?.Value["weeklyBudgetRequired"]?.GetValue<bool>() != true)
                await store.CommitAsync(partition, [new(fencedGate, gate?.ETag)], ct);
            return existing.Value.Copy();
        }
        var reservations = new List<JsonObject>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await store.QueryAsync(partition, "aiReservation", null, null, 100, cursor, ct);
            foreach (var record in page.Items)
            {
                var created = JsonSerializer.Deserialize<DateTimeOffset>(record.Value["createdAt"]!.ToJsonString());
                if (created >= start && created < start.AddDays(7)) reservations.Add(record.Value);
            }
            cursor = page.ContinuationToken;
            if (++pages > 40) throw new InvalidOperationException("Reservation import page bound exceeded");
        } while (cursor is not null);
        // Full UTC days within this JST week must agree with the durable daily counters.
        for (var day = start.UtcDateTime.Date.AddDays(1); day <= now.UtcDateTime.Date; day = day.AddDays(1))
        {
            var dayId = "ai-day-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var saved = await store.ReadAsync(partition, dayId, ct);
            if (saved is not null && Count(saved.Value, "calls") != reservations.Count(r => r.Text("dayId") == dayId))
                throw new InvalidOperationException("Incomplete existing reservation history");
        }
        decimal usd = 0;
        foreach (var r in reservations)
        {
            var fx = r["usdToJpy"]?.GetValue<decimal>() ?? 0;
            if (fx <= 0) throw new InvalidOperationException("Missing original FX in reservation");
            var paid = r["chargedEstimateJpy"]?.GetValue<decimal>() ?? Money(r);
            if (paid < 0) throw new InvalidOperationException("Negative reservation cost");
            usd += paid / fx; // Uncertain/pending reservations retain their full reserved amount.
        }
        var week = New("aiUsageWeek", id, tenant);
        week["periodStartUtc"] = start; week["periodEndUtc"] = start.AddDays(7); week["timeZone"] = "Asia/Tokyo";
        week["calls"] = (long)reservations.Count; week["usd"] = usd; week["limitJpy"] = limits.WeeklyTotalJpy;
        week["callLimit"] = (long)limits.WeeklyCalls; week["reservedNonRuntimeJpy"] = limits.InfrastructureReserveJpy + limits.ExternalAiReserveJpy;
        week["infrastructureReserveJpy"] = limits.InfrastructureReserveJpy; week["externalAiReserveJpy"] = limits.ExternalAiReserveJpy;
        week["usdToJpy"] = limits.UsdToJpy; week["approvalReference"] = approvalReference;
        week["importedReservations"] = reservations.Count; week["importedUsd"] = usd;
        week["importSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', reservations.Select(r => r.Text("id")).Order()))));
        var writes = new List<RecordWrite> { RecordWrite.Create(week) };
        // Fence import against a concurrently changed slot even though all inference processes were stopped.
        writes.Add(new(fencedGate, gate?.ETag));
        await store.CommitAsync(partition, writes, ct); return week.Copy();
    }
}
