using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// The conversations row is authoritative; catalog is a recoverable, versioned projection.
public sealed class KnowledgeService(IPrimaryRepository repository, IRecordStore store, ConversationService chat, AccessFence fence,
    IKnowledgeDrafter drafter, IDecisionProvider decisions, IdentityResolver identities, IConfiguration config, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    public static string Id(string sid) => "knowledge-" + ConversationService.Hash(sid);
    public static string JobId(string sid, int version) => $"{Id(sid)}:publish:{version}";
    private static void Owner(ActorAccess a, JsonObject s) { if (a.PrincipalId != s.Text("ownerId")) throw new DomainException(403, "FORBIDDEN"); }
    private static void Version(JsonObject state, JsonObject input) { if (state.Number("sourceVersion") != input.Number("version")) throw new DomainException(409, "STALE_APPROVAL"); }
    private static KnowledgeSummary Summary(JsonObject state) => new(state["sections"]!.Deserialize<SummarySection[]>(Json)!);
    private static JsonObject NewState(ActorAccess a, JsonObject session, DateTimeOffset now)
    {
        var state = RecordJson.New("knowledgeDraft", Id(session.Text("id")), a.TenantId, session.Text("ownerId"), session.Strings("scopeIds"), now);
        state["contractVersion"] = "knowledge-v1"; state["sessionId"] = session.Text("id"); state["status"] = "absent"; state["sections"] = new JsonArray();
        state["sourceVersion"] = 0; state["accessVersion"] = 1; state["consents"] = new JsonObject();
        state["approvedBy"] = null; state["activeVersion"] = null; state["analysisConsent"] = "not_granted";
        return state;
    }
    public async Task<JsonObject> StateAsync(ActorAccess a, string sid, CancellationToken ct)
    {
        var session = await chat.SessionAsync(a, sid, ct);
        var state = (await repository.ReadAsync(RecordPartition.Conversation(a.TenantId, sid), Id(sid), ct))?.Value ?? NewState(a, session.Value, clock.GetUtcNow());
        var result = state.Copy(); if (state.Text("status") == "absent") result["etag"] = session.ETag;
        result["sessionEtag"] = session.ETag; result["canApprove"] = a.HasRole("publisher");
        result["publicationJob"] = state.Number("sourceVersion") > 0 ? (await repository.ReadAsync(RecordPartition.Conversation(a.TenantId, sid), JobId(sid, state.Number("sourceVersion")), ct))?.Value.DeepClone() : null;
        result["outcome"] = (await repository.ReadAsync(RecordPartition.Conversation(a.TenantId, sid), sid + ":outcome", ct))?.Value.DeepClone();
        result["feedback"] = (await repository.QueryAsync(RecordPartition.Conversation(a.TenantId, sid), "feedback", null, null, 100, null, ct)).Items.Select(x => x.Value).Aggregate(new JsonArray(), (array, x) => { array.Add(x.DeepClone()); return array; });
        return result;
    }
    private async Task<string[]> OriginalParticipantsAsync(ActorAccess actor, JsonObject session, CancellationToken ct)
    {
        var page = await repository.QueryAsync(RecordPartition.Conversation(actor.TenantId, session.Text("id")), "participation", null, null, 100, null, ct);
        if (page.ContinuationToken is not null) throw new DomainException(409, "PARTICIPANT_LIMIT");
        // Pre-Issue-12 sessions did not have originalParticipantIds. Accepted/left invitations retain that history.
        return session.Strings("originalParticipantIds").Concat(session.Strings("participantIds"))
            .Concat(page.Items.Where(p => p.Value.Text("status") is "accepted" or "left").Select(p => p.Value.Text("personId"))).Distinct().ToArray();
    }
    private static void ValidateSections(KnowledgeSummary summary, HashSet<string> evidence)
    {
        if (summary.Sections is null || summary.Sections.Length != 6 || !summary.Sections.Select(x => x?.Field).ToHashSet().SetEquals(KnowledgeSummary.Fields) ||
            summary.Sections.Any(x => x is null || string.IsNullOrWhiteSpace(x.Text) || x.Text.Length > 1500 || x.Basis is not ("participant_report" or "inference" or "unknown") ||
                x.EvidenceIds is null || x.EvidenceIds.Length > 20 || x.EvidenceIds.Distinct().Count() != x.EvidenceIds.Length || x.EvidenceIds.Any(id => !evidence.Contains(id)) ||
                x.Basis != "unknown" && x.EvidenceIds.Length == 0) || summary.Text.Length > 8000)
            throw new DomainException(422, "UNGROUNDED_SUMMARY");
    }
    public async Task<JsonObject> GenerateAsync(ActorAccess a, string sid, string key, string etag, CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key, etag, true);
        var session = await chat.SessionAsync(a, sid, ct); Owner(a, session.Value);
        if (session.ETag != etag) throw new DomainException(412, "STALE_VERSION");
        var partition = RecordPartition.Conversation(a.TenantId, sid);
        var outcome = await repository.ReadAsync(partition, sid + ":outcome", ct) ?? throw new DomainException(409, "OUTCOME_REQUIRED");
        var existing = await repository.ReadAsync(partition, Id(sid), ct);
        if (existing is not null) return existing.Value; // Draft once; subsequent changes are human edits.
        if (session.Value.Number("lastSequence") > 90) throw new DomainException(422, "SUMMARY_INPUT_LIMIT");
        var messageIds = new List<string>();
        for (var sequence = 1; sequence <= session.Value.Number("lastSequence"); sequence++)
        {
            var message = await repository.ReadAsync(partition, ConversationService.MessageId(sid, sequence), ct) ?? throw new DomainException(503, "HISTORY_INCOMPLETE");
            // AI suggestions are not participant reports. Use human reports and outcome, never old AI citations.
            if (message.Value.Text("authorType") == "human") messageIds.Add(message.Value.Text("id"));
        }
        var ids = messageIds.Append(outcome.Value.Text("id")).ToArray();
        var result = await drafter.DraftAsync(new(a, "draft-" + ConversationService.Hash(sid + key), "knowledge-draft-v1", Retriever.TaxonomyVersion,
            session.Value.Number("sourceVersion"), [], [], sid) { ConversationEvidenceIds = ids }, ct);
        if (result.Status != AiStatus.Succeeded || result.Value is null) throw new DomainException(422, result.Status == AiStatus.BudgetStopped ? "AI_BUDGET_STOPPED" : "AI_DRAFT_FAILED");
        ValidateSections(result.Value, ids.ToHashSet());
        return await fence.RunAsync(a.TenantId, token => chat.LockedAsync(a, sid, async () =>
        {
            var current = await chat.SessionAsync(a, sid, token);
            if (current.ETag != session.ETag || (await repository.ReadAsync(partition, outcome.Value.Text("id"), token))?.ETag != outcome.ETag) throw new DomainException(409, "SOURCE_REVOKED");
            var saved = await repository.ReadAsync(partition, Id(sid), token); if (saved is not null) return saved.Value;
            var value = NewState(a, current.Value, clock.GetUtcNow()); value["sourceVersion"] = 1; value["status"] = "draft";
            value["sections"] = Node(result.Value.Sections); value["evidenceIds"] = RecordJson.Array(ids); value["draftRunId"] = result.RunId;
            value["processingMode"] = config["Ai:Mode"] == "Mock" ? "fixture" : "live";
            value["participantIds"] = RecordJson.Array(await OriginalParticipantsAsync(a, current.Value, token));
            value["title"] = "相談からの知識";
            await repository.CommitAsync(partition, [RecordWrite.Create(value)], token);
            return (await repository.ReadAsync(partition, Id(sid), token))!.Value;
        }, token), ct);
    }
    public Task<JsonObject> ChangeAsync(ActorAccess a, string sid, string operation, string key, string etag, JsonObject input, CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key, etag, true);
        ValidateInput(operation, input);
        return fence.RunAsync(a.TenantId, token => chat.LockedAsync(a, sid, async () =>
        {
            var session = await chat.SessionAsync(a, sid, token); var partition = RecordPartition.Conversation(a.TenantId, sid);
            var row = await repository.ReadAsync(partition, Id(sid), token);
            var state = row?.Value.Copy() ?? NewState(a, session.Value, clock.GetUtcNow());
            var receiptId = "idempotency-" + ConversationService.Hash(JsonSerializer.Serialize(new[] { a.PrincipalId, operation, key }));
            var fingerprint = ConversationService.Hash(RecordJson.Canonical(input) + etag);
            var receipt = await repository.ReadAsync(partition, receiptId, token);
            if (receipt is not null)
            { if (receipt.Value.Text("fingerprint") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT"); return await StateAsync(a, sid, token); }
            if ((row?.ETag ?? session.ETag) != etag) throw new DomainException(412, "STALE_VERSION");
            List<RecordWrite> writes = [];
            void Invalidate()
            {
                state["sourceVersion"] = state.Number("sourceVersion") + 1; state["accessVersion"] = state.Number("accessVersion") + 1;
                state["status"] = "draft"; state["approvedBy"] = null; state["activeVersion"] = null; state["consents"] = new JsonObject();
                state["analysisConsent"] = "not_granted";
            }
            switch (operation)
            {
                case "outcome":
                    Owner(a, session.Value);
                    if (input.Text("resolution") is not ("resolved" or "partial" or "unresolved") || string.IsNullOrWhiteSpace(input.Text("result")) || input.Text("result").Length > 4000) throw new ArgumentException("Invalid outcome");
                    var outcome = await repository.ReadAsync(partition, sid + ":outcome", token);
                    var result = outcome?.Value.Copy() ?? RecordJson.New("outcome", sid + ":outcome", a.TenantId, a.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
                    result["contractVersion"] = "knowledge-v1"; result["sessionId"] = sid; result["resolution"] = input.Text("resolution"); result["result"] = input.Text("result"); result["reportedBy"] = a.PrincipalId;
                    result["sourceVersion"] = result.Number("sourceVersion") + 1; result["updatedAt"] = clock.GetUtcNow();
                    writes.Add(new(result, outcome?.ETag));
                    if (row is not null) Invalidate();
                    break;
                case "feedback":
                    var value = input["value"]?.GetValue<int>(); var comment = input.Text("comment");
                    if (value is < 1 or > 5 || comment.Length > 2000) throw new ArgumentException("Invalid feedback");
                    var fid = sid + ":feedback:" + ConversationService.Hash(a.PrincipalId)[..16]; var feedback = await repository.ReadAsync(partition, fid, token);
                    var f = feedback?.Value.Copy() ?? RecordJson.New("feedback", fid, a.TenantId, a.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
                    f["contractVersion"] = "knowledge-v1"; f["sessionId"] = sid; f["evaluatorId"] = a.PrincipalId; f["scaleVersion"] = "usefulness-v1"; f["value"] = value;
                    f["sourceVersion"] = f.Number("sourceVersion") + 1; f["comment"] = string.IsNullOrWhiteSpace(comment) ? null : comment; f["updatedAt"] = clock.GetUtcNow(); writes.Add(new(f, feedback?.ETag)); break;
                case "edit":
                    Owner(a, session.Value); if (row is null) throw new DomainException(409, "DRAFT_REQUIRED");
                    var summary = new KnowledgeSummary(input["sections"]!.Deserialize<SummarySection[]>(Json)!); ValidateSections(summary, state.Strings("evidenceIds").ToHashSet());
                    var scopes = input.Strings("scopeIds");
                    if (scopes.Length is < 1 or > 20 || scopes.Distinct().Count() != scopes.Length || scopes.Any(s => !session.Value.Strings("scopeIds").Contains(s) || !a.ScopeIds.Contains(s))) throw new DomainException(403, "FORBIDDEN");
                    Invalidate(); state["sections"] = Node(summary.Sections); state["scopeIds"] = RecordJson.Array(scopes); break;
                case "request-review":
                    Owner(a, session.Value); Version(state, input); if (state.Text("status") != "draft") throw new DomainException(409, "INVALID_TRANSITION");
                    state["status"] = "review_requested"; state["membershipVersion"] = session.Value.Number("membershipVersion");
                    state["participantIds"] = RecordJson.Array(await OriginalParticipantsAsync(a, session.Value, token)); break;
                case "consent":
                    Version(state, input); if (state.Text("status") is not ("review_requested" or "published")) throw new DomainException(409, "INVALID_TRANSITION");
                    var publication = input["publication"]!.GetValue<bool>(); var analysis = input["analysis"]!.GetValue<bool>();
                    state["consents"]![a.PersonId] = new JsonObject { ["version"] = state.Number("sourceVersion"), ["scopeIds"] = state["scopeIds"]!.DeepClone(),
                        ["publication"] = publication, ["analysis"] = analysis, ["principalId"] = a.PrincipalId, ["confirmedAt"] = clock.GetUtcNow() };
                    state["analysisConsent"] = AllConsented(state, true) ? "granted" : "not_granted";
                    if (!publication && state.Text("status") == "published") { state["status"] = "withdrawn"; state["activeVersion"] = null; state["accessVersion"] = state.Number("accessVersion") + 1; }
                    break;
                case "approve":
                    if (!a.HasRole("publisher")) throw new DomainException(403, "FORBIDDEN"); Version(state, input);
                    if (state.Text("status") == "published") break;
                    if (state.Text("status") != "review_requested" || state.Number("membershipVersion") != session.Value.Number("membershipVersion") || !AllConsented(state, false)) throw new DomainException(409, "CONSENT_REQUIRED");
                    state["status"] = "published"; state["approvedBy"] = new JsonObject { ["id"] = a.PrincipalId, ["version"] = state.Number("sourceVersion"), ["at"] = clock.GetUtcNow() };
                    state["activeVersion"] = state.Number("sourceVersion");
                    var job = Jobs.Create(state, "knowledgePublication", clock.GetUtcNow(), JobId(sid, state.Number("sourceVersion")));
                    job["container"] = "conversations"; job["sessionId"] = sid; job["notificationState"] = "pending";
                    writes.Add(RecordWrite.Create(job)); break;
                case "withdraw":
                    if (row is null) throw new DomainException(409, "DRAFT_REQUIRED");
                    state["status"] = "withdrawn"; state["activeVersion"] = null; state["accessVersion"] = state.Number("accessVersion") + 1; state["analysisConsent"] = "not_granted"; break;
                default: throw new ArgumentException("Invalid operation");
            }
            if (row is not null) { state["updatedAt"] = clock.GetUtcNow(); writes.Add(RecordWrite.Replace(state, row.ETag)); }
            else { session.Value["sourceVersion"] = session.Value.Number("sourceVersion") + 1; writes.Add(RecordWrite.Replace(session.Value, session.ETag)); }
            var audit = RecordJson.New("audit", "audit-" + Guid.NewGuid().ToString("N"), a.TenantId, a.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
            audit["sessionId"] = sid; audit["operation"] = operation; audit["aggregateId"] = Id(sid); audit["previousVersion"] = row?.Value.Number("sourceVersion") ?? 0;
            audit["resultVersion"] = state.Number("sourceVersion"); audit["publicationState"] = state.Text("status"); writes.Add(RecordWrite.Create(audit));
            var savedReceipt = RecordJson.New("idempotency", receiptId, a.TenantId, a.PrincipalId, session.Value.Strings("scopeIds"), clock.GetUtcNow());
            savedReceipt["sessionId"] = sid; savedReceipt["fingerprint"] = fingerprint; writes.Add(RecordWrite.Create(savedReceipt));
            await repository.CommitAsync(partition, writes, token);
            return await StateAsync(a, sid, token);
        }, token), ct);
    }
    public static void ValidateInput(string operation, JsonObject input)
    {
        var allowed = operation switch { "outcome" => new[] { "resolution", "result" }, "feedback" => ["value", "comment"], "edit" => ["sections", "scopeIds"],
            "request-review" or "approve" => ["version"], "consent" => ["version", "publication", "analysis"], "withdraw" => [], _ => throw new ArgumentException("Invalid operation") };
        if (input.Any(p => !allowed.Contains(p.Key)) || operation != "feedback" && allowed.Any(k => input[k] is null)) throw new ArgumentException("Invalid input");
        try
        {
            switch (operation)
            {
                case "outcome": _ = input.Text("resolution"); _ = input.Text("result"); break;
                case "feedback": _ = input["value"]?.GetValue<int>(); _ = input.Text("comment"); break;
                case "edit":
                    _ = input.Strings("scopeIds");
                    var sections = input["sections"]!.Deserialize<SummarySection[]>(Json);
                    if (sections is null || sections.Any(x => x is null || x.EvidenceIds is null)) throw new ArgumentException("Invalid sections");
                    break;
                case "consent": _ = input["publication"]!.GetValue<bool>(); _ = input["analysis"]!.GetValue<bool>(); goto case "approve";
                case "approve": case "request-review": if (input.Number("version") < 1) throw new ArgumentException("Invalid version"); break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or JsonException or FormatException) { throw new ArgumentException("Invalid input", e); }
    }
    internal static bool AllConsented(JsonObject state, bool analysis) => state.Strings("participantIds") is { Length: > 0 } people && people.All(p =>
        state["consents"]?[p] is JsonObject consent && consent.Number("version") == state.Number("sourceVersion") &&
        JsonNode.DeepEquals(consent["scopeIds"], state["scopeIds"]) && consent[analysis ? "analysis" : "publication"]?.GetValue<bool>() == true);
    public static async Task<JsonObject?> PublishedAsync(IPrimaryRepository repo, string tenant, string sid, int version, CancellationToken ct)
    {
        var p = RecordPartition.Conversation(tenant, sid); var state = (await repo.ReadAsync(p, Id(sid), ct))?.Value;
        var session = (await repo.ReadAsync(p, sid, ct))?.Value;
        return state is not null && session is not null && state.Text("status") == "published" && state.Number("sourceVersion") == version &&
            state.Number("activeVersion") == version && state["approvedBy"]?["version"]?.GetValue<int>() == version && AllConsented(state, false) &&
            state.Number("membershipVersion") == session.Number("membershipVersion") ? state : null;
    }
    // Manual and automatic replay both reread authority; a job never grants publication rights.
    public async Task ProcessPublicationAsync(string tenant, string sid, int version, CancellationToken ct)
    {
        var cp = RecordPartition.Conversation(tenant, sid); var job = await repository.ReadAsync(cp, JobId(sid, version), ct);
        if (job is null || job.Value.Text("status") == "ready") return;
        var state = await PublishedAsync(repository, tenant, sid, version, ct); if (state is null) return;
        var existingProjection = await store.ReadAsync(RecordPartition.Catalog(tenant), $"{Id(sid)}:projection:{version}", ct);
        if (existingProjection is not null)
        {
            await fence.RunAsync(tenant, async token =>
            {
                if (await PublishedAsync(repository, tenant, sid, version, token) is null) throw new DomainException(409, "SOURCE_REVOKED");
                var pending = (await repository.ReadAsync(cp, JobId(sid, version), token))!;
                pending.Value["status"] = "ready"; pending.Value["resultId"] = Id(sid); pending.Value["notificationState"] = "sent"; pending.Value["lastErrorCode"] = null;
                await repository.CommitAsync(cp, [RecordWrite.Replace(pending.Value, pending.ETag)], token); return true;
            }, ct);
            return;
        }
        var actor = config["Data:Mode"] == "Demo" ? IdentityResolver.DemoPerson(state.Strings("participantIds")[0]) : identities.ResolvePerson(tenant, state.Strings("participantIds")[0]);
        if (actor is null || !state.Strings("scopeIds").Intersect(actor.ScopeIds).Any()) throw new DomainException(409, "SOURCE_REVOKED");
        if (job.Value.Number("attempt") >= 3) throw new DomainException(409, "PUBLICATION_RETRY_EXHAUSTED");
        job.Value["attempt"] = job.Value.Number("attempt") + 1;
        try { await repository.CommitAsync(cp, [RecordWrite.Replace(job.Value, job.ETag)], ct); }
        catch (StoreConflictException) { throw new DomainException(409, "PUBLICATION_BUSY"); }
        var classified = await decisions.DecideAsync(new(actor, job.Value.Text("id"), "knowledge-classify-v1", Retriever.TaxonomyVersion, version, [], [], sid) { KnowledgeVersion = version }, Summary(state).Text,
            [new("domain", "承認済み要約の分野。Probabilityは経験年数や評価ではない。", ProfileExtraction.Domains)],
            ProfileExtraction.Domains.Select(p => new NoulQuestion(p.Key, p.Value + "の記載の有無", ProfileExtraction.HasDomainEvidence(Summary(state).Text, p.Key))).ToArray(), ct);
        if (classified.Status != AiStatus.Succeeded || classified.Value is null) throw new DomainException(422, "KNOWLEDGE_CLASSIFICATION_FAILED");
        await fence.RunAsync(tenant, async token =>
        {
            var current = await PublishedAsync(repository, tenant, sid, version, token);
            if (current is null || RecordJson.Canonical(current) != RecordJson.Canonical(state)) throw new DomainException(409, "SOURCE_REVOKED");
            var partition = RecordPartition.Catalog(tenant); var id = Id(sid); var catalog = await repository.ReadAsync(partition, id, token);
            var projectionId = $"{id}:projection:{version}";
            if (await store.ReadAsync(partition, projectionId, token) is null)
            {
                // Only reviewed summary leaves conversations. No messages, participant IDs, or private evidence IDs.
                var k = RecordJson.New("knowledge", id, tenant, state.Text("ownerId"), state.Strings("scopeIds"), clock.GetUtcNow());
                k["contractVersion"] = "knowledge-v1"; k["sessionId"] = sid; k["title"] = state.Text("title"); k["summary"] = Summary(state).Text; k["sections"] = Node(Summary(state).Sections.Select(x => new { x.Field, x.Text, x.Basis }));
                k["sourceVersion"] = version; k["activeVersion"] = version; k["accessVersion"] = state.Number("accessVersion"); k["approvedBy"] = state["approvedBy"]!.DeepClone(); k["status"] = "published";
                k["processingMode"] = state.Text("processingMode");
                var evidence = new[] { new EvidenceReference(id, id, version) };
                var answer = classified.Value;
                var unit = new SearchUnit(projectionId, id, "knowledge", tenant, id, k.Text("title"), k.Text("summary"), k.Strings("scopeIds"), "active", true, Retriever.TaxonomyVersion,
                    answer.Choices.SelectMany(q => q.Value.Probabilities.Select(p => new CategoryProbability(q.Key, p.Key, p.Value))).ToArray(),
                    answer.Nouls.Select(p => new ExperienceSignal(p.Key, p.Value.YesProbability, p.Value.State == NoulState.Supported ? "supported" : "unknown")).ToArray(),
                    ProfileExtraction.Terms(k.Text("summary")), [new(id, k.Text("title"), "承認済み要約", k.Text("summary"))], "reviewed")
                { SourceVersion = version, ProjectionVersion = version, AccessVersion = state.Number("accessVersion"), ProjectionState = "ready", EvidenceRefs = evidence, ProcessingMode = state.Text("processingMode"), SkillIds = ProfileExtraction.Skills(k.Text("summary")) };
                var projection = Node(unit).AsObject();
                projection["classificationResultIds"] = RecordJson.Array([$"{id}:classification:{version}"]);
                foreach (var pair in RecordJson.New("searchUnit", projectionId, tenant, state.Text("ownerId"), k.Strings("scopeIds"), clock.GetUtcNow())) if (!projection.ContainsKey(pair.Key)) projection[pair.Key] = pair.Value?.DeepClone();
                var classification = RecordJson.New("classificationResult", $"{id}:classification:{version}", tenant, state.Text("ownerId"), k.Strings("scopeIds"), clock.GetUtcNow());
                classification["axis"] = "domain"; classification["choice"] = answer.Choices["domain"].Choice; classification["probabilities"] = Node(answer.Choices["domain"].Probabilities);
                classification["confidence"] = answer.Choices["domain"].Confidence; classification["origin"] = config["Ai:Mode"] == "Mock" ? "mock" : "ai";
                classification["unitId"] = id; classification["evidenceRefs"] = Node(evidence); classification["processingMode"] = state.Text("processingMode");
                classification["entityId"] = id; classification["decisions"] = Node(answer); classification["aiRunId"] = classified.RunId;
                classification["dataVersion"] = version; classification["promptVersion"] = "knowledge-classify-v1"; classification["taxonomyVersion"] = Retriever.TaxonomyVersion;
                await store.CommitAsync(partition, [new(k, catalog?.ETag), RecordWrite.Create(projection), RecordWrite.Create(classification)], token);
            }
            var latest = (await repository.ReadAsync(cp, JobId(sid, version), token))!; latest.Value["status"] = "ready"; latest.Value["resultId"] = id;
            latest.Value["notificationState"] = "sent"; latest.Value["lastErrorCode"] = null;
            await repository.CommitAsync(cp, [RecordWrite.Replace(latest.Value, latest.ETag)], token); return true;
        }, ct);
    }
    public async Task RecordPublicationFailureAsync(string tenant, string sid, int version, Exception error, CancellationToken ct)
    {
        // Diagnostic update cannot undo approval. Do not store provider response or exception text.
        try
        {
            var p = RecordPartition.Conversation(tenant, sid); var job = await repository.ReadAsync(p, JobId(sid, version), ct);
            if (job is null || job.Value.Text("status") == "ready") return;
            job.Value["lastErrorCode"] = error is DomainException de ? de.Code : error.GetType().Name;
            job.Value["notificationState"] = "pending";
            await repository.CommitAsync(p, [RecordWrite.Replace(job.Value, job.ETag)], ct);
        }
        catch { /* Original durable Job is still authoritative. */ }
    }
    public async Task<int> RecoverAsync(string tenant, string sid, CancellationToken ct)
    {
        var page = await repository.QueryAsync(RecordPartition.Conversation(tenant, sid), "job", null, null, 100, null, ct);
        var count = 0; string? cursor = null;
        do
        {
            if (cursor is not null) page = await repository.QueryAsync(RecordPartition.Conversation(tenant, sid), "job", null, null, 100, cursor, ct);
            foreach (var job in page.Items.Where(x => x.Value.Text("jobType") == "knowledgePublication" && x.Value.Text("status") != "ready"))
            { await ProcessPublicationAsync(tenant, sid, job.Value.Number("aggregateVersion"), ct); count++; }
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        return count;
    }
    public Task<T> AnalyzeAndConsumeAsync<T>(ActorAccess actor, Func<JsonObject[], CancellationToken, Task<T>> consume, CancellationToken ct) =>
        fence.RunAsync(actor.TenantId, async token => await consume(await AnalysisInputsAsync(actor, token), token), ct);
    public async Task<JsonObject[]> AnalysisInputsAsync(ActorAccess a, CancellationToken ct)
    {
        if (!a.HasRole("analyst")) throw new DomainException(403, "FORBIDDEN");
        List<JsonObject> inputs = []; string? cursor = null;
        do
        {
            var page = await repository.QueryAsync(RecordPartition.Catalog(a.TenantId), "knowledge", null, null, 100, cursor, ct);
            foreach (var row in page.Items)
            {
                if (!AccessPolicy.CanUse(a, row.Value) || row.Value.Text("sessionId") == "") continue;
                var authority = await PublishedAsync(repository, a.TenantId, row.Value.Text("sessionId"), row.Value.Number("activeVersion"), ct);
                if (authority is null || !AllConsented(authority, true)) continue;
                inputs.Add(new JsonObject { ["knowledgeId"] = row.Value.Text("id"), ["version"] = row.Value.Number("activeVersion"), ["summary"] = row.Value.Text("summary") });
            }
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        return inputs.ToArray();
    }
}
