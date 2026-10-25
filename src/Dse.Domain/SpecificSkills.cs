using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dse.Domain;

// Separate from taxonomy-v1's domain probabilities. NormalizationCandidate is not an approved taxonomy ID.
public sealed record SpecificSkill(string Raw, string? NormalizationCandidate, string EvidenceId, string Quote, string Participation)
{
    public SkillAnalysis? Analysis { get; init; }
}
public sealed record SkillRejection(SpecificSkill Item, string Reason);
public sealed record SpecificSkills(string Version, string State, SpecificSkill[]? Items)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SkillRejection[]? RejectedItems { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GuardVersion { get; init; }
    public static SpecificSkills NotEvaluated => new(SpecificSkillRules.Version, "not_evaluated", null);
    public string[] SearchTerms => State == "extracted" ? Items!.Where(SpecificSkillRules.IsSearchExperience)
        .Select(s => SpecificSkillRules.Normalize(s.Raw)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
    public string[] ContentTerms => State == "extracted" ? Items!.Where(s => s.Analysis?.ExperienceState == "content_only")
        .Select(s => SpecificSkillRules.Normalize(s.Raw)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [];
    public string[] TermsFor(string entityType) => entityType == "knowledge" ? ContentTerms : SearchTerms;
}
public interface IConcreteSkillExtractor
{
    Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context, string text, CancellationToken ct);
}
public static class SpecificSkillRules
{
    public const string Version = "specific-skills-v1";
    public const string GuardVersion = "scoped-assertion-v3";
    public static readonly string[] Participations = ["performed", "joint", "assisted", "consultation", "aspiration", "other_person", "negated", "unclear"];
    public static string Normalize(string raw) => raw.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant() switch
    { "回帰テスト" => "回帰試験", "c sharp" => "c#", var value => value };
    public static bool IsExperience(SpecificSkill skill) => skill.Participation is "performed" or "joint" or "assisted";
    // Old in-memory fixtures remain readable. Every production boundary calls Assess
    // against current evidence; AI input cannot supply this derived analysis.
    public static bool IsSearchExperience(SpecificSkill skill) => IsExperience(skill) &&
        (skill.Analysis is null || skill.Analysis.ExperienceState == "eligible");
    private static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Sentence boundaries respect reported quotations: a full quote is grounding, not proof of ownership.
    private static List<(int Start, int End)> Sentences(string text)
    {
        var result = new List<(int, int)>(); var start = 0; var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if ("「『“\"".Contains(text[i])) { if (text[i] == '"') depth = depth == 0 ? 1 : 0; else depth++; }
            else if ("」』”".Contains(text[i])) depth = Math.Max(0, depth - 1);
            if (depth == 0 && "。！？\r\n".Contains(text[i])) { result.Add((start, i + 1)); start = i + 1; }
        }
        if (depth != 0) return []; // Unbalanced reported speech cannot establish a complete assertion.
        if (start < text.Length) result.Add((start, text.Length));
        return result;
    }
    private static bool CompletePassage(string source, string quote)
    {
        var spans = Sentences(source);
        var starts = new HashSet<int>(); var ends = new HashSet<int>();
        foreach (var span in spans)
        {
            var start = span.Start; var end = span.End;
            while (start < end && char.IsWhiteSpace(source[start])) start++;
            while (end > start && char.IsWhiteSpace(source[end - 1])) end--;
            starts.Add(start); ends.Add(end);
        }
        var text = quote.Trim();
        for (var offset = source.IndexOf(text, StringComparison.Ordinal); offset >= 0;
            offset = source.IndexOf(text, offset + 1, StringComparison.Ordinal))
            if (starts.Contains(offset) && ends.Contains(offset + text.Length)) return true;
        return false;
    }
    private const string Foreign = @"(?:同僚|他人|他者|別の人|顧客|上司|部下|彼|彼女|先輩|後輩|別チーム|担当者|管理者|技術者|別部署)(?:が|は|の(?:実績|成果|資料|経験))|[\p{L}]{1,12}(?:さん|氏|様)(?:が|は)|\b(?:my colleague|a colleague|the customer|she|he)\b";
    private const string Own = @"(?:私|自分|本人)(?:が|は|の担当は)";
    private const string Negation = @"(?:して|し|では|じゃ|でき|経験(?:は|が)?|担当(?:は|が)?)(?:い?ない|なかった|ません|ませんでした)|(?:なかった|ない|ません(?:でした)?)(?:[。！？.!?]|\s*$)|未経験|未実施|未着手|見送った|取りやめた|\b(?:not|never|without)\b";
    private const string ReportOrWish = @"(?:と仮定|たら|なら|れば|場合は|る(?:計画|予定|構想)|したい|したかった|希望(?:した|する|して)|予定(?:した|する|して|だった|でした|です)|計画(?:した|する|して|だった|でした|です)|つもり|相談(?:した|する|して|を記録)|質問(?:した|する|して)|依頼(?:した|する|して|された)|伝聞|聞いた|とのこと|という(?:話|記録|資料|相談|報告)|人を探|人(?:に|の)|方に|か(?:を)?(?:確認|調査|質問)|らしい|との報告|ことを(?:記録|報告|紹介)|と(?:聞|述べ|記録))|\b(?:wanted|wished|planned|asked|reported|heard|would|if)\b";
    // Productive finite assertion morphology, independent of the technical action's vocabulary.
    // This remains a conservative syntactic guard; it does not claim semantic or human confirmation.
    private static bool CompletedAssertion(string clause) =>
        Match(clause.TrimEnd('。', '！', '？', '.', ' ', '\r', '\n'), @"(?:した|しました|[いっん]だ|[いっしせけげめべねてでつれえ]た|[\p{IsCJKUnifiedIdeographs}]た|している|しています|していた|していました|に従事)(?:ところ)?$|\b(?:I|we) (?:[a-z]+ed|built|ran|wrote|made)\b[^.!?]*$");

    private static string LocalRoleScope(string clause, string raw)
    {
        var start = clause.IndexOf(raw, StringComparison.Ordinal); var end = start + raw.Length;
        while (start > 0 && clause[start - 1] is not ('、' or ',')) start--;
        while (end < clause.Length && clause[end] is not ('、' or ',')) end++;
        return clause[start..end];
    }

    private static string? ExperienceFailure(SpecificSkill item, string source)
    {
        if (!CompletePassage(source, item.Quote)) return "incomplete_passage";
        if (ProfileExtraction.Domains.ContainsKey(Normalize(item.Raw)) || ProfileExtraction.Domains.Values.Contains(item.Raw)) return "broad_domain";
        var spans = Sentences(source);
        var reason = "experience_not_established";
        foreach (var span in spans)
        {
            var sentence = source[span.Start..span.End];
            if (!sentence.Contains(item.Raw, StringComparison.Ordinal) || !item.Quote.Contains(sentence.Trim(), StringComparison.Ordinal)) continue;
            // Retractions apply to this assertion and its immediate continuation, not unrelated earlier events.
            var next = spans.FirstOrDefault(s => s.Start == span.End);
            var continuation = source[span.Start..(next.End > span.End ? next.End : span.End)];
            if (Match(continuation, @"実際(?:には|は)(?:本人は|その作業を)?(?:担当していない|実施していない)|これは(?:同僚|他者|他人)の(?:成果|実績)|というのは(?:誤り|事実でない)"))
            { reason = "retracted_assertion"; continue; }
            // Resolve one clause at a time. A later negative/foreign clause must not erase an earlier assertion.
            var clauses = Regex.Split(sentence, @"(?:(?<=た)が[、,]?|けれど[、,]?|一方[、,]?|しかし[、,]?|[、,](?=(?:私|自分|本人|同僚|上司|他者|他人|顧客)(?:は|が)))");
            foreach (var clause in clauses.Where(c => c.Contains(item.Raw, StringComparison.Ordinal)))
            {
                var scope = clause;
                var quoted = Regex.Matches(scope, "「([^」]*)」|『([^』]*)』|“([^”]*)”|\"([^\"]*)\"");
                if (quoted.Any(q => q.Groups.Cast<Group>().Skip(1).Any(g => g.Success && g.Value.Length > 0 && g.Value != item.Raw &&
                    (g.Value.Contains(item.Raw, StringComparison.Ordinal) || item.Raw.Contains(g.Value, StringComparison.Ordinal)))))
                { reason = "reported_quote"; continue; }
                // Contrastive exclusion: the excluded operand is not evidence of experience.
                var contrast = Regex.Match(scope, @"(.+?)(?:(?<!だけ)(?<!のみ)(?:ではなく|でなく|じゃなく)|せず(?:に|、)|[わかがさたなばまら]ず(?:に|、)|しないで)(.+)");
                if (contrast.Success)
                {
                    if (contrast.Groups[1].Value.Contains(item.Raw, StringComparison.Ordinal)) { reason = "excluded_operand"; continue; }
                    scope = contrast.Groups[2].Value;
                    if (!scope.Contains(item.Raw, StringComparison.Ordinal)) { reason = "excluded_operand"; continue; }
                }
                // A negative relative modifier is not a denial of the governing action.
                // Only mask a proven condition in a private analysis view; never alter
                // the stored raw, quote or source, and never mask the excluded action.
                var assertion = SkillElements.AssertionView(scope, item.Raw);
                if (Match(assertion, Negation)) { reason = "negated_assertion"; continue; }
                if (Match(scope, ReportOrWish)) { reason = "reported_or_intended"; continue; }
                if (!CompletedAssertion(scope)) { reason = "no_completed_assertion"; continue; }
                var own = Match(scope, Own); var foreign = Match(scope, Foreign);
                if (foreign) { reason = "other_actor"; continue; }
                if (!own && Match(scope, @"(?:チーム|部門|部署)(?:が|は|で)")) { reason = "actor_not_established"; continue; }
                var preceding = source[..span.Start];
                if (!own && Match(preceding, @"本人の担当.{0,100}のみ")) { reason = "ownership_restricted"; continue; }
                // Explicit own subject resets inherited foreign context; implicit narrative inherits the latest subject.
                var subjects = Regex.Matches(preceding, Foreign + "|" + Own);
                if (!own && subjects.Count > 0 && Match(subjects[^1].Value, Foreign)) { reason = "other_actor"; continue; }
                if (!own && Match(preceding, @"他人の資料|他者の実績")) { reason = "other_actor"; continue; }
                // Restrictive ownership statement is authoritative; do not turn unrelated described work into a tag.
                if (!own && Match(source[(span.End)..], @"本人の担当.{0,100}のみ|本人.{0,100}担当していない")) { reason = "ownership_restricted"; continue; }
                // Joint work in an earlier phase does not make a later individual handover joint.
                var context = LocalRoleScope(scope, item.Raw);
                // A framing sentence can specify joint/assisted involvement for the following work.
                if (!Match(scope, Own + "|" + Foreign))
                {
                    var frame = spans.Where(s => s.End <= span.Start).Select(s => source[s.Start..s.End])
                        .LastOrDefault(s => Match(s, @"次の|以下の|副業務として"));
                    if (frame is not null && !Match(frame, Foreign + "|" + Negation + "|" + ReportOrWish)) context = frame + context;
                }
                var joint = Match(context, @"共同|協働|一緒|joint|(?:後任|同僚|先輩|技術者|担当者|[\p{L}]+さん)と(?:並走|連携|協力|共に|ともに)"); var assisted = Match(context, "補助|支援|サポート|assist");
                if (item.Participation == "joint" ? !joint : item.Participation == "assisted" ? !assisted : joint || assisted) { reason = "involvement_mismatch"; continue; }
                return null;
            }
        }
        return reason;
    }
    private static string? Failure(SpecificSkill s, IReadOnlyDictionary<string, string> evidence)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.Raw) || s.Raw.Length > 100 || s.NormalizationCandidate is { Length: > 100 } ||
            s.NormalizationCandidate is not null && string.IsNullOrWhiteSpace(s.NormalizationCandidate) || !Participations.Contains(s.Participation) ||
            string.IsNullOrWhiteSpace(s.EvidenceId) || string.IsNullOrWhiteSpace(s.Quote) || s.Quote.Length > 8000) return "invalid_candidate";
        if (!evidence.TryGetValue(s.EvidenceId, out var source)) return "unknown_evidence";
        if (!source.Contains(s.Quote, StringComparison.Ordinal)) return "quote_not_in_source";
        if (!s.Quote.Contains(s.Raw, StringComparison.Ordinal)) return "raw_not_in_quote";
        if (!CompletePassage(source,s.Quote)) return "incomplete_passage";
        return IsExperience(s) ? ExperienceFailure(s, source) : null;
    }
    // Envelope failures still fail the operation. Candidate failures are retained explicitly but never indexed.
    // Re-assess persisted candidates against current evidence, including previously quarantined candidates.
    public static SpecificSkills Assess(SpecificSkills result, IReadOnlyDictionary<string, string> evidence, string? fragmentText = null, string sourceKind = "person")
    {
        if (result.RejectedItems?.Any(r => r is null || r.Item is null) == true)
            throw new ArgumentException("Invalid quarantined candidate");
        var candidates = (result.Items ?? []).Concat((result.RejectedItems ?? []).Select(r => r.Item)).ToArray();
        if (result.Version != Version || result.State is not ("extracted" or "unknown" or "not_evaluated") ||
            (result.State == "extracted" ? result.Items is null : result.Items is not null || result.RejectedItems is { Length: > 0 }) || candidates.Length > 40 || candidates.Any(c => c is null))
            throw new ArgumentException("Invalid specific skill state");
        var accepted = new List<SpecificSkill>(); var rejected = new List<SkillRejection>(); var seen = new HashSet<(string, string, string, string)>();
        foreach (var item in candidates)
        {
            var reason = Failure(item, evidence);
            if (reason is null && IsExperience(item) && fragmentText is not null && !fragmentText.Contains(item.Raw, StringComparison.Ordinal)) reason = "not_in_fragment";
            if (reason == "invalid_candidate") throw new ArgumentException("Invalid specific skill candidate");
            if (!seen.Add((item.Raw, item.EvidenceId, item.Quote, item.Participation))) reason = "duplicate_candidate";
            var analysis = SkillElements.Analyze(item, evidence.GetValueOrDefault(item.EvidenceId), sourceKind, reason);
            // Reassessment is value-idempotent, including derived array values.
            if (item.Analysis is not null && System.Text.Json.JsonSerializer.Serialize(item.Analysis) == System.Text.Json.JsonSerializer.Serialize(analysis)) analysis = item.Analysis;
            var assessed = item with { Analysis = analysis };
            if (reason is null) accepted.Add(assessed); else rejected.Add(new(assessed, reason));
        }
        return result with { Items = result.State == "extracted" ? accepted.ToArray() : null, RejectedItems = rejected.Count == 0 ? null : rejected.ToArray(), GuardVersion = GuardVersion };
    }
    public static void Validate(SpecificSkills result, IReadOnlyDictionary<string, string> evidence)
    {
        if (Assess(result, evidence).RejectedItems is { Length: > 0 }) throw new ArgumentException("Ungrounded or ambiguous specific skill");
    }
}
