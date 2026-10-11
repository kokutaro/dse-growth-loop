using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Internal resolution only: never serialize this type as an HTTP response.
public sealed record AnonymousWorkspaceResolvedParticipant(string PersonId, string PrincipalId, string AliasId, string AliasLabel, bool IsSelf);
public sealed record AnonymousWorkspaceRoomResolution(string PublicRoomId, string SessionId, string ChallengeId,
    string SessionEtag, int MembershipVersion, bool IsOwner, AnonymousWorkspaceResolvedParticipant[] Participants);

public sealed partial class AnonymousWorkspaceService
{
    private readonly IPrimaryRepository repository;
    private readonly IProjectionRepository projections;
    private readonly SourceGuard guard;
    private readonly AccessFence fence;
    private readonly ConversationService chat;
    private readonly KnowledgeService knowledge;
    private readonly IdentityResolver identities;
    private readonly IConfiguration config;
    private readonly TimeProvider clock;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public AnonymousWorkspaceService(IPrimaryRepository repository, IProjectionRepository projections, SourceGuard guard,
        AccessFence fence, ConversationService chat, KnowledgeService knowledge, IdentityResolver identities, IConfiguration config, TimeProvider clock)
    {
        this.repository = repository; this.projections = projections; this.guard = guard; this.fence = fence;
        this.chat = chat; this.knowledge = knowledge; this.identities = identities; this.config = config; this.clock = clock;
    }
    private static void Member(ActorAccess actor) { if (!actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN"); }
    private async Task FreeInputAsync(ActorAccess actor, IEnumerable<string> values, IEnumerable<AnonymousWorkspaceResolvedParticipant>? participants, CancellationToken ct)
    {
        var people = participants?.ToArray() ?? [new(actor.PersonId,actor.PrincipalId,"","",true)];
        var identifiers = new List<string> { actor.TenantId };
        foreach (var person in people)
        {
            identifiers.Add(person.PersonId); identifiers.Add(person.PrincipalId);
            var profile = await repository.ReadAsync(Catalog(actor),person.PersonId,ct);
            if (profile is not null && profile.Value.Text("displayName").Length > 1) identifiers.Add(profile.Value.Text("displayName"));
        }
        AnonymousWorkspaceKnownIdentifiers.RequireSafe(values,identifiers);
    }
    private async Task<string> DisplayTextAsync(ActorAccess actor,string text,IEnumerable<AnonymousWorkspaceResolvedParticipant>? people,CancellationToken ct)
    {
        try { await FreeInputAsync(actor,[text],people,ct); return text; }
        catch (DomainException e) when (e.Code == "IDENTIFYING_CONTENT") { return "識別情報を含むため表示できません。"; }
    }
    private static string Opaque(string prefix) => prefix + Guid.NewGuid().ToString("N");
    private static void PublicId(string id, string prefix)
    {
        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(id[prefix.Length..], "N", out _))
            throw new DomainException(404, "NOT_FOUND");
    }
    private static string Index(string type, params string[] ids) => "aw-index-" + ConversationService.Hash(type + ":" + JsonSerializer.Serialize(ids));
    private static RecordPartition Catalog(ActorAccess actor) => RecordPartition.Catalog(actor.TenantId);
    private ActorAccess? Person(ActorAccess actor, string person) => config["Data:Mode"] == "Demo"
        ? IdentityResolver.DemoPerson(person) : new IdentityResolver(config).ResolvePerson(actor.TenantId, person);
    private async Task<bool> BoundProfileAsync(ActorAccess actor,ActorAccess person,CancellationToken ct)
    {
        var profile = await repository.ReadAsync(Catalog(actor),person.PersonId,ct);
        return profile is not null && profile.Value.Text("kind") == "person" && profile.Value.Text("tenantId") == actor.TenantId &&
            profile.Value.Text("ownerId") == person.PrincipalId;
    }
    private JsonObject Mapping(ActorAccess actor, string id, string type, string sid)
    {
        var value = RecordJson.New("idempotency", id, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow());
        value["mappingType"] = type; value["internalSessionId"] = sid; return value;
    }
    private async Task<StoredRecord> ReadMapping(ActorAccess actor, string id, string type, CancellationToken ct)
    {
        Member(actor);
        var row = await repository.ReadAsync(Catalog(actor), id, ct);
        if (row is null || row.Value.Text("kind") != "idempotency" || row.Value.Text("mappingType") != type ||
            row.Value.Text("tenantId") != actor.TenantId) throw new DomainException(404, "NOT_FOUND");
        return row;
    }
    public AnonymousWorkspaceCapabilities Capabilities() => new("anonymous-workspace-v1", true, true, true, true, false,
        config["Workspace:LegacyApisRestricted"] == "true" ? "operator-or-development" : "not-enforced", config["Data:Mode"] == "Demo" ? "fixture" : "server");

    // Caller owns the tenant fence. Mapping indexes contain internal IDs only in private storage.
    private async Task<StoredRecord> EnsureRoomMapAsync(ActorAccess actor, JsonObject challenge, JsonObject session, CancellationToken ct)
    {
        var sid = session.Text("id"); var indexId = Index("room", sid);
        var index = await repository.ReadAsync(Catalog(actor), indexId, ct);
        StoredRecord map;
        if (index is null)
        {
            var rid = Opaque("awr-"); var cid = Opaque("awc-"); var value = Mapping(actor, rid, "room", sid);
            value["internalChallengeId"] = challenge.Text("id"); value["publicChallengeId"] = cid;
            value["aliases"] = new JsonArray(); value["scopeTokens"] = new JsonObject(); value["evidenceTokens"] = new JsonObject();
            var pointer = Mapping(actor, indexId, "room-index", sid); pointer["publicRoomId"] = rid;
            var reference = Mapping(actor, cid, "challenge", sid); reference["publicRoomId"] = rid;
            await repository.CommitAsync(Catalog(actor), [RecordWrite.Create(value), RecordWrite.Create(pointer), RecordWrite.Create(reference)], ct);
            map = await ReadMapping(actor, rid, "room", ct);
        }
        else map = await ReadMapping(actor, index.Value.Text("publicRoomId"), "room", ct);
        await EnsureAliasesAsync(actor, map, session, ct);
        return await ReadMapping(actor, map.Value.Text("id"), "room", ct);
    }
    private async Task EnsureAliasesAsync(ActorAccess actor, StoredRecord map, JsonObject session, CancellationToken ct)
    {
        var aliases = map.Value["aliases"]!.AsArray(); var changed = false;
        foreach (var pid in session.Strings("originalParticipantIds").Concat(session.Strings("participantIds")).Distinct())
        {
            var prior = aliases.Select(x => x!.AsObject()).SingleOrDefault(x => x.Text("internalPersonId") == pid);
            // Historical authors keep their original alias; their later departure must not
            // prevent remaining participants from reading their own room.
            if (prior is not null && !session.Strings("participantIds").Contains(pid)) continue;
            var person = Person(actor, pid);
            if (person is null) throw new DomainException(409, "MEMBERSHIP_UNRESOLVED");
            if (!await BoundProfileAsync(actor,person,ct)) throw new DomainException(409,"SOURCE_REVOKED");
            if (prior is not null)
            {
                if (prior.Text("internalPrincipalId") != person.PrincipalId) throw new DomainException(409,"SOURCE_REVOKED");
                continue;
            }
            var isOwner = person.PrincipalId == session.Text("ownerId");
            var label = isOwner ? "相談した人" : "協力者" + (aliases.Count(x => x!.AsObject().Text("role") != "owner") + 1);
            aliases.Add(new JsonObject { ["internalPersonId"] = pid, ["internalPrincipalId"] = person.PrincipalId,
                ["aliasId"] = Opaque("awa-"), ["aliasLabel"] = label, ["role"] = isOwner ? "owner" : "collaborator" });
            changed = true;
        }
        if (changed) await repository.CommitAsync(Catalog(actor), [RecordWrite.Replace(map.Value, map.ETag)], ct);
    }
    private static AnonymousWorkspaceResolvedParticipant Alias(ActorAccess actor, JsonObject map, string pid)
    {
        var row = map["aliases"]!.AsArray().Select(x => x!.AsObject()).SingleOrDefault(x => x.Text("internalPersonId") == pid)
            ?? throw new DomainException(409, "MEMBERSHIP_UNRESOLVED");
        return new(pid, row.Text("internalPrincipalId"), row.Text("aliasId"), row.Text("aliasLabel"), pid == actor.PersonId);
    }
    // No nested fence/lock: identity-disclosure calls this while already holding both.
    // Every call rereads current source, membership and scope. It never authorizes an invitation as a room.
    public async Task<AnonymousWorkspaceRoomResolution> ResolveRoomAsync(ActorAccess actor, string publicRoomId, CancellationToken ct)
    {
        PublicId(publicRoomId, "awr-"); var map = await ReadMapping(actor, publicRoomId, "room", ct);
        var session = await chat.SessionAsync(actor, map.Value.Text("internalSessionId"), ct);
        var challenge = await chat.ChallengeAsync(actor, map.Value.Text("internalChallengeId"), ct);
        if (challenge.Value.Text("sessionId") != session.Value.Text("id") || challenge.Value.Text("ownerId") != session.Value.Text("ownerId"))
            throw new DomainException(409, "SOURCE_REVOKED");
        await EnsureAliasesAsync(actor, map, session.Value, ct); map = await ReadMapping(actor, publicRoomId, "room", ct);
        return new(publicRoomId, session.Value.Text("id"), challenge.Value.Text("id"), session.ETag,
            session.Value.Number("membershipVersion"), session.Value.Text("ownerId") == actor.PrincipalId,
            session.Value.Strings("participantIds").Select(pid => Alias(actor, map.Value, pid)).ToArray());
    }
    // Internal consent invalidation only. No chat/knowledge/body endpoint calls this resolver.
    // It preserves preexisting peer aliases when a peer becomes unavailable so the
    // disclosure service can permanently clear grants before reporting unavailable.
    public async Task<AnonymousWorkspaceRoomResolution> ResolveIdentityConsentRoomAsync(ActorAccess actor,string publicRoomId,CancellationToken ct)
    {
        PublicId(publicRoomId,"awr-"); var map = await ReadMapping(actor,publicRoomId,"room",ct);
        var current = Person(actor,actor.PersonId);
        if (current is null || current.PrincipalId != actor.PrincipalId || !current.HasRole("member")) throw new DomainException(404,"NOT_FOUND");
        var effective = current with { ScopeIds = current.ScopeIds.Intersect(actor.ScopeIds).ToArray() };
        var session = await chat.SessionAsync(effective,map.Value.Text("internalSessionId"),ct);
        var challenge = await chat.ChallengeAsync(effective,map.Value.Text("internalChallengeId"),ct);
        if (challenge.Value.Text("sessionId") != session.Value.Text("id") || challenge.Value.Text("ownerId") != session.Value.Text("ownerId")) throw new DomainException(409,"SOURCE_REVOKED");
        if (session.Value.Strings("participantIds").Any(pid => !map.Value["aliases"]!.AsArray().Any(x => x!.AsObject().Text("internalPersonId") == pid)))
        {
            // Creating a new alias still requires the complete strict binding/profile check.
            await EnsureAliasesAsync(effective,map,session.Value,ct); map = await ReadMapping(actor,publicRoomId,"room",ct);
        }
        var self = Alias(effective,map.Value,actor.PersonId);
        if (self.PrincipalId != actor.PrincipalId) throw new DomainException(404,"NOT_FOUND");
        return new(publicRoomId,session.Value.Text("id"),challenge.Value.Text("id"),session.ETag,session.Value.Number("membershipVersion"),
            session.Value.Text("ownerId") == actor.PrincipalId,session.Value.Strings("participantIds").Select(pid => Alias(effective,map.Value,pid)).ToArray());
    }
    public Task<T> ConsumeRoomAsync<T>(ActorAccess actor, string roomId, Func<AnonymousWorkspaceRoomResolution, CancellationToken, Task<T>> consume, CancellationToken ct) =>
        fence.RunAsync(actor.TenantId, async token =>
        {
            PublicId(roomId, "awr-"); var map = await ReadMapping(actor, roomId, "room", token);
            return await chat.LockedAsync(actor, map.Value.Text("internalSessionId"), async () =>
                await consume(await ResolveRoomAsync(actor, roomId, token), token), token);
        }, ct);
    public Task<T> ConsumeAsync<T>(ActorAccess actor, Func<CancellationToken, Task<T>> consume, CancellationToken ct)
    { Member(actor); return fence.RunAsync(actor.TenantId, consume, ct); }
    private async Task<(StoredRecord Challenge, StoredRecord Session, StoredRecord Map)> ResolveChallengeAsync(ActorAccess actor, string publicChallengeId, CancellationToken ct)
    {
        PublicId(publicChallengeId, "awc-"); var pointer = await ReadMapping(actor, publicChallengeId, "challenge", ct);
        var map = await ReadMapping(actor, pointer.Value.Text("publicRoomId"), "room", ct);
        var challenge = await chat.ChallengeAsync(actor, map.Value.Text("internalChallengeId"), ct);
        var session = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, map.Value.Text("internalSessionId")), map.Value.Text("internalSessionId"), ct)
            ?? throw new DomainException(404, "NOT_FOUND");
        if (!AccessPolicy.InScope(actor, session.Value)) throw new DomainException(404, "NOT_FOUND");
        return (challenge, session, map);
    }
    private async Task<AnonymousWorkspaceChallenge> ChallengeDtoAsync(ActorAccess actor, StoredRecord challenge, StoredRecord session, JsonObject map,CancellationToken ct)
    {
        var people = map["aliases"]!.AsArray().Select(x => x!.AsObject()).Select(x => new AnonymousWorkspaceResolvedParticipant(x.Text("internalPersonId"),x.Text("internalPrincipalId"),x.Text("aliasId"),x.Text("aliasLabel"),false));
        return new(map.Text("publicChallengeId"), AccessPolicy.CanRead(actor, session.Value) ? map.Text("id") : null,
            await DisplayTextAsync(actor,challenge.Value.Text("title"),people,ct), await DisplayTextAsync(actor,challenge.Value.Text("summary"),people,ct), session.Value.Text("status"),
            challenge.Value.Text("ownerId") == actor.PrincipalId, challenge.ETag);
    }
    public Task<AnonymousWorkspaceChallenge> CreateAsync(ActorAccess actor, string key, AnonymousWorkspaceCreate input, CancellationToken ct) =>
        ConsumeAsync(actor, async token =>
        {
            await FreeInputAsync(actor,[input.Title,input.Summary],null,token);
            var value = await chat.CreateAsync(actor, key, new(input.Title, input.Summary), token);
            var session = await chat.SessionAsync(actor, value.Text("sessionId"), token);
            var challenge = await chat.ChallengeAsync(actor, value.Text("id"), token);
            var map = await EnsureRoomMapAsync(actor, challenge.Value, session.Value, token);
            return await ChallengeDtoAsync(actor, challenge, session, map.Value,token);
        }, ct);
    // Read methods below run only in a Consume callback; HTTP serialization stays inside that fence.
    public async Task<AnonymousWorkspacePage<AnonymousWorkspaceChallenge>> ChallengesUnderFenceAsync(ActorAccess actor, int size, string? cursor, CancellationToken ct)
    {
        var page = await chat.ChallengesAsync(actor, size, cursor, ct); List<AnonymousWorkspaceChallenge> items = [];
        foreach (var challenge in page.Items)
        {
            var sid = challenge.Value.Text("sessionId");
            await chat.LockedAsync(actor, sid, async () =>
            {
                var current = await chat.ChallengeAsync(actor, challenge.Value.Text("id"), ct);
                var session = (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, sid), sid, ct))!;
                var map = await EnsureRoomMapAsync(actor, current.Value, session.Value, ct);
                items.Add(await ChallengeDtoAsync(actor, current, session, map.Value,ct)); return true;
            }, ct);
        }
        return new(items.ToArray(), page.ContinuationToken);
    }
    public async Task<AnonymousWorkspaceChallenge> ChallengeUnderFenceAsync(ActorAccess actor, string id, CancellationToken ct)
    {
        var resolved = await ResolveChallengeAsync(actor, id, ct);
        return await chat.LockedAsync(actor, resolved.Session.Value.Text("id"), async () =>
        {
            resolved = await ResolveChallengeAsync(actor, id, ct);
            return await ChallengeDtoAsync(actor, resolved.Challenge, resolved.Session, resolved.Map.Value,ct);
        }, ct);
    }
    private async Task<StoredRecord> EnsureInvitationMapAsync(ActorAccess actor, JsonObject map, JsonObject participation, CancellationToken ct)
    {
        var idx = Index("invite", map.Text("id"), participation.Text("id")); var index = await repository.ReadAsync(Catalog(actor), idx, ct);
        if (index is not null) return await ReadMapping(actor, index.Value.Text("publicInvitationId"), "invitation", ct);
        var id = Opaque("awi-"); var value = Mapping(actor, id, "invitation", map.Text("internalSessionId"));
        value["publicRoomId"] = map.Text("id"); value["internalParticipationId"] = participation.Text("id"); value["internalPersonId"] = participation.Text("personId");
        var recipient = Person(actor,participation.Text("personId"));
        if (recipient is null || !await BoundProfileAsync(actor,recipient,ct)) throw new DomainException(409,"SOURCE_REVOKED");
        value["recipientPrincipalId"] = recipient.PrincipalId;
        var pointer = Mapping(actor, idx, "invitation-index", map.Text("internalSessionId")); pointer["publicInvitationId"] = id;
        await repository.CommitAsync(Catalog(actor), [RecordWrite.Create(value), RecordWrite.Create(pointer)], ct);
        return await ReadMapping(actor, id, "invitation", ct);
    }
    private async Task<AnonymousWorkspaceInvitation> InvitationDtoAsync(ActorAccess actor, StoredRecord reference, JsonObject map, StoredRecord challenge, StoredRecord session, StoredRecord participation, CancellationToken ct)
    {
        var pid = participation.Value.Text("personId"); var owner = session.Value.Text("ownerId") == actor.PrincipalId;
        if (!AccessPolicy.InScope(actor, session.Value) || (!owner && pid != actor.PersonId)) throw new DomainException(404, "NOT_FOUND");
        // Listing/opening an invitation grants the same recipient authority as a
        // decision, including the original stored principal and legacy alias pin.
        if (!owner) await DecisionAuthorityAsync(actor, reference.Value.Text("id"), ct);
        var aliases = map["aliases"]!.AsArray();
        if (!aliases.Any(x => x!.AsObject().Text("internalPersonId") == pid))
        {
            var person = Person(actor, pid) ?? throw new DomainException(409, "MEMBERSHIP_UNRESOLVED");
            aliases.Add(new JsonObject { ["internalPersonId"] = pid, ["internalPrincipalId"] = person.PrincipalId,
                ["aliasId"] = Opaque("awa-"), ["aliasLabel"] = "協力者" + (aliases.Count(x => x!.AsObject().Text("role") != "owner") + 1), ["role"] = "collaborator" });
            var row = await ReadMapping(actor, map.Text("id"), "room", ct);
            await repository.CommitAsync(Catalog(actor), [RecordWrite.Replace(map, row.ETag)], ct);
        }
        var alias = Alias(actor, map, pid);
        var context = await ChallengeDtoAsync(actor,challenge,session,map,ct);
        return new(reference.Value.Text("id"), map.Text("publicChallengeId"), participation.Value.Text("status") == "accepted" && AccessPolicy.CanRead(actor, session.Value) ? map.Text("id") : null,
            context.Title, context.Summary, participation.Value.Text("status"), owner, alias.AliasId, alias.AliasLabel, participation.ETag);
    }
    public async Task<AnonymousWorkspacePage<AnonymousWorkspaceInvitation>> InvitationsUnderFenceAsync(ActorAccess actor, int size, string? cursor, CancellationToken ct)
    {
        var page = await chat.ChallengesAsync(actor, size, cursor, ct); List<AnonymousWorkspaceInvitation> items = [];
        foreach (var challenge in page.Items)
        {
            var sid = challenge.Value.Text("sessionId");
            await chat.LockedAsync(actor, sid, async () =>
            {
                var current = await chat.ChallengeAsync(actor, challenge.Value.Text("id"), ct);
                var session = (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, sid), sid, ct))!;
                var map = await EnsureRoomMapAsync(actor, current.Value, session.Value, ct);
                var invited = await chat.ParticipationsAsync(actor, sid, ct);
                foreach (var participation in invited.Items)
                {
                    var reference = await EnsureInvitationMapAsync(actor, map.Value, participation.Value, ct);
                    items.Add(await InvitationDtoAsync(actor, reference, map.Value, current, session, participation, ct));
                    map = await ReadMapping(actor, map.Value.Text("id"), "room", ct);
                }
                return true;
            }, ct);
        }
        return new(items.ToArray(), page.ContinuationToken);
    }
    public async Task<AnonymousWorkspaceRoom> RoomUnderFenceAsync(ActorAccess actor, AnonymousWorkspaceRoomResolution resolved, CancellationToken ct)
    {
        var map = await ReadMapping(actor, resolved.PublicRoomId, "room", ct);
        var session = await chat.SessionAsync(actor, resolved.SessionId, ct); var challenge = await chat.ChallengeAsync(actor, resolved.ChallengeId, ct);
        AnonymousWorkspaceInvitation? participation = null;
        if (!resolved.IsOwner)
        {
            var own = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, resolved.SessionId), ConversationService.ParticipationId(actor.PersonId), ct);
            if (own is not null) participation = await InvitationDtoAsync(actor, await EnsureInvitationMapAsync(actor, map.Value, own.Value, ct), map.Value, challenge, session, own, ct);
        }
        return new(resolved.PublicRoomId, await ChallengeDtoAsync(actor, challenge, session, map.Value,ct), session.Value.Text("status"), session.Value.Text("aiAssistance"),
            resolved.IsOwner, resolved.Participants.Length, resolved.Participants.Select(p => new AnonymousWorkspaceAlias(p.AliasId, p.AliasLabel, p.IsSelf)).ToArray(), session.ETag, participation);
    }
    public async Task<AnonymousWorkspacePage<AnonymousWorkspaceRoom>> RoomsUnderFenceAsync(ActorAccess actor, int size, string? cursor, CancellationToken ct)
    {
        var page = await ChallengesUnderFenceAsync(actor, size, cursor, ct); List<AnonymousWorkspaceRoom> rooms = [];
        foreach (var challenge in page.Items.Where(c => c.RoomId is not null))
        {
            var map = await ReadMapping(actor, challenge.RoomId!, "room", ct);
            rooms.Add(await chat.LockedAsync(actor, map.Value.Text("internalSessionId"), async () =>
                await RoomUnderFenceAsync(actor, await ResolveRoomAsync(actor, challenge.RoomId!, ct), ct), ct));
        }
        return new(rooms.ToArray(), page.ContinuationToken);
    }
    private static SearchRequest Conditions(string text)
    {
        SearchInput.RequireSafe(text);
        var terms = ProfileExtraction.Terms(text).Concat(new[] { "引き継ぎ", "チェックリスト", "標準化" }.Where(t => text.Contains(t, StringComparison.Ordinal))).Distinct().Take(20).ToArray();
        var categories = ProfileExtraction.Skills(text).Where(x => x != "unknown").Select(x => new CategoryKey("domain", x)).Take(10).ToArray();
        if (terms.Length + categories.Length == 0) throw new DomainException(422, "SEARCH_CONDITIONS_REQUIRED");
        return new(categories, [], terms);
    }
    public async Task<AnonymousWorkspacePage<AnonymousWorkspaceCandidate>> CandidatesUnderFenceAsync(ActorAccess actor, string challengeId, CancellationToken ct)
    {
        var resolved = await ResolveChallengeAsync(actor, challengeId, ct);
        if (resolved.Challenge.Value.Text("ownerId") != actor.PrincipalId) throw new DomainException(403, "FORBIDDEN");
        return await chat.LockedAsync<AnonymousWorkspacePage<AnonymousWorkspaceCandidate>>(actor, resolved.Session.Value.Text("id"), async () =>
        {
            resolved = await ResolveChallengeAsync(actor, challengeId, ct);
            if (resolved.Session.Value.Text("status") != "open") throw new DomainException(409, "INVALID_TRANSITION");
            var request = Conditions(resolved.Challenge.Value.Text("title") + " " + resolved.Challenge.Value.Text("summary"));
            using var budget = new SearchBudget();
            var raw = await projections.FindAsync(request, actor.Search, ct);
            var allowed = await guard.FilterUnderFenceAsync(actor, raw.Where(x => x.EntityType == "person" && x.EntityId != actor.PersonId), ct);
            var candidates = Retriever.Search(allowed, request, actor.Search).Candidates.Take(10).ToArray(); List<AnonymousWorkspaceCandidate> response = [];
            foreach (var candidate in candidates)
            {
                var person = Person(actor, candidate.EntityId);
                if (person is null || !person.HasRole("member") || !AccessPolicy.InScope(person, resolved.Session.Value) || !await BoundProfileAsync(actor,person,ct)) continue;
                if (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, resolved.Session.Value.Text("id")), ConversationService.ParticipationId(candidate.EntityId), ct) is not null) continue;
                var token = Opaque("awt-"); var row = Mapping(actor, token, "candidate", resolved.Session.Value.Text("id"));
                row["boundPrincipalId"] = actor.PrincipalId; row["boundChallengeId"] = resolved.Challenge.Value.Text("id"); row["boundChallengeEtag"] = resolved.Challenge.ETag;
                row["unit"] = JsonSerializer.SerializeToNode(candidate.Units[0], Json); row["expiresAt"] = clock.GetUtcNow().AddMinutes(10);
                await repository.CommitAsync(Catalog(actor), [RecordWrite.Create(row)], ct);
                response.Add(new(token, "協力候補" + (response.Count + 1), candidate.Routes));
            }
            return new(response.ToArray());
        }, ct);
    }
    public Task<AnonymousWorkspaceInvitation> InviteAsync(ActorAccess actor, string roomId, string key, string etag, AnonymousWorkspaceInvite input, CancellationToken ct) =>
        ConsumeAsync(actor, async token =>
        {
            var resolved = await ResolveRoomAsync(actor, roomId, token);
            if (!resolved.IsOwner) throw new DomainException(403, "FORBIDDEN");
            PublicId(input.CandidateToken, "awt-"); var candidate = await ReadMapping(actor, input.CandidateToken, "candidate", token);
            if (candidate.Value.Text("boundPrincipalId") != actor.PrincipalId || candidate.Value.Text("internalSessionId") != resolved.SessionId ||
                candidate.Value["expiresAt"]!.Deserialize<DateTimeOffset>(Json) <= clock.GetUtcNow()) throw new DomainException(409, "CANDIDATE_EXPIRED");
            var challenge = await chat.ChallengeAsync(actor, resolved.ChallengeId, token);
            if (candidate.Value.Text("boundChallengeId") != resolved.ChallengeId || candidate.Value.Text("boundChallengeEtag") != challenge.ETag) throw new DomainException(409, "SOURCE_REVOKED");
            var unit = candidate.Value["unit"]!.Deserialize<SearchUnit>(Json)!;
            await guard.RequireAsync(actor, unit, token);
            var person = Person(actor,unit.EntityId);
            if (person is null || !await BoundProfileAsync(actor,person,token)) throw new DomainException(409,"SOURCE_REVOKED");
            await chat.InviteAsync(actor, resolved.SessionId, key, etag, new(unit.EntityId), config["Data:Mode"] == "Demo", token);
            // The saved invitation is re-read under the room lock for the public ack.
            return await chat.LockedAsync(actor, resolved.SessionId, async () =>
            {
                var session = await chat.SessionAsync(actor, resolved.SessionId, token); var map = await ReadMapping(actor, roomId, "room", token);
                var participation = (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, resolved.SessionId), ConversationService.ParticipationId(unit.EntityId), token))!;
                return await InvitationDtoAsync(actor, await EnsureInvitationMapAsync(actor, map.Value, participation.Value, token), map.Value, challenge, session, participation, token);
            }, token);
        }, ct);
    private async Task<(StoredRecord Reference,StoredRecord Participation)> DecisionAuthorityAsync(ActorAccess actor,string invitationId,CancellationToken ct)
    {
        PublicId(invitationId, "awi-"); var reference = await ReadMapping(actor, invitationId, "invitation", ct);
        if (reference.Value.Text("internalPersonId") != actor.PersonId) throw new DomainException(404, "NOT_FOUND");
        var target = Person(actor,actor.PersonId);
        var participation = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId,reference.Value.Text("internalSessionId")),reference.Value.Text("internalParticipationId"),ct);
        var session = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId,reference.Value.Text("internalSessionId")),reference.Value.Text("internalSessionId"),ct);
        var expectedRecipient = reference.Value.Text("recipientPrincipalId");
        if (string.IsNullOrEmpty(expectedRecipient))
        {
            // Older local mappings can only recover their binding from an already stored alias.
            var roomMap = await ReadMapping(actor,reference.Value.Text("publicRoomId"),"room",ct);
            expectedRecipient = roomMap.Value["aliases"]!.AsArray().Select(x => x!.AsObject()).SingleOrDefault(x => x.Text("internalPersonId") == actor.PersonId)?.Text("internalPrincipalId") ?? "";
        }
        if (target is null || target.PrincipalId != actor.PrincipalId || expectedRecipient != actor.PrincipalId || !await BoundProfileAsync(actor,target,ct) ||
            session is null || participation is null || session.Value.Text("kind") != "session" || participation.Value.Text("kind") != "participation" ||
            participation.Value.Text("personId") != actor.PersonId || participation.Value.Text("ownerId") != session.Value.Text("ownerId") ||
            !AccessPolicy.InScope(actor,session.Value) || !AccessPolicy.InScope(target,session.Value) || !AccessPolicy.InScope(actor,participation.Value))
            throw new DomainException(404,"NOT_FOUND");
        return (reference,participation!);
    }
    public async Task<AnonymousWorkspaceMutation> DecideAsync(ActorAccess actor, string invitationId, string key, string etag, AnonymousWorkspaceDecision input, CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);
        if (input.Decision is not ("accepted" or "declined" or "left")) throw new ArgumentException("Invalid decision");
        // Replay authority never grants room body access. This narrow fence is released
        // before the existing normal mutation acquires its own durable fence.
        var checkedRequest = await ConsumeAsync(actor,async token =>
        {
            PublicId(invitationId,"awi-"); var reference = await ReadMapping(actor,invitationId,"invitation",token);
            return await chat.LockedAsync(actor,reference.Value.Text("internalSessionId"),async () =>
            {
                var authority = await DecisionAuthorityAsync(actor,invitationId,token); var sid = authority.Reference.Value.Text("internalSessionId");
                var receiptId = "idempotency-"+ConversationService.Hash(JsonSerializer.Serialize(new[]{actor.PrincipalId,"decision",key}));
                var receipt = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId,sid),receiptId,token);
                if (receipt is null)
                {
                    if (authority.Participation.Value.Text("status") is "declined" or "left") throw new DomainException(409,"INVALID_TRANSITION");
                    return (authority.Reference, Replay: (AnonymousWorkspaceMutation?)null);
                }
                var fingerprint = ConversationService.Hash(RecordJson.Canonical(new JsonObject { ["decision"] = input.Decision })+":"+etag);
                if (receipt.Value.Text("kind") != "idempotency" || receipt.Value.Text("tenantId") != actor.TenantId || receipt.Value.Text("ownerId") != actor.PrincipalId ||
                    receipt.Value.Text("sessionId") != sid || receipt.Value.Text("fingerprint") != fingerprint || receipt.Value["result"] is not JsonObject result ||
                    result.Text("sessionId") != sid || result.Text("id") != authority.Participation.Value.Text("id") || result.Text("status") != input.Decision)
                    throw new DomainException(409,"IDEMPOTENCY_CONFLICT");
                var status = authority.Participation.Value.Text("status");
                return (authority.Reference, Replay: (AnonymousWorkspaceMutation?)new(invitationId,status,status == "accepted" ? authority.Reference.Value.Text("publicRoomId") : null));
            },token);
        },ct);
        if (checkedRequest.Replay is not null) return checkedRequest.Replay;
        var result = await chat.DecideAsync(actor,checkedRequest.Reference.Value.Text("internalSessionId"),key,etag,new(input.Decision),ct);
        return new(invitationId,result.Text("status"),result.Text("status") == "accepted" ? checkedRequest.Reference.Value.Text("publicRoomId") : null);
    }
    public async Task<AnonymousWorkspaceInvitation> InvitationUnderFenceAsync(ActorAccess actor, string id, CancellationToken ct)
    {
        PublicId(id,"awi-"); var reference = await ReadMapping(actor,id,"invitation",ct); var sid = reference.Value.Text("internalSessionId");
        return await chat.LockedAsync(actor,sid,async () =>
        {
            var map = await ReadMapping(actor,reference.Value.Text("publicRoomId"),"room",ct);
            var session = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId,sid),sid,ct) ?? throw new DomainException(404,"NOT_FOUND");
            var challenge = await repository.ReadAsync(Catalog(actor),map.Value.Text("internalChallengeId"),ct) ?? throw new DomainException(404,"NOT_FOUND");
            var participation = await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId,sid),reference.Value.Text("internalParticipationId"),ct) ?? throw new DomainException(404,"NOT_FOUND");
            return await InvitationDtoAsync(actor,reference,map.Value,challenge,session,participation,ct);
        },ct);
    }
    public async Task<AnonymousWorkspaceMutation> DecisionUnderFenceAsync(ActorAccess actor,string id,CancellationToken ct)
    {
        var authority = await DecisionAuthorityAsync(actor,id,ct);
        var invitation = await InvitationUnderFenceAsync(actor,authority.Reference.Value.Text("id"),ct); return new(id,invitation.Status,invitation.RoomId);
    }
    public Task<AnonymousWorkspaceRoom> UpdateAsync(ActorAccess actor, string roomId, string key, string etag, AnonymousWorkspaceUpdate input, CancellationToken ct) =>
        ConsumeAsync(actor, async token =>
        {
            var resolved = await ResolveRoomAsync(actor, roomId, token);
            await chat.UpdateAsync(actor, resolved.SessionId, key, etag, new(input.AiAssistance, input.Status), token);
            return await chat.LockedAsync(actor, resolved.SessionId, async () => await RoomUnderFenceAsync(actor, await ResolveRoomAsync(actor, roomId, token), token), token);
        }, ct);
    private async Task<AnonymousWorkspaceMessage> MessageDtoAsync(ActorAccess actor, AnonymousWorkspaceRoomResolution resolved, JsonObject message, CancellationToken ct)
    {
        var map = await ReadMapping(actor, resolved.PublicRoomId, "room", ct);
        var indexId = Index("message", resolved.SessionId, message.Text("id")); var index = await repository.ReadAsync(Catalog(actor), indexId, ct);
        string id;
        if (index is null)
        {
            id = Opaque("awm-"); var value = Mapping(actor, indexId, "message-index", resolved.SessionId); value["publicMessageId"] = id;
            await repository.CommitAsync(Catalog(actor), [RecordWrite.Create(value)], ct);
        }
        else id = index.Value.Text("publicMessageId");
        var self = message.Text("ownerId") == actor.PrincipalId && message.Text("authorType") == "human";
        var author = map.Value["aliases"]!.AsArray().Select(x => x!.AsObject()).SingleOrDefault(x => x.Text("internalPrincipalId") == message.Text("ownerId"));
        var ai = message.Text("authorType") == "ai";
        var clientId = self && Guid.TryParse(message.Text("clientMessageId"), out _) ? message.Text("clientMessageId") : null;
        return new(id, resolved.PublicRoomId, await DisplayTextAsync(actor,message.Text("body"),resolved.Participants,ct), ai ? "ai" : "human", ai ? "ai" : author?.Text("aliasId") ?? "unknown",
            ai ? "AI支援" : author?.Text("aliasLabel") ?? "参加者", self, message.Number("sequence"), "saved", message.Text("citationState"), clientId);
    }
    public async Task<AnonymousWorkspaceMessages> MessagesUnderFenceAsync(ActorAccess actor, AnonymousWorkspaceRoomResolution resolved, int after, int size, CancellationToken ct)
    {
        var page = await chat.MessagesAsync(actor, resolved.SessionId, after, size, ct); List<AnonymousWorkspaceMessage> items = [];
        foreach (var value in page.Items) items.Add(await MessageDtoAsync(actor, resolved, value, ct));
        return new(items.ToArray(), page.Next, page.More ? page.Next.ToString() : null);
    }
    public async Task<AnonymousWorkspaceMessage> MessageAtUnderFenceAsync(ActorAccess actor,AnonymousWorkspaceRoomResolution room,int sequence,CancellationToken ct)
    {
        var page = await MessagesUnderFenceAsync(actor,room,sequence-1,1,ct);
        return page.Items.SingleOrDefault(x => x.Sequence == sequence) ?? throw new DomainException(404,"NOT_FOUND");
    }
    public Task<AnonymousWorkspaceMessage> SendAsync(ActorAccess actor, string roomId, string key, string etag, AnonymousWorkspaceSend input, CancellationToken ct) =>
        ConsumeAsync(actor, async token =>
        {
            if (!Guid.TryParseExact(input.ClientMessageId, "D", out _)) throw new ArgumentException("Random client message UUID required");
            var resolved = await ResolveRoomAsync(actor, roomId, token);
            await FreeInputAsync(actor,[input.Body],resolved.Participants,token);
            var value = await chat.SendAsync(actor, resolved.SessionId, key, etag, new(input.ClientMessageId, input.Body), token);
            return await chat.LockedAsync(actor, resolved.SessionId, async () => await MessageDtoAsync(actor, await ResolveRoomAsync(actor, roomId, token), value, token), token);
        }, ct);
    public async Task NotifySavedAsync(ActorAccess actor,string publicRoomId,int sequence,JobNotifications notifications,CancellationToken ct)
    {
        // Routing stays private. The durable worker rechecks current membership/source before AI.
        // A hint is best effort; committed data and retryable outbox remain authoritative.
        using var notification = CancellationTokenSource.CreateLinkedTokenSource(ct);
        notification.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            PublicId(publicRoomId,"awr-"); var map = await ReadMapping(actor,publicRoomId,"room",notification.Token);
            var sid = map.Value.Text("internalSessionId");
            await notifications.TrySendAsync(actor.TenantId,$"{sid}:committed:{sequence:D10}",notification.Token,sid);
        }
        catch (Exception) { /* A lost hint must not report a committed message as an unsaved message. */ }
    }
}
