using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace Dse.Tests;

public class WorkbookDateSearchTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CellSource Cell(string format, string type="n", string state="available") => new("any","AZ900",null,"visible",false,false,type,"", "",format,false,state);
    private static StoredRecord Row(string id,string text,CellSource? cell=null) => new(new JsonObject{["id"]=id,["text"]=text,["sourceCell"]=JsonSerializer.SerializeToNode(cell,Json)},"etag");
    [Theory]
    [InlineData("yyyy年m月","month")][InlineData("yyyy-mm","month")][InlineData("yyyy-mm-dd","day")]
    [InlineData("yyyy\"days\"","unknown")][InlineData("mm-dd","unknown")][InlineData("yyyy","unknown")]
    [InlineData("17","month")][InlineData("14","day")][InlineData("0","unknown")][InlineData("yyyy-mm;yyyy-mm-dd","unknown")]
    public void Explicit_format_determines_precision_without_fabricating_a_day(string format,string precision)
    {
        var date=WorkbookDates.Read("2024-01-01T00:00:00",Cell(format));Assert.Equal(precision,date.Precision);
        Assert.Equal(precision=="month"?"2024-01":precision=="day"?"2024-01-01":null,date.Value);
    }
    [Fact]
    public void Month_endpoints_are_inclusive_while_day_contract_stays_unchanged()
    {
        var section=new WorkbookSection("project",["s","e"]){StartEvidenceId="s",EndEvidenceId="e",Precision="day",EndState="dated"};
        var month=WorkbookDates.Resolve(section,[Row("s","2023-07-01T00:00:00",Cell("yyyy年m月")),Row("e","2023-12-01T00:00:00",Cell("yyyy年m月"))]);
        Assert.Equal(new ExperiencePeriod("2023-07","2023-12","month",6),month.Period);Assert.Contains("WORKBOOK_PRECISION_FROM_SOURCE",month.Warnings);
        var day=WorkbookDates.Resolve(section,[Row("s","2023-07-01"),Row("e","2023-12-01")]);
        Assert.Equal(new ExperiencePeriod("2023-07-01","2023-12-01","day",5),day.Period);
        var mixed=WorkbookDates.Resolve(section,[Row("s","2023-07"),Row("e","2023-12-01")]);
        Assert.Null(mixed.Period.Months);Assert.Contains("WORKBOOK_MIXED_DATE_PRECISION",mixed.Warnings);
    }
    [Fact]
    public void Ongoing_unknown_reversed_and_unreadable_end_never_use_today_or_guess_duration()
    {
        var section=new WorkbookSection("project",["s","e"]){StartEvidenceId="s",EndEvidenceId="e",Precision="month",EndState="ongoing"};
        var rows=new[]{Row("s","2024年1月"),Row("e","継続中")};
        var ongoing=WorkbookDates.Resolve(section,rows);Assert.Equal("ongoing",ongoing.EndState);Assert.Null(ongoing.Period.End);Assert.Null(ongoing.Period.Months);
        Assert.Equal("unknown",WorkbookDates.Resolve(section with{EndState="unknown"},rows).EndState);
        Assert.Equal("unknown",WorkbookDates.Resolve(section with{EndState="dated"},rows).EndState);
        Assert.Equal("unknown",WorkbookDates.Resolve(section with{EndState="dated"},[Row("s","2025年1月"),Row("e","2024年1月")]).Period.Precision);
        Assert.Null(WorkbookDates.Read("2024-01-01",Cell("yyyy-mm-dd",state:"formula_cache_missing")).Value);
        Assert.Null(WorkbookDates.Read("2024-01-01",Cell("yyyy-mm-dd","e")).Value);
        WorkbookRules.Validate(new([section]),["s","e"],true);
        Assert.Throws<ArgumentException>(()=>WorkbookRules.Validate(new([section with{EndEvidenceId="foreign"}]),["s","e"],true));
    }
    [Fact]
    public void Date_wire_preserves_original_text_and_supplies_shared_source_format_and_display_granularity()
    {
        var row=Row("stable","2024-01-01T00:00:00",Cell("yyyy年m月")).Value;
        var original=row.ToJsonString();var(wire,map)=WorkbookWire.Prepare(new JsonObject{["evidence"]=new JsonArray(row.DeepClone())});
        Assert.Equal("2024-01-01T00:00:00",wire["cells"]!["c0"]!["text"]!.GetValue<string>());
        Assert.Equal("2024年1月",wire["cells"]!["c0"]!["displayValue"]!.GetValue<string>());
        Assert.Equal("month",wire["formats"]!["f0"]!["precision"]!.GetValue<string>());
        Assert.Equal("yyyy年m月",wire["formats"]!["f0"]!["code"]!.GetValue<string>());
        Assert.Equal(original,row.ToJsonString());Assert.Equal("stable",map["c0"]);
    }
    [Theory]
    [InlineData("夜間一次対応の手順書を作成","夜間一次対応",true)]
    [InlineData("確認順序を整理した","確認順序",true)]
    [InlineData("NoSQLの移行","SQL",false)][InlineData("JavaScript開発","Java",false)]
    [InlineData("C#開発","C",false)][InlineData("C++開発","C+",false)]
    [InlineData("SQLで照会","ＳＱＬ",true)][InlineData("CI/CDの構築","CI",true)]
    [InlineData("設計を実施","計",false)][InlineData("C","C",true)]
    public void Literal_skill_retrieval_keeps_identifier_boundaries_and_never_rewrites_source(string term,string query,bool expected) => Assert.Equal(expected,SkillTextMatch.Matches(term,query));
    [Fact]
    public void Short_queries_only_expand_current_eligible_experience_and_keep_acl_and_test_separation()
    {
        var items=new[]{new SpecificSkill("夜間一次対応の手順書を作成",null,"e","夜間一次対応の手順書を作成"){Basis="experience"},new SpecificSkill("SQLの知識",null,"k","SQLの知識"){Basis="knowledge"},new SpecificSkill("API開発は未実施",null,"n","API開発は未実施"){Basis="not_experience"}};
        var assessed=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",items),items.ToDictionary(i=>i.EvidenceId,i=>i.Quote));
        var unit=new SearchUnit("u","p","person","t","f","test","source",["s"],"active",true,Retriever.TaxonomyVersion,[],[],[],[],"ai_unconfirmed"){SpecificSkills=assessed,SyntheticTest=true};
        SearchResult Search(string q, SearchUnit? u=null) => Retriever.Search([u??unit],new([],[],[q]){SyntheticOnly=true},new("t",["s"]));
        Assert.Single(Search("夜間一次対応").Candidates);Assert.Empty(Search("SQL").Candidates);Assert.Empty(Search("API").Candidates);
        Assert.Empty(Search("夜間一次対応",unit with{ScopeIds=["other"]}).Candidates);Assert.Empty(Search("夜間一次対応",unit with{Status="withdrawn"}).Candidates);
        Assert.Empty(Search("夜間一次対応",unit with{SyntheticTest=false}).Candidates);
        Assert.Empty(Search("夜間一次対応",unit with{SpecificSkills=assessed with{Version=SpecificSkillRules.LegacyVersion}}).Candidates);
        var sql=CosmosCatalog.BuildQuery(new([],[],["夜間一次対応"]){SyntheticOnly=true},new("t",["s"])).QueryText;
        Assert.Contains("CONTAINS(t, @skillQuery0)",sql);Assert.DoesNotContain("夜間一次対応",sql);Assert.Contains("u.syntheticTest = true",sql);Assert.Contains("@scopes",sql);
        Assert.DoesNotContain("CONTAINS(t, @skillQuery0)",CosmosCatalog.BuildQuery(new([],[],["C"]),new("t",["s"])).QueryText);
    }
    private sealed class WorkbookSkills : IConcreteSkillExtractor
    {
        public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext c,string text,CancellationToken ct) => Task.FromResult(new AiResult<SpecificSkills>(AiStatus.Succeeded,
            new(SpecificSkillRules.Version,"extracted",[new("夜間一次対応の手順書を作成",null,c.Evidence[0].Id,text){Basis="experience"},new("SQLの資料を読んだ",null,c.Evidence[0].Id,text){Basis="knowledge"}]),"local-fixed",null));
    }
    [Fact]
    public async Task Old_workbook_lexical_rows_are_rechecked_without_exposing_knowledge_and_revocation_still_wins()
    {
        using var h=new IngestionHarness();
        const string text="夜間一次対応の手順書を作成。SQLの資料を読んだ。";
        var sheet=$"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"900\"><c r=\"AZ900\" t=\"inlineStr\"><is><t>{text}</t></is></c></row></sheetData></worksheet>";
        var upload=await h.Upload(bytes:OfficeFixtures.Package(true,sheet),excel:true);await h.Processor.RunOnceAsync(default);
        var doc=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),upload.Body!["documentId"]!.GetValue<string>(),default))!;
        var ai=new PipelineAi{WorkbookFactory=c=>new([new("project",c.Evidence.Select(e=>e.Id).ToArray())])};
        var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock"}).Build();
        var pipeline=new ProfilePipeline(h.Repo,h.Store,new(h.Repo),ai,ai,new(config),config,h.Clock,new WorkbookSkills());
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(doc.Value,"enrichment",h.Clock.GetUtcNow()),default),[],default);
        var units=(await new MemoryProjectionRepository(h.Store).FindAsync(new([],[],["SQL"]),IdentityResolver.Demo.Search,default)).Where(u=>u.EntityId=="demo-person-01" && u.SpecificSkills is not null).ToArray();
        Assert.Single(units);Assert.DoesNotContain("sql",units[0].NormalizedTerms);
        // Simulate a previously saved broad lexical row. Validation remains read-only.
        var old=units[0] with{NormalizedTerms=["sql",text]};
        var valid=await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,[old],default);
        Assert.Single(valid);Assert.Empty(Retriever.Search(valid,new([],[],["SQL"]),IdentityResolver.Demo.Search).Candidates);
        Assert.Single(Retriever.Search(valid,new([],[],["夜間一次対応"]),IdentityResolver.Demo.Search).Candidates);
        var current=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),doc.Value.Text("id"),default))!;current.Value["scopeIds"]=RecordJson.Array(["revoked-scope"]);
        await h.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(current.Value,current.ETag)],default);
        Assert.Empty(await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,[old],default));
    }
}
