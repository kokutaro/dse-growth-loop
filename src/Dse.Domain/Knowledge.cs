namespace Dse.Domain;

public sealed record SummarySection(string Field, string Text, string Basis, string[] EvidenceIds);
public sealed record KnowledgeSummary(SummarySection[] Sections)
{
    public static readonly string[] Fields = ["challenge", "assumptions", "attempts", "result", "reuseConditions", "unresolvedPoints"];
    public string Text => string.Join("\n", Sections.Select(s => $"{s.Field} ({s.Basis}): {s.Text}"));
}
public interface IKnowledgeDrafter
{
    Task<AiResult<KnowledgeSummary>> DraftAsync(AiContext context, CancellationToken ct);
}
