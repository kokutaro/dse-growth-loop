using System.Text.Json.Serialization;

namespace Dse.Domain;

public sealed record ChallengeCreate([property: JsonRequired] string Title, [property: JsonRequired] string Summary);
public sealed record ParticipationCreate([property: JsonRequired] string PersonId);
public sealed record ParticipationDecision([property: JsonRequired] string Decision);
public sealed record SessionUpdate([property: JsonRequired] string AiAssistance, [property: JsonRequired] string Status);
public sealed record MessageCreate([property: JsonRequired] string ClientMessageId, [property: JsonRequired] string Body);
// Durable outbox payload; no message text or credentials. The future consumer re-reads membership and AI settings.
public sealed record MessageCommitted(string EventId, string TenantId, string SessionId, string MessageId,
    int Sequence, int MembershipVersion, string PayloadVersion = "1.0");
