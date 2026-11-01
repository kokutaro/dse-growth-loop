using System.Diagnostics;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

// Bounded operational metadata only: no source text, principal, query, response or exception message.
public sealed class PipelineDiagnostics : IDisposable
{
    private static readonly AsyncLocal<PipelineDiagnostics?> Ambient = new();
    private static readonly AsyncLocal<Stage?> CurrentStage = new();
    private readonly PipelineDiagnostics? previous;
    private readonly object sync = new();
    private readonly List<Stage> stages = [];
    private readonly TimeProvider clock;
    private readonly ILogger? logger;
    private readonly long started;
    private int dropped;
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public string JobId { get; }
    public string? AggregateId { get; }
    public string? CancellationReason { get; private set; }
    private string? fenceHolder, fenceEtag;private string? blockerHash,blockerEtag;private int contentionCount;private bool admissionExhausted;
    private readonly string? jobCreatedAt;
    private readonly DateTimeOffset scopeStartedAt;private DateTimeOffset? processingStartedAt;
    private PipelineDiagnostics(string jobId, string? aggregateId, TimeProvider clock, ILogger? logger, string? jobCreatedAt)
    {
        JobId=jobId;AggregateId=aggregateId;this.clock=clock;this.logger=logger;this.jobCreatedAt=jobCreatedAt;
        started=clock.GetTimestamp();scopeStartedAt=clock.GetUtcNow();previous=Ambient.Value;Ambient.Value=this;
    }
    public static PipelineDiagnostics Begin(string jobId,string? aggregateId,TimeProvider clock,ILogger? logger=null,string? jobCreatedAt=null) => new(jobId,aggregateId,clock,logger,jobCreatedAt);
    public static IDisposable? BeginIfMissing(string jobId,TimeProvider clock,ILogger? logger=null) => Ambient.Value is null ? Begin(jobId,null,clock,logger) : null;
    public static Stage Measure(string name,string? operation=null,string? aiRunId=null) => new(Ambient.Value,name,operation,aiRunId);
    public void ProcessingStarted(){lock(sync)processingStartedAt=clock.GetUtcNow();}
    public static async Task<T> DuringAsync<T>(string name,Func<Task<T>> action)
    {using var stage=Measure(name);try{return await action();}catch(Exception error){stage.Failed(error);throw;}}
    public static void Cancel(string reason) { if(Ambient.Value is { } d)lock(d.sync){if(d.CancellationReason is null || reason is "job_deadline" or "lease_renewal_failed" or "service_stopping")d.CancellationReason=reason;} }
    public static void BlockedFence(string holder,string etag)
    {if(Ambient.Value is{}d)lock(d.sync){d.blockerHash=OfficeExtractor.Hash(System.Text.Encoding.UTF8.GetBytes(holder));d.blockerEtag=etag;d.contentionCount++;}}
    public static void AdmissionExhausted(){if(Ambient.Value is{}d)lock(d.sync)d.admissionExhausted=true;}
    public static void Fence(string holder,string etag) { if(Ambient.Value is { } d)lock(d.sync){d.fenceHolder=holder;d.fenceEtag=etag;CurrentStage.Value?.SetFence(holder,etag);} }
    public static JsonObject? Snapshot() => Ambient.Value?.Export();
    public static RepositoryCall Repository(string operation) => new(CurrentStage.Value,operation);
    public JsonObject Export()
    {
        lock(sync)return new(){["version"]="pipeline-stages-v1",["correlationId"]=CorrelationId,["jobId"]=JobId,["aggregateId"]=AggregateId,
            ["jobCreatedAt"]=jobCreatedAt,["scopeStartedAt"]=scopeStartedAt,["processingStartedAt"]=processingStartedAt,["elapsedMs"]=clock.GetElapsedTime(started).TotalMilliseconds,
            ["cancellationReason"]=CancellationReason,["droppedStages"]=dropped,["admissionExhaustedBeforeEntry"]=admissionExhausted,["contentionCount"]=contentionCount,["blockedHolderHash"]=blockerHash,["blockedFenceEtag"]=blockerEtag,["sdkInternalRetryCount"]=null,
            ["stages"]=new JsonArray(stages.Select(s=>(JsonNode)s.Export()).ToArray())};
    }
    public void Dispose() { logger?.LogInformation("PipelineSummary {Diagnostic}",Export().ToJsonString());Ambient.Value=previous; }
    public sealed class Stage : IDisposable
    {
        private readonly PipelineDiagnostics? root;
        private readonly Stage? previous;
        private readonly long started;
        private readonly string name;
        private readonly string? operation,aiRunId;private string? holder,etag;
        private bool ended;private string outcome="running";private double? elapsed;
        private int reads,queries,writes,failures,cacheHits,transportCalls;
        private double repositoryMs;
        internal Stage(PipelineDiagnostics? root,string name,string? operation,string? run)
        {
            this.root=root;this.name=name;previous=CurrentStage.Value;this.operation=operation??previous?.operation;aiRunId=run??previous?.aiRunId;
            started=root?.clock.GetTimestamp()??0;holder=root?.fenceHolder;etag=root?.fenceEtag;
            if(root is not null)lock(root.sync){if(root.stages.Count<256)root.stages.Add(this);else root.dropped++;}
            CurrentStage.Value=this;
        }
        internal void SetFence(string holder,string etag){this.holder=holder;this.etag=etag;}
        public void Cancelled(){outcome="cancelled";}
        public void Failed(){outcome="failed";}
        public void Failed(Exception error) { outcome=error is OperationCanceledException?"cancelled":"failed"; }
        public void CacheHit(){if(root is not null)lock(root.sync)cacheHits++;}
        public void TransportCalled(){if(root is not null)lock(root.sync)transportCalls++;}
        internal void Record(string kind,double ms,bool failed)
        { if(root is not null)lock(root.sync){if(kind=="read")reads++;else if(kind=="query")queries++;else writes++;repositoryMs+=ms;if(failed)failures++;} }
        internal JsonObject Export()=>new(){["stage"]=name,["operation"]=operation,["aiRunId"]=aiRunId,["fenceHolder"]=holder,["fenceEtag"]=etag,
            ["status"]=outcome,["durationMs"]=elapsed??root?.clock.GetElapsedTime(started).TotalMilliseconds,["repositoryReads"]=reads,["repositoryQueries"]=queries,
            ["repositoryWrites"]=writes,["repositoryMs"]=repositoryMs,["repositoryFailedCalls"]=failures,["readCacheHits"]=cacheHits,
            ["transportCalls"]=transportCalls,["sdkInternalRetryCount"]=null};
        public void Dispose(){if(ended)return;ended=true;elapsed=root?.clock.GetElapsedTime(started).TotalMilliseconds;if(outcome=="running")outcome="completed";CurrentStage.Value=previous;}
        internal long Now()=>root?.clock.GetTimestamp()??Stopwatch.GetTimestamp();
        internal double Elapsed(long from)=>root is null?Stopwatch.GetElapsedTime((long)from).TotalMilliseconds:root.clock.GetElapsedTime((long)from).TotalMilliseconds;
    }
    public sealed class RepositoryCall : IDisposable
    {
        private readonly Stage? stage;private readonly string operation;private readonly long start;private bool failed;
        internal RepositoryCall(Stage? stage,string operation){this.stage=stage;this.operation=operation;start=stage?.Now()??0;}
        public void Fail(){failed=true;}
        public void Dispose()=>stage?.Record(operation,stage.Elapsed(start),failed);
    }
}
