using System.Collections.Concurrent;
using System.Diagnostics;
using Dse.Domain;
using Microsoft.Azure.Cosmos;

namespace Dse.Infrastructure;

public enum MeasurementEvidence { Fixture, RealService }
public enum MeasurementKind { ProjectionPage, SourceRead, EvidenceRead, FenceRead, FenceWrite,
    AuditWrite, LedgerRead, LedgerReserve, LedgerSettle, HttpRequest, Other }
public enum MeasurementPhase { Service, Warm, Cold, Setup, Restore }

public sealed class OperationMeasurements : IDisposable
{
    private static readonly AsyncLocal<OperationMeasurements?> Ambient = new();
    private readonly OperationMeasurements? previous;
    private readonly ConcurrentDictionary<string, MeasuredOperation> operations = new();
    private readonly int maximum;
    private int begun, dropped, disposed;
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string RequestId { get; } = Guid.NewGuid().ToString("N");
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public MeasurementEvidence Evidence { get; }
    public MeasurementPhase Phase { get; }
    public static bool IsActive => Ambient.Value is { } current && Volatile.Read(ref current.disposed) == 0;
    private OperationMeasurements(MeasurementEvidence evidence, MeasurementPhase phase, int maximumOperations)
    {
        if (maximumOperations is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        Evidence = evidence; Phase = phase; maximum = maximumOperations;
        previous = Ambient.Value; Ambient.Value = this;
    }
    public static OperationMeasurements Begin(MeasurementEvidence evidence, MeasurementPhase phase = MeasurementPhase.Service,
        int maximumOperations = 10000) => new(evidence, phase, maximumOperations);
    public static MeasurementOperation Start(MeasurementKind kind, bool cosmosSdk = false) => new(
        IsActive ? Ambient.Value : null, kind, cosmosSdk);
    internal bool Add(MeasuredOperation operation)
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Increment(ref begun) > maximum)
        { Interlocked.Increment(ref dropped); return false; }
        return operations.TryAdd(operation.OperationId, operation);
    }
    internal void Finish(MeasuredOperation operation) => operations[operation.OperationId] = operation;
    internal MeasuredOperation New(string id, MeasurementKind kind, bool sdk, DateTimeOffset now) => new(
        id, 1, RunId, RequestId, CorrelationId, Label(kind), Label(Phase), now, null,
        "pending", null, null, new(null, "ms", "not_collected", null), new(null, "RU", "not_collected", null),
        null, new(null, "JPY", "not_collected", null), sdk ? "cosmos_sdk_response" : "local_operation");
    public OperationMeasurementExport Export(params MeasurementKind[] expectedKinds)
    {
        var rows = operations.Values.OrderBy(r => r.StartedAt).ThenBy(r => r.OperationId, StringComparer.Ordinal).ToArray();
        var missing = expectedKinds.Select(Label).Distinct().Except(rows.Select(r => r.Kind)).Order().ToArray();
        var observedRows = rows.Where(r => r.RequestUnits.Status == "observed").ToArray();
        double? observed = observedRows.Length == 0 ? null : observedRows.Sum(r => r.RequestUnits.Value ?? 0);
        return new("issue34-operation-export-v1", RunId, Evidence == MeasurementEvidence.Fixture ? "fixture" : "real_service",
            "incomplete", DateTimeOffset.UtcNow, rows, observed, null, false, false, dropped, missing,
            ["sdk_internal_retry_attempts_and_charges", "ai_usage_failures_and_reservations", "data_index_backup_storage",
             "replica_job_runtime", "network_and_logging", "applicable_prices_fx_tax_contract", "invoice_reconciliation", "ui_completion_and_cold_warm_population"],
            ["Partial operation export; merge into the separately approved issue34-measurement-v1 run contract.",
             "One operation per public SDK response; batch total only. Attempt=1 is not an SDK wire-attempt count.",
             "Missing categories do not prove zero operations or zero cost. Local/Demo RU stays null.",
             "No source IDs, principals, payloads, route names, SDK diagnostics or arbitrary exception messages are retained."]);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Ambient.Value = previous;
    }
    internal static string Label(MeasurementKind kind) => kind switch
    {
        MeasurementKind.ProjectionPage => "projection_page", MeasurementKind.SourceRead => "source_read",
        MeasurementKind.EvidenceRead => "evidence_read", MeasurementKind.FenceRead => "fence_read",
        MeasurementKind.FenceWrite => "fence_write", MeasurementKind.AuditWrite => "audit_write",
        MeasurementKind.LedgerRead => "ledger_read", MeasurementKind.LedgerReserve => "ledger_reserve",
        MeasurementKind.LedgerSettle => "ledger_settle", MeasurementKind.HttpRequest => "http_request", _ => "other"
    };
    private static string Label(MeasurementPhase phase) => phase switch
    { MeasurementPhase.Warm => "warm", MeasurementPhase.Cold => "cold", MeasurementPhase.Setup => "setup",
      MeasurementPhase.Restore => "restore", _ => "service" };
}

public sealed class MeasurementOperation : IDisposable
{
    private readonly OperationMeasurements? scope;
    private readonly bool sdk;
    private readonly long started = Stopwatch.GetTimestamp();
    private MeasuredOperation? row;
    private int finished;
    internal MeasurementOperation(OperationMeasurements? scope, MeasurementKind kind, bool sdk)
    {
        this.sdk = sdk;
        var value = scope?.New(Guid.NewGuid().ToString("N"), kind, sdk, DateTimeOffset.UtcNow);
        if (value is not null && scope!.Add(value)) { this.scope = scope; row = value; }
    }
    public void Classify(MeasurementKind kind) { if (row is not null) row = row with { Kind = OperationMeasurements.Label(kind) }; }
    public void ObserveResponse(double? ru, int? httpStatus)
    {
        if (row is null) return;
        var observed = sdk && scope!.Evidence == MeasurementEvidence.RealService && ru is { } n && double.IsFinite(n) && n >= 0;
        row = row with { HttpStatus = httpStatus,
            RequestUnits = new(observed ? ru : null, "RU", observed ? "observed" : "not_collected", observed ? "cosmos_sdk_response" : null),
            Status = httpStatus >= 400 ? "failed" : "succeeded", FailureCode = httpStatus >= 400 ? "http_failure" : null };
    }
    public void Fail(Exception exception)
    {
        if (row is null) return;
        if (exception is CosmosException cosmos) ObserveResponse(cosmos.RequestCharge, (int)cosmos.StatusCode);
        var status = exception is OperationCanceledException ? "cancelled" : "failed";
        var code = exception switch { OperationCanceledException => "cancelled", CosmosException => "cosmos_failure",
            StoreConflictException => "store_conflict", DependencyRetryException => "dependency_retry",
            DomainException => "domain_failure", ArgumentException => "invalid_operation", _ => "unclassified_failure" };
        row = row with { Status = status, FailureCode = code };
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref finished, 1) != 0 || row is null) return;
        row = row with { FinishedAt = DateTimeOffset.UtcNow, Status = row.Status == "pending" ? "succeeded" : row.Status,
            DurationMs = new(Stopwatch.GetElapsedTime(started).TotalMilliseconds, "ms",
                scope!.Evidence == MeasurementEvidence.Fixture ? "fixture" : "observed", "monotonic_stopwatch") };
        scope!.Finish(row);
    }
}

public static class OperationMeasurementKinds
{
    // Input values are inspected only to choose a fixed enum and are never stored/exported.
    public static MeasurementKind Read(string id) => id == "system-access-fence" ? MeasurementKind.FenceRead :
        id.StartsWith("ai-day-", StringComparison.Ordinal) || id.StartsWith("ai-week-", StringComparison.Ordinal) ||
        id.StartsWith("ai-res-", StringComparison.Ordinal) || id == "system-ai-gate" ? MeasurementKind.LedgerRead : MeasurementKind.SourceRead;
    public static MeasurementKind Record(string kind, MeasurementKind fallback = MeasurementKind.SourceRead) => kind switch
    { "evidenceSpan" or "document" or "documentVersion" => MeasurementKind.EvidenceRead,
      "accessFence" => MeasurementKind.FenceRead, "aiUsageDay" or "aiUsageWeek" or "aiUsageGate" or "aiReservation" => MeasurementKind.LedgerRead,
      "searchUnit" => MeasurementKind.ProjectionPage, _ => fallback };
    public static MeasurementKind Query(string kind) => Record(kind);
    public static MeasurementKind Write(IReadOnlyList<RecordWrite> writes)
    {
        if (writes.Any(w => w.Value.Text("kind") == "accessFence")) return MeasurementKind.FenceWrite;
        var reservation = writes.FirstOrDefault(w => w.Value.Text("kind") == "aiReservation");
        if (reservation is not null) return reservation.Value.Text("state") == "pending" ? MeasurementKind.LedgerReserve : MeasurementKind.LedgerSettle;
        if (writes.Any(w => w.Value.Text("kind") is "retrievalRun" or "aiRun")) return MeasurementKind.AuditWrite;
        return MeasurementKind.Other;
    }
}

// Demo/Local adapter. CosmosRecordStore already measures its SDK responses and must not be wrapped.
public sealed class OperationMeasuredRecordStore(IRecordStore inner) : IRecordStore
{
    public async Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
    {
        using var operation = OperationMeasurements.Start(OperationMeasurementKinds.Read(id));
        try { var value = await inner.ReadAsync(partition, id, ct); if (value is not null) operation.Classify(OperationMeasurementKinds.Record(value.Value.Text("kind"))); return value; }
        catch (Exception ex) { operation.Fail(ex); throw; }
    }
    public async Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        using var operation = OperationMeasurements.Start(OperationMeasurementKinds.Write(writes));
        try { await inner.CommitAsync(partition, writes, ct); }
        catch (Exception ex) { operation.Fail(ex); throw; }
    }
    public async Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? token, CancellationToken ct)
    {
        using var operation = OperationMeasurements.Start(OperationMeasurementKinds.Query(kind));
        try { return await inner.QueryAsync(partition, kind, field, parent, size, token, ct); }
        catch (Exception ex) { operation.Fail(ex); throw; }
    }
}
