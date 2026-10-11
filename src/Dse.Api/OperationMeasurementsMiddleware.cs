using System.Collections.Concurrent;
using Dse.Domain;
using Dse.Infrastructure;

namespace Dse.Api;

// Bounded process-local, numeric-only reports. No persistence, cloud writes or automatic public endpoint.
public sealed class OperationMeasurementBuffer
{
    private readonly ConcurrentQueue<OperationMeasurementExport> completed = new();
    private int retainedOperations;
    public void Add(OperationMeasurementExport report)
    {
        completed.Enqueue(report);
        Interlocked.Add(ref retainedOperations, report.Operations.Length);
        while (completed.Count > 64 || Volatile.Read(ref retainedOperations) > 20000)
            if (completed.TryDequeue(out var removed)) Interlocked.Add(ref retainedOperations, -removed.Operations.Length);
            else break;
    }
    public OperationMeasurementExport[] Snapshot() => completed.ToArray();
}
public sealed class OperationMeasurementsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IConfiguration configuration, OperationMeasurementBuffer buffer)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        using var scope = OperationMeasurements.Begin(configuration["Data:Mode"] == "Demo"
            ? MeasurementEvidence.Fixture : MeasurementEvidence.RealService);
        context.Response.Headers["X-DSE-Measurement-Run"] = scope.RunId;
        using var operation = OperationMeasurements.Start(MeasurementKind.HttpRequest);
        try { await next(context); operation.ObserveResponse(null, context.Response.StatusCode); }
        catch (Exception ex) { operation.Fail(ex); throw; }
        finally { operation.Dispose(); buffer.Add(scope.Export()); }
    }
}
