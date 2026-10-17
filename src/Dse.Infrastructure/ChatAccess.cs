using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Call only while the tenant access fence is held. No participant or source text cache.
public sealed class ChatAccess(IPrimaryRepository repository, SourceGuard guard, IdentityResolver identities, IConfiguration config)
{
    public async Task<(StoredRecord Session, ActorAccess[] Participants)> RequireAsync(AiContext context, CancellationToken ct)
    {
        _ = identities.Configuration; _ = config["Data:Mode"];
        var sid = context.SessionId ?? throw new ArgumentException("Session required");
        var row = await repository.ReadAsync(RecordPartition.Conversation(context.Actor.TenantId, sid), sid, ct) ?? throw new DomainException(409, "SOURCE_REVOKED");
        var session = row.Value;
        AccessPolicy.RequireRead(context.Actor, session);
        await new AuthorityProvider(repository,config).RequireSessionAsync(context.Actor, session, ct);
        if (session.Text("status") != "open" || session.Text("aiAssistance") != "enabled" ||
            session.Number("membershipVersion") != context.ChatMembershipVersion || session.Number("lastSequence") != context.ChatLastSequence)
            throw new DomainException(409, "SOURCE_REVOKED");
        var participants = await Task.WhenAll(session.Strings("participantIds").Select(id => new AuthorityProvider(repository,config).PersonAsync(context.Actor.TenantId, id, ct)));
        foreach (var participant in participants.OfType<ActorAccess>())
            await new AuthorityProvider(repository,config).RequireSessionAsync(participant, session, ct);
        if (participants.Length == 0 || participants.Any(p => p is null || !p.HasRole("member") || !AccessPolicy.CanRead(p, session))) throw new DomainException(409, "SOURCE_REVOKED");
        return (row, participants.Select(p => p!).ToArray());
    }
    public async Task<SearchUnit[]> FilterAsync(AiContext context, IEnumerable<SearchUnit> units, CancellationToken ct)
    {
        var (_, participants) = await RequireAsync(context, ct);
        var result = units.Where(u => u.EntityType == "knowledge").ToArray();
        foreach (var actor in participants) result = await guard.FilterUnderFenceAsync(actor, result, ct);
        return result;
    }
    public async Task<string> ContextTextAsync(AiContext context, CancellationToken ct)
    {
        await RequireAsync(context, ct);
        List<string> text = [];
        foreach (var id in context.ConversationEvidenceIds ?? [])
        {
            var row = await repository.ReadAsync(RecordPartition.Conversation(context.Actor.TenantId, context.SessionId!), id, ct);
            if (row is null || row.Value.Text("sessionId") != context.SessionId || row.Value.Text("kind") != "message" || row.Value.Text("authorType") != "human" || row.Value.Number("sequence") > context.ChatLastSequence) throw new DomainException(409, "SOURCE_REVOKED");
            text.Add(row.Value.Text("body"));
        }
        // Same deterministic bound in the consumer and immediately before each AI call.
        var joined = string.Join("\n", text);
        return joined.Length > 1800 ? joined[^1800..] : joined;
    }
}
