using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class SpecificSkillsTests
{

    [Theory]
    [InlineData("experience", "C#で共同実施した。", true)]
    [InlineData("experience", "C#の検証を補助した。", true)]
    [InlineData("knowledge", "C#の資料を読んだ。", false)]
    [InlineData("not_experience", "C#で実装したい。", false)]
    [InlineData("not_experience", "C#は未経験。", false)]
    [InlineData("uncertain", "C#について記載。", false)]
    public void Ai_context_controls_experience_without_involvement_subtypes(string basis,string text,bool eligible)
    {
        var value=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[new("C#","Azure","e1",text){Basis=basis}]),new Dictionary<string,string>{{"e1",text}});
        Assert.Equal(eligible,value.SearchTerms.Length==1);Assert.DoesNotContain("azure",value.SearchTerms);Assert.Null(value.Items![0].Participation);
    }
    [Fact]
    public void Source_integrity_and_null_empty_contract_are_preserved()
    {
        SpecificSkills Value(string quote,string id="e1")=>new(SpecificSkillRules.Version,"extracted",[new("C#",null,id,quote){Basis="experience"}]);
        Assert.Throws<ArgumentException>(()=>SpecificSkillRules.Validate(Value("C#で改ざんした。"),new Dictionary<string,string>{{"e1","C#で実装した。"}}));
        Assert.Throws<ArgumentException>(()=>SpecificSkillRules.Validate(Value("C#で実装した。","wrong"),new Dictionary<string,string>{{"e1","C#で実装した。"}}));
        SpecificSkillRules.Validate(new(SpecificSkillRules.Version,"extracted",[]),new Dictionary<string,string>());
        SpecificSkillRules.Validate(new(SpecificSkillRules.Version,"unknown",null),new Dictionary<string,string>());
        Assert.Throws<ArgumentException>(()=>SpecificSkillRules.Validate(new(SpecificSkillRules.Version,"unknown",[]),new Dictionary<string,string>()));
        Assert.Throws<ArgumentException>(()=>SpecificSkillRules.Validate(new(SpecificSkillRules.Version,"extracted",null),new Dictionary<string,string>()));
    }
    [Fact]
    public void Concrete_search_is_separate_from_domain_ids_and_unapproved_normalization()
    {
        var skills=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[new("C#","Azure","e1","C#で実装した。"){Basis="experience"}]),new Dictionary<string,string>{{"e1","C#で実装した。"}});
        var unit=new SearchUnit("unit","person","person","demo","exp","架空","架空",["demo"],"active",true,"taxonomy-v1",[new("domain","software",.9)],[],["azure"],[new("e1","fixture","p1","C#で実装した。")],"ai_unconfirmed"){SkillIds=["software"],SpecificSkills=skills};
        SearchRequest Query(string v)=>new([],[],[]){SpecificSkills=[v]};
        Assert.Single(Retriever.Search([unit],Query("Ｃ＃"),new("demo",["demo"])).Candidates);
        Assert.Empty(Retriever.Search([unit],Query("software"),new("demo",["demo"])).Candidates);
        Assert.Empty(Retriever.Search([unit],Query("Azure"),new("demo",["demo"])).Candidates);
    }
    private sealed class Extractor(string quote, bool corruptSibling = false, string? evidenceId = null) : IConcreteSkillExtractor
    {
        public int Calls;
        public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context,string text,CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiResult<SpecificSkills>(AiStatus.Succeeded,new(SpecificSkillRules.Version,"extracted",[new("C#","C#",evidenceId ?? context.Evidence[0].Id,quote){Basis="experience"}, ..(corruptSibling ? new SpecificSkill[] { new("Azure",null,"invented","Azureで構築した。"){Basis="experience"} } : [])]),"skill-run-test",null));
        }
    }
    [Fact]
    public async Task Actual_ingestion_stores_evidence_backed_skills_and_reuses_them_on_projection()
    {
        using var h=new IngestionHarness(); const string text="C#で回帰試験を実施した。";
        var upload=await h.Upload(bytes:OfficeFixtures.Package(body:$"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>")); await h.Processor.RunOnceAsync(default);
        var doc=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),upload.Body!["documentId"]!.GetValue<string>(),default))!;
        var ai=new PipelineAi();var extractor=new Extractor(text,corruptSibling:true);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Data:Mode"]="Demo",["Ai:Mode"]="Mock" }).Build();
        var pipeline=new ProfilePipeline(h.Repo,h.Store,new(h.Repo),ai,ai,new(config),config,h.Clock,extractor);
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(doc.Value,"enrichment",h.Clock.GetUtcNow()),default),[],default);
        var person=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!;
        var exp=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),person.Value.Strings("experienceIds")[0],default))!;
        var result=exp.Value["specificSkills"]!.Deserialize<SpecificSkills>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(["c#"],result.SearchTerms);Assert.Equal("unknown_evidence",Assert.Single(result.RejectedItems!).Reason);Assert.Equal("skill-run-test",exp.Value.Text("skillRunId"));
        Assert.Equal(1,extractor.Calls);
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(person.Value,"projection",h.Clock.GetUtcNow()),default),[],default);
        Assert.Equal(1,extractor.Calls);
        var units=(await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit",null,null,100,null,default)).Items.Select(r=>r.Value.Deserialize<SearchUnit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        var valid=await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,units,default);
        Assert.Single(Retriever.Search(valid,new([],[],[]) { SpecificSkills=["C#"] },IdentityResolver.Demo.Search).Candidates);
        Assert.All(valid.Where(u=>u.EntityId=="demo-person-01"),u=>Assert.Null(u.SkillIds));
        var service=new ProfileService(h.Repo,new(h.Repo),new(h.Repo,h.Clock));
        person=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!;
        await service.UpdateAsync(IdentityResolver.Demo,"demo-person-01","skill-correct-test",person.ETag,new("架空",true,"correct",[new(exp.Value.Text("id"),"本人の担当は会議案内のみ。",new(null,null,"unknown",null))]),default);
        Assert.Equal("not_evaluated",(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),exp.Value.Text("id"),default))!.Value["specificSkills"]!["state"]!.GetValue<string>());
        Assert.Empty(await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,valid.Where(u=>u.EntityId=="demo-person-01"),default));
    }

    [Fact]
    public async Task Imported_technology_columns_do_not_bypass_the_same_evidence_and_involvement_contract()
    {
        using var h=new IngestionHarness(); var cells=InternalResumeFixture.ShiftedCells(23,1);
        cells["F24"]="≪担当業務≫C#で回帰試験を共同実施した。";cells["I23"]="C#";cells["L23"]="Azure";
        var upload=await h.Upload(bytes:InternalResumeFixture.Package(cells),excel:true);await h.Processor.RunOnceAsync(default);
        var doc=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),upload.Body!["documentId"]!.GetValue<string>(),default))!;
        var sourceRows=await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"evidenceSpan","documentId",doc.Value.Text("id"),100,null,default);
        var reference=sourceRows.Items.Single(r=>r.Value.Text("text").Contains("共同実施した",StringComparison.Ordinal)).Value.Text("id");
        var source=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),reference,default))!.Value.Text("text");
        var quote=System.Text.RegularExpressions.Regex.Matches(source,@"[^。！？\r\n]+[。！？]?")
            .Single(m=>m.Value.Contains("共同実施した",StringComparison.Ordinal)).Value.Trim();
        var ai=new PipelineAi();var extractor=new Extractor(quote,evidenceId:reference);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Data:Mode"]="Demo",["Ai:Mode"]="Mock" }).Build();
        var pipeline=new ProfilePipeline(h.Repo,h.Store,new(h.Repo),ai,ai,new(config),config,h.Clock,extractor);
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(doc.Value,"enrichment",h.Clock.GetUtcNow()),default),[],default);
        var person=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!.Value;
        var exp=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),Assert.Single(person.Strings("experienceIds")),default))!.Value;
        Assert.Equal("extracted",person["specificSkills"]!["state"]!.GetValue<string>());
        Assert.Equal("C#",Assert.Single(person["specificSkills"]!["items"]!.AsArray())!["raw"]!.GetValue<string>());
        var skill=Assert.Single(exp["specificSkills"]!["items"]!.AsArray())!;
        Assert.Equal("experience",skill["basis"]!.GetValue<string>());Assert.Null(skill["participation"]);Assert.Equal(reference,skill["evidenceId"]!.GetValue<string>());
        var units=(await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit","entityId","demo-person-01",100,null,default)).Items.Select(r=>r.Value.Deserialize<SearchUnit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Single(Retriever.Search(units,new([],[],[]) { SpecificSkills=["C#"] },IdentityResolver.Demo.Search).Candidates);
        Assert.Empty(Retriever.Search(units,new([],[],[]) { SpecificSkills=["Azure"] },IdentityResolver.Demo.Search).Candidates);
        Assert.Contains(units,u=>u.NormalizedTerms.Contains("azure")); // lexical hints remain explicitly separate
    }
}
