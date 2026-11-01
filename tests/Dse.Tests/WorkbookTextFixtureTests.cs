using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

// Structure-only invented records. No source workbook, names, clients or original work descriptions.
public static class WorkbookTextFixture
{
    public const string SheetName = "02. スキル記入シート(例つき)"; // Test data only; production never selects a sheet by name.
    public static Dictionary<string, string> Cells(int first = 23, int count = 7, bool reverse = false) => Build(first, count, reverse);
    private static Dictionary<string, string> Build(int first = 23, int count = 7, bool reverse = false)
    {
        Dictionary<string, string> cells = new()
        {
            ["D6"] = "自己申告専用技術", ["D7"] = "自己申告専用業務", ["J5"] = "架空資格", ["D51"] = "自己PR専用文",
            ["C8"] = "番号", ["D8"] = "企業", ["G8"] = "期間", ["H8"] = "雇用形態", ["J8"] = "職種", ["L8"] = "事業内容",
            ["C9"] = "例", ["D9"] = "雇用歴の例", ["C10"] = "1", ["D10"] = "架空組織", ["G10"] = "201801～202412",
            ["F17"] = "案件・業務内容", ["B17"] = "作業期間", ["H17"] = "役割", ["I17"] = "使用言語", ["J17"] = "DB",
            ["K17"] = "サーバOS", ["L17"] = "FW・MW・ツール等", ["M18"] = "要件\n定義", ["P18"] = "実装・単体",
            ["A19"] = "例", ["F19"] = "■説明用の架空案件", ["F20"] = "≪担当業務≫例専用スキル\n≪習得スキル≫サンプル技能",
        };
        for (int i = 0; i < count; i++)
        {
            var row = first + i * 4; var number = reverse ? count - i : i + 1;
            cells["A" + row] = number.ToString(CultureInfo.InvariantCulture);
            cells["B" + row] = "2020-01"; cells["E" + row] = "2020-03";
            cells["F" + row] = $"■架空の部品照合{number}";
            cells["F" + (row + 1)] = "≪担当業務≫\n架空部品の照合画面を実装。\n≪習得スキル≫\nReact.Js 18\n≪コメント≫\n希望専用技能を今後学習したい。";
            cells["H" + row] = "実装担当"; cells["H" + (row + 1)] = "架空チーム3名";
            cells["I" + row] = "NodeJS 20\nAsp .Net Core 8";
            cells["J" + row] = "Mongo DB 7、Postgre SQL 16";
            cells["K" + row] = "架空CloudService";
            cells["L" + row] = "EF Core 8\n.NET Framework 4.8\n.NET 8／MAUI\n架空IDE、架空AIツール";
            cells["M" + row] = "●"; cells["P" + row] = "●";
            cells["B" + (row + 3)] = "3";
        }
        return cells;
    }
    public static Dictionary<string, string> ShiftedCells(int first, int count, bool reverse = false) => Build(first, count, reverse);
    public static byte[] Package(Dictionary<string, string>? cells = null, bool date1904 = false, bool missingCache = false)
    {
        cells ??= Build();
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var data = new XElement(s + "sheetData");
        foreach (var group in cells.GroupBy(c => int.Parse(System.Text.RegularExpressions.Regex.Match(c.Key, @"\d+$").Value, CultureInfo.InvariantCulture)).OrderBy(g => g.Key))
        {
            var row = new XElement(s + "row", new XAttribute("r", group.Key));
            foreach (var (address, value) in group)
            {
                var cell = new XElement(s + "c", new XAttribute("r", address));
                if ((address.StartsWith('B') || address.StartsWith('E')) && value.Length == 7 && DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", out var month))
                {
                    var origin = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
                    cell.Add(new XAttribute("s", "1"), new XElement(s + "v", (month.ToDateTime(TimeOnly.MinValue) - origin).TotalDays.ToString(CultureInfo.InvariantCulture)));
                }
                else if (address.StartsWith('B') && int.TryParse(value, out _))
                {
                    cell.Add(new XElement(s + "f", $"DATEDIF(B{group.Key - 3},E{group.Key - 3},\"M\")+1"));
                    if (!missingCache) cell.Add(new XElement(s + "v", value));
                }
                else cell.Add(new XAttribute("t", "inlineStr"), new XElement(s + "is", new XElement(s + "t", value)));
                row.Add(cell);
            }
            data.Add(row);
        }
        var merges = cells.Keys.Where(k => k.StartsWith('F') && cells[k].StartsWith('■')).Select(k =>
        {
            var r = int.Parse(k[1..], CultureInfo.InvariantCulture);
            return new XElement(s + "mergeCell", new XAttribute("ref", $"F{r}:G{r}"));
        });
        var worksheet = new XElement(s + "worksheet", new XElement(s + "dimension", new XAttribute("ref", "A1:Z59")), data, new XElement(s + "mergeCells", merges));
        var workbook = $"<workbook xmlns=\"{s}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"{(date1904 ? 1 : 0)}\"/><sheets><sheet name=\"説明専用\" sheetId=\"1\" r:id=\"s1\"/><sheet name=\"{WorkbookTextFixture.SheetName}\" sheetId=\"180468472\" r:id=\"s2\"/><sheet name=\"説明補足\" sheetId=\"3\" r:id=\"s3\"/></sheets></workbook>";
        return OfficeFixtures.Package(true, extra: new()
        {
            ["xl/workbook.xml"] = workbook,
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + string.Join("", Enumerable.Range(1, 3).Select(i => $"<Relationship Id=\"s{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>")) + "</Relationships>",
            ["xl/worksheets/sheet2.xml"] = worksheet.ToString(),
            ["xl/worksheets/sheet3.xml"] = $"<worksheet xmlns=\"{s}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>説明タブ専用スキル</t></is></c></row></sheetData></worksheet>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{s}\"><numFmts><numFmt numFmtId=\"165\" formatCode=\"yyyy年m月\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"165\"/></cellXfs></styleSheet>",
            ["xl/comments1.xml"] = "<comments>ノート専用サンプル技能</comments>"
        });
    }
}

public sealed class WorkbookTextFixtureTests
{
    [Fact]
    public async Task Native_capture_preserves_all_sheets_cells_and_declarations_without_template_adapter()
    {
        using var h = new IngestionHarness();
        var upload = await h.Upload(bytes: WorkbookTextFixture.Package(), excel: true);
        await h.Processor.RunOnceAsync(default);
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), upload.Body!["documentId"]!.GetValue<string>(), default))!.Value;
        Assert.Null(doc["resumeImport"]); Assert.NotNull(doc["workbookCapture"]);
        var spans = new List<StoredRecord>(); string? cursor = null;
        do { var page = await h.Repo.QueryAsync(RecordPartition.Catalog("demo"), "evidenceSpan", "documentId", doc.Text("id"), 100, cursor, default); spans.AddRange(page.Items); cursor = page.ContinuationToken; } while (cursor is not null);
        Assert.Contains(spans, r => r.Value.Text("text") == "自己PR専用文");
        Assert.Contains(spans, r => r.Value.Text("text") == "説明タブ専用スキル");
        Assert.Contains(spans, r => r.Value.Text("text").Contains("希望専用技能"));
        Assert.True(spans.Count > 100); // capture is complete; AI will hold this with an explicit capacity reason
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;
        var output=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"workbook-capture-contract.json"),JsonSerializer.Serialize(new[]{new {schema="Document",value=doc}}.Cast<object>().Concat(spans.Select(r=>(object)new {schema="EvidenceSpan",value=r.Value}))));
    }
}

public sealed class AllTextOnlyTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Arbitrary_shape_keeps_all_sheets_example_declarations_and_date_sources(bool date1904)
    {
        var cells=WorkbookTextFixture.ShiftedCells(300,1);cells.Remove("I17");cells.Remove("F17");
        cells["AZ900"]="自己PRを移動しても全文を残す。";cells["F301"]="≪担当業務≫照合した。\n≪習得スキル≫知識。\n≪コメント≫未実施。";
        var capture=new OfficeExtractor(new()).Extract(WorkbookTextFixture.Package(cells,date1904),"arbitrary.xlsx",OfficeExtractor.ExcelMime,default);
        Assert.Contains(capture.Spans,s=>s.Text=="自己PRを移動しても全文を残す。");
        Assert.Contains(capture.Spans,s=>s.Text=="説明タブ専用スキル");
        Assert.Contains(capture.Spans,s=>s.Text.Contains("例専用スキル",StringComparison.Ordinal));
        Assert.Contains(capture.Spans,s=>s.Text==cells["F301"]);
        var date=Assert.Single(capture.Workbook!.Cells,c=>c.Address=="B300"&&c.Sheet==WorkbookTextFixture.SheetName);
        Assert.Equal("2020-01",WorkbookDates.Read(date.Text,date).Value);Assert.Equal("month",WorkbookDates.Read(date.Text,date).Precision);
        Assert.DoesNotContain("resumeImport",JsonSerializer.Serialize(capture,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    [Fact]
    public void Missing_headers_and_incomplete_project_blocks_are_not_template_errors()
    {
        var cells=new Dictionary<string,string>{{"ZZ777","見出しのない担当業務を原文のまま保持。"},{"D35","自己PR"}};
        var result=new OfficeExtractor(new()).Extract(WorkbookTextFixture.Package(cells),"unstructured.xlsx",OfficeExtractor.ExcelMime,default);
        Assert.Contains(result.Spans,s=>s.Text==cells["ZZ777"]);Assert.Contains(result.Spans,s=>s.Text==cells["D35"]);
        Assert.DoesNotContain(result.Warnings,w=>w.StartsWith("INTERNAL_RESUME_",StringComparison.Ordinal));
    }
    [Fact]
    public async Task Excel_without_capture_is_held_before_AI_instead_of_row_template_fallback()
    {
        using var h=new IngestionHarness();var upload=await h.Upload(excel:true);await h.Processor.RunOnceAsync(default);
        var p=RecordPartition.Catalog("demo");var doc=(await h.Repo.ReadAsync(p,upload.Body!["documentId"]!.GetValue<string>(),default))!;
        doc.Value.Remove("workbookCapture");await h.Repo.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);
        var before=(await h.Repo.ReadAsync(p,"demo-person-01",default))!;var ai=new PipelineAi();var pipeline=ProfilePipelineTests.Create(h,ai);
        var error=await Assert.ThrowsAsync<DomainException>(()=>pipeline.BuildAsync(Jobs.Create(doc.Value,"enrichment",h.Clock.GetUtcNow()),default));
        Assert.Equal("WORKBOOK_CAPTURE_REQUIRED",error.Code);Assert.Equal(0,ai.Calls);
        Assert.Equal(before.ETag,(await h.Repo.ReadAsync(p,"demo-person-01",default))!.ETag);
    }
    [Fact]
    public void Formula_cache_missing_stays_explicit_after_template_removal()
    {
        var result=new OfficeExtractor(new()).Extract(WorkbookTextFixture.Package(WorkbookTextFixture.Cells(count:1),missingCache:true),"formula.xlsx",OfficeExtractor.ExcelMime,default);
        Assert.Contains("FORMULA_CACHE_MISSING",result.Warnings);
        Assert.Contains(result.Workbook!.Cells,c=>c.ValueState=="formula_cache_missing");
    }
}
