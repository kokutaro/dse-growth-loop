using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dse.Tests;

// Timing/fault replay through the real product worker, pipeline, AiService and gates. No sockets/AI.
public sealed class PipelineDeadlineReplayTests
{
    private sealed class DelayedRepository(IPrimaryRepository inner,ManualPipelineClock clock):IPrimaryRepository
    {
        public bool DelayPreparationOnce;public Func<bool>? BlockPreparation;private int delayed;
        public async Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
        {
            if((DelayPreparationOnce||BlockPreparation?.Invoke()==true)&&FencedReadPhase.IsActive&&Interlocked.Exchange(ref delayed,1)==0)await Task.Delay(TimeSpan.FromSeconds(121),clock,ct);
            else await Task.Delay(TimeSpan.FromMilliseconds(100),clock,ct);
            return await inner.ReadAsync(p,id,ct);
        }
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite>w,CancellationToken ct)=>inner.CommitAsync(p,w,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,size,cursor,ct);
    }
    private sealed class ReplayQueue(QueueNotice notice):IIngestionQueue
    {
        private bool received;public bool FailRenewal;public int Renewals,Deletes,Poison;
        public Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility,CancellationToken ct)=>Task.FromResult(received?null:Take());
        private QueueDelivery Take(){received=true;return new("fixture-delivery","fixture-receipt",JsonSerializer.Serialize(notice));}
        public Task<QueueDelivery> RenewAsync(QueueDelivery d,TimeSpan visibility,CancellationToken ct){ct.ThrowIfCancellationRequested();Renewals++;if(FailRenewal)throw new IOException("fixture renewal failure");return Task.FromResult(d);}
        public Task SendAsync(QueueNotice n,CancellationToken ct,bool poison=false){if(poison)Poison++;return Task.CompletedTask;}
        public Task DeleteAsync(QueueDelivery d,CancellationToken ct){Deletes++;return Task.CompletedTask;}
    }
    private sealed class ReplayTransport(ManualPipelineClock clock,string mode):IDispatchTrackingAiTransport
    {
        public int Calls;public TaskCompletionSource<AiWireResponse> Pending {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AiWireResponse> SendAsync(AiModel m,JsonObject b,int max,CancellationToken ct)=>throw new InvalidOperationException("Dispatch tracking required");
        public async Task<AiWireResponse> SendAsync(AiModel model,JsonObject body,int max,CancellationToken ct,AiDispatchState dispatch)
        {
            dispatch.MarkDispatched(ct);Calls++;
            if(mode=="unknown")return await Pending.Task;
            await Task.Delay(TimeSpan.FromSeconds(mode=="ai_timeout"?61:5),clock,ct);
            var response=await MockAiTransport.Instance.SendAsync(model,body,max,ct);
            if(body["response_format"]?["json_schema"]?["name"]?.GetValue<string>()=="structureWorkbook")
            {
                var input=JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;var cells=input["cells"]!.AsObject();
                var groups=cells.GroupBy(p=>{var row=int.Parse(Regex.Match(p.Value!["at"]!.GetValue<string>(),"![A-Z]+([0-9]+)").Groups[1].Value);return row is >=23 and <=26?"one":row is >=27 and <=30?"two":"context";});
                var sections=new JsonArray(groups.Select(g=>(JsonNode)new JsonObject{["kind"]=g.Key=="context"?"context":"project",["evidenceIds"]=RecordJson.Array(g.Select(x=>x.Key)),["startEvidenceId"]=null,["endEvidenceId"]=null,["precision"]="unknown",["endState"]="unknown"}).ToArray());
                response.Body["choices"]![0]!["message"]!["content"]=new JsonObject{["sections"]=sections}.ToJsonString();
            }
            return response with{Usage=new(100,50,false)};
        }
    }
    private sealed class CapturedLog<T>:ILogger<T>
    {
        public readonly ConcurrentBag<string> Lines=[];
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter)=>Lines.Add(formatter(state,error));
    }
    private static void LiveFixtureConfiguration(SyntheticHarness h)
    {
        foreach(var p in new Dictionary<string,string>{["Ai:Mode"]="Live",["AiUsage:LiveOptIn"]="true",["AiUsage:Stopped"]="false",["AiUsage:AccountingMode"]="Disabled",["AiUsage:MaxInputTokens"]="10240",["AiUsage:MaxOutputTokens"]="4096",["AiUsage:TimeoutSeconds"]="60",["AiUsage:MaxAttempts"]="1",
            ["Foundry:Endpoint"]="https://dummy.openai.azure.com/",["Foundry:Deployment"]="dummy-deployment",["Foundry:ModelId"]="dummy-model-2026-09-28",["Foundry:ModelVersion"]="2026-09-28",["Foundry:Region"]="test",["Foundry:Sku"]="GlobalStandard",["Foundry:InputUsdPerMillion"]="0.2",["Foundry:OutputUsdPerMillion"]="1.2",["Foundry:OutputLimit"]="4096",["Jev:Model"]="jev-1.13.0",["Jev:InputUsdPerMillion"]="0.042",["Jev:OutputUsdPerMillion"]="0",["Jev:OutputLimit"]="4096"})h.Config[p.Key]=p.Value;
    }
    [Theory]
    [InlineData("success",7,false)]
    [InlineData("job_deadline",0,false)]
    [InlineData("cancel_after_1",1,false)]
    [InlineData("cancel_after_2",2,false)]
    [InlineData("cancel_after_3",3,false)]
    [InlineData("cancel_after_4",4,false)]
    [InlineData("cancel_after_5",5,false)]
    [InlineData("cancel_after_6",6,false)]

    [InlineData("lease_renewal",0,false)]
    [InlineData("ai_timeout",1,true)]
    [InlineData("unknown",1,true)]
    public async Task Real_120_second_worker_60_second_AI_and_seven_call_pipeline_preserve_old_generation_on_failure(string mode,int calls,bool closed)
    {
        using var h=new SyntheticHarness();var person=await h.Person();
        var bytes=WorkbookTextFixture.Package(new Dictionary<string,string>{["F23"]="C#で回帰試験を実施した。",["F27"]="別案件でC#で回帰試験を実施した。",["D35"]="自己PR：Azureを利用したい。"});
        var upload=await h.Upload(person,bytes);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        var original=await h.Read(upload.Body!["documentId"]!.GetValue<string>());var oldHead=(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString();
        var oldProjection=(await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit","entityId",person.Text("id"),100,null,default)).Items.Select(x=>(x.Value.Text("id"),x.ETag)).ToArray();
        Assert.NotEmpty(oldProjection);LiveFixtureConfiguration(h);
        var generations=new ReextractionGenerations(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System);
        var quote=await generations.EstimateAsync(SyntheticHarness.Operator,person.Text("id"),original.Text("id"),default);
        var accepted=await generations.StartAsync(SyntheticHarness.Operator,person.Text("id"),original.Text("id"),new(quote.Text("estimateId"),quote["maximumUsd"]!.GetValue<decimal>(),"固定fixtureの期限再生"),Guid.NewGuid().ToString("N"),quote.Text("sourceEtag"),default);
        var genId=accepted.Body!["generationId"]!.GetValue<string>();await h.Processor.RunOnceAsync(default);var generation=await h.Read(genId);var job=await h.Read(generation.Text("enrichmentJobId"));
        var oldBudget=(await h.Read(SyntheticImportBudget.Id(original.Text("id")))).ToJsonString();
        var clock=new ManualPipelineClock();var repo=new DelayedRepository(h.Repo,clock){DelayPreparationOnce=mode is "job_deadline" or "lease_renewal"};var fence=new AccessFence(repo);var transport=new ReplayTransport(clock,mode);var logs=new CapturedLog<IngestionProcessor>();
        if(mode.StartsWith("cancel_after_")){var completed=int.Parse(mode["cancel_after_".Length..]);repo.BlockPreparation=()=>transport.Calls==completed;}
        using var ai=new AiService(h.Config,repo,new(repo,h.Config),fence,new(h.Store,clock),transport,clock);
        var pipeline=new ProfilePipeline(repo,h.Store,fence,ai,ai,new(h.Config),h.Config,clock,ai,ai);var queue=new ReplayQueue(new("demo",job.Text("id"),job.Number("aggregateVersion"))){FailRenewal=mode=="lease_renewal"};
        var processor=new IngestionProcessor(repo,h.Blobs,queue,new OfficeExtractor(new()),new(){ProcessingSeconds=120,Attempts=1},fence,clock,logs,pipeline);
        var work=processor.RunOnceAsync(default);
        try
        {
            await clock.Drive(work);var result=await h.Read(job.Text("id"));var final=await h.Read(genId);Assert.True(calls==transport.Calls,$"{mode}: expected {calls} calls, actual {transport.Calls}, job={result.Text("status")}/{result.Text("lastErrorCode")}");
            if(mode=="success") { Assert.Equal("ready",result.Text("status"));Assert.Equal("published",final.Text("generationState"));Assert.NotEqual(oldHead,(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString()); }
            else
            {
                Assert.Equal(mode is "ai_timeout" or "unknown"?"needs_review":"failed",result.Text("status"));Assert.Equal("failed",final.Text("generationState"));Assert.Equal(oldHead,(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString());
                Assert.Equal(oldProjection,(await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit","entityId",person.Text("id"),100,null,default)).Items.Select(x=>(x.Value.Text("id"),x.ETag)).ToArray());
                if(mode=="job_deadline")Assert.Equal("PROCESSING_TIMEOUT",result.Text("lastErrorCode"));
                if(mode=="lease_renewal")Assert.Equal("LEASE_RENEWAL_FAILED",result.Text("lastErrorCode"));
            }
            Assert.Equal(closed,(await h.Read("system-access-fence"))["holder"] is not null);Assert.Equal(oldBudget,(await h.Read(SyntheticImportBudget.Id(original.Text("id")))).ToJsonString());
            Assert.Equal(calls,(await h.Read(SyntheticImportBudget.Id(genId)))["dispatches"]!.AsObject().Count);
            var gate=await h.Store.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default);Assert.Equal(closed?1:0,gate?.Value.Strings("active").Length??0);
            Assert.Contains(logs.Lines,l=>l.StartsWith("PipelineSummary ")&&l.Contains(job.Text("id"))&&l.Contains(genId));
            Assert.DoesNotContain(logs.Lines,l=>l.Contains("C#で回帰試験")||l.Contains("自己PR")||l.Contains("dummy-secret"));
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;
            var output=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output,"pipeline-deadline-"+mode+".json"),new JsonObject{["evidence"]="fixture_virtual_time_no_network",["jobSeconds"]=120,["aiSeconds"]=60,["maximumCalls"]=7,["actualFixtureCalls"]=calls,["job"]=result.DeepClone(),["summary"]=JsonNode.Parse(logs.Lines.Single(x=>x.StartsWith("PipelineSummary ")).Substring("PipelineSummary ".Length))}.ToJsonString());
        }
        finally {transport.Pending.TrySetResult(new(new(){},new(0,0,false)));}
    }
}
