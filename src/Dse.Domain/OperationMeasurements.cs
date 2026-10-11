namespace Dse.Domain;

// Only generated measurement identifiers, fixed enum labels, timestamps and numeric values.
// Never add tenant/principal/source IDs, route strings, request bodies or exception messages.
public sealed record MeasurementValue(double? Value, string Unit, string Status, string? SourceReference);
public sealed record MeasuredOperation(string OperationId, int Attempt, string RunId, string RequestId,
    string CorrelationId, string Kind, string Phase, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string Status, int? HttpStatus, string? FailureCode, MeasurementValue DurationMs,
    MeasurementValue RequestUnits, object? Ai, MeasurementValue EstimatedCostJpy, string SourceReference);
public sealed record OperationMeasurementExport(string SchemaVersion, string RunId, string EvidenceKind,
    string State, DateTimeOffset ObservedAt, MeasuredOperation[] Operations, double? ObservedSdkRequestUnits,
    double? CompleteRequestUnits, bool CompleteMeasurement, bool AcceptanceGatePassed,
    int DroppedOperations, string[] MissingExpectedKinds, string[] UnknownComponents, string[] Limitations);
