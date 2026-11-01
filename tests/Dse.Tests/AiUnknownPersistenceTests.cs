using Dse.Domain;
using Dse.Infrastructure;
using Xunit;
namespace Dse.Tests;
public partial class AiTests
{
    private sealed class UnknownAiWrite(IRecordStore inner,string checkpoint):IRecordStore
    {
        private bool fired;private int audits;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,cursor,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {
            var audit=writes.Any(w=>w.Value.Text("kind")=="aiRun");if(audit)audits++;
            var fail=!fired&&(checkpoint=="audit"&&audit&&audits==2||checkpoint=="settlement"&&writes.Any(w=>w.Value.Text("kind")=="aiExecution"&&w.Value.Text("state")=="completed"));
            await inner.CommitAsync(p,writes,ct);
            if(fail){fired=true;if(checkpoint=="audit")throw new IOException("fixture unknown persisted audit");throw new Azure.RequestFailedException(503,"fixture unknown persisted settlement");}
        }
    }
    [Theory]
    [InlineData("audit",0)][InlineData("settlement",1)]
    public async Task Unknown_audit_or_settlement_result_never_releases_fence_or_retries_and_keeps_observed_usage(string checkpoint,int calls)
    {
        var memory=new MemoryRecordStore();var fault=new UnknownAiWrite(memory,checkpoint);var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using var ai=Service(AccountingOff(),fault,handler);var result=await Decide(ai);Assert.Equal(AiFailure.Unavailable,result.Failure);Assert.Equal(calls,handler.Calls);
        var p=RecordPartition.Catalog("demo");Assert.NotNull((await memory.ReadAsync(p,"system-access-fence",default))!.Value["holder"]);
        var run=Assert.Single((await memory.QueryAsync(p,"aiRun",null,null,100,null,default)).Items);Assert.Equal("failed",run.Value.Text("status"));
        if(checkpoint=="settlement"){Assert.Equal(318L,run.Value["trace"]!["inputTokens"]!.GetValue<long>());Assert.Equal(34L,run.Value["trace"]!["outputTokens"]!.GetValue<long>());Assert.False(run.Value["trace"]!["usageEstimated"]!.GetValue<bool>());}
        Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>Decide(ai,Context() with{JobId="must-not-retry"}))).Code);Assert.Equal(calls,handler.Calls);
    }
}
