namespace Dse.Domain;

// AI selects source cells and their context, never writes a replacement narrative.
public sealed record WorkbookSection(string Kind, string[] EvidenceIds)
{
    public string? StartEvidenceId { get; init; }
    public string? EndEvidenceId { get; init; }
    public string Precision { get; init; } = "unknown";
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? EndState { get; init; }
}
public sealed record WorkbookStructure(WorkbookSection[] Sections);
public interface IWorkbookStructurer
{
    Task<AiResult<WorkbookStructure>> StructureWorkbookAsync(AiContext context, CancellationToken ct);
}
public static class WorkbookRules
{
    public const string Version = "workbook-context-v2";
    public const string CellSkillPrompt = "specific-skills-cell-v1";
    public const int MaxEvidence = 100;
    public static void Validate(WorkbookStructure result, IReadOnlyCollection<string> evidenceIds, bool synthetic)
    {
        if (result.Sections is null || result.Sections.Length is < 1 or > 20 ||
            result.Sections.Any(s => s is null || s.Kind is not ("project" or "context") || s.EvidenceIds is null || s.EvidenceIds.Length == 0))
            throw new ArgumentException("Invalid workbook sections");
        if (result.Sections.Any(s => s.Precision is not ("unknown" or "month" or "day") || s.EndState is not (null or "dated" or "ongoing" or "unknown") ||
            (s.EndState is "dated" or "ongoing") && s.EndEvidenceId is null || s.Kind=="context" && (s.EndState is "dated" or "ongoing") ||
            s.StartEvidenceId is not null && !s.EvidenceIds.Contains(s.StartEvidenceId) ||
            s.EndEvidenceId is not null && !s.EvidenceIds.Contains(s.EndEvidenceId) ||
            s.Kind == "context" && (s.StartEvidenceId is not null || s.EndEvidenceId is not null)))
            throw new ArgumentException("Invalid period source cells");
        var ids = result.Sections.SelectMany(s => s.EvidenceIds).ToArray();
        // Every captured cell/chunk must occur exactly once, including examples and declarations.
        if (ids.Length != evidenceIds.Count || ids.Distinct().Count() != ids.Length || !ids.ToHashSet().SetEquals(evidenceIds))
            throw new ArgumentException("Incomplete or overlapping workbook coverage");
        if (synthetic && (result.Sections.Count(s => s.Kind == "project") > 2 || result.Sections.Length > 3))
            throw new ArgumentException("Synthetic workbook section limit");
    }
}
