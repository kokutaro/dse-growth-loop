using System.Text.RegularExpressions;

namespace Dse.Domain;

public sealed record RetrievalRules(string Version = "retrieval-v2", int QuestionTopCategories = 3,
    double QuestionThreshold = 0.1, double DocumentThreshold = 0.2, double NoulThreshold = 0.5,
    int RescueSlots = 2, int PersonLimit = 10, int CaseLimit = 10, int EvidenceBytesPerCandidate = 1200)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 80 || QuestionTopCategories is < 1 or > 5 ||
            !double.IsFinite(QuestionThreshold) || QuestionThreshold is < 0 or > 1 ||
            !double.IsFinite(DocumentThreshold) || DocumentThreshold is < 0 or > 1 ||
            !double.IsFinite(NoulThreshold) || NoulThreshold is < 0 or > 1 ||
            PersonLimit is < 1 or > 50 || CaseLimit is < 1 or > 50 || RescueSlots < 0 || RescueSlots > Math.Min(PersonLimit, CaseLimit) ||
            EvidenceBytesPerCandidate is < 64 or > 8000) throw new ArgumentException("Invalid retrieval rules");
    }
}
public sealed record NaturalQuery(string Query)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Query) || Query.Length > 2000) throw new ArgumentException("Invalid query");
        SearchInput.RequireSafe(Query);
    }
}
public static class SearchInput
{
    // Reject credential-bearing input before persistence or either AI call. No query text is logged.
    public static void RequireSafe(string text)
    {
        if (Regex.IsMatch(text, @"(?i)(password|passwd|secret|api[_ -]?key|access[_ -]?token|authorization|bearer|パスワード|秘密鍵|ログイン情報)|-----BEGIN .*PRIVATE KEY|eyJ[A-Za-z0-9_-]+\.eyJ", RegexOptions.CultureInvariant))
            throw new ArgumentException("機密ログイン情報を除いてください。");
    }
}
public sealed record QueryConversion(SearchRequest? Conditions, CategoryProbability[] Distribution,
    string Status, string? AiRunId, RetrievalRules Rules);
public sealed record NaturalSearchRequest(string Query, SearchRequest? Conditions = null, string Target = "all");
public sealed record CandidateAssessment(string EntityId, string EntityType, string Reason, string[] EvidenceIds, string[] MissingInformation);
public sealed record EvidenceCut(string CandidateId, string EvidenceId, int OriginalBytes, int SentBytes);
public sealed record NaturalSearchResponse(string RunId, SearchRequest Conditions, SearchResult Primary,
    CandidateAssessment[] Assessments, string Stage, string RerankingStatus, string? AiRunId);
