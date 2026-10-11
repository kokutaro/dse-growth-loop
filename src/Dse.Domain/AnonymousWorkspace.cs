using System.Text.Json.Serialization;

namespace Dse.Domain;

// Public DTOs are explicit allowlists. Every ID/token below is random and scoped
// to the operation/room; none is an employee, principal, source or Cosmos ID.
public sealed record AnonymousWorkspaceCapabilities(string ContractVersion, bool Available,
    bool StructuredIdentifiersRemoved, bool RoomLocalAliases, bool OpaqueOperationIds,
    bool FreeTextAnonymized, string LegacyApiIsolation, string ProcessingMode);
public sealed record AnonymousWorkspacePage<T>(T[] Items, string? ContinuationToken = null);
public sealed record AnonymousWorkspaceAlias(string AliasId, string AliasLabel, bool IsSelf);
public sealed record AnonymousWorkspaceChallenge(string Id, string? RoomId, string Title, string Summary,
    string Status, bool IsOwner, string Etag);
public sealed record AnonymousWorkspaceInvitation(string Id, string ChallengeId, string? RoomId,
    string Title, string Summary, string Status, bool IsOwner, string AliasId, string AliasLabel, string Etag);
public sealed record AnonymousWorkspaceRoom(string Id, AnonymousWorkspaceChallenge Challenge, string Status,
    string AiAssistance, bool IsOwner, int ParticipantCount, AnonymousWorkspaceAlias[] Participants,
    string Etag, AnonymousWorkspaceInvitation? Participation);
public sealed record AnonymousWorkspaceMessage(string Id, string RoomId, string Body, string AuthorType,
    string AuthorAliasId, string AuthorAliasLabel, bool IsSelf, int Sequence, string DeliveryState, string CitationState,
    string? ClientMessageId);
public sealed record AnonymousWorkspaceMessages(AnonymousWorkspaceMessage[] Items, int LastSequence, string? ContinuationToken);
public sealed record AnonymousWorkspaceCandidate(string Token, string AliasLabel, string[] MatchingRoutes);
public sealed record AnonymousWorkspaceMutation(string Id, string Status, string? RoomId);
public sealed record AnonymousWorkspaceSection(string Field, string Text, string Basis, string[] EvidenceIds);
public sealed record AnonymousWorkspaceConsent(int Version, bool Publication, bool Analysis, bool IsSelf);
public sealed record AnonymousWorkspaceOutcome(string Resolution, string Result);
public sealed record AnonymousWorkspaceFeedback(string EvaluatorId, int? Value);
public sealed record AnonymousWorkspacePublication(string Status, string? LastErrorCode);
// Legacy-compatible field names contain room-local opaque reference/target tokens,
// never the underlying scope/evidence/person values.
public sealed record AnonymousWorkspaceKnowledge(string Etag, string SessionEtag, string Status, int SourceVersion,
    AnonymousWorkspaceSection[] Sections, string[] ScopeIds, string[] EvidenceIds,
    IReadOnlyDictionary<string, string> ScopeLabels, IReadOnlyDictionary<string, string> EvidenceLabels,
    IReadOnlyDictionary<string, string> ParticipantLabels, IReadOnlyDictionary<string, AnonymousWorkspaceConsent> Consents,
    bool CanApprove, string ProcessingMode, AnonymousWorkspaceOutcome? Outcome,
    AnonymousWorkspaceFeedback[] Feedback, AnonymousWorkspacePublication? PublicationJob,
    BusinessAnalysisMetadata? AnalysisMetadata);
public sealed record AnonymousWorkspacePublishedSection(string Field, string Text, string Basis);
public sealed record AnonymousWorkspaceCase(string Id, string Title, string Summary,
    AnonymousWorkspacePublishedSection[] Sections, int Version, string ProcessingMode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousWorkspaceCreate([property: JsonRequired] string Title, [property: JsonRequired] string Summary);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousWorkspaceInvite([property: JsonRequired] string CandidateToken);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousWorkspaceDecision([property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousWorkspaceUpdate([property: JsonRequired] string AiAssistance, [property: JsonRequired] string Status);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousWorkspaceSend([property: JsonRequired] string ClientMessageId, [property: JsonRequired] string Body);
