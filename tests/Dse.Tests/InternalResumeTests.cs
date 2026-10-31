using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

// Structure-only invented records. No source workbook, names, clients or original work descriptions.
public static class InternalResumeFixture
{
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
        var workbook = $"<workbook xmlns=\"{s}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"{(date1904 ? 1 : 0)}\"/><sheets><sheet name=\"説明専用\" sheetId=\"1\" r:id=\"s1\"/><sheet name=\"{InternalResumeSheet.SheetName}\" sheetId=\"180468472\" r:id=\"s2\"/><sheet name=\"説明補足\" sheetId=\"3\" r:id=\"s3\"/></sheets></workbook>";
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

// Legacy adapter read-compatibility only; production OfficeExtractor never calls Adapt.
public sealed class InternalResumeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Extraction Extract(Dictionary<string, string>? cells = null, bool date1904 = false, bool missingCache = false) =>
        InternalResumeSheet.Adapt(new OfficeExtractor(new()).Extract(InternalResumeFixture.Package(cells, date1904, missingCache), "fictional.xlsx", OfficeExtractor.ExcelMime, default));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_xlsx_shape_selects_seven_projects_and_separates_self_reports(bool date1904)
    {
        var result = Extract(date1904: date1904); var resume = result.ResumeImport!;
        Assert.Equal(7, resume.Projects.Length); Assert.Equal(4, resume.SelfDeclarations.Length);
        Assert.Equal(11, result.Spans.Length);
        foreach (var project in resume.Projects)
        {
            Assert.Equal("2020-01", project.Start); Assert.Equal("2020-03", project.End); Assert.Equal("month", project.Precision); Assert.Equal(3, project.Months);
            Assert.Null(project.Domain); Assert.Contains("≪担当業務≫", project.Description); Assert.Contains("担当工程：要件定義、実装・単体", project.Description);
            Assert.DoesNotContain("希望専用", project.Description); Assert.DoesNotContain("自己申告", project.Description);
            Assert.DoesNotContain("例専用", project.Description); Assert.DoesNotContain("雇用歴", project.Description);
            Assert.Contains(project.SourceCells, c => c.Location.EndsWith($"!F{int.Parse(project.Key) * 4 + 19}:G{int.Parse(project.Key) * 4 + 19}", StringComparison.Ordinal));
            Assert.All(project.SkillCandidates, skill => Assert.True(skill.NeedsReview));
        }
        Assert.Equal(3, ProfileExtraction.TotalMonths(resume.Projects.Select(p => p.Period)));
        Assert.DoesNotContain(result.Spans, s => s.Text.Contains("ノート専用", StringComparison.Ordinal) || s.Text.Contains("説明タブ専用", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("Mongo DB 7", "MongoDB 7")]
    [InlineData("Postgre SQL 16", "PostgreSQL 16")]
    [InlineData("React.Js 18", "React 18")]
    [InlineData("NodeJS 20", "Node.js 20")]
    [InlineData("Asp .Net Core 8", "ASP.NET Core 8")]
    [InlineData("EF Core 8", "Entity Framework Core 8")]
    [InlineData(".NET Framework 4.8", ".NET Framework 4.8")]
    [InlineData(".NET 8", ".NET 8")]
    [InlineData("MAUI", "MAUI")]
    [InlineData("Ｍｏｎｇｏ ＤＢ ７", "MongoDB 7")]
    [InlineData("架空の誤字", "架空の誤字")]
    public void Skill_aliases_preserve_versions_and_unknown_spelling(string raw, string normalized)
    {
        var candidate = Assert.Single(InternalResumeSheet.SkillCandidates(raw, "server_os", "fictional!K23"));
        Assert.Equal(raw, candidate.Raw); Assert.Equal(normalized, candidate.Normalized); Assert.Equal("server_os", candidate.ColumnHint);
    }
    [Fact]
    public void Header_project_detection_supports_moved_rows_reverse_order_sparse_and_full_width_cells()
    {
        var cells = InternalResumeFixture.ShiftedCells(27, 2, true);
        cells["A27"] = "２"; cells["B27"] = "２０２００１"; cells["E27"] = "２０２０年３月"; cells.Remove("J27"); cells["L27"] = "－";
        var resume = Extract(cells).ResumeImport!;
        Assert.Equal(["2", "1"], resume.Projects.Select(p => p.Key));
        Assert.Equal("2020-01", resume.Projects[0].Start); Assert.Equal(3, resume.Projects[0].Months);
        Assert.DoesNotContain(resume.Projects[0].Skills, s => s == "－" || s.Contains("Mongo", StringComparison.Ordinal));
    }
    [Fact]
    public void Unknown_dates_and_missing_formula_cache_are_never_zero_or_current_date()
    {
        var cells = InternalResumeFixture.ShiftedCells(23, 1); cells["E23"] = "現在";
        var resume = Extract(cells, missingCache: true).ResumeImport!;
        var project = Assert.Single(resume.Projects);
        Assert.Equal("2020-01", project.Start); Assert.Null(project.End); Assert.Null(project.Months); Assert.Equal("month", project.Precision);
        project.Period.Validate(); Assert.Null(ProfileExtraction.TotalMonths([project.Period]));
        Assert.Contains("PERIOD_UNRESOLVED", project.Warnings); Assert.Contains("MONTH_COUNT_UNAVAILABLE", project.Warnings);
        Assert.Contains("FORMULA_CACHE_MISSING", resume.Warnings);
        cells["E23"] = ""; cells["B23"] = "";
        Assert.Null(Assert.Single(Extract(cells).ResumeImport!.Projects).Months);
    }
    [Fact]
    public void Inclusive_month_validation_warns_on_cached_count_mismatch_and_reversed_dates()
    {
        var cells = InternalResumeFixture.ShiftedCells(23, 1); cells["B26"] = "2";
        var project = Assert.Single(Extract(cells).ResumeImport!.Projects);
        Assert.Equal(3, project.Months); Assert.Contains("MONTH_COUNT_MISMATCH", project.Warnings);
        cells["E23"] = "2019-12";
        project = Assert.Single(Extract(cells).ResumeImport!.Projects);
        Assert.Null(project.Start); Assert.Null(project.End); Assert.Null(project.Months); Assert.Contains("REVERSED_PERIOD", project.Warnings);
        Assert.Throws<ArgumentException>(() => new ExperiencePeriod("2020-01", "2020-03", "month", 2).Validate());
        Assert.Throws<ArgumentException>(() => new ExperiencePeriod("2020-01", "2020-03", "day", 3).Validate());
        Assert.Equal(4, ProfileExtraction.TotalMonths([new("2020-01", "2020-03", "month", 3), new("2020-02", "2020-04", "month", 3)]));
        Assert.Equal(12, ProfileExtraction.TotalMonths([new("2020-01-01", "2021-01-01", "day", 12)]));
    }
    [Fact]
    public void Recognized_form_never_falls_back_when_header_or_project_is_incomplete()
    {
        var cells = InternalResumeFixture.ShiftedCells(23, 1); cells.Remove("I17");
        Assert.Equal("INTERNAL_RESUME_HEADER_MISSING", Assert.Throws<DomainException>(() => Extract(cells)).Code);
        cells = InternalResumeFixture.ShiftedCells(23, 1); cells.Remove("F24");
        Assert.Equal("INTERNAL_RESUME_INVALID", Assert.Throws<DomainException>(() => Extract(cells)).Code);
        cells = InternalResumeFixture.ShiftedCells(23, 1); cells["A23"] = "例";
        Assert.Equal("INTERNAL_RESUME_NO_PROJECTS", Assert.Throws<DomainException>(() => Extract(cells)).Code);
    }
    [Fact]
    public void Duplicate_projects_and_limits_stop_without_partial_import()
    {
        var cells = InternalResumeFixture.ShiftedCells(23, 2); cells["A27"] = "1";
        Assert.Equal("INTERNAL_RESUME_INVALID", Assert.Throws<DomainException>(() => Extract(cells)).Code);
        cells = InternalResumeFixture.ShiftedCells(23, 21);
        Assert.Equal("PROFILE_FRAGMENT_LIMIT", Assert.Throws<DomainException>(() => Extract(cells)).Code);
        cells = InternalResumeFixture.ShiftedCells(23, 1); cells["L23"] = string.Join("、", Enumerable.Range(0, 101).Select(i => "架空技能" + i));
        Assert.Equal("PROFILE_SKILL_LIMIT", Assert.Throws<DomainException>(() => Extract(cells)).Code);
    }
    [Fact]
    public void Narrative_learning_and_aspirations_remain_reviewable_without_becoming_skill_terms()
    {
        var cells = InternalResumeFixture.ShiftedCells(23, 1);
        var narrative = string.Concat(Enumerable.Repeat("架空の設計作業から得た知見を記録する。", 8));
        cells["F24"] = "≪担当業務≫架空部品の照合画面を実装。\n≪習得スキル≫" + narrative + "\n希望する技能\n≪コメント≫未経験の技能を予定する。";
        var project = Assert.Single(Extract(cells).ResumeImport!.Projects);
        Assert.Contains(project.SkillCandidates, c => c.Raw == narrative && c.ColumnHint == "reported_learning_text");
        Assert.Contains(project.SkillCandidates, c => c.Raw == "希望する技能" && c.ColumnHint == "aspiration_or_unknown");
        Assert.DoesNotContain(project.Skills, s => s == narrative || s.Contains("希望", StringComparison.Ordinal));
        Assert.Contains(project.SourceCells, c => c.Text.Contains(narrative, StringComparison.Ordinal));
    }
    [Fact]
    public async Task Native_capture_preserves_all_sheets_cells_and_declarations_without_template_adapter()
    {
        using var h = new IngestionHarness();
        var upload = await h.Upload(bytes: InternalResumeFixture.Package(), excel: true);
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
