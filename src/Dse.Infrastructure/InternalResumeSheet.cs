using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record ResumeCell(string Address, string Text, string Location);
public sealed record ResumeSkill(string Raw, string Normalized, string ColumnHint, string Location, bool NeedsReview);
public sealed record ResumeDeclaration(string Kind, string Text, string Location, string ConfirmationState);
public sealed record ResumeProject(string Key, string Location, string Description, string? Start, string? End,
    string Precision, int? Months, string[] Skills, string? Domain, string[] EvidenceIds,
    ResumeSkill[] SkillCandidates, ResumeCell[] SourceCells, string[] Warnings)
{
    [JsonIgnore] public ExperiencePeriod Period => new(Start, End, Precision, Months);
}
public sealed record ResumeImport(string Format, ResumeProject[] Projects, ResumeDeclaration[] SelfDeclarations, string[] Warnings);

// A bounded, mechanical adapter for the supplied internal form. It never calls AI or evaluates formulas.
public static class InternalResumeSheet
{
    public const string Format = "internal-resume-v1";
    public const string SheetName = "02. スキル記入シート(例つき)";
    private static readonly string[] Stages = ["要件定義", "基本設計", "詳細設計", "実装・単体", "結合テスト", "総合テスト", "保守・運用"];
    private static string N(string value) => value.Normalize(NormalizationForm.FormKC).Trim();
    private static string Compact(string value) => Regex.Replace(N(value), @"\s+", "");

    public static Extraction Adapt(Extraction extraction, CancellationToken ct = default)
    {
        var sheet = extraction.Spans.Where(s => s.Type == "cells" && s.Label.Contains(" / " + SheetName + "!", StringComparison.Ordinal)).ToArray();
        if (sheet.Length == 0) return extraction;
        if (sheet.Length > 4096) throw new DomainException(422, "INTERNAL_RESUME_CELL_LIMIT");
        var cells = new Dictionary<string, ResumeCell>(StringComparer.Ordinal);
        foreach (var span in sheet)
        {
            ct.ThrowIfCancellationRequested();
            var match = Regex.Match(span.Label, @"!([A-Z]+\d+)(?::[A-Z]+\d+)?$");
            // Chunked/ambiguous cells must not silently become an incomplete project.
            if (!match.Success || !cells.TryAdd(match.Groups[1].Value, new(match.Groups[1].Value, span.Text, span.Label)))
                throw new DomainException(422, "INTERNAL_RESUME_INVALID");
        }
        string Text(string col, int row) => cells.GetValueOrDefault(col + row)?.Text ?? "";
        var byRow = cells.Values.GroupBy(c => Row(c.Address)).ToDictionary(g => g.Key, g => g.ToArray());
        var rows = byRow.Keys.Order().ToArray();
        // Locate the two-row project header by its column meanings; no fixed project row numbers.
        var headers = rows.Where(r =>
        {
            ct.ThrowIfCancellationRequested();
            var values = (byRow.GetValueOrDefault(r) ?? []).Concat(byRow.GetValueOrDefault(r + 1) ?? []).Select(c => Compact(c.Text)).ToArray();
            return values.Any(v => v.Contains("案件", StringComparison.Ordinal) || v.Contains("プロジェクト", StringComparison.Ordinal) || v.Contains("業務内容", StringComparison.Ordinal) || v.Contains("作業内容", StringComparison.Ordinal)) &&
                values.Any(v => v.Contains("期間", StringComparison.Ordinal) || v.Contains("開始", StringComparison.Ordinal)) &&
                values.Any(v => v.Contains("使用言語", StringComparison.Ordinal) || v == "言語") &&
                values.Any(v => v.Contains("要件定義", StringComparison.Ordinal));
        }).ToArray();
        if (headers.Length == 0) throw new DomainException(422, "INTERNAL_RESUME_HEADER_MISSING");
        var header = headers.Min();
        var starts = rows.Where(r => r > header &&
            (Regex.IsMatch(N(Text("A", r)), @"^\d+$") || N(Text("A", r)) == "例" || N(Text("F", r)).StartsWith('■'))).ToArray();
        var declarations = new List<ResumeDeclaration>();
        foreach (var (address, kind) in new[] { ("D6", "self_reported_technology"), ("D7", "self_reported_work"), ("J5", "qualification"), ("D51", "self_promotion") })
            if (cells.ContainsKey(address) && (Row(address) < header || address == "D51"))
            {
                var source = cells[address];
                // The supplied self-PR anchor is only accepted outside a detected project block.
                if (starts.Any(r => Row(address) >= r && Row(address) <= r + 3)) continue;
                declarations.Add(new(kind, source.Text, source.Location, "ai_unconfirmed"));
            }
        List<ResumeProject> projects = []; List<ExtractedSpan> spans = []; HashSet<string> warnings = [.. extraction.Warnings];
        var keys = new HashSet<string>();
        foreach (var r in starts)
        {
            ct.ThrowIfCancellationRequested();
            if (N(Text("A", r)) == "例") continue;
            if (!Regex.IsMatch(N(Text("A", r)), @"^[0-9]{1,9}$") || !N(Text("F", r)).StartsWith('■') ||
                !keys.Add(N(Text("A", r))) || starts.Any(next => next > r && next <= r + 3))
                throw new DomainException(422, "INTERNAL_RESUME_INVALID");
            var body = Text("F", r + 1);
            if (!body.Contains("担当業務", StringComparison.Ordinal)) throw new DomainException(422, "INTERNAL_RESUME_INVALID");
            var source = Enumerable.Range(r, 4).SelectMany(row => byRow.GetValueOrDefault(row) ?? []).Where(c => Column(c.Address) <= 19)
                .OrderBy(c => Row(c.Address)).ThenBy(c => Column(c.Address)).ToArray();
            var location = cells["F" + r].Location[..cells["F" + r].Location.LastIndexOf('!')] + $"!A{r}:S{r + 3}";
            var rawText = string.Join("\n", source.Select(c => c.Address + ": " + c.Text));
            if (rawText.EnumerateRunes().Count() > 8000) throw new DomainException(422, "PROFILE_TEXT_LIMIT");
            var localWarnings = new HashSet<string>();
            var start = Month(Text("B", r)); var end = Month(Text("E", r));
            int? months = null;
            if (start is not null && end is not null)
            {
                var first = DateOnly.ParseExact(start + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var last = DateOnly.ParseExact(end + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (last < first) { localWarnings.Add("REVERSED_PERIOD"); start = end = null; }
                else months = (last.Year - first.Year) * 12 + last.Month - first.Month + 1;
            }
            if (start is null || end is null) localWarnings.Add("PERIOD_UNRESOLVED");
            var display = N(Text("B", r + 3));
            var displayMatch = Regex.Match(display, @"^(\d+)\s*(?:か月|ヶ月|ケ月|月)?$");
            if (displayMatch.Success && int.TryParse(displayMatch.Groups[1].Value, out var displayed))
            {
                if (months is not null && displayed != months) localWarnings.Add("MONTH_COUNT_MISMATCH");
            }
            else if (display.Length > 0) localWarnings.Add("MONTH_COUNT_UNREADABLE");
            else localWarnings.Add("MONTH_COUNT_UNAVAILABLE");
            List<ResumeSkill> skills = [];
            foreach (var (col, hint) in new[] { ("I", "language"), ("J", "database"), ("K", "server_os"), ("L", "framework_middleware_tools") })
                if (cells.TryGetValue(col + r, out var cell)) skills.AddRange(SkillCandidates(cell.Text, hint, cell.Location));
            var learned = Regex.Match(body, @"[≪《<＜]\s*習得スキル\s*[≫》>＞](?<text>.*?)(?=[≪《<＜]|$)", RegexOptions.Singleline);
            if (learned.Success) skills.AddRange(SkillCandidates(learned.Groups["text"].Value, "reported_learning", cells["F" + (r + 1)].Location));
            if (skills.Count > 100 || skills.Any(s => s.Normalized.Length > 100 && s.ColumnHint is not ("reported_learning_text" or "aspiration_or_unknown")))
                throw new DomainException(422, "PROFILE_SKILL_LIMIT");
            var tasks = Regex.Match(body, @"[≪《<＜]\s*担当業務\s*[≫》>＞](?<text>.*?)(?=[≪《<＜]|$)", RegexOptions.Singleline);
            if (!tasks.Success || string.IsNullOrWhiteSpace(tasks.Groups["text"].Value)) throw new DomainException(422, "INTERNAL_RESUME_INVALID");
            // Comments may describe aspirations. Keep their original cells, outside the experience description.
            var description = Text("F", r) + "\n≪担当業務≫" + tasks.Groups["text"].Value.TrimEnd();
            if (!string.IsNullOrWhiteSpace(Text("H", r))) description += "\n役割：" + Text("H", r);
            if (!string.IsNullOrWhiteSpace(Text("H", r + 1))) description += "\n体制・規模：" + Text("H", r + 1);
            var stages = Enumerable.Range(0, 7).Where(i => N(Text(((char)('M' + i)).ToString(), r)) == "●").Select(i => Stages[i]).ToArray();
            if (stages.Length > 0) description += "\n担当工程：" + string.Join("、", stages);
            // Technical columns are source hints, never an approved skill taxonomy or proficiency score.
            foreach (var col in new[] { "I", "J", "K", "L" })
            {
                var usable = skills.Where(s => s.Location == cells.GetValueOrDefault(col + r)?.Location && s.ColumnHint != "aspiration_or_unknown").Select(s => s.Raw).ToArray();
                if (usable.Length > 0) description += "\n技術欄(" + col + ")：" + string.Join("、", usable);
            }
            var project = new ResumeProject(N(Text("A", r)), location, description, start, end, "month", months,
                skills.Where(s => s.ColumnHint is not ("aspiration_or_unknown" or "reported_learning_text")).Select(s => s.Normalized).Distinct(StringComparer.Ordinal).ToArray(), null, [], skills.ToArray(), source, localWarnings.Order().ToArray());
            projects.Add(project); spans.Add(new("cells", location, rawText));
            foreach (var warning in localWarnings) warnings.Add(warning);
        }
        if (projects.Count == 0) throw new DomainException(422, "INTERNAL_RESUME_NO_PROJECTS");
        if (projects.Count > ProfilePipeline.MaxFragments) throw new DomainException(422, "PROFILE_FRAGMENT_LIMIT");
        spans.AddRange(declarations.Select(d => new ExtractedSpan("cells", d.Location, d.Text)));
        warnings.Add("INTERNAL_RESUME_UNCONFIRMED");
        return new(spans.ToArray(), warnings.Order().ToArray(), new(Format, projects.ToArray(), declarations.ToArray(), warnings.Order().ToArray()));
    }

    private static int Row(string address) => int.Parse(Regex.Match(address, @"\d+$").Value, CultureInfo.InvariantCulture);
    private static int Column(string address) => address.TakeWhile(char.IsAsciiLetter).Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
    public static string? Month(string raw)
    {
        var text = N(raw);
        if (DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.Year <= 9998 ? date.ToString("yyyy-MM", CultureInfo.InvariantCulture) : null;
        var match = Regex.Match(text, @"^(\d{4})\s*(?:[-/年]\s*)?(\d{1,2})\s*月?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var year) || !int.TryParse(match.Groups[2].Value, out var month) ||
            year is < 1 or > 9998 || month is < 1 or > 12) return null;
        return $"{year:D4}-{month:D2}";
    }
    public static ResumeSkill[] SkillCandidates(string raw, string hint, string location) =>
        Regex.Split(raw, @"[\r\n,、;；/／]+").Select(s => s.Trim()).Where(s => s.Length > 0 && s is not ("-" or "－" or "ー" or "―" or "なし" or "不明"))
            .Select(s => new ResumeSkill(s, NormalizeSkill(s), Regex.IsMatch(s, "希望|今後|予定|未経験") ? "aspiration_or_unknown" :
                hint == "reported_learning" && s.Length > 100 ? "reported_learning_text" : hint, location, true)).ToArray();
    public static string NormalizeSkill(string raw)
    {
        var value = N(raw);
        foreach (var (pattern, replacement) in new[]
        {
            (@"^Mongo\s*DB(?=$|\s|\d)", "MongoDB"), (@"^Postgre\s*SQL(?=$|\s|\d)", "PostgreSQL"),
            (@"^React(?:\.Js)?(?=$|\s|\d)", "React"), (@"^Node(?:JS|\.js)(?=$|\s|\d)", "Node.js"),
            (@"^Asp\s*\.?\s*Net\s+Core(?=$|\s|\d)", "ASP.NET Core"), (@"^EF\s+Core(?=$|\s|\d)", "Entity Framework Core")
        })
            if (Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return Regex.Replace(value, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return value;
    }
}
