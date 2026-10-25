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
    [InlineData("C#で回帰試験を実施した。", "performed", true)]
    [InlineData("C#で回帰試験を共同実施した。", "joint", true)]
    [InlineData("C#で回帰試験を実施した。", "joint", false)]
    [InlineData("C#で回帰試験の補助を実施した。", "assisted", true)]
    [InlineData("C#で回帰試験を共同実施した。", "performed", false)]
    [InlineData("C#を実装したい。", "performed", false)]
    [InlineData("C#を実装したい。", "aspiration", true)]
    [InlineData("C#で実装した人に相談した。", "performed", false)]
    [InlineData("C#で実装した人に相談した。", "consultation", true)]
    [InlineData("同僚がC#で実装した。", "performed", false)]
    [InlineData("同僚がC#で実装した。", "other_person", true)]
    [InlineData("C#を担当していない。", "performed", false)]
    [InlineData("C#を担当していない。", "negated", true)]
    [InlineData("技術欄：C#、Azure", "performed", false)]
    [InlineData("技術欄：C#、Azure", "unclear", true)]
    public void Ownership_and_context_gate_personal_tags(string text, string participation, bool valid)
    {
        var result = new SpecificSkills(SpecificSkillRules.Version, "extracted", [new("C#", "sales", "e1", text, participation)]);
        if (!valid) { Assert.Throws<ArgumentException>(() => SpecificSkillRules.Validate(result, new Dictionary<string, string> { ["e1"] = text })); return; }
        SpecificSkillRules.Validate(result, new Dictionary<string, string> { ["e1"] = text });
        Assert.Equal(SpecificSkillRules.IsExperience(result.Items![0]) ? new[] { "c#" } : [], result.SearchTerms);
        Assert.DoesNotContain("sales", result.SearchTerms); // unapproved normalization proposal is not indexed
    }

    [Fact]
    public void Quotes_cannot_cherry_pick_and_missing_empty_unknown_remain_distinct()
    {
        const string source = "C#で実装した人に相談した。";
        Assert.Throws<ArgumentException>(() => SpecificSkillRules.Validate(new(SpecificSkillRules.Version, "extracted", [new("C#", null, "e1", "C#で実装した", "performed")]), new Dictionary<string,string> { ["e1"] = source }));
        SpecificSkillRules.Validate(new(SpecificSkillRules.Version, "extracted", []), new Dictionary<string,string>());
        SpecificSkillRules.Validate(new(SpecificSkillRules.Version, "unknown", null), new Dictionary<string,string>());
        Assert.Throws<ArgumentException>(() => SpecificSkillRules.Validate(new(SpecificSkillRules.Version, "unknown", []), new Dictionary<string,string>()));
        Assert.Throws<ArgumentException>(() => SpecificSkillRules.Validate(new(SpecificSkillRules.Version, "extracted", null), new Dictionary<string,string>()));
        var positive = new SpecificSkills(SpecificSkillRules.Version, "extracted", [new("C#", "C#", "e1", "C#で実装した。", "performed")]);
        SpecificSkillRules.Validate(positive, new Dictionary<string,string> { ["e1"] = "C#で実装した。Azureを学習したい。" });
        Assert.Throws<ArgumentException>(() => SpecificSkillRules.Validate(positive, new Dictionary<string,string> { ["e1"] = "他人の資料。C#で実装した。本人の担当は案内のみ。" }));
    }

    [Fact]
    public void Concrete_search_is_separate_from_legacy_domain_ids_and_lexical_hints()
    {
        var unit = new SearchUnit("unit", "person", "person", "demo", "exp", "架空", "架空", ["demo"], "active", true, "taxonomy-v1", [new("domain","software",.9)], [], ["azure"], [new("e1","fixture","p1","C#で実装した。")], "ai_unconfirmed")
        { SkillIds = ["software"], SpecificSkills = new(SpecificSkillRules.Version,"extracted",[new("C#", "Azure", "e1","C#で実装した。","performed")]) };
        SearchRequest Skill(string text) => new([], [], []) { SpecificSkills = [text] };
        var hit=Retriever.Search([unit],Skill("Ｃ＃"),new("demo",["demo"]));
        Assert.Contains("specificSkills",Assert.Single(hit.Candidates).Routes);
        Assert.Equal(0,hit.Candidates[0].TermMatches);Assert.Equal(1,hit.Candidates[0].SpecificSkillMatches);
        Assert.Contains("specificSkillMatches",JsonSerializer.Serialize(hit.Candidates[0],new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.DoesNotContain("specificSkillMatches",JsonSerializer.Serialize(hit.Candidates[0] with { SpecificSkillMatches=0 },new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Empty(Retriever.Search([unit],Skill("software"),new("demo",["demo"])).Candidates);
        Assert.Empty(Retriever.Search([unit],Skill("Azure"),new("demo",["demo"])).Candidates);
        Assert.Empty(Retriever.Search([unit with { SpecificSkills = null }],Skill("C#"),new("demo",["demo"])).Candidates);
        var query=CosmosCatalog.BuildQuery(Skill("C#"),new("demo",["demo"]));
        Assert.Contains("u.specificSkills.searchTerms",query.QueryText); Assert.DoesNotContain("skillIds",query.QueryText);
    }

    private sealed class Extractor(string quote, string participation = "performed", bool corruptSibling = false) : IConcreteSkillExtractor
    {
        public int Calls;
        public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context,string text,CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiResult<SpecificSkills>(AiStatus.Succeeded,new(SpecificSkillRules.Version,"extracted",[new("C#","C#",context.Evidence[0].Id,quote,participation), ..(corruptSibling ? new SpecificSkill[] { new("Azure",null,"invented","Azureで構築した。","performed") } : [])]),"skill-run-test",null));
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
        var reference=Assert.Single(doc.Value["resumeImport"]!["projects"]!.AsArray())!["evidenceIds"]![0]!.GetValue<string>();
        var source=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),reference,default))!.Value.Text("text");
        var quote=System.Text.RegularExpressions.Regex.Matches(source,@"[^。！？\r\n]+[。！？]?")
            .Single(m=>m.Value.Contains("共同実施した",StringComparison.Ordinal)).Value.Trim();
        var ai=new PipelineAi();var extractor=new Extractor(quote,"joint");
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Data:Mode"]="Demo",["Ai:Mode"]="Mock" }).Build();
        var pipeline=new ProfilePipeline(h.Repo,h.Store,new(h.Repo),ai,ai,new(config),config,h.Clock,extractor);
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(doc.Value,"enrichment",h.Clock.GetUtcNow()),default),[],default);
        var person=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!.Value;
        var exp=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),Assert.Single(person.Strings("experienceIds")),default))!.Value;
        Assert.Equal("extracted",person["specificSkills"]!["state"]!.GetValue<string>());
        Assert.Equal("C#",Assert.Single(person["specificSkills"]!["items"]!.AsArray())!["raw"]!.GetValue<string>());
        var skill=Assert.Single(exp["specificSkills"]!["items"]!.AsArray())!;
        Assert.Equal("joint",skill["participation"]!.GetValue<string>());Assert.Equal(reference,skill["evidenceId"]!.GetValue<string>());
        var units=(await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit","entityId","demo-person-01",100,null,default)).Items.Select(r=>r.Value.Deserialize<SearchUnit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Single(Retriever.Search(units,new([],[],[]) { SpecificSkills=["C#"] },IdentityResolver.Demo.Search).Candidates);
        Assert.Empty(Retriever.Search(units,new([],[],[]) { SpecificSkills=["Azure"] },IdentityResolver.Demo.Search).Candidates);
        Assert.Contains(units,u=>u.NormalizedTerms.Contains("azure")); // lexical hints remain explicitly separate
    }
}
