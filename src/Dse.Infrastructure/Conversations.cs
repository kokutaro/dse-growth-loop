using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// One API replica. Stripes bound memory; ETags/transactional batches remain the durable concurrency guard.
public sealed class ConversationLocks
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public async Task<T> RunAsync<T>(string key, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = gates[(uint)StringComparer.Ordinal.GetHashCode(key) % gates.Length];
        await gate.WaitAsync(ct);
        try { return await action(); } finally { gate.Release(); }
    }
}

public sealed class ConversationService(IPrimaryRepository repository, ConversationLocks locks, IdentityResolver identities, TimeProvider clock, AccessFence? accessFence = null)
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashParts(params string[] parts) => Hash(JsonSerializer.Serialize(parts));
    public static string MessageId(string sid, int sequence) => $"{sid}:message:{sequence:D10}";
    public static string ParticipationId(string person) => "participation-" + Hash(person);
    private static void Id(string? id) { if (id is null || !RecordJson.IsId(id)) throw new ArgumentException("Invalid id"); }
    private static void Text(string? value, int max) { if (string.IsNullOrWhiteSpace(value) || value.EnumerateRunes().Count() > max) throw new ArgumentException("Invalid text"); }
    private static void Member(ActorAccess actor) { if (!actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN"); }
    private static void Match(StoredRecord record, string etag) { if (record.ETag != etag) throw new DomainException(412, "STALE_VERSION"); }
    private static void Open(JsonObject session) { if (session.Text("status") != "open") throw new DomainException(409, "INVALID_TRANSITION"); }
    private void Touch(JsonObject value) { value["sourceVersion"] = value.Number("sourceVersion") + 1; value["updatedAt"] = clock.GetUtcNow(); }
    private static RecordPartition Partition(ActorAccess actor, string session) { Id(session); return RecordPartition.Conversation(actor.TenantId, session); }
    public Task<T> LockedAsync<T>(ActorAccess actor, string session, Func<Task<T>> action, CancellationToken ct) => locks.RunAsync(actor.TenantId + ":" + session, action, ct);

    public async Task<StoredRecord> SessionAsync(ActorAccess actor, string sessionId, CancellationToken ct)
    {
        Member(actor);
        var row = await repository.ReadAsync(Partition(actor, sessionId), sessionId, ct);
        if (row is null || row.Value.Text("kind") != "session") throw new DomainException(404, "NOT_FOUND");
        AccessPolicy.RequireRead(actor, row.Value);
        return row;
    }
    // Invitation access deliberately does not grant conversation/history access.
    private async Task<StoredRecord> InvitationSession(ActorAccess actor, string id, CancellationToken ct)
    {
        Member(actor);
        var session = await repository.ReadAsync(Partition(actor, id), id, ct);
        if (session is null || session.Value.Text("kind") != "session" || !AccessPolicy.InScope(actor, session.Value)) throw new DomainException(404, "NOT_FOUND");
        var invitation = await repository.ReadAsync(Partition(actor, id), ParticipationId(actor.PersonId), ct);
        if (session.Value.Text("ownerId") != actor.PrincipalId &&
            (invitation is null || invitation.Value.Text("status") is not ("requested" or "accepted"))) throw new DomainException(404, "NOT_FOUND");
        return session;
    }

    public Task<JsonObject> CreateAsync(ActorAccess actor, string key, ChallengeCreate input, CancellationToken ct)
    {
        Member(actor); Text(input.Title, 300); Text(input.Summary, 8000); IdempotencyExecutor.ValidateHeaders(key, null, false);
        var id = "challenge-" + HashParts(actor.TenantId, actor.PrincipalId, key);
        return LockedAsync(actor, id, async () =>
        {
            var partition = RecordPartition.Catalog(actor.TenantId);
            var fingerprint = Hash(JsonSerializer.Serialize(input));
            var challenge = await repository.ReadAsync(partition, id, ct);
            var jobId = "session-init-" + Hash(id);
            if (challenge is null)
            {
                var now = clock.GetUtcNow();
                var value = RecordJson.New("challenge", id, actor.TenantId, actor.PrincipalId, actor.ScopeIds, now);
                value["title"] = input.Title; value["summary"] = input.Summary; value["status"] = "open";
                value["sessionId"] = "session-" + Hash(id); value["analysisConsent"] = "not_granted";
                value["consentedVersion"] = null; value["accessVersion"] = 1;
                var job = Jobs.Create(value, "sessionInitialization", now, jobId);
                job["requestHash"] = fingerprint; job["ownerPersonId"] = actor.PersonId;
                await repository.CommitAsync(partition, [RecordWrite.Create(value), RecordWrite.Create(job)], ct);
                challenge = (await repository.ReadAsync(partition, id, ct))!;
            }
            AccessPolicy.RequireRead(actor, challenge.Value);
            var savedJob = (await repository.ReadAsync(partition, jobId, ct))!;
            if (savedJob.Value.Text("requestHash") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
            await InitializeAsync(challenge.Value, savedJob, ct);
            return challenge.Value;
        }, ct);
    }
    // Catalog -> conversations is a resumable, deterministic saga, never a cross-container transaction.
    private async Task InitializeAsync(JsonObject challenge, StoredRecord job, CancellationToken ct)
    {
        var sid = challenge.Text("sessionId");
        var partition = RecordPartition.Conversation(challenge.Text("tenantId"), sid);
        var existing = await repository.ReadAsync(partition, sid, ct);
        if (existing is null)
        {
            var now = clock.GetUtcNow();
            var session = RecordJson.New("session", sid, challenge.Text("tenantId"), challenge.Text("ownerId"), challenge.Strings("scopeIds"), now);
            session["sessionId"] = sid; session["challengeId"] = challenge.Text("id");
            session["participantIds"] = RecordJson.Array([job.Value.Text("ownerPersonId")]);
            session["originalParticipantIds"] = session["participantIds"]!.DeepClone();
            session["membershipVersion"] = 1; session["status"] = "open"; session["aiAssistance"] = "stopped"; session["lastSequence"] = 0;
            try { await repository.CommitAsync(partition, [RecordWrite.Create(session)], ct); }
            catch (StoreConflictException) { if (await repository.ReadAsync(partition, sid, ct) is null) throw; }
        }
        if (job.Value.Text("status") != "ready")
        {
            job.Value["status"] = "ready"; job.Value["resultId"] = sid; job.Value["notificationState"] = "sent";
            try { await repository.CommitAsync(RecordPartition.Catalog(challenge.Text("tenantId")), [RecordWrite.Replace(job.Value, job.ETag)], ct); }
            catch (StoreConflictException) { /* Another recovery completed the same deterministic initialization. */ }
        }
    }
    public async Task<StoredRecord> ChallengeAsync(ActorAccess actor, string id, CancellationToken ct)
    {
        Member(actor); Id(id);
        var row = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), id, ct);
        if (row is null || row.Value.Text("kind") != "challenge" || !AccessPolicy.InScope(actor, row.Value)) throw new DomainException(404, "NOT_FOUND");
        if (row.Value.Text("ownerId") == actor.PrincipalId)
        {
            var job = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), "session-init-" + Hash(id), ct);
            if (job is not null && job.Value.Text("status") != "ready") await InitializeAsync(row.Value, job, ct);
        }
        else await InvitationSession(actor, row.Value.Text("sessionId"), ct);
        return row;
    }
    public async Task<RecordPage> ChallengesAsync(ActorAccess actor, int size, string? cursor, CancellationToken ct)
    {
        Member(actor);
        var page = await repository.QueryAsync(RecordPartition.Catalog(actor.TenantId), "challenge", null, null, size, cursor, ct);
        List<StoredRecord> visible = [];
        foreach (var row in page.Items)
        {
            try { visible.Add(await ChallengeAsync(actor, row.Value.Text("id"), ct)); }
            catch (DomainException e) when (e.Status == 404) { }
        }
        return new(visible.ToArray(), page.ContinuationToken);
    }
    public async Task<RecordPage> ParticipationsAsync(ActorAccess actor, string sid, CancellationToken ct)
    {
        var session = await InvitationSession(actor, sid, ct);
        if (session.Value.Text("ownerId") != actor.PrincipalId)
        {
            var own = await repository.ReadAsync(Partition(actor, sid), ParticipationId(actor.PersonId), ct);
            return new(own is null ? [] : [own], null);
        }
        return await repository.QueryAsync(Partition(actor, sid), "participation", null, null, 100, null, ct);
    }

    private async Task<JsonObject> Mutate(ActorAccess actor, string sid, string operation, string key, string etag, JsonObject body,
        Func<StoredRecord, Task<(RecordWrite[] Writes, JsonObject Result)>> build, bool invitation, CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key, etag, true);
        return await LockedAsync(actor, sid, async () =>
        {
            var session = invitation ? await InvitationSession(actor, sid, ct) : await SessionAsync(actor, sid, ct);
            var partition = Partition(actor, sid);
            var receiptId = "idempotency-" + HashParts(actor.PrincipalId, operation, key);
            var fingerprint = Hash(RecordJson.Canonical(body) + ":" + etag);
            var receipt = await repository.ReadAsync(partition, receiptId, ct);
            if (receipt is not null)
            {
                if (receipt.Value.Text("fingerprint") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
                return receipt.Value["result"]!.DeepClone().AsObject();
            }
            var plan = await build(session);
            var result = new JsonObject { ["id"] = plan.Result.Text("id"), ["sessionId"] = sid,
                ["status"] = plan.Result.Text("status"), ["sourceVersion"] = plan.Result.Number("sourceVersion") };
            var record = RecordJson.New("idempotency", receiptId, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow());
            record["sessionId"] = sid; record["fingerprint"] = fingerprint; record["result"] = result.Copy();
            await repository.CommitAsync(partition, [.. plan.Writes, RecordWrite.Create(record)], ct);
            return result;
        }, ct);
    }
    public Task<JsonObject> InviteAsync(ActorAccess actor, string sid, string key, string etag, ParticipationCreate input, bool demo, CancellationToken ct)
    {
        Id(input.PersonId);
        return Mutate(actor, sid, "invite", key, etag, new() { ["personId"] = input.PersonId }, async session =>
        {
            if (session.Value.Text("ownerId") != actor.PrincipalId) throw new DomainException(403, "FORBIDDEN");
            Match(session, etag); Open(session.Value);
            var target = demo ? IdentityResolver.DemoPerson(input.PersonId) : identities.ResolvePerson(actor.TenantId, input.PersonId);
            var person = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), input.PersonId, ct);
            if (target is null || person is null || !AccessPolicy.CanUse(actor, person.Value) || !AccessPolicy.InScope(target, session.Value) ||
                !target.HasRole("member") || input.PersonId == actor.PersonId) throw new DomainException(404, "NOT_FOUND");
            var partition = Partition(actor, sid);
            var invitations = await repository.QueryAsync(partition, "participation", null, null, 100, null, ct);
            if (invitations.Items.Length >= 19 || invitations.ContinuationToken is not null) throw new DomainException(409, "PARTICIPANT_LIMIT");
            var id = ParticipationId(input.PersonId);
            if (await repository.ReadAsync(partition, id, ct) is not null) throw new DomainException(409, "INVALID_TRANSITION");
            var participation = RecordJson.New("participation", id, actor.TenantId, actor.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
            participation["sessionId"] = sid; participation["challengeId"] = session.Value.Text("challengeId");
            participation["personId"] = input.PersonId; participation["status"] = "requested"; participation["decidedBy"] = null;
            Touch(session.Value);
            return ([RecordWrite.Replace(session.Value, session.ETag), RecordWrite.Create(participation)], participation);
        }, false, ct);
    }
    public Task<JsonObject> DecideAsync(ActorAccess actor, string sid, string key, string etag, ParticipationDecision input, CancellationToken ct)
    {
        if (input.Decision is not ("accepted" or "declined" or "left")) throw new ArgumentException("Invalid decision");
        return (accessFence ?? new AccessFence(repository)).RunAsync(actor.TenantId, token => Mutate(actor, sid, "decision", key, etag, new() { ["decision"] = input.Decision }, async session =>
        {
            var row = await repository.ReadAsync(Partition(actor, sid), ParticipationId(actor.PersonId), ct) ?? throw new DomainException(404, "NOT_FOUND");
            Match(row, etag); Open(session.Value);
            if (input.Decision == "left" ? row.Value.Text("status") != "accepted" : row.Value.Text("status") != "requested") throw new DomainException(409, "INVALID_TRANSITION");
            row.Value["status"] = input.Decision; row.Value["decidedBy"] = new JsonObject { ["type"] = "principal", ["id"] = actor.PrincipalId };
            Touch(row.Value); Touch(session.Value);
            if (input.Decision is "accepted" or "left")
            {
                var participants = session.Value.Strings("participantIds").Where(x => x != actor.PersonId).ToList();
                if (input.Decision == "accepted")
                {
                    participants.Add(actor.PersonId);
                    session.Value["originalParticipantIds"] = RecordJson.Array(session.Value.Strings("originalParticipantIds").Concat(participants).Distinct());
                }
                session.Value["participantIds"] = RecordJson.Array(participants);
                session.Value["membershipVersion"] = session.Value.Number("membershipVersion") + 1;
            }
            return ([RecordWrite.Replace(row.Value, row.ETag), RecordWrite.Replace(session.Value, session.ETag)], row.Value);
        }, true, token), ct);
    }
    public Task<JsonObject> UpdateAsync(ActorAccess actor, string sid, string key, string etag, SessionUpdate input, CancellationToken ct)
    {
        if (input.AiAssistance is not ("enabled" or "stopped") || input.Status is not ("open" or "closed")) throw new ArgumentException("Invalid session");
        return Mutate(actor, sid, "update", key, etag, new() { ["aiAssistance"] = input.AiAssistance, ["status"] = input.Status }, session =>
        {
            Match(session, etag); Open(session.Value);
            if (input.Status == "closed" && session.Value.Text("ownerId") != actor.PrincipalId) throw new DomainException(403, "FORBIDDEN");
            session.Value["aiAssistance"] = input.AiAssistance; session.Value["status"] = input.Status; Touch(session.Value);
            return Task.FromResult<(RecordWrite[], JsonObject)>(([RecordWrite.Replace(session.Value, session.ETag)], session.Value));
        }, false, ct);
    }
    public async Task<JsonObject> SendAsync(ActorAccess actor, string sid, string key, string etag, MessageCreate input, CancellationToken ct)
    {
        Id(input.ClientMessageId); Text(input.Body, 8000); IdempotencyExecutor.ValidateHeaders(key, etag, true);
        try
        {
            return await LockedAsync(actor, sid, async () =>
            {
                var session = await SessionAsync(actor, sid, ct);
                var partition = Partition(actor, sid);
                var dedupId = "idempotency-" + HashParts("message", actor.PrincipalId, input.ClientMessageId);
                var requestId = "idempotency-" + HashParts("send", actor.PrincipalId, key);
                var fingerprint = Hash(JsonSerializer.Serialize(input));
                foreach (var id in new[] { requestId, dedupId })
                {
                    var saved = await repository.ReadAsync(partition, id, ct);
                    if (saved is null) continue;
                    if (saved.Value.Text("fingerprint") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
                    return (await repository.ReadAsync(partition, saved.Value.Text("messageId"), ct))!.Value;
                }
                Match(session, etag); Open(session.Value);
                var sequence = checked(session.Value.Number("lastSequence") + 1);
                var message = RecordJson.New("message", MessageId(sid, sequence), actor.TenantId, actor.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
                message["sessionId"] = sid; message["clientMessageId"] = input.ClientMessageId; message["sequence"] = sequence;
                message["authorType"] = "human"; message["body"] = input.Body; message["deliveryState"] = "saved";
                message["sourceSnapshots"] = new JsonArray(); message["retrievalRunId"] = null;
                session.Value["lastSequence"] = sequence; Touch(session.Value);
                var job = Jobs.Create(message, "messageCommitted", clock.GetUtcNow(), $"{sid}:committed:{sequence:D10}");
                job["event"] = JsonSerializer.SerializeToNode(new MessageCommitted(job.Text("id"), actor.TenantId, sid, message.Text("id"), sequence, session.Value.Number("membershipVersion")), ProjectionRules.Json);
                List<RecordWrite> writes = [RecordWrite.Create(message), RecordWrite.Replace(session.Value, session.ETag), RecordWrite.Create(job)];
                foreach (var id in new[] { requestId, dedupId })
                {
                    var receipt = RecordJson.New("idempotency", id, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow());
                    receipt["sessionId"] = sid; receipt["fingerprint"] = fingerprint; receipt["messageId"] = message.Text("id");
                    writes.Add(RecordWrite.Create(receipt));
                }
                await repository.CommitAsync(partition, writes, ct);
                return (await repository.ReadAsync(partition, message.Text("id"), ct))!.Value;
            }, ct);
        }
        catch (Exception e) when (e is not ArgumentException && e is not StoreConflictException && (e is not DomainException de || de.Status >= 500))
        { throw new DomainException(503, "SAVE_FAILED"); }
    }
    public Task<JsonObject> FeedbackAsync(ActorAccess actor, string sid, string messageId, string key, string etag, MessageFeedback input, CancellationToken ct)
    {
        Id(messageId);
        if (input.Decision is not ("helpful" or "unnecessary")) throw new ArgumentException("Invalid feedback");
        return Mutate(actor, sid, "ai-feedback", key, etag, new() { ["messageId"] = messageId, ["decision"] = input.Decision }, async session =>
        {
            Match(session, etag);
            var partition = Partition(actor, sid);
            var message = await repository.ReadAsync(partition, messageId, ct);
            if (message?.Value.Text("authorType") != "ai" || message.Value.Text("messageKind") != "ai_case") throw new DomainException(404, "NOT_FOUND");
            var id = "feedback-" + HashParts(actor.PrincipalId, messageId);
            var existing = await repository.ReadAsync(partition, id, ct);
            var row = existing?.Value.Copy() ?? RecordJson.New("feedback", id, actor.TenantId, actor.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
            row["sessionId"] = sid; row["messageId"] = messageId; row["targetId"] = messageId; row["authorPersonId"] = actor.PersonId;
            row["contractVersion"] = "chat-assistance-v1"; row["feedbackType"] = "ai_case"; row["decision"] = input.Decision; row["status"] = "saved"; Touch(row); Touch(session.Value);
            return ([new RecordWrite(row, existing?.ETag), RecordWrite.Replace(session.Value, session.ETag)], row);
        }, false, ct);
    }
    public async Task<JsonObject> CaseSourcesAsync(ActorAccess actor, string sid, string messageId, CancellationToken ct)
    {
        await SessionAsync(actor, sid, ct); Id(messageId);
        var message = await repository.ReadAsync(Partition(actor, sid), messageId, ct);
        if (message?.Value.Text("authorType") != "ai" || message.Value.Text("messageKind") != "ai_case") throw new DomainException(404, "NOT_FOUND");
        var guard = new SourceGuard(repository, identities.Configuration);
        var snapshots = message.Value["sourceSnapshots"]!.Deserialize<SourceSnapshot[]>(ProjectionRules.Json)!;
        await guard.RequireSnapshotsAsync(actor, snapshots, ct);
        var knowledge = (await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), message.Value.Text("caseId"), ct))!.Value;
        var citations = new JsonArray();
        foreach (var reference in message.Value["citations"]!.Deserialize<EvidenceReference[]>(ProjectionRules.Json)!)
        {
            var evidence = await guard.EvidenceAsync(actor, reference, ct) ?? throw new DomainException(409, "SOURCE_REVOKED");
            citations.Add(new JsonObject { ["id"] = reference.Id, ["sourceVersion"] = reference.SourceVersion, ["title"] = evidence.Document.Text("title"),
                ["location"] = evidence.Span["location"]!.DeepClone(), ["text"] = evidence.Span.Text("text") });
        }
        return new JsonObject { ["id"] = knowledge.Text("id"), ["title"] = knowledge.Text("title"), ["summary"] = knowledge.Text("summary"), ["activeVersion"] = knowledge.Number("activeVersion"), ["citations"] = citations };
    }
    // Sequence is an immutable zero-padded ID; point reads avoid cross-partition scans and opaque Cosmos cursor gaps.
    public async Task<(JsonObject[] Items, int Next, bool More)> MessagesAsync(ActorAccess actor, string sid, int after, int size, CancellationToken ct)
    {
        if (after < 0 || size is < 1 or > 100) throw new ArgumentException("Invalid page");
        var session = await SessionAsync(actor, sid, ct);
        var last = session.Value.Number("lastSequence");
        if (after > last) throw new ArgumentException("Invalid sequence");
        List<JsonObject> messages = [];
        var end = (int)Math.Min(last, (long)after + size);
        for (var offset = 1; offset <= end - after; offset++)
        {
            var sequence = after + offset;
            var message = await repository.ReadAsync(Partition(actor, sid), MessageId(sid, sequence), ct) ?? throw new DomainException(503, "HISTORY_INCOMPLETE");
            if (message.Value.Text("authorType") == "ai" && message.Value["sourceSnapshots"] is JsonArray { Count: > 0 })
            {
                try { await new SourceGuard(repository, identities.Configuration).RequireSnapshotsAsync(actor, message.Value["sourceSnapshots"]!.Deserialize<SourceSnapshot[]>(ProjectionRules.Json)!, ct); }
                catch (DomainException e) when (e.Code == "SOURCE_REVOKED")
                {
                    message.Value["body"] = "参照知識が撤回または変更されたため、この引用は表示できません。";
                    message.Value["sourceSnapshots"] = new JsonArray(); message.Value["retrievalRunId"] = null; message.Value["citationState"] = "revoked";
                    message.Value["citations"] = new JsonArray(); message.Value["retrievalReason"] = null; message.Value["caseId"] = null;
                }
            }
            messages.Add(message.Value);
        }
        return (messages.ToArray(), end, end < last);
    }
}
