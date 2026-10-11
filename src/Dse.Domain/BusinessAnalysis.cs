namespace Dse.Domain;

// Metadata must itself be included in the approved, analysis-consented source version.
// A missing value is unknown; it is never inferred from a private conversation or a model.
public sealed record BusinessAnalysisMetadata(string? DepartmentId, string? Resolution, decimal? ReportedHours, int? Recurrences, int? Feedback);
public sealed record BusinessAnalysisSource(string KnowledgeId, int Version, string ChallengeId, string SessionId,
    string Summary, string[] NormalizedTerms, CategoryProbability[] Classifications, BusinessAnalysisMetadata Metadata);
public sealed record BusinessHypothesis(string Audience, string Problem, string ExistingWorkaround, string ValueHypothesis,
    string CounterEvidence, string NextCustomerCheck, string[] EvidenceIds);
public sealed record BusinessAnalysisMetrics(int IndependentChallenges, int DistinctDepartments, int? RecurringChallenges,
    double? UnresolvedRatio, decimal? ReportedHours, int? LowRatedChallenges, string[] MissingMetadata);
public sealed record BusinessAnalysisRequest(string RunId, [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset Cutoff, string ConfigVersion);
public sealed record BusinessAnalysisDecision(string Status, string? GroupId = null);
public interface IBusinessHypothesisDrafter
{
    Task<AiResult<BusinessHypothesis>> DraftAsync(ActorAccess actor, string runId, BusinessAnalysisSource[] sources, CancellationToken ct);
}

public static class BusinessAnalysisMetadataContract
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web)
    { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
    public static BusinessAnalysisMetadata? Parse(System.Text.Json.Nodes.JsonNode? input)
    {
        if (input is null) return null;
        if (input is not System.Text.Json.Nodes.JsonObject obj || obj.Any(x => x.Key is not ("departmentId" or "resolution" or "reportedHours" or "recurrences" or "feedback")))
            throw new ArgumentException("Invalid public analysis metadata");
        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<BusinessAnalysisMetadata>(obj.ToJsonString(), Json)!;
            if (value.DepartmentId is not null && !RecordJson.IsId(value.DepartmentId) || value.Resolution is not (null or "resolved" or "partial" or "unresolved") ||
                value.ReportedHours is < 0 or > 100000 || value.ReportedHours is decimal hours && decimal.Round(hours, 2) != hours ||
                value.Recurrences is < 1 or > 10000 || value.Feedback is < 1 or > 5)
                throw new ArgumentException("Invalid public analysis metadata");
            return value;
        }
        catch (System.Text.Json.JsonException e) { throw new ArgumentException("Invalid public analysis metadata", e); }
    }
}
