using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

// This canned grouping knows only this test fixture. It is not a production template detector.
public sealed class FixtureWorkbookSections(IPrimaryRepository repo) : IWorkbookStructurer
{
    public async Task<AiResult<WorkbookStructure>> StructureWorkbookAsync(AiContext c, CancellationToken ct)
    {
        var ids = new Dictionary<string,List<string>> { ["one"]=[],["two"]=[],["context"]=[] };
        foreach(var e in c.Evidence)
        {
            var s=(await repo.ReadAsync(RecordPartition.Catalog(c.Actor.TenantId),e.Id,ct))!.Value;
            var cell=s["sourceCell"]?.Deserialize<CellSource>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var row=cell is null?0:int.Parse(new string(cell.Address.SkipWhile(char.IsAsciiLetter).ToArray()));
            var group=cell?.Sheet==InternalResumeSheet.SheetName&&row is >=23 and <=26?"one":cell?.Sheet==InternalResumeSheet.SheetName&&row is >=27 and <=30?"two":"context";
            ids[group].Add(e.Id);
        }
        return new(AiStatus.Succeeded,new(ids.Where(p=>p.Value.Count>0).Select(p=>new WorkbookSection(p.Key=="context"?"context":"project",p.Value.ToArray())).ToArray()),"fixture-workbook",null);
    }
}

public sealed class WorkbookCaptureTests
{
    [Fact]
    public void Compact_wire_roundtrips_all_text_and_source_positions_without_executing_embedded_instructions()
    {
        const string text="共同で確認した。末尾は未実施。\n\"},\"cells\":{} </script>";
        var source=new JsonObject { ["text"]=text,["workbookPolicy"]=WorkbookRules.CellSkillPrompt,["sectionKind"]="context",
            ["evidence"]=new JsonArray(new JsonObject { ["id"]="stable-source-id",["text"]=text,["documentVersionId"]="v1",["sourceVersion"]=1,
                ["sourceCell"]=JsonSerializer.SerializeToNode(new CellSource("任意のシート","AZ500","AZ500:BA500","hidden",true,false,"inlineStr","",text,"0",false,"available"),new JsonSerializerOptions(JsonSerializerDefaults.Web)) }),["candidates"]=new JsonArray() };
        var (wire,map)=WorkbookWire.Prepare(source);
        var parsed=JsonNode.Parse(WorkbookWire.Serialize(wire))!;
        Assert.Equal(text,parsed["cells"]!["c0"]!["text"]!.GetValue<string>());
        Assert.Equal("stable-source-id",map["c0"]);Assert.Null(parsed["evidence"]);
        Assert.Contains("AZ500:BA500",parsed["cells"]!["c0"]!["at"]!.GetValue<string>());
        Assert.Contains("hidden row",parsed["cells"]!["c0"]!["at"]!.GetValue<string>());
        Assert.Equal(text,source["text"]!.GetValue<string>()); // original input is never rewritten
        var bad=new SpecificSkills(SpecificSkillRules.Version,"extracted",[new("共同",null,"stable-source-id",text){Basis="experience"}]);
        var restored=WorkbookWire.Restore(bad,map);
        Assert.StartsWith("unmapped-",restored.Items![0].EvidenceId); // a guessed storage ID is not a sent wire ID
    }
    [Theory]
    [InlineData("D35")][InlineData("AZ501")][InlineData("XFD1048576")]
    public void Cell_position_and_template_name_do_not_discard_any_text(string address)
    {
        var bytes=OfficeFixtures.Package(true, $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"501\" hidden=\"1\"><c r=\"{address}\" t=\"inlineStr\"><is><t>自己PR：共同で検証した。希望は未実施。</t></is></c></row></sheetData></worksheet>");
        var result=new OfficeExtractor(new()).Extract(bytes,"test.xlsx",OfficeExtractor.ExcelMime,default);
        Assert.Null(result.ResumeImport); Assert.Equal("workbook-cells-v1",result.Workbook!.Version);
        Assert.Equal("自己PR：共同で検証した。希望は未実施。",Assert.Single(result.Spans).Text);
        var cell=Assert.Single(result.Workbook.Cells);Assert.Equal(address,cell.Address);Assert.True(cell.HiddenRow);
    }
    [Fact]
    public void Hidden_sheets_columns_merges_empty_cells_and_missing_caches_are_traceable()
    {
        const string ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var data=$"<worksheet xmlns=\"{ns}\"><cols><col min=\"2\" max=\"2\" hidden=\"1\"/></cols><sheetData><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>同じ文</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>同じ文</t></is></c><c r=\"C2\"><f>EXTERNAL()</f></c><c r=\"D2\"/></row></sheetData><mergeCells><mergeCell ref=\"A2:B2\"/></mergeCells></worksheet>";
        var bytes=OfficeFixtures.Package(true,data,new(){["xl/workbook.xml"]=$"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"任意の非表示シート\" state=\"veryHidden\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>"});
        var result=new OfficeExtractor(new()).Extract(bytes,"x.xlsx",OfficeExtractor.ExcelMime,default);
        Assert.Equal(2,result.Spans.Length);Assert.All(result.Spans,s=>Assert.Equal("同じ文",s.Text)); // duplicate values in distinct cells are not collapsed
        Assert.Equal(4,result.Workbook!.Cells.Length);Assert.Equal("veryHidden",result.Workbook.Cells[0].SheetState);
        Assert.Equal("A2:B2",result.Workbook.Cells[1].MergeRange);Assert.True(result.Workbook.Cells[1].HiddenColumn);
        Assert.Equal("formula_cache_missing",result.Workbook.Cells[2].ValueState);Assert.Contains("FORMULA_CACHE_MISSING",result.Warnings);
    }
    [Fact]
    public void Grouping_requires_complete_disjoint_known_cell_coverage_and_keeps_context_separate()
    {
        WorkbookRules.Validate(new([new("project",["a"]),new("context",["b"])]),["a","b"],true);
        foreach(var answer in new[]{new WorkbookStructure([new("project",["a"])]),new([new("project",["a","b"]),new("context",["b"])]),new([new("project",["a","invented"])])})
            Assert.Throws<ArgumentException>(()=>WorkbookRules.Validate(answer,["a","b"],false));
        Assert.Throws<ArgumentException>(()=>WorkbookRules.Validate(new([new("project",["a"]),new("project",["b"]),new("project",["c"])]),["a","b","c"],true));
    }
    [Fact]
    public void Whole_cell_quote_preserves_final_negation_and_rejects_foreign_or_rewritten_raw()
    {
        const string text="担当者と共同で原因を調査した。\n構成設計は未実施。";
        var good=new SpecificSkill("担当者と共同で原因を調査した",null,"cell-source",text){Basis="experience"};
        var assessed=SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[good]),new Dictionary<string,string>{{"cell-source",text}},text);
        Assert.Empty(assessed.RejectedItems??[]);Assert.Contains("構成設計は未実施",assessed.Items![0].Quote);
        Assert.Equal("raw_not_in_quote",Assert.Single(SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[good with{Raw="担当者を調査した"}]),new Dictionary<string,string>{{"cell-source",text}},text).RejectedItems!).Reason);
        Assert.Equal("unknown_evidence",Assert.Single(SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[good with{EvidenceId="other"}]),new Dictionary<string,string>{{"cell-source",text}},text).RejectedItems!).Reason);
        Assert.Empty(SpecificSkillRules.Assess(new(SpecificSkillRules.Version,"extracted",[good]),new Dictionary<string,string>{{"cell-source",text}},text,"context").SearchTerms);
    }
    [Fact]
    public async Task Complete_capture_exceeds_AI_capacity_without_partial_publication_or_paid_calls()
    {
        using var h=new IngestionHarness();var upload=await h.Upload(bytes:InternalResumeFixture.Package(),excel:true);await h.Processor.RunOnceAsync(default);
        var doc=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),upload.Body!["documentId"]!.GetValue<string>(),default))!.Value;
        var before=(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!;
        var ai=new PipelineAi();var pipeline=ProfilePipelineTests.Create(h,ai);
        var e=await Assert.ThrowsAsync<DomainException>(()=>pipeline.BuildAsync(Jobs.Create(doc,"enrichment",h.Clock.GetUtcNow()),default));
        Assert.Equal("WORKBOOK_AI_EVIDENCE_LIMIT",e.Code);Assert.Equal(0,ai.Calls);
        Assert.Equal(before.ETag,(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),"demo-person-01",default))!.ETag);
        Assert.NotNull(doc["workbookCapture"]);
    }
    [Theory]
    [InlineData("トランザクションとデータ型の相違を確認")]
    [InlineData("自動化の失敗箇所をログから確認。")]
    [InlineData("説明資料の構成と確認観点を整理")]
    public void Saved_scope_misses_are_retained_only_when_the_same_full_cell_is_the_fragment(string raw)
    {
        const string duties="≪担当業務≫共同で検証した。";
        var cell=duties+"\n≪習得スキル≫"+raw+"\n≪コメント≫構成設計は未経験。";
        var skill=new SpecificSkill(raw,null,"cell",cell){Basis="experience"};
        var value=new SpecificSkills(SpecificSkillRules.Version,"extracted",[skill]);var evidence=new Dictionary<string,string>{{"cell",cell}};
        Assert.Equal("not_in_fragment",Assert.Single(SpecificSkillRules.Assess(value,evidence,duties).RejectedItems!).Reason);
        Assert.Single(SpecificSkillRules.Assess(value,evidence,cell).Items!);
        Assert.Contains("構成設計は未経験",SpecificSkillRules.Assess(value,evidence,cell).Items![0].Quote);
        // This is mechanical replay; whether the learned text is actual experience is still unreviewed AI output.
    }

}
