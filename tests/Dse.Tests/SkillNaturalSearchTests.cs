using Dse.Domain;
using Dse.Infrastructure;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public sealed class SkillNaturalSearchTests
{
    [Fact]
    public async Task Guarded_AiService_codec_budget_and_cache_are_used_without_any_external_transport()
    {
        var config=SkillSemanticTestRuntime.Config();config["Data:LocalPath"]="/tmp/dse-workflow-wire-fixture";config["AiUsage:MaxOutputTokens"]="3000";config["Jev:OutputLimit"]="3000";
        config["KeyVault:Uri"]="https://dummy.vault.azure.net/";config["Jev:SecretName"]="dummy-key";config["Jev:SecretVersion"]=new string('a',32);
        foreach(var pair in new Dictionary<string,string>{{"Endpoint","https://dummy.openai.azure.com/"},{"Deployment","dummy"},{"ModelId","dummy-2026-09-28"},{"ModelVersion","2026-09-28"},{"Region","test"},{"Sku","GlobalStandard"},{"InputUsdPerMillion","1"},{"OutputUsdPerMillion","1"},{"OutputLimit","3000"}})config["Foundry:"+pair.Key]=pair.Value;
        var f=await SkillDictionaryFixture.Create();var wire=new Wire();using var root=DemoWorkflowTests.Root(f.Store,suppliedConfiguration:config,transport:wire);
        var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);var dictionary=context.Services.GetRequiredService<SkillDictionaryService>();
        wire.Selected=await Approve(dictionary,context.Actor,"Git");await Approve(dictionary,context.Actor,"Git",wire.Selected);
        var service=context.Services.GetRequiredService<NaturalSearchService>();NaturalSearchResponse? response=null;
        for(var i=0;i<2;i++)await service.SearchAndConsumeAsync(new("バージョン管理の知識が記載された資料を探す"),context.Actor,(r,_)=>{response=r;return Task.CompletedTask;},default);
        Assert.Equal(2,response!.Primary.Candidates.Length);Assert.Equal("succeeded",response.RerankingStatus);Assert.Equal(2,wire.Calls);
        var runs=await context.Services.GetRequiredService<IPrimaryRepository>().QueryAsync(RecordPartition.Catalog("demo"),"aiRun",null,null,100,null,default);
        Assert.Equal(4,runs.Items.Length);Assert.Equal(2,runs.Items.Count(r=>r.Value["trace"]?["cachedFromRunId"] is not null));
    }
    private sealed class Wire:IAiTransport
    {
        public string Selected="";public int Calls;
        public async Task<AiWireResponse> SendAsync(AiModel model,JsonObject body,int maximum,CancellationToken ct)
        {
            Calls++;var result=await MockAiTransport.Instance.SendAsync(model,body,maximum,ct);
            if(model.Provider=="Jev")foreach(var pair in body["questions"]!.AsObject().Where(p=>p.Value?["type"]?.GetValue<string>()=="choice"))
            {
                var selected=pair.Key=="domain"?"software":Selected;var probabilities=new JsonObject();foreach(var option in pair.Value!["criteria"]!.AsObject())probabilities[option.Key]=option.Key==selected?1d:0d;
                result.Body["answers"]![pair.Key]=new JsonObject{["type"]="choice",["choice"]=selected,["probabilities"]=probabilities,["confidence"]=1d};
            }
            return result;
        }
    }
    private sealed class Ai(string selected):IDecisionProvider,IReranker
    {
        public int Converted,Ranked;public ChoiceQuestion[] Questions=[];public Func<Task>? BeforeReturn;
        public Task<AiResult<Decisions>> DecideAsync(AiContext context,string query,ChoiceQuestion[] choices,NoulQuestion[] nouls,CancellationToken ct)
        {
            Converted++;Questions=choices;
            var answers=choices.ToDictionary(q=>q.Id,q=>new ChoiceAnswer(q.Id=="domain"?"software":selected,q.Options.Keys.ToDictionary(id=>id,id=>id==(q.Id=="domain"?"software":selected)?1d:0d),1));
            return Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded,new(answers,new Dictionary<string,NoulAnswer>()),"recorded-conversion",null));
        }
        public async Task<AiResult<Ranking>> RerankAsync(AiContext context,string query,CancellationToken ct)
        {
            Ranked++;if(BeforeReturn is not null)await BeforeReturn();
            return new(AiStatus.Succeeded,new(context.Candidates.Select(u=>new RankedCandidate(u.Id,u.SourceRefs[0].Text,[u.SourceRefs[0].Id])).ToArray()),"recorded-rerank",null);
        }
    }
    private static async Task<string> Approve(SkillDictionaryService s,ActorAccess actor,string raw,string? target=null)
    {
        var c=(await s.ListUnderFenceAsync(actor,null,default))["items"]!.AsArray().Select(n=>n!.AsObject()).First(n=>n["item"]?["raw"]?.GetValue<string>()==raw&&n.Text("state")=="pending");var d=await s.ReadUnderFenceAsync(actor,null,default);
        var result=await s.ReviewAsync(actor,c.Text("id"),Guid.NewGuid().ToString("N"),c.Text("etag"),new(target is null?"new":"alias","架空原文の確認",d.Number("version"),d.Text("etag"),TargetId:target,Name:target is null?raw:null,Definition:target is null?raw+"の知識記載":null,Boundary:target is null?"本人経験として扱わない":null),default);
        return result.Body!["conceptId"]!.GetValue<string>();
    }
    private static NaturalSearchService Service(DemoWorkflowContext c,Ai ai)=>new(c.Services.GetRequiredService<IProjectionRepository>(),c.Services.GetRequiredService<IPrimaryRepository>(),c.Services.GetRequiredService<SourceGuard>(),c.Services.GetRequiredService<AccessFence>(),ai,ai,c.Services.GetRequiredService<IConfiguration>(),TimeProvider.System);

    [Fact]
    public async Task Natural_AI_conversion_uses_the_approved_catalog_and_normal_ranker_with_current_citations()
    {
        var f=await SkillDictionaryFixture.Create();using var root=DemoWorkflowTests.Root(f.Store);var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var dictionary=context.Services.GetRequiredService<SkillDictionaryService>();var git=await Approve(dictionary,context.Actor,"Git");await Approve(dictionary,context.Actor,"Git",git);await Approve(dictionary,context.Actor,"SQLite");
        var ai=new Ai(git);NaturalSearchResponse? response=null;
        await Service(context,ai).SearchAndConsumeAsync(new("バージョン管理の知識が記載された資料を探す"),context.Actor,(r,_)=>{response=r;return Task.CompletedTask;},default);
        Assert.Equal(new[]{git},response!.Conditions.SkillIds);Assert.Equal(2,response.Conditions.SkillDictionaryVersion);
        Assert.Equal(2,response.Primary.Candidates.Length);Assert.Equal(1,ai.Converted);Assert.Equal(1,ai.Ranked);Assert.Contains(ai.Questions,q=>q.Options.ContainsKey(git));
        Assert.All(response.Primary.Candidates,c=>{Assert.Equal("document",c.EntityType);Assert.NotEmpty(c.Units[0].SourceRefs);Assert.Contains("skillDictionary",c.Routes);});
    }

    [Fact]
    public async Task Explicit_old_version_stays_fixed_and_retraction_during_AI_cannot_restore_the_quote()
    {
        var f=await SkillDictionaryFixture.Create();using var root=DemoWorkflowTests.Root(f.Store);var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var dictionary=context.Services.GetRequiredService<SkillDictionaryService>();var git=await Approve(dictionary,context.Actor,"Git");await Approve(dictionary,context.Actor,"Git",git);await Approve(dictionary,context.Actor,"SQLite");
        var ai=new Ai(git);var service=Service(context,ai);var conditions=new SearchRequest([],[],[]){SkillIds=[git],SkillDictionaryVersion=1};NaturalSearchResponse? response=null;
        await service.SearchAndConsumeAsync(new("バージョン管理の資料",conditions),context.Actor,(r,_)=>{response=r;return Task.CompletedTask;},default);
        Assert.Equal(2,response!.Primary.Candidates.Length);Assert.Equal(0,ai.Converted);
        ai.BeforeReturn=async()=>{var store=context.Services.GetRequiredService<IRecordStore>();var p=RecordPartition.Catalog("demo");var doc=(await store.ReadAsync(p,"doc-technical-list-a-distinct",default))!;doc.Value["activeVersion"]=null;await store.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);};
        await service.SearchAndConsumeAsync(new("バージョン管理の資料",conditions),context.Actor,(r,_)=>{response=r;return Task.CompletedTask;},default);
        Assert.Empty(response!.Primary.Candidates);Assert.Empty(response.Assessments);Assert.Equal("source_revoked",response.RerankingStatus);
    }
}
