using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dse.Tests;

public sealed class SkillContextRegressionTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static JsonArray Captured()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"DseGrowthLoop.slnx")))dir=dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Dse.Tests/Fixtures/pr68-technical-list-captured.json")))!["cases"]!.AsArray();
    }
    private static SpecificSkills PolicySkills(string raw,string text,string basis,string? contentKind)
    {
        var item=new JsonObject{["raw"]=raw,["normalizationCandidate"]=raw,["evidenceId"]="e1",["quote"]=text,["basis"]=basis,["contextPolicy"]="skill-context-v1",["contentKind"]=contentKind};
        return new JsonObject{["version"]=SpecificSkillRules.Version,["state"]="extracted",["items"]=new JsonArray(item)}.Deserialize<SpecificSkills>(Json)!;
    }
    [Theory]
    [InlineData("context","experience","通信処理の開発担当",false,"non_skill_context")]
    [InlineData(null,"experience","通信処理の開発担当",false,"skill_context_unverified")]
    [InlineData("uncertain","experience","仕様整理",false,"skill_context_unverified")]
    [InlineData("technology_list","experience","Git",false,"skill_context_conflict")]
    [InlineData("context","knowledge","通信処理の開発担当",false,"non_skill_context")]
    [InlineData("invented","experience","仕様整理",false,"skill_context_unverified")]
    [InlineData("technology_list","knowledge","Git",false,null)]
    [InlineData("skill_statement","experience","担当者と共同で通信処理の単体試験を実施した。",true,null)]
    [InlineData("skill_statement","not_experience","通信処理の単体試験は担当していない。",false,null)]
    public void Current_context_policy_requires_concrete_skill_evidence_without_role_word_filters(string? contentKind,string basis,string raw,bool eligible,string? reason)
    {
        var result=SpecificSkillRules.Assess(PolicySkills(raw,raw,basis,contentKind),new Dictionary<string,string>{{"e1",raw}},raw,"person");
        Assert.Equal(eligible,result.SearchTerms.Length==1);
        if(reason is not null)Assert.Equal(reason,Assert.Single(result.RejectedItems!).Reason);else Assert.Empty(result.RejectedItems??[]);
        // Combining and reassessing must not resurrect a quarantined role.
        var copy=JsonSerializer.Deserialize<SpecificSkills>(JsonSerializer.Serialize(result,Json),Json)!;
        var again=SpecificSkillRules.Assess(copy,new Dictionary<string,string>{{"e1",raw}},raw,"person");
        Assert.Equal(result.SearchTerms,again.SearchTerms);
    }
    [Fact]
    public void Existing_legacy_skill_is_not_automatically_rewritten()
    {
        const string text="担当者と共同で通信処理の単体試験を実施した。";
        var old=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[new(text,null,"e1",text){Basis="experience"}]),new Dictionary<string,string>{{"e1",text}},text,"person");
        Assert.Single(old.SearchTerms);Assert.Empty(old.RejectedItems??[]);
    }
    private sealed class SavedExtractor(JsonObject captured):IConcreteSkillExtractor
    {
        public int Calls;
        public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context,string text,CancellationToken ct)
        {
            Calls++;
            var items=captured["items"]!.AsArray().Select(i=>new SpecificSkill(i!["raw"]!.GetValue<string>(),i["normalizationCandidate"]?.GetValue<string>(),context.Evidence[0].Id,captured["source"]!.GetValue<string>()){Basis=i["basis"]!.GetValue<string>()}).ToArray();
            return Task.FromResult(new AiResult<SpecificSkills>(AiStatus.Succeeded,new(SpecificSkillRules.Version,"extracted",items),"saved-captured-response",null));
        }
    }
    [Theory]
    [InlineData(0,false)]
    [InlineData(1,false)]
    [InlineData(0,true)]
    [InlineData(1,true)]
    public async Task Recorded_response_through_upload_queue_worker_projection_and_search_does_not_promote_role_context(int index,bool excel)
    {
        var captured=Captured()[index]!.AsObject();var text=captured["source"]!.GetValue<string>();
        using var h=new IngestionHarness();
        var ai=new PipelineAi();var saved=new SavedExtractor(captured);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock"}).Build();
        var pipeline=new ProfilePipeline(h.Repo,h.Store,new(h.Repo),ai,ai,new(config),config,h.Clock,saved);
        var options=new IngestionOptions();
        var worker=new IngestionProcessor(h.Repo,h.Blobs,h.Queue,new(options),options,new(h.Repo),h.Clock,NullLogger<IngestionProcessor>.Instance,pipeline,h.Notifications);
        var body=excel?$"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{text}</t></is></c></row></sheetData></worksheet>":$"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";
        var upload=await h.Upload(bytes:OfficeFixtures.Package(excel,body),excel:excel);
        Assert.True(await worker.RunOnceAsync(default)); // Durable upload notification and Office extraction.
        Assert.True(await worker.RunOnceAsync(default)); // Enrichment notification, projection and commit.
        var doc=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),upload.Body!["documentId"]!.GetValue<string>(),default))!;
        var enrichment=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),doc.Value.Text("enrichmentJobId"),default))!;
        Assert.Equal("ready",enrichment.Value.Text("status"));
        var person=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!;
        var fragment=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),Assert.Single(person.Value.Strings("experienceIds")),default))!.Value;
        var result=fragment["specificSkills"]!.Deserialize<SpecificSkills>(Json)!;
        Assert.Equal(3,result.Items!.Length);Assert.All(result.Items,i=>Assert.Equal("knowledge",i.Basis));
        Assert.Empty(result.SearchTerms);if(index==1)Assert.Equal("skill_context_unverified",Assert.Single(result.RejectedItems!).Reason);
        var units=(await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit","entityId","demo-person-01",100,null,default)).Items.Select(r=>r.Value.Deserialize<SearchUnit>(Json)!).ToArray();
        var valid=await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,units,default);
        Assert.Empty(Retriever.Search(valid,new([],[],[]){SpecificSkills=["通信処理の開発担当"]},IdentityResolver.Demo.Search).Candidates);
        // Source text remains available to ordinary text search; it must not count as skill evidence.
        Assert.All(Retriever.Search(valid,new([],[],["通信処理の開発担当"]),IdentityResolver.Demo.Search).Candidates,c=>Assert.Equal(0,c.SpecificSkillMatches));
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(person.Value,"projection",h.Clock.GetUtcNow()),default),[],default);
        Assert.Equal(1,saved.Calls);
        var after=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),fragment.Text("id"),default))!.Value["specificSkills"]!.Deserialize<SpecificSkills>(Json)!;
        Assert.Empty(after.SearchTerms);if(index==1)Assert.Single(after.RejectedItems!);
        person=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!;
        var profiles=new ProfileService(h.Repo,new(h.Repo),new(h.Repo,h.Clock));
        await profiles.UpdateAsync(IdentityResolver.Demo,"demo-person-01","context-correction-key",person.ETag,
            new("本人訂正",true,"correct",[new(fragment.Text("id"),"本人が訂正した担当内容。",new(null,null,"unknown",null))]),default);
        await pipeline.CommitAsync(await pipeline.BuildAsync(enrichment.Value,default),[],default);
        var corrected=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),fragment.Text("id"),default))!.Value;
        Assert.Equal("本人が訂正した担当内容。",corrected.Text("description"));
        Assert.Equal("conflict",corrected.Text("correctionState"));Assert.NotNull(corrected["pendingExtraction"]);
    }
}
