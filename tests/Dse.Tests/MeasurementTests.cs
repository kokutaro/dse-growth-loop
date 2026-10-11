using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Api;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class MeasurementTests
{
    [Fact]
    public async Task DemoStoreCapturesReadBatchAndQueryWithoutPayloadOrInventedRu()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.Fixture);
        var store = new OperationMeasuredRecordStore(new FakeStore());
        var partition = new RecordPartition("private-container", "secret-tenant", "private-session");
        await store.ReadAsync(partition, "private-person-id", default);
        await store.CommitAsync(partition, [RecordWrite.Create(new() { ["kind"] = "retrievalRun", ["id"] = "private-run", ["body"] = "SECRET-BODY" })], default);
        await store.QueryAsync(partition, "searchUnit", "personId", "private-parent", 10, "SECRET-TOKEN", default);
        var export = scope.Export();
        Assert.Equal(3, export.Operations.Length);
        Assert.Contains(export.Operations, r => r.Kind == "source_read");
        Assert.Contains(export.Operations, r => r.Kind == "audit_write");
        Assert.Contains(export.Operations, r => r.Kind == "projection_page");
        Assert.All(export.Operations, r => { Assert.Null(r.RequestUnits.Value); Assert.Equal("not_collected", r.RequestUnits.Status); Assert.Equal("fixture", r.DurationMs.Status); });
        Assert.Null(export.ObservedSdkRequestUnits); Assert.Null(export.CompleteRequestUnits); Assert.False(export.AcceptanceGatePassed);
        var json = JsonSerializer.Serialize(export);
        foreach (var forbidden in new[] { "SECRET", "private-", "secret-tenant", "user-principal" }) Assert.DoesNotContain(forbidden, json);
    }
    [Fact]
    public void FailedSdkResponseAndThrownCosmosChargeAreRetained()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.RealService);
        using (var op = OperationMeasurements.Start(MeasurementKind.SourceRead, true))
        { op.ObserveResponse(2.5, 429); op.Fail(new CosmosException("SECRET endpoint/person", HttpStatusCode.TooManyRequests, 0, "SECRET activity", 2.5)); }
        var row = Assert.Single(scope.Export().Operations);
        Assert.Equal(2.5, row.RequestUnits.Value); Assert.Equal(429, row.HttpStatus); Assert.Equal("failed", row.Status);
        Assert.Equal("cosmos_failure", row.FailureCode);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(row));
    }
    [Fact]
    public void BatchResponseIsCountedOnceAndRepeatedObservationDoesNotSumItemRu()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.RealService);
        using (var op = OperationMeasurements.Start(MeasurementKind.LedgerReserve, true))
        { op.ObserveResponse(8, 201); op.ObserveResponse(8, 201); }
        var export = scope.Export();
        Assert.Equal(8, export.ObservedSdkRequestUnits); Assert.Single(export.Operations);
        Assert.Null(export.CompleteRequestUnits); Assert.False(export.CompleteMeasurement);
        Assert.Contains("sdk_internal_retry_attempts_and_charges", export.UnknownComponents);
    }
    [Fact]
    public async Task ParallelRequestsStayIsolatedWhileParallelOperationsShareTheirRun()
    {
        var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            using var scope = OperationMeasurements.Begin(MeasurementEvidence.RealService);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            { using var op = OperationMeasurements.Start(MeasurementKind.SourceRead, true); await Task.Yield(); op.ObserveResponse(n + 1, 200); }));
            return scope.Export();
        })));
        Assert.Equal(6, runs.Select(r => r.RunId).Distinct().Count());
        foreach (var run in runs) { Assert.Equal(4, run.Operations.Length); Assert.All(run.Operations, op => Assert.Equal(run.RunId, op.RunId)); }
        Assert.Equal(24, runs.SelectMany(r => r.Operations).Select(r => r.OperationId).Distinct().Count());
        Assert.False(OperationMeasurements.IsActive);
    }
    [Fact]
    public void PendingDroppedAndMissingCategoriesKeepUnknownGate()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.RealService, maximumOperations: 1);
        using var pending = OperationMeasurements.Start(MeasurementKind.FenceRead, true);
        using (OperationMeasurements.Start(MeasurementKind.LedgerRead, true)) { }
        var export = scope.Export(MeasurementKind.FenceRead, MeasurementKind.LedgerRead);
        Assert.Equal("pending", Assert.Single(export.Operations).Status); Assert.Equal(1, export.DroppedOperations);
        Assert.Equal(["ledger_read"], export.MissingExpectedKinds); Assert.False(export.AcceptanceGatePassed);
        Assert.Null(export.CompleteRequestUnits);
    }
    [Fact]
    public void DemoNeverPromotesInjectedSdkNumberToObservedRu()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.Fixture);
        using (var op = OperationMeasurements.Start(MeasurementKind.ProjectionPage, true)) op.ObserveResponse(123, 200);
        Assert.Null(Assert.Single(scope.Export().Operations).RequestUnits.Value);
    }
    [Fact]
    public async Task CancellationAndNonSdkFailuresRemainUnknownAndSanitized()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.RealService);
        var store = new OperationMeasuredRecordStore(new FakeStore { Failure = new OperationCanceledException("SECRET") });
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync(RecordPartition.Catalog("private"), "id", default));
        var row = Assert.Single(scope.Export().Operations);
        Assert.Equal("cancelled", row.Status); Assert.Null(row.RequestUnits.Value);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(row));
    }
    [Fact]
    public void FixedClassificationHandlesFenceLedgerEvidenceAndMixedBatches()
    {
        Assert.Equal(MeasurementKind.FenceRead, OperationMeasurementKinds.Read("system-access-fence"));
        Assert.Equal(MeasurementKind.LedgerRead, OperationMeasurementKinds.Read("system-ai-gate"));
        Assert.Equal(MeasurementKind.LedgerRead, OperationMeasurementKinds.Read("ai-res-private"));
        Assert.Equal(MeasurementKind.EvidenceRead, OperationMeasurementKinds.Record("evidenceSpan"));
        RecordWrite Write(string kind, string? state = null) => RecordWrite.Create(new() { ["kind"] = kind, ["state"] = state });
        Assert.Equal(MeasurementKind.LedgerReserve, OperationMeasurementKinds.Write([Write("aiUsageDay"), Write("aiReservation", "pending")]));
        Assert.Equal(MeasurementKind.LedgerSettle, OperationMeasurementKinds.Write([Write("aiReservation", "uncertain")]));
        Assert.Equal(MeasurementKind.FenceWrite, OperationMeasurementKinds.Write([Write("accessFence"), Write("person")]));
    }
    [Fact]
    public async Task HttpMiddlewareMeasuresServicePhaseAndDoesNotRetainUrlOrPrincipal()
    {
        var buffer = new OperationMeasurementBuffer();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo" }).Build();
        var middleware = new OperationMeasurementsMiddleware(async context =>
        { using var op = OperationMeasurements.Start(MeasurementKind.SourceRead); await Task.Yield(); context.Response.StatusCode = 422; });
        var context = new DefaultHttpContext(); context.Request.Path = "/api/private-person/secret";
        await middleware.InvokeAsync(context, config, buffer);
        var export = Assert.Single(buffer.Snapshot());
        Assert.Equal(2, export.Operations.Length); Assert.All(export.Operations, r => Assert.Equal("service", r.Phase));
        Assert.Equal(422, export.Operations.Single(r => r.Kind == "http_request").HttpStatus);
        Assert.DoesNotContain("private-person", JsonSerializer.Serialize(export));
    }
    [Fact]
    public void NestedScopeRestoresParentAndBufferIsBounded()
    {
        using var parent = OperationMeasurements.Begin(MeasurementEvidence.Fixture);
        using (var child = OperationMeasurements.Begin(MeasurementEvidence.Fixture))
        { using (OperationMeasurements.Start(MeasurementKind.Other)) { } Assert.Single(child.Export().Operations); }
        using (OperationMeasurements.Start(MeasurementKind.Other)) { }
        Assert.Single(parent.Export().Operations);
        var buffer = new OperationMeasurementBuffer(); for (var i = 0; i < 70; i++) buffer.Add(parent.Export());
        Assert.Equal(64, buffer.Snapshot().Length);
    }
    [Fact]
    public void InvalidAndAbsentChargesStayUnknown()
    {
        using var scope = OperationMeasurements.Begin(MeasurementEvidence.RealService);
        foreach (var charge in new double?[] { null, -1, double.NaN, double.PositiveInfinity })
        { using var op = OperationMeasurements.Start(MeasurementKind.Other, true); op.ObserveResponse(charge, 503); }
        Assert.All(scope.Export().Operations, r => Assert.Null(r.RequestUnits.Value));
        Assert.Null(scope.Export().CompleteRequestUnits);
    }
    private sealed class FakeStore : IRecordStore
    {
        public Exception? Failure { get; init; }
        public async Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
        { await Task.Yield(); if (Failure is not null) throw Failure; return new(new() { ["kind"] = "person", ["id"] = id, ["body"] = "SECRET-BODY", ["ownerId"] = "user-principal" }, "SECRET-ETAG"); }
        public Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct) => Task.CompletedTask;
        public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) => Task.FromResult(new RecordPage([], null));
    }
}
