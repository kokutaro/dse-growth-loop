using System.Net;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    private static IConfigurationRoot AccountingOff()
    {
        var c = Config(); c["AiUsage:AccountingMode"] = "Disabled"; c["AiUsage:MaxAttempts"] = "1";
        foreach (var key in new[] { "DailyTokenLimit", "DailyCallLimit", "DailyEstimatedJpyLimit", "UsdToJpy", "WeeklyCallLimit", "WeeklyTotalJpyLimit" }) c["AiUsage:" + key] = "0";
        c["AiUsage:PriceVersion"] = ""; c["AiUsage:FxVersion"] = ""; c["AiUsage:BudgetPeriod"] = "Weekly";
        return c;
    }
    [Fact]
    public void Disabled_accounting_smoke_needs_explicit_bounded_dummy_approval_but_no_budget()
    {
        var c=AccountingOff();c["Data:Mode"]="Azure";
        c["AiSmoke:Approved"]="true";c["AiSmoke:DummyOnly"]="true";c["AiSmoke:OperatorPersonId"]="demo-person-01";
        c["AiSmoke:ApprovalReference"]="approved-dummy-check";c["AiSmoke:Count"]="2";
        c["AiSmoke:MaxInputTokens"]="8000";c["AiSmoke:MaxOutputTokens"]="1000";
        AiSmoke.Validate(c);
        c["AiSmoke:DummyOnly"]="false";Assert.Throws<InvalidOperationException>(()=>AiSmoke.Validate(c));
    }
    private sealed class StagedCredentials : IAiCredentials
    {
        public Func<CancellationToken,Task<string>> Fetch = _ => throw new HttpRequestException("credential fetch failed before inference");
        public Task<string> BearerAsync(string provider,CancellationToken ct) => Fetch(ct);
    }
    [Theory] [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task Credential_fetch_failure_releases_admission_without_dispatching_and_allows_a_corrected_call(bool disabled,bool bodyReadFailure)
    {
        var c=disabled?AccountingOff():Config();var store=new MemoryRecordStore();await AuthorityProvider.SeedDemoAsync(store,"demo",default);
        var repo=new PrimaryRepository(store);var credentials=new StagedCredentials {Fetch=_=>Task.FromException<string>(bodyReadFailure ? new IOException("credential response read failed") : new HttpRequestException("credential request failed"))};var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using var service=new AiService(c,repo,new(repo),new(repo),new(store,TimeProvider.System),new AiHttpTransport(new HttpClient(handler),credentials,TimeProvider.System,c),TimeProvider.System);
        Assert.Equal(AiFailure.Unavailable,(await Decide(service)).Failure);Assert.Equal(0,handler.Calls);
        Assert.Empty((await store.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default))!.Value.Strings("active"));
        credentials.Fetch=_=>Task.FromResult("dummy-credential");Assert.Equal(AiStatus.Succeeded,(await Decide(service,Context() with {JobId="credentials-fixed"})).Status);Assert.Equal(1,handler.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Timed_out_noncooperative_credentials_are_abandoned_and_cannot_dispatch_later(bool disabled)
    {
        var c=disabled?AccountingOff():Config();c["AiUsage:TimeoutSeconds"]="1";var store=new MemoryRecordStore();await AuthorityProvider.SeedDemoAsync(store,"demo",default);
        var repo=new PrimaryRepository(store);var delayed=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var credentials=new StagedCredentials {Fetch=_=>delayed.Task};var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using var service=new AiService(c,repo,new(repo),new(repo),new(store,TimeProvider.System),new AiHttpTransport(new HttpClient(handler),credentials,TimeProvider.System,c),TimeProvider.System);
        try
        {
            Assert.Equal(AiFailure.Timeout,(await Decide(service).WaitAsync(TimeSpan.FromSeconds(5))).Failure);Assert.Equal(0,handler.Calls);
            Assert.Empty((await store.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default))!.Value.Strings("active"));
            credentials.Fetch=_=>Task.FromResult("dummy-credential");Assert.Equal(AiStatus.Succeeded,(await Decide(service,Context() with {JobId="credential-timeout-fixed"})).Status);
            delayed.TrySetResult("late-credential");await Task.Yield();Assert.Equal(1,handler.Calls);
        }
        finally {delayed.TrySetResult("late-credential");}
    }
    [Fact]
    public void Abandoned_transport_dispatch_state_cannot_be_reopened()
    {var state=new AiDispatchState();Assert.True(state.TryAbandonBeforeDispatch());Assert.Throws<OperationCanceledException>(()=>state.MarkDispatched(default));}
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Definite_rejection_with_simultaneous_caller_cancellation_releases_the_gate(bool disabled)
    {
        var c=disabled?AccountingOff():Config();var store=new MemoryRecordStore();using var cancel=new CancellationTokenSource();
        var handler=new Handler((_,_)=>{cancel.Cancel();return Task.FromResult(Response("rejected",HttpStatusCode.BadRequest));});
        using var service=Service(c,store,handler);
        Assert.Equal(AiFailure.Unavailable,(await Decide(service,ct:cancel.Token)).Failure);Assert.Equal(1,handler.Calls);
        Assert.Empty((await store.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default))!.Value.Strings("active"));
        using var corrected=Service(c,store,new Handler((_,_)=>Task.FromResult(Response(Fixture("choice")))));
        Assert.Equal(AiStatus.Succeeded,(await Decide(corrected,Context() with {JobId="rejection-cancel-fixed"})).Status);
    }
    private sealed class AccountingProbe(MemoryRecordStore inner) : IRecordStore
    {
        public List<string> ReadIds = []; public List<string> WrittenKinds = [];
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct) { ReadIds.Add(id); return inner.ReadAsync(p,id,ct); }
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? token,CancellationToken ct) => inner.QueryAsync(p,kind,field,parent,size,token,ct);
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct) { WrittenKinds.AddRange(writes.Select(w=>w.Value.Text("kind"))); return inner.CommitAsync(p,writes,ct); }
    }
    [Fact]
    public async Task Disabled_accounting_crosses_weeks_without_reading_or_writing_cost_or_call_ledgers()
    {
        var c = AccountingOff(); var limits = AiLimits.Read(c); var model = AiModel.Read(c,"Jev",limits.OutputTokens);
        var memory = new MemoryRecordStore(); var store = new AccountingProbe(memory); var clock = new Clock(); var ledger = new AiLedger(store,clock);
        var p = RecordPartition.Catalog("demo");
        var gate = RecordJson.New("aiUsageGate","system-ai-gate","demo","system",["system"],clock.Now);
        gate["active"] = new JsonArray(); gate["weeklyBudgetRequired"] = true;
        await memory.CommitAsync(p,[RecordWrite.Create(gate)],default);
        for (var week = 0; week < 3; week++)
        {
            clock.Now = clock.Now.AddDays(7);
            var h = await ledger.ReserveAsync("demo","run-off-"+week,model,limits,100,100,default);
            Assert.True(h.AccountingDisabled); await ledger.SettleAsync(h,new(90,80,false),model,limits,default);
            Assert.Equal(AiFailure.Busy,(await Assert.ThrowsAsync<AiException>(()=>ledger.ReserveAsync("demo","run-off-"+week,model,limits,100,100,default))).Failure);
        }
        Assert.DoesNotContain(store.ReadIds,id=>id.StartsWith("ai-day-") || id.StartsWith("ai-week-") || id.StartsWith("ai-res-"));
        Assert.All(store.WrittenKinds,kind=>Assert.Contains(kind,new[]{"aiUsageGate","aiExecution"}));
        Assert.True((await memory.ReadAsync(p,"system-ai-gate",default))!.Value["weeklyBudgetRequired"]!.GetValue<bool>());
        foreach (var row in (await memory.QueryAsync(p,"aiExecution",null,null,100,null,default)).Items)
        { Assert.Equal("completed",row.Value.Text("state")); Assert.False(row.Value.ContainsKey("jpy")); Assert.False(row.Value.ContainsKey("calls")); }
    }
    [Fact]
    public async Task Disabled_accounting_shares_admission_with_legacy_mode_and_keeps_provider_ceiling_stop()
    {
        var store=new MemoryRecordStore();var ledger=new AiLedger(store,TimeProvider.System);
        var enabled=AiLimits.Read(Config());var disabled=AiLimits.Read(AccountingOff());var model=AiModel.Read(Config(),"Jev",1000);
        var old=await ledger.ReserveAsync("demo","legacy-pending",model,enabled,100,100,default);
        Assert.Equal(AiFailure.Busy,(await Assert.ThrowsAsync<AiException>(()=>ledger.ReserveAsync("demo","new-pending",model,disabled,100,100,default))).Failure);
        await ledger.SettleAsync(old,new(90,90,false),model,enabled,default);
        var current=await ledger.ReserveAsync("demo","new-pending",model,disabled,100,100,default);
        Assert.Equal(AiFailure.Busy,(await Assert.ThrowsAsync<AiException>(()=>ledger.ReserveAsync("demo","legacy-again",model,enabled with {Concurrency=8},100,100,default))).Failure);
        await ledger.SettleAsync(current,new(101,90,false),model,disabled,default);
        Assert.Equal(AiFailure.OutputLimit,(await Assert.ThrowsAsync<AiException>(()=>ledger.ReserveAsync("demo","after-contract-overrun",model,disabled,100,100,default))).Failure);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Disabled_accounting_keeps_per_request_input_and_output_bounds(bool input)
    {
        var c=AccountingOff();var limits=AiLimits.Read(c);var model=AiModel.Read(c,"Jev",1000);var ledger=new AiLedger(new MemoryRecordStore(),TimeProvider.System);
        Assert.Equal(AiFailure.InputLimit,(await Assert.ThrowsAsync<AiException>(()=>ledger.ReserveAsync("demo","invalid-input",model,limits,input?limits.InputTokens+1:1,input?1:limits.OutputTokens+1,default))).Failure);
    }
    [Theory] [InlineData("AiUsage:Stopped","true")] [InlineData("AiUsage:LiveOptIn","false")]
    public async Task Disabled_accounting_keeps_stop_and_optin_before_provider_send(string key,string value)
    {
        var c=AccountingOff();c[key]=value;var wire=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using var service=Service(c,new MemoryRecordStore(),wire); Assert.Equal(AiFailure.Stopped,(await Decide(service)).Failure);Assert.Equal(0,wire.Calls);
    }
    [Theory] [InlineData("AiUsage:MaxAttempts","2")] [InlineData("AiUsage:MaxConcurrency","2")] [InlineData("AiUsage:AccountingMode","unknown")]
    public void Disabled_accounting_rejects_unbounded_or_ambiguous_runtime_settings(string key,string value)
    {var c=AccountingOff();c[key]=value;Assert.Throws<InvalidOperationException>(()=>AiLimits.Read(c));}
    [Theory] [InlineData(200)] [InlineData(400)] [InlineData(404)] [InlineData(429)]
    public async Task Disabled_accounting_live_success_and_provider_rejection_do_not_create_accounting_rows_or_retry(int status)
    {
        var rejected = status != 200;
        {
            var c=AccountingOff();var memory=new MemoryRecordStore();var probe=new AccountingProbe(memory);var wire=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"),(HttpStatusCode)status)));
            using var service=Service(c,probe,wire);var result=await Decide(service);
            Assert.Equal(rejected?AiStatus.Failed:AiStatus.Succeeded,result.Status);Assert.Equal(1,wire.Calls);
            Assert.DoesNotContain(probe.WrittenKinds,kind=>kind is "aiUsageDay" or "aiUsageWeek" or "aiReservation");
            var execution=Assert.Single((await memory.QueryAsync(RecordPartition.Catalog("demo"),"aiExecution",null,null,100,null,default)).Items);
            Assert.Equal("completed",execution.Value.Text("state"));
            var run=Assert.Single((await memory.QueryAsync(RecordPartition.Catalog("demo"),"aiRun",null,null,100,null,default)).Items);
            Assert.Null(run.Value["trace"]!["estimatedCostJpy"]);
            if (rejected)
            {
                Assert.NotEqual(AiFailure.Busy,(await Decide(service,Context() with {JobId="corrected-job"})).Failure);
                Assert.Equal(2,wire.Calls);
            }
            if (!rejected)
            {
                var output=Path.Combine(Root,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output,"ai-run-disabled.json"),run.Value.ToJsonString());
            }
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Independent_post_dispatch_transport_cancellation_keeps_both_modes_pending_without_retry(bool disabled)
    {
        var c=disabled?AccountingOff():Config();var store=new MemoryRecordStore();using var caller=new CancellationTokenSource();
        using var independent=new CancellationTokenSource();independent.Cancel();
        var handler=new Handler((_,ct)=>{Assert.False(ct.IsCancellationRequested);Assert.False(caller.IsCancellationRequested);return Task.FromCanceled<HttpResponseMessage>(independent.Token);});
        using var service=Service(c,store,handler);
        Assert.Equal(AiFailure.Timeout,(await Decide(service,ct:caller.Token)).Failure);Assert.False(caller.IsCancellationRequested);Assert.Equal(1,handler.Calls);
        var kind=disabled?"aiExecution":"aiReservation";var pending=Assert.Single((await store.QueryAsync(RecordPartition.Catalog("demo"),kind,null,null,100,null,default)).Items);
        Assert.Equal("pending",pending.Value.Text("state"));Assert.Contains(pending.Value.Text("id"),(await store.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default))!.Value.Strings("active"));
        Assert.Equal(AiFailure.Busy,(await Decide(service,Context() with {JobId="after-independent-cancel"})).Failure);Assert.Equal(1,handler.Calls);
        using var otherMode=Service(disabled?Config():AccountingOff(),store,new Handler((_,_)=>throw new InvalidOperationException("Must remain blocked across modes")));
        Assert.Equal(AiFailure.Busy,(await Decide(otherMode,Context() with {JobId="other-mode-after-independent-cancel"})).Failure);
    }
    [Fact]
    public async Task Disabled_accounting_timeout_keeps_execution_slot_and_never_retries_even_if_transport_ignores_cancellation()
    {
        var c=AccountingOff();c["AiUsage:TimeoutSeconds"]="1";var memory=new MemoryRecordStore();
        var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire=new Handler((_,_)=>pending.Task);using var service=Service(c,memory,wire);
        try
        {
            var result=await Decide(service).WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(AiFailure.Timeout,result.Failure);Assert.Equal(1,wire.Calls);
            Assert.Equal(AiFailure.Busy,(await Decide(service,Context() with {JobId="job-other"})).Failure);Assert.Equal(1,wire.Calls);
            var execution=Assert.Single((await memory.QueryAsync(RecordPartition.Catalog("demo"),"aiExecution",null,null,100,null,default)).Items);
            Assert.Equal("pending",execution.Value.Text("state"));
            var ledger=new AiLedger(memory,TimeProvider.System);
            await Assert.ThrowsAsync<ArgumentException>(()=>ledger.RecoverSlotAsync("demo",execution.Value.Text("id"),false,default));
            await Assert.ThrowsAsync<ArgumentException>(()=>ledger.RecoverSlotAsync("demo",execution.Value.Text("id"),true,default));
            var proof=new AiExecutionRecoveryEvidence(true,true,true,"offline-approval","provider-stopped-proof",new string('a',64));
            foreach(var invalid in new[]{proof with {ProvidersStopped=false},proof with {ApiStopped=false},proof with {WorkerStopped=false},proof with {StopEvidenceSha256="invalid"},proof with {StopEvidenceReference=""}})
                await Assert.ThrowsAsync<ArgumentException>(()=>ledger.RecoverSlotAsync("demo",execution.Value.Text("id"),true,default,invalid));
            pending.TrySetResult(Response(Fixture("choice"))); // The fake provider is now quiescent before offline recovery.
            await ledger.RecoverSlotAsync("demo",execution.Value.Text("id"),true,default,proof);
            Assert.Empty((await memory.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default))!.Value.Strings("active"));
        }
        finally {pending.TrySetResult(Response(Fixture("choice")));}
    }
}
