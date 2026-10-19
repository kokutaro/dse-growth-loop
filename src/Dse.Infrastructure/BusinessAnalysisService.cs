using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed class BusinessAnalysisService(IPrimaryRepository repository, IRecordStore store, AccessFence fence,
    IBusinessHypothesisDrafter drafter, IConfiguration config, TimeProvider clock)
{
    public const string ConfigVersion = "business-analysis-demo-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly AuthorityProvider authorityProvider = new(repository,config);
    private sealed record Input(BusinessAnalysisSource Source, string[] Scopes, string Fingerprint, string[] KnownIdentifiers);
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    private static bool Timestamp(JsonNode? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value is null) return false;
        try { timestamp = value.Deserialize<DateTimeOffset>(Json); return true; }
        catch (JsonException) { return false; }
    }
    public static string StorageId(string principalId, string runId) => "analysis-" + ConversationService.Hash(principalId + ":" + runId);
    private static string Id(ActorAccess actor, string runId) => StorageId(actor.PrincipalId, runId);
    private static void Analyst(ActorAccess actor)
    {
        if (!actor.HasRole("analyst") || !actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN");
    }
    private int Limit(string key, int fallback, int maximum)
    {
        if (!int.TryParse(config["BusinessAnalysis:" + key] ?? fallback.ToString(), out var value) || value < 1 || value > maximum)
            throw new DomainException(422, "ANALYSIS_CONFIGURATION_INVALID");
        return value;
    }
    private JsonObject Limits() => new()
    {
        ["maxInputs"] = Limit("MaxInputs", 50, 100), ["maxInputBytes"] = Limit("MaxInputBytes", 8000, 16000),
        ["maxInputTokens"] = Limit("MaxInputTokens", 8000, 16000), ["tokenEstimatePolicy"] = "conservative_one_token_per_utf8_byte",
        ["timeoutSeconds"] = Limit("TimeoutSeconds", 30, 120), ["maxConcurrencyPerTenant"] = 1,
        ["maxAttempts"] = 1, ["minimumIndependentChallenges"] = Limit("MinimumIndependentChallenges", 3, 100),
        ["minimumDepartments"] = Limit("MinimumDepartments", 2, 100), ["thresholdPurpose"] = "demo_parameter_not_market_validation"
    };
    private static string Fingerprint(JsonObject catalog, JsonObject authority, JsonObject session, JsonObject? projection) => ConversationService.Hash(
        RecordJson.Canonical(catalog) + RecordJson.Canonical(authority) + session.Text("challengeId") + ":" + session.Number("membershipVersion") + RecordJson.Canonical(projection));
    private async Task<Input?> ReadInputAsync(ActorAccess actor, JsonObject catalog, CancellationToken ct, DateTimeOffset? cutoff = null)
    {
        if (!AccessPolicy.CanUse(actor, catalog) || catalog.Text("sessionId") == "") return null;
        var sid = catalog.Text("sessionId"); var version = catalog.Number("activeVersion");
        var authority = await KnowledgeService.PublishedAsync(repository, actor.TenantId, sid, version, ct);
        if (authority is null || !await authorityProvider.CurrentTokenAsync(actor.TenantId, catalog, ct) || !KnowledgeService.AllConsented(authority, true) || !AccessPolicy.InScope(actor, authority) ||
            !JsonNode.DeepEquals(authority["scopeIds"], catalog["scopeIds"]) || catalog.Number("sourceVersion") != version ||
            catalog.Number("accessVersion") != authority.Number("accessVersion")) return null;
        var consenting = await KnowledgeService.CurrentConsentParticipantsAsync(repository,config,actor.TenantId,authority,true,ct);
        if (consenting is null) return null;
        if (cutoff is not null && (!Timestamp(authority["updatedAt"], out var authorityUpdated) || authorityUpdated > cutoff)) return null;
        var session = (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, sid), sid, ct))!.Value;
        // Only the approved summary is consumed. Session is read solely for its challenge link and membership version.
        var sections = authority["sections"]!.Deserialize<SummarySection[]>(Json)!;
        var approved = new KnowledgeSummary(sections).Text;
        if (catalog.Text("summary") != approved) return null;
        var known = BusinessAnalysisLivePrivacy.Known(actor, catalog, authority).Concat(consenting.SelectMany(p => new[] { p.Identity.PrincipalId, p.DisplayName })).Append(session.Text("challengeId")).ToArray();
        AnonymousWorkspaceKnownIdentifiers.RequireSafe([approved, catalog.Text("title")], known);
        var metadata = new BusinessAnalysisMetadata(null, null, null, null, null);
        if (authority["analysisMetadata"] is JsonObject publishedMetadata && JsonNode.DeepEquals(publishedMetadata, catalog["analysisMetadata"]))
        {
            try
            {
                metadata = BusinessAnalysisMetadataContract.Parse(publishedMetadata) ?? metadata;
            }
            catch (ArgumentException) { throw new DomainException(422, "ANALYSIS_METADATA_INVALID"); }
        }
        var challenge = (await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), session.Text("challengeId"), ct))?.Value;
        var verifiedDepartment = challenge is null || string.IsNullOrEmpty(challenge.Text("creatorPersonId")) ? null :
            await authorityProvider.DepartmentSnapshotAsync(actor.TenantId, challenge.Text("creatorPersonId"), challenge["createdAt"]!.GetValue<DateTimeOffset>(), ct);
        if (!JsonNode.DeepEquals(verifiedDepartment, authority["departmentSnapshot"]) || !JsonNode.DeepEquals(verifiedDepartment, catalog["departmentSnapshot"])) return null;
        metadata = metadata with { DepartmentId = verifiedDepartment?.Text("departmentId") };
        // Classification and terms are projections: use only the current ready projection with the same approved summary/version.
        var projection = await store.ReadAsync(RecordPartition.Catalog(actor.TenantId), $"{catalog.Text("id")}:projection:{version}", ct);
        // Field labels/basis markers and generic attempt text must not join unrelated challenges.
        var challengeTerms = ProfileExtraction.Terms(string.Join(" ", sections.Where(x => x.Field == "challenge").Select(x => x.Text)));
        CategoryProbability[] probabilities = []; string[] terms = challengeTerms;
        if (projection is not null && projection.Value.Number("sourceVersion") == version && projection.Value.Number("accessVersion") == authority.Number("accessVersion") &&
            projection.Value.Number("projectionVersion") == version && projection.Value.Text("projectionState") == "ready" && projection.Value.Text("summary") == approved &&
            projection.Value.Text("entityId") == catalog.Text("id") && projection.Value.Text("tenantId") == actor.TenantId &&
            projection.Value.Text("status") == "active" && projection.Value["available"]?.GetValue<bool>() == true &&
            projection.Value.Text("taxonomyVersion") == Retriever.TaxonomyVersion && JsonNode.DeepEquals(projection.Value["scopeIds"], authority["scopeIds"]))
        {
            try
            {
                var classified = projection.Value["classifications"]?.Deserialize<CategoryProbability[]>(Json) ?? [];
                var normalized = projection.Value.Strings("normalizedTerms");
                if (classified.Length <= 100 && classified.All(x => x is not null && RecordJson.IsId(x.Axis) && RecordJson.IsId(x.CategoryId) && double.IsFinite(x.Probability) && x.Probability >= 0 && x.Probability <= 1) &&
                    classified.Select(x => (x.Axis, x.CategoryId)).Distinct().Count() == classified.Length && normalized.Length <= 100 && normalized.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 100))
                { probabilities = classified; terms = normalized.Select(Retriever.Normalize).Intersect(challengeTerms.Select(Retriever.Normalize)).ToArray(); }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { /* Invalid projections cannot supply classification routes. */ }
        }
        return new(new(catalog.Text("id"), version, session.Text("challengeId"), sid, approved, terms, probabilities, metadata),
            catalog.Strings("scopeIds"), Fingerprint(catalog, authority, session, projection?.Value), known);
    }
    private async Task<Input[]> InputsAsync(ActorAccess actor, DateTimeOffset cutoff, JsonObject limits, CancellationToken ct)
    {
        List<Input> inputs = []; string? cursor = null; var pages = 0;
        do
        {
            if (++pages > 10) throw new DomainException(422, "ANALYSIS_SCAN_LIMIT");
            var page = await repository.QueryAsync(RecordPartition.Catalog(actor.TenantId), "knowledge", null, null, 100, cursor, ct);
            foreach (var row in page.Items)
            {
                if (!Timestamp(row.Value["updatedAt"], out var updated) || updated > cutoff) continue;
                var input = await ReadInputAsync(actor, row.Value, ct, cutoff); if (input is null) continue;
                inputs.Add(input);
                if (inputs.Count > limits.Number("maxInputs")) throw new DomainException(422, "ANALYSIS_INPUT_LIMIT");
            }
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        if (inputs.Sum(x => Encoding.UTF8.GetByteCount(x.Source.Summary)) > Math.Min(limits.Number("maxInputBytes"), limits.Number("maxInputTokens")))
            throw new DomainException(422, "ANALYSIS_INPUT_LIMIT");
        return inputs.OrderBy(x => x.Source.KnowledgeId, StringComparer.Ordinal).ToArray();
    }
    // Union-find prevents aliases connected by either challenge or original session from inflating demand.
    public static BusinessAnalysisSource[] Deduplicate(BusinessAnalysisSource[] sources)
    {
        var groups = new List<List<BusinessAnalysisSource>>();
        foreach (var source in sources)
        {
            var matches = groups.Where(g => g.Any(x => x.SessionId == source.SessionId || source.ChallengeId != "" && x.ChallengeId == source.ChallengeId)).ToArray();
            var merged = matches.SelectMany(x => x).Append(source).ToList(); foreach (var group in matches) groups.Remove(group); groups.Add(merged);
        }
        return groups.Select(g => g.OrderByDescending(x => x.Version).ThenBy(x => x.KnowledgeId, StringComparer.Ordinal).First()).ToArray();
    }
    public static BusinessAnalysisSource[][] Group(BusinessAnalysisSource[] sources)
    {
        List<List<BusinessAnalysisSource>> groups = [];
        bool Related(BusinessAnalysisSource x, BusinessAnalysisSource y) => x.NormalizedTerms.Select(Retriever.Normalize).Intersect(y.NormalizedTerms.Select(Retriever.Normalize)).Any() ||
            x.Classifications.Any(a => a.Probability >= .3 && y.Classifications.Any(b => b.Axis == a.Axis && b.CategoryId == a.CategoryId && b.Probability >= .3));
        foreach (var source in Deduplicate(sources))
        {
            var matches = groups.Where(g => g.Any(x => Related(x, source))).ToArray();
            var merged = matches.SelectMany(x => x).Append(source).ToList(); foreach (var group in matches) groups.Remove(group); groups.Add(merged);
        }
        return groups.Select(g => g.OrderBy(x => x.KnowledgeId, StringComparer.Ordinal).ToArray()).ToArray();
    }
    public static BusinessAnalysisMetrics Metrics(BusinessAnalysisSource[] sources)
    {
        var unique = Deduplicate(sources); List<string> missing = [];
        if (unique.Any(s => string.IsNullOrWhiteSpace(s.Metadata.DepartmentId))) missing.Add("departmentId");
        if (unique.Any(s => s.Metadata.Recurrences is null)) missing.Add("recurrences");
        if (unique.Any(s => s.Metadata.Resolution is null)) missing.Add("resolution");
        if (unique.Any(s => s.Metadata.ReportedHours is null)) missing.Add("reportedHours");
        if (unique.Any(s => s.Metadata.Feedback is null)) missing.Add("feedback");
        return new(unique.Length, unique.Select(x => x.Metadata.DepartmentId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Count(),
            missing.Contains("recurrences") ? null : unique.Count(x => x.Metadata.Recurrences > 1),
            missing.Contains("resolution") || unique.Length == 0 ? null : (double)unique.Count(x => x.Metadata.Resolution != "resolved") / unique.Length,
            missing.Contains("reportedHours") ? null : unique.Sum(x => x.Metadata.ReportedHours ?? 0),
            missing.Contains("feedback") ? null : unique.Count(x => x.Metadata.Feedback <= 2), missing.ToArray());
    }
    private async Task<bool> CurrentAsync(ActorAccess actor, JsonObject run, CancellationToken ct)
    {
        actor = await authorityProvider.RequireAsync(actor, ct);
        Analyst(actor);
        foreach (var saved in run["sourceManifest"]!.AsArray().Select(x => x!.AsObject()))
        {
            var row = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), saved.Text("knowledgeId"), ct);
            if (row is null) return false;
            Input? current;
            try { current = await ReadInputAsync(actor, row.Value, ct); }
            catch (DomainException e) when (e.Code == "IDENTIFYING_CONTENT") { return false; }
            if (current is null || current.Fingerprint != saved.Text("fingerprint")) return false;
        }
        return true;
    }
    private static JsonObject Public(JsonObject run, bool current)
    {
        if (!current || run.Text("analysisStatus") == "invalidated") return new JsonObject { ["runId"] = run.Text("runId"), ["status"] = "invalidated", ["reason"] = "SOURCE_REVOKED", ["groups"] = new JsonArray(), ["etag"] = run.Text("etag") };
        var result = run.Copy();
        foreach (var key in new[] { "sourceManifest", "ownerId", "createdBy", "correctedBy", "tenantId", "scopeIds", "kind", "jobType", "schemaVersion", "id", "requestHash" }) result.Remove(key);
        result["status"] = run.Text("analysisStatus"); result.Remove("analysisStatus"); return result;
    }
    public async Task<JsonObject> StartAsync(ActorAccess actor, BusinessAnalysisRequest request, string key, CancellationToken ct)
    {
        Analyst(actor);
        BusinessAnalysisLiveRuntime.ValidateForStart(config);
        actor = await authorityProvider.RequireAsync(actor, ct);
        if (!BusinessAnalysisLiveRuntime.IsFixture(config) && actor.TenantId != config["Auth:TenantId"]) throw new DomainException(403, "FORBIDDEN");
        IdempotencyExecutor.ValidateHeaders(key, null, false);
        if (!RecordJson.IsId(request.RunId) || key != request.RunId || request.ConfigVersion != BusinessAnalysisLiveRuntime.ConfigVersion(config) || request.Cutoff == default || request.Cutoff > clock.GetUtcNow().AddSeconds(5))
            throw new ArgumentException("Invalid analysis request");
        var limits = Limits(); var gate = gates.GetOrAdd(actor.TenantId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new DomainException(409, "ANALYSIS_BUSY");
        try
        {
            var partition = RecordPartition.Catalog(actor.TenantId); var id = Id(actor, request.RunId);
            var configurationHash = BusinessAnalysisLiveRuntime.ConfigurationHash(config);
            var fingerprint = ConversationService.Hash(RecordJson.Canonical(Node(request)) + RecordJson.Canonical(limits) + configurationHash);
            var existing = await repository.ReadAsync(partition, id, ct);
            if (existing is not null)
            {
                if (existing.Value.Text("requestHash") != fingerprint) throw new DomainException(409, "IDEMPOTENCY_CONFLICT");
                return await GetAsync(actor, request.RunId, ct); // Never automatically retry an interrupted/failed/partial run.
            }
            var initialSlot = await repository.ReadAsync(partition, "business-analysis-slot", ct);
            if (initialSlot?.Value.Text("status") == "blocked") throw new DomainException(409, "ANALYSIS_RECOVERY_REQUIRED");
            if (initialSlot?.Value.Text("status") == "processing") throw new DomainException(409, "ANALYSIS_BUSY");
            var run = await fence.RunAsync(actor.TenantId, async token =>
            {
                var slot = await repository.ReadAsync(partition, "business-analysis-slot", token);
                if (slot?.Value.Text("status") == "blocked") throw new DomainException(409, "ANALYSIS_RECOVERY_REQUIRED");
                if (slot?.Value.Text("status") == "processing") throw new DomainException(409, "ANALYSIS_BUSY");
                var inputs = await InputsAsync(actor, request.Cutoff, limits, token);
                var value = RecordJson.New("job", id, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow());
                value["jobType"] = "businessAnalysis"; value["runId"] = request.RunId; value["status"] = "processing"; value["analysisStatus"] = "processing";
                value["cutoff"] = request.Cutoff; value["configVersion"] = request.ConfigVersion; value["configurationHash"] = configurationHash; value["requestHash"] = fingerprint; value["limits"] = limits.DeepClone();
                value["processingMode"] = BusinessAnalysisLiveRuntime.IsFixture(config) ? "fixture" : "live";
                value["sourceManifest"] = Node(inputs.Select(x => new { x.Source.KnowledgeId, x.Source.Version, x.Fingerprint }));
                value["inputHash"] = ConversationService.Hash(RecordJson.Canonical(value["sourceManifest"]));
                value["sourceCount"] = inputs.Length; value["duplicateCount"] = inputs.Length - Deduplicate(inputs.Select(x => x.Source).ToArray()).Length;
                value["groups"] = new JsonArray(); value["exclusions"] = new JsonArray(); value["decision"] = "candidate";
                var held = slot?.Value.Copy() ?? RecordJson.New("job", "business-analysis-slot", actor.TenantId, "system", ["system"], clock.GetUtcNow());
                held["jobType"] = "businessAnalysisSlot"; held["status"] = "processing"; held["analysisRunId"] = id; held["updatedAt"] = clock.GetUtcNow();
                await repository.CommitAsync(partition, [RecordWrite.Create(value), new(held, slot?.ETag)], token);
                return (Value: value, Inputs: inputs);
            }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(limits.Number("timeoutSeconds")));
            var failed = false; var quiescenceUnknown = false;
            foreach (var group in Group(run.Inputs.Select(x => x.Source).ToArray()))
            {
                var metrics = Metrics(group); var gid = "group-" + ConversationService.Hash(string.Join(":", group.Select(x => x.KnowledgeId)))[..16];
                if (metrics.IndependentChallenges < limits.Number("minimumIndependentChallenges") || metrics.DistinctDepartments < limits.Number("minimumDepartments") || metrics.MissingMetadata.Contains("departmentId"))
                {
                    run.Value["exclusions"]!.AsArray().Add(new JsonObject { ["groupId"] = gid, ["reason"] = metrics.MissingMetadata.Contains("departmentId") ? "DEPARTMENT_METADATA_MISSING" : "DEMO_THRESHOLD_NOT_MET", ["metrics"] = Node(metrics) }); continue;
                }
                try
                {
                    // Authoritative consent validation and consumption share one fence, closing the send-time gap.
                    // A timed-out provider may ignore cancellation: retain the durable slot until offline recovery.
                    var drafted = await fence.RunAsync(actor.TenantId, async token =>
                    {
                        if (!await CurrentAsync(actor, run.Value, token)) throw new DomainException(409, "SOURCE_REVOKED");
                        try
                        {
                            var task = drafter is IBusinessFencedHypothesisDrafter protectedDrafter
                                ? protectedDrafter.DraftUnderFenceAsync(actor, id + ":" + gid, group, timeout.Token)
                                : drafter.DraftAsync(actor, id + ":" + gid, group, timeout.Token);
                            return await task.WaitAsync(timeout.Token);
                        }
                        catch (OperationCanceledException) { quiescenceUnknown = true; return new AiResult<BusinessHypothesis>(AiStatus.Failed, null, request.RunId, AiFailure.Timeout); }
                        catch (IOException) { return new AiResult<BusinessHypothesis>(AiStatus.Failed, null, request.RunId, AiFailure.Unavailable); }
                    }, ct);
                    var h = drafted.Value;
                    if (drafted.Status == AiStatus.NotEvaluated && drafted.Failure is null)
                    { run.Value["exclusions"]!.AsArray().Add(new JsonObject { ["groupId"] = gid, ["reason"] = "NO_COMMON_PROBLEM" }); continue; }
                    if (!drafted.ProviderRejected && (drafted.Failure is AiFailure.Timeout or AiFailure.Cancelled or AiFailure.Unavailable or AiFailure.OutputLimit)) quiescenceUnknown = true;
                    if (drafted.Status != AiStatus.Succeeded || h is null || h.EvidenceIds is null || h.EvidenceIds.Length == 0 ||
                        h.EvidenceIds.Distinct().Count() != h.EvidenceIds.Length || h.EvidenceIds.Any(x => !group.Any(s => s.KnowledgeId == x)) ||
                        new[] { h.Audience, h.Problem, h.ExistingWorkaround, h.ValueHypothesis, h.CounterEvidence, h.NextCustomerCheck }.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 1500))
                    { failed = true; run.Value["exclusions"]!.AsArray().Add(new JsonObject { ["groupId"] = gid, ["reason"] = drafted.Failure == AiFailure.Timeout ? "TIMEOUT_OR_CANCELLED" : drafted.Failure?.ToString() ?? "UNGROUNDED_HYPOTHESIS" }); if (drafted.Status == AiStatus.BudgetStopped || quiescenceUnknown || drafted.Failure == AiFailure.Unavailable) break; continue; }
                    if (BusinessAnalysisLivePrivacy.HypothesisText(h).Any(text => AnonymousWorkspaceKnownIdentifiers.Contains(text, run.Inputs.SelectMany(x => x.KnownIdentifiers))))
                    { failed = true; run.Value["exclusions"]!.AsArray().Add(new JsonObject { ["groupId"] = gid, ["reason"] = "IDENTIFYING_CONTENT" }); continue; }
                    run.Value["groups"]!.AsArray().Add(new JsonObject { ["id"] = gid, ["metrics"] = Node(metrics), ["hypothesis"] = Node(h),
                        ["evidenceIds"] = RecordJson.Array(group.Select(x => x.KnowledgeId)), ["decision"] = "candidate" });
                }
                catch (DomainException e) when (e.Code == "SOURCE_REVOKED") { failed = true; break; }
                catch (IOException) { failed = true; run.Value["exclusions"]!.AsArray().Add(new JsonObject { ["groupId"] = gid, ["reason"] = "DRAFTER_UNAVAILABLE" }); break; }
                catch (OperationCanceledException) { quiescenceUnknown = true; failed = true; run.Value["exclusions"]!.AsArray().Add(new JsonObject { ["groupId"] = gid, ["reason"] = "TIMEOUT_OR_CANCELLED" }); break; }
            }
            // A cancellation may not erase the durable run. Finalization is a bounded, independent attempt; no AI retry.
            using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await fence.RunAsync(actor.TenantId, async token =>
            {
                var saved = (await repository.ReadAsync(partition, id, token))!;
                var current = await CurrentAsync(actor, run.Value, token);
                run.Value["status"] = "ready"; run.Value["analysisStatus"] = current ? failed ? "partial_failure" : "completed" : "invalidated";
                if (!current) { run.Value["groups"] = new JsonArray(); run.Value["exclusions"] = new JsonArray(); run.Value["sourceCount"] = null; run.Value["duplicateCount"] = null; }
                run.Value["recoveryRequired"] = quiescenceUnknown; run.Value["updatedAt"] = clock.GetUtcNow();
                var held = await repository.ReadAsync(partition, "business-analysis-slot", token) ?? throw new DomainException(503, "ANALYSIS_SLOT_MISSING");
                if (held.Value.Text("analysisRunId") != id) throw new DomainException(503, "ANALYSIS_SLOT_CONFLICT");
                held.Value["status"] = quiescenceUnknown ? "blocked" : "ready"; if (!quiescenceUnknown) held.Value["analysisRunId"] = null; held.Value["updatedAt"] = clock.GetUtcNow();
                await repository.CommitAsync(partition, [RecordWrite.Replace(run.Value, saved.ETag), RecordWrite.Replace(held.Value, held.ETag)], token);
                var result = (await repository.ReadAsync(partition, id, token))!; return Public(result.Value, current);
            }, finalization.Token);
        }
        finally { gate.Release(); }
    }
    public Task<JsonObject> GetAsync(ActorAccess actor, string runId, CancellationToken ct) => ConsumeAsync(actor, runId, (result, _) => Task.FromResult(result), ct);
    // API must serialize inside this callback while the shared tenant fence is held.
    public Task<T> ConsumeAsync<T>(ActorAccess actor, string runId, Func<JsonObject, CancellationToken, Task<T>> consume, CancellationToken ct)
    {
        Analyst(actor);
        if (!RecordJson.IsId(runId)) throw new ArgumentException("Invalid run ID");
        return fence.RunAsync(actor.TenantId, async token =>
        {
            actor = await authorityProvider.RequireAsync(actor, token); Analyst(actor);
            var row = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), Id(actor, runId), token) ?? throw new DomainException(404, "NOT_FOUND");
            AccessPolicy.RequireRead(actor, row.Value);
            if (row.Value.Text("ownerId") != actor.PrincipalId) throw new DomainException(404, "NOT_FOUND");
            return await consume(Public(row.Value, await CurrentAsync(actor, row.Value, token)), token);
        }, ct);
    }
    public Task<JsonObject> SourceAsync(ActorAccess actor, string runId, string knowledgeId, CancellationToken ct) =>
        ConsumeSourceAsync(actor, runId, knowledgeId, (result, _) => Task.FromResult(result), ct);
    // Return only the approved publication, never conversation IDs, principals or private citation IDs.
    public Task<T> ConsumeSourceAsync<T>(ActorAccess actor, string runId, string knowledgeId,
        Func<JsonObject, CancellationToken, Task<T>> consume, CancellationToken ct)
    {
        Analyst(actor);
        if (!RecordJson.IsId(runId) || !RecordJson.IsId(knowledgeId)) throw new ArgumentException("Invalid source ID");
        return fence.RunAsync(actor.TenantId, async token =>
        {
            actor = await authorityProvider.RequireAsync(actor, token); Analyst(actor);
            var partition = RecordPartition.Catalog(actor.TenantId);
            var run = (await repository.ReadAsync(partition, Id(actor, runId), token))?.Value ?? throw new DomainException(404, "NOT_FOUND");
            AccessPolicy.RequireRead(actor, run);
            if (run.Text("ownerId") != actor.PrincipalId) throw new DomainException(404, "NOT_FOUND");
            if (run["sourceManifest"] is not JsonArray manifest || !manifest.Any(x => x?["knowledgeId"]?.GetValue<string>() == knowledgeId)) throw new DomainException(404, "NOT_FOUND");
            if (run.Text("analysisStatus") == "invalidated" || !await CurrentAsync(actor, run, token)) throw new DomainException(409, "SOURCE_REVOKED");
            try
            {
                var catalog = (await repository.ReadAsync(partition, knowledgeId, token))?.Value ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
                var approved = await KnowledgeService.PublishedAsync(repository, actor.TenantId, catalog.Text("sessionId"), catalog.Number("activeVersion"), token)
                    ?? throw new DomainException(409, "SOURCE_REVOKED");
                var sections = approved["sections"]!.Deserialize<SummarySection[]>(Json)!;
                var publicMetadata = approved["analysisMetadata"]?.DeepClone().AsObject();
                if (publicMetadata is not null) publicMetadata["departmentId"] = null;
                var body = new JsonObject { ["knowledgeId"] = knowledgeId, ["title"] = catalog.Text("title"), ["version"] = approved.Number("sourceVersion"),
                    ["sections"] = Node(sections.Select(x => new { x.Field, x.Text, x.Basis })), ["analysisMetadata"] = publicMetadata };
                return await consume(body, token);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { throw new DomainException(503, "DEPENDENCY_UNAVAILABLE"); }
        }, ct);
    }
    public Task<JsonObject> DecideAsync(ActorAccess actor, string runId, BusinessAnalysisDecision input, string key, string etag, CancellationToken ct)
    {
        Analyst(actor);
        if (!RecordJson.IsId(runId)) throw new ArgumentException("Invalid run ID");
        IdempotencyExecutor.ValidateHeaders(key, etag, true);
        if (input.Status is not ("candidate" or "investigating" or "on_hold" or "rejected")) throw new ArgumentException("Invalid decision");
        return fence.RunAsync(actor.TenantId, async token =>
        {
            actor = await authorityProvider.RequireAsync(actor, token); Analyst(actor);
            var partition = RecordPartition.Catalog(actor.TenantId); var row = await repository.ReadAsync(partition, Id(actor, runId), token) ?? throw new DomainException(404, "NOT_FOUND");
            AccessPolicy.RequireRead(actor, row.Value);
            if (row.Value.Text("ownerId") != actor.PrincipalId) throw new DomainException(404, "NOT_FOUND");
            if (!await CurrentAsync(actor, row.Value, token)) throw new DomainException(409, "SOURCE_REVOKED");
            if (row.Value.Text("analysisStatus") is not ("completed" or "partial_failure")) throw new DomainException(409, "ANALYSIS_NOT_READY");
            var receiptId = "analysis-decision-" + ConversationService.Hash(actor.PrincipalId + ":" + runId + ":" + key);
            var receipt = await repository.ReadAsync(partition, receiptId, token);
            var hash = ConversationService.Hash(input.Status + ":" + input.GroupId + ":" + etag);
            if (receipt is not null) { if (receipt.Value.Text("fingerprint") != hash) throw new DomainException(409, "IDEMPOTENCY_CONFLICT"); return Public(row.Value, true); }
            if (row.ETag != etag) throw new DomainException(412, "STALE_VERSION");
            var selected = row.Value["groups"]!.AsArray().Where(g => input.GroupId is null || g!["id"]!.GetValue<string>() == input.GroupId).ToArray();
            if (input.GroupId is not null && selected.Length != 1) throw new DomainException(404, "NOT_FOUND");
            if (input.GroupId is null) row.Value["decision"] = input.Status;
            row.Value["updatedAt"] = clock.GetUtcNow();
            foreach (var group in selected) group!["decision"] = input.Status;
            var value = RecordJson.New("idempotency", receiptId, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow()); value["fingerprint"] = hash;
            await repository.CommitAsync(partition, [RecordWrite.Replace(row.Value, row.ETag), RecordWrite.Create(value)], token);
            return Public((await repository.ReadAsync(partition, row.Value.Text("id"), token))!.Value, true);
        }, ct);
    }
}
