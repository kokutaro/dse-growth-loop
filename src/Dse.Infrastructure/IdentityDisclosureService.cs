using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed class IdentityDisclosureService(IPrimaryRepository repository, AnonymousWorkspaceService workspace,
    ConversationService chat, AccessFence fence, IConfiguration configuration, TimeProvider clock)
{
    private sealed record Snapshot(AnonymousWorkspaceRoomResolution Room, StoredRecord Session,
        string Version, string Etag, bool Eligible, IReadOnlyDictionary<string, string> Names);

    // Production resolutions are recreated from the current binding/grant configuration,
    // so disabling an account or changing a grant invalidates previously issued consent.
    private ActorAccess? Identity(string tenant, string person)
    {
        if (configuration["Data:Mode"] == "Demo" && tenant == "demo") return IdentityResolver.DemoPerson(person);
        return new IdentityResolver(configuration).ResolvePerson(tenant, person);
    }
    private ActorAccess CurrentActor(ActorAccess actor)
    {
        var current = Identity(actor.TenantId, actor.PersonId);
        if (current is null || current.PrincipalId != actor.PrincipalId || !current.HasRole("member") || !actor.HasRole("member"))
            throw new DomainException(404, "NOT_FOUND");
        var scopes = current.ScopeIds.Intersect(actor.ScopeIds, StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0) throw new DomainException(404, "NOT_FOUND");
        return current with { ScopeIds = scopes };
    }
    private async Task<Snapshot> SnapshotAsync(ActorAccess actor, string publicRoomId, CancellationToken ct)
    {
        var room = await workspace.ResolveIdentityConsentRoomAsync(CurrentActor(actor), publicRoomId, ct);
        var session = await chat.SessionAsync(CurrentActor(actor), room.SessionId, ct);
        var state = session.Value["identityDisclosure"] as JsonObject;
        var members = session.Value.Strings("participantIds").Order(StringComparer.Ordinal).ToArray();
        var fingerprint = new JsonObject
        {
            ["room"] = publicRoomId, ["session"] = room.SessionId, ["membership"] = session.Value.Number("membershipVersion"),
            ["scopeIds"] = session.Value["scopeIds"]?.DeepClone(), ["status"] = session.Value.Text("status"),
            ["generation"] = state?.Text("generation") ?? "initial", ["fieldSet"] = IdentityDisclosureConsent.DisplayNameFieldSet
        };
        var people = new JsonArray(); var names = new Dictionary<string, string>();
        var eligible = members.Length >= 2 && room.Participants.Length == members.Length;
        foreach (var personId in members)
        {
            var identity = Identity(actor.TenantId, personId);
            var person = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), personId, ct);
            // Keep all current profile content in the private fingerprint, never the public DTO.
            var row = new JsonObject { ["person"] = personId, ["profile"] = person?.Value.DeepClone(),
                ["identity"] = JsonSerializer.SerializeToNode(identity, ProjectionRules.Json),
                ["binding"] = JsonSerializer.SerializeToNode((configuration.GetSection("Auth:Bindings").Get<IdentityBinding[]>() ?? [])
                    .SingleOrDefault(b => b.Enabled && b.TenantId == actor.TenantId && b.PersonId == personId), ProjectionRules.Json) };
            people.Add(row);
            var valid = identity is not null && person is not null && identity.HasRole("member") &&
                person.Value.Text("kind") == "person" && person.Value.Text("id") == personId &&
                person.Value.Text("ownerId") == identity.PrincipalId && AccessPolicy.InScope(identity, session.Value) &&
                AccessPolicy.InScope(identity, person.Value) && AccessPolicy.InScope(actor, person.Value) &&
                person.Value.Number("sourceVersion") >= 1 && person.Value.Number("accessVersion") >= 1 &&
                person.Value["available"]?.GetValue<bool>() == true && (person.Value.Text("publicationState") is "" or "published") &&
                !string.IsNullOrWhiteSpace(person.Value.Text("displayName")) && person.Value.Text("displayName").EnumerateRunes().Count() <= 100;
            eligible &= valid;
            if (valid) names.Add(personId, person!.Value.Text("displayName"));
        }
        fingerprint["people"] = people;
        var version = ConversationService.Hash(RecordJson.Canonical(fingerprint));
        if (state is not null && state["consents"] is JsonObject oldConsents && oldConsents.Count > 0 && state.Text("boundVersion") != version)
        {
            // An observed authority/profile change permanently invalidates prior grants,
            // including when a permission setting is subsequently restored to its old value.
            state["generation"] = Guid.NewGuid().ToString("N"); state["consents"] = new JsonObject(); state["boundVersion"] = null;
            session.Value["sourceVersion"] = session.Value.Number("sourceVersion") + 1; session.Value["updatedAt"] = clock.GetUtcNow();
            await repository.CommitAsync(RecordPartition.Conversation(actor.TenantId, room.SessionId),
                [RecordWrite.Replace(session.Value, session.ETag)], ct);
            return await SnapshotAsync(actor, publicRoomId, ct);
        }
        var etag = "\"" + ConversationService.Hash(version + ":" + RecordJson.Canonical(state)) + "\"";
        return new(room, session, version, etag, eligible, names);
    }
    private static IdentityDisclosureView View(Snapshot snapshot, ActorAccess actor)
    {
        var consents = snapshot.Session.Value["identityDisclosure"]?["consents"] as JsonObject;
        bool Granted(string person) => consents?[person] is JsonObject consent &&
            consent.Text("reviewedVersion") == snapshot.Version && consent["granted"]?.GetValue<bool>() == true;
        var all = snapshot.Eligible && snapshot.Room.Participants.All(p => Granted(p.PersonId));
        return new(snapshot.Room.PublicRoomId, snapshot.Version, IdentityDisclosureConsent.DisplayNameFieldSet,
            snapshot.Etag, Granted(actor.PersonId), all ? "shared" : snapshot.Eligible ? "pending" : "unavailable",
            snapshot.Room.Participants.Select(p => new IdentityDisclosureParticipant(p.AliasId, p.AliasLabel,
                all ? snapshot.Names[p.PersonId] : null)).ToArray());
    }
    private Task<T> LockedAsync<T>(ActorAccess actor, string publicRoomId, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        => fence.RunAsync(actor.TenantId, async token =>
        {
            var room = await workspace.ResolveIdentityConsentRoomAsync(CurrentActor(actor), publicRoomId, token);
            return await chat.LockedAsync(actor, room.SessionId, () => action(token), token);
        }, ct);

    // The API must serialize in consume. Neither names nor a stale cached response leave this boundary.
    public Task<T> ConsumeAsync<T>(ActorAccess actor, string publicRoomId,
        Func<IdentityDisclosureView, CancellationToken, Task<T>> consume, CancellationToken ct)
        => LockedAsync(actor, publicRoomId, async token =>
        {
            var snapshot = await SnapshotAsync(actor, publicRoomId, token);
            return await consume(View(snapshot, actor), token);
        }, ct);
    public Task<IdentityDisclosureView> ReadAsync(ActorAccess actor, string publicRoomId, CancellationToken ct)
        => ConsumeAsync(actor, publicRoomId, (value, _) => Task.FromResult(value), ct);

    public Task<T> ChangeConsumeAsync<T>(ActorAccess actor, string publicRoomId, string key, string etag,
        IdentityDisclosureConsent input, Func<IdentityDisclosureView, CancellationToken, Task<T>> consume, CancellationToken ct)
    {
        input.Validate(); IdempotencyExecutor.ValidateHeaders(key, etag, true);
        return LockedAsync(actor, publicRoomId, async token =>
        {
            var snapshot = await SnapshotAsync(actor, publicRoomId, token);
            var partition = RecordPartition.Conversation(actor.TenantId, snapshot.Room.SessionId);
            var receiptId = "identity-consent-" + ConversationService.Hash(actor.PrincipalId + ":" + key);
            var hash = ConversationService.Hash(JsonSerializer.Serialize(input, ProjectionRules.Json) + ":" + etag);
            var receipt = await repository.ReadAsync(partition, receiptId, token);
            if (receipt is not null)
            {
                if (receipt.Value.Text("fingerprint") != hash) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
                // Retries return current state; they never replay a prior name disclosure or restore withdrawn consent.
                return await consume(View(await SnapshotAsync(actor, publicRoomId, token), actor), token);
            }
            if (snapshot.Etag != etag) throw new DomainException(412, "STALE_VERSION");
            if (snapshot.Version != input.ReviewedVersion) throw new DomainException(409, "STALE_IDENTITY_REVIEW");
            if (input.Granted && !snapshot.Eligible) throw new DomainException(409, "IDENTITY_UNAVAILABLE");
            var state = snapshot.Session.Value["identityDisclosure"] as JsonObject ?? new JsonObject { ["generation"] = "initial", ["consents"] = new JsonObject() };
            if (!input.Granted)
            {
                // Withdrawal invalidates everyone's prior grant, including an old idempotent request.
                state["generation"] = Guid.NewGuid().ToString("N"); state["consents"] = new JsonObject();
                state["boundVersion"] = null;
            }
            else
            {
                (state["consents"] ??= new JsonObject())[actor.PersonId] = new JsonObject
                { ["granted"] = true, ["reviewedVersion"] = snapshot.Version, ["at"] = clock.GetUtcNow() };
                state["boundVersion"] = snapshot.Version;
            }
            // Assign only when not already attached to the session JsonObject.
            if (state.Parent is null) snapshot.Session.Value["identityDisclosure"] = state;
            snapshot.Session.Value["sourceVersion"] = snapshot.Session.Value.Number("sourceVersion") + 1;
            snapshot.Session.Value["updatedAt"] = clock.GetUtcNow();
            var saved = RecordJson.New("idempotency", receiptId, actor.TenantId, actor.PrincipalId, snapshot.Session.Value.Strings("scopeIds"), clock.GetUtcNow());
            saved["sessionId"] = snapshot.Room.SessionId; saved["fingerprint"] = hash; saved["state"] = "complete";
            saved["operation"] = "identity-consent"; saved["granted"] = input.Granted;
            saved["reviewedVersion"] = input.ReviewedVersion; saved["fieldSet"] = input.FieldSet;
            await repository.CommitAsync(partition, [RecordWrite.Replace(snapshot.Session.Value, snapshot.Session.ETag), RecordWrite.Create(saved)], token);
            return await consume(View(await SnapshotAsync(actor, publicRoomId, token), actor), token);
        }, ct);
    }
    public Task<IdentityDisclosureView> ChangeAsync(ActorAccess actor, string publicRoomId, string key, string etag,
        IdentityDisclosureConsent input, CancellationToken ct)
        => ChangeConsumeAsync(actor, publicRoomId, key, etag, input, (value, _) => Task.FromResult(value), ct);
}
