using System.Net;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    private static async Task<AiContext> DurableContext(IRecordStore store, string stage="classify:fragment-one")
    {
        var doc=RecordJson.New("document","doc-durable-result","demo",IdentityResolver.Demo.PrincipalId,["demo"],DateTimeOffset.UtcNow);
        var job=Jobs.Create(doc,"enrichment",DateTimeOffset.UtcNow,"job-durable-result");
        job["status"]="processing";job["attempt"]=1;
        var p=RecordPartition.Catalog("demo");
        if(await store.ReadAsync(p,job.Text("id"),default) is null)await store.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(job)],default);
        return Context() with{JobId=job.Text("id"),PipelineStage=stage};
    }

    [Fact]
    public async Task Durable_result_reuses_validated_success_after_service_restart_without_provider_send()
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var first=Service(Config(),store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(first,context)).Status);
        using(var second=Service(Config(),store,handler))
        {var reused=await Decide(second,context);Assert.Equal(AiStatus.Succeeded,reused.Status);Assert.True(reused.Cached);}
        Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData("model")][InlineData("prompt")][InlineData("input")][InlineData("actor")]
    public async Task Durable_stage_changed_binding_holds_without_resending(string change)
    {
        var store=new MemoryRecordStore();var context=await DurableContext(store);var config=Config();
        var handler=new Handler((_,_)=>Task.FromResult(Response(Fixture("choice"))));
        using(var first=Service(config,store,handler))Assert.Equal(AiStatus.Succeeded,(await Decide(first,context)).Status);
        if(change=="model")config["Jev:InputUsdPerMillion"]="0.043";
        if(change=="prompt")context=context with{PromptVersion="changed-prompt"};
        if(change=="actor")
        {
            var p=RecordPartition.Catalog("demo");var authority=(await store.ReadAsync(p,AuthorityProvider.PersonId(context.Actor.PersonId),default))!;
            authority.Value["accessEpoch"]=Guid.NewGuid().ToString("N");await store.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default);
        }
        using var restarted=Service(config,store,handler);
        var error=await Assert.ThrowsAsync<DomainException>(()=>change=="input"?restarted.DecideAsync(context,"Changed billing request",Questions(),[],default):Decide(restarted,context));
        Assert.Equal("AI_RESULT_REVIEW_REQUIRED",error.Code);Assert.Equal(1,handler.Calls);
    }
}
