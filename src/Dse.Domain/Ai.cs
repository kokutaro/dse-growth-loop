namespace Dse.Domain;

public enum AiStatus { Succeeded, Failed, BudgetStopped, NotEvaluated }
public enum AiFailure { Stopped, Budget, Busy, InputLimit, OutputLimit, Configuration, Unauthorized, RateLimited, Unavailable, Timeout, Cancelled, InvalidJson, SchemaMismatch, Refused, Truncated, InventedId, ModelMismatch }
public enum NoulState { Supported, Unknown, NotEvaluated }
public sealed record AiResult<T>(AiStatus Status, T? Value, string RunId, AiFailure? Failure, bool Cached = false);
public sealed record AiContext(ActorAccess Actor, string JobId, string PromptVersion, string TaxonomyVersion,
    int DataVersion, EvidenceReference[] Evidence, SearchUnit[] Candidates, string? SessionId = null)
{
    public int? EvidenceBytesPerCandidate { get; init; }
}
public sealed record ChoiceQuestion(string Id, string Instructions, IReadOnlyDictionary<string, string> Options);
public sealed record NoulQuestion(string Id, string Instructions, bool HasEvidence);
public sealed record ChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence);
public sealed record NoulAnswer(NoulState State, double? YesProbability);
public sealed record Decisions(IReadOnlyDictionary<string, ChoiceAnswer> Choices, IReadOnlyDictionary<string, NoulAnswer> Nouls);
public sealed record GroundedText(string Text, string[] EvidenceIds);
public sealed record StructuredText(GroundedText[] Items);
public sealed record RankedCandidate(string CandidateId, string Reason, string[] EvidenceIds);
public sealed record Ranking(RankedCandidate[] Items);
public interface ITextStructurer
{
    Task<AiResult<StructuredText>> StructureAsync(AiContext context, string text, CancellationToken ct);
}
public interface IDecisionProvider
{
    Task<AiResult<Decisions>> DecideAsync(AiContext context, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct);
}
public interface IReranker
{
    Task<AiResult<Ranking>> RerankAsync(AiContext context, string query, CancellationToken ct);
}
public interface ISummarizer
{
    Task<AiResult<GroundedText>> SummarizeAsync(AiContext context, string text, CancellationToken ct);
}
