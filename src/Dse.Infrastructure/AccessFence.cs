using Dse.Domain;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

// A durable, non-expiring mutex. A crashed holder deliberately fails closed. Recovery requires
// stopping both API and Worker and proving no commit is still in flight (see persistence.md).
// Unlike a timed lease, a paused old process cannot resume after a revocation and publish stale text.
public sealed class AccessFence(IPrimaryRepository repository,TimeProvider? admissionClock=null,ILogger<AccessFence>? logger=null)
{
    private sealed class Lease(IPrimaryRepository repository, string tenant)
    {
        public IPrimaryRepository Repository { get; } = repository;
        public string Tenant { get; } = tenant;
        private readonly object sync = new();
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool open = true;
        private int users;
        public bool Faulted { get; private set; }
        public void MarkFaulted(){lock(sync)Faulted=true;}
        public bool Enter() { lock (sync) { if (!open) return false; users++; return true; } }
        public void Exit(bool faulted) { lock (sync) { Faulted |= faulted; if (--users == 0 && !open) drained.TrySetResult(); } }
        public Task CloseAsync() { lock (sync) { open = false; return users == 0 ? Task.CompletedTask : drained.Task; } }
    }
    private static readonly AsyncLocal<Lease?> Current = new();
    internal static void MarkUncertainResult()=>Current.Value?.MarkFaulted();
    private sealed class ReadOnlyPreparationCancelled(OperationCanceledException inner) : OperationCanceledException("Read-only preparation cancelled",inner,inner.CancellationToken);
    internal Task<TResult> PrepareThenRunAsync<TPrepared,TResult>(string tenant,
        Func<IPrimaryRepository,CancellationToken,Task<TPrepared>> prepare,
        Func<TPrepared,CancellationToken,Task<TResult>> execute,CancellationToken ct) => BeforeEffectsAsync(tenant,async token=>
    {
        TPrepared prepared;
        using(var phase=FencedReadPhase.Begin(repository))
        {
            try { prepared=await prepare(phase,token); }
            catch(Exception error)
            {
                phase.Failed(error);
                if(error is OperationCanceledException cancelled && token.IsCancellationRequested && phase.CloseForCancellation())
                { PipelineDiagnostics.Cancel("source_preparation_cancelled_before_effects");throw new ReadOnlyPreparationCancelled(cancelled); }
                throw;
            }
        }
        // Everything after this boundary (audit, reservation, send, publish) retains fail-closed cancellation.
        token.ThrowIfCancellationRequested();
        return await execute(prepared,token);
    },ct);
    // Retry only acquisition rejections before the callback has ever started. Never replay an action.
    internal async Task<T> BeforeEffectsAsync<T>(string tenant,Func<CancellationToken,Task<T>> action,CancellationToken ct)
    {
        var clock=admissionClock??TimeProvider.System;var started=clock.GetTimestamp();var maximum=TimeSpan.FromSeconds(5);
        DomainException? last=null;var attempts=0;
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            if(last is not null&&clock.GetElapsedTime(started)>=maximum)
            { PipelineDiagnostics.AdmissionExhausted();throw last; }
            var entered=false;
            try
            {
                return await RunAsync(tenant,token=>
                {
                    entered=true;
                    return action(ct);
                },ct);
            }
            catch(DomainException e)when(e.Status==503&&e.Code=="ACCESS_BUSY"&&!entered)
            {
                last=e;attempts++;
                var remaining=maximum-clock.GetElapsedTime(started);
                if(remaining<=TimeSpan.Zero){PipelineDiagnostics.AdmissionExhausted();throw;}
                var delay=TimeSpan.FromMilliseconds(Math.Min(500,100*Math.Pow(2,Math.Min(attempts-1,3))));
                if(delay>remaining)delay=remaining;
                using var waiting=PipelineDiagnostics.Measure("fence_wait");
                try{await Task.Delay(delay,clock,ct);}catch(Exception error){waiting.Failed(error);throw;}
            }
        }
    }
    public async Task<T> RunAsync<T>(string tenant, Func<CancellationToken, Task<T>> action, CancellationToken ct, bool nested = false)
    {
        // Nested service boundaries in the same operation share its durable lease.
        if (nested && Current.Value is { } parent && ReferenceEquals(parent.Repository, repository) && parent.Tenant == tenant && parent.Enter())
        {
            var faulted = false;
            try { return await action(ct); }
            catch (Exception e) { faulted = !KnownRejection(e); throw; }
            finally { parent.Exit(faulted); }
        }
        using var acquisition=PipelineDiagnostics.Measure("fence_acquire");
        var clock=admissionClock??TimeProvider.System;var started=clock.GetTimestamp();
        var partition = RecordPartition.Catalog(tenant);
        StoredRecord held;
        try
        {
        var current = await repository.ReadAsync(partition, "system-access-fence", ct);
        if (current?.Value.Text("holder") is { Length: > 0 } blocker) { PipelineDiagnostics.BlockedFence(blocker,current.ETag);acquisition.Failed();throw new DomainException(503,"ACCESS_BUSY"); }
        var value = current?.Value.Copy() ?? RecordJson.New("accessFence", "system-access-fence", tenant, "system", ["system"], DateTimeOffset.UtcNow);
        value["holder"] = Guid.NewGuid().ToString("N");
        value["ownerTrace"]=FenceOwnerTrace.Capture(clock);
        value["updatedAt"] = DateTimeOffset.UtcNow;
        try { await repository.CommitAsync(partition, [new(value, current?.ETag)], ct); }
        catch (StoreConflictException) { acquisition.Failed();throw new DomainException(503, "ACCESS_BUSY"); }
        held = await repository.ReadAsync(partition, "system-access-fence", ct)
            ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
        PipelineDiagnostics.Fence(held.Value.Text("holder"),held.ETag);
        Trace("acquired",held,clock.GetElapsedTime(started).TotalMilliseconds,null);
        acquisition.Dispose();
        }
        catch(Exception error){acquisition.Failed(error);throw;}
        var release = false;
        string? cancellation=null;
        var heldAt=clock.GetTimestamp();
        var previous = Current.Value;
        var lease = new Lease(repository, tenant);
        try { Current.Value = lease; var result = await action(ct); release = true; return result; }
        catch (Exception e) when (KnownRejection(e)) { release = true; throw; }
        catch(OperationCanceledException){cancellation=PipelineDiagnostics.OwnerContext()?.Text("cancellationReason") is{Length:>0} reason?reason:"operation_cancelled";throw;}
        finally
        {
            Current.Value = previous;
            await lease.CloseAsync();
            if (release && !lease.Faulted)
            {
                try{await ReleaseAsync(partition,held,clock.GetElapsedTime(heldAt).TotalMilliseconds,clock);Trace("released",held,clock.GetElapsedTime(heldAt).TotalMilliseconds,null);}
                catch{Trace("release_unknown",held,clock.GetElapsedTime(heldAt).TotalMilliseconds,cancellation);throw;}
            }
            else Trace("retained_unknown",held,clock.GetElapsedTime(heldAt).TotalMilliseconds,cancellation);
        }
    }
    // ACCESS_BUSY is raised before entering the rejected acquisition's callback. It must not
    // poison an already-owned outer lease. Unknown dependency failures/cancellation stay closed.
    private static bool KnownRejection(Exception error) => error is ReadOnlyPreparationCancelled or ArgumentException or StoreConflictException ||
        error is DomainException d && (d.Status < 500 || d.Code == "SEARCH_LIMIT_EXCEEDED" || d.Status == 503 && d.Code == "ACCESS_BUSY");
    private void Trace(string state,StoredRecord held,double duration,string? cancellation)
    {
        var data=new System.Text.Json.Nodes.JsonObject{["state"]=state,["holderHash"]=FenceOwnerTrace.Hash(held.Value.Text("holder")),["acquiredEtag"]=held.ETag,
            ["owner"]=held.Value["ownerTrace"]?.DeepClone(),["durationMs"]=duration,["cancellationReason"]=cancellation};
        logger?.LogInformation("FenceLifecycle {Diagnostic}",data.ToJsonString());
    }
    private async Task ReleaseAsync(RecordPartition partition, StoredRecord held,double duration,TimeProvider clock)
    {
        var released = held.Value.Copy();
        released["holder"] = null;
        if(released["ownerTrace"]is System.Text.Json.Nodes.JsonObject owner){owner["state"]="released";owner["releasedAt"]=clock.GetUtcNow();owner["holdMs"]=duration;}
        // Release failures leave the durable fence closed for offline recovery.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await repository.CommitAsync(partition, [RecordWrite.Replace(released, held.ETag)], timeout.Token);
    }
}
