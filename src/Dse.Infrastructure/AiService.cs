using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed class AiService(IConfiguration config, IPrimaryRepository repository, SourceGuard guard,
    AccessFence fence, AiLedger ledger, IAiTransport transport, TimeProvider clock) : ITextStructurer, IConcreteSkillExtractor, IDecisionProvider, IReranker, ISummarizer, IKnowledgeDrafter, IDisposable
{
    private readonly AuthorityProvider authorityProvider = new(repository, config);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 128 });
    private sealed record Cached(byte[] Value, string RunId);
    public void Dispose() => cache.Dispose();

    public Task<AiResult<KnowledgeSummary>> DraftAsync(AiContext context, CancellationToken ct) =>
        RunAsync(context, "knowledgeDraft", "", [], [], (body, model, evidence, _) => AiCodecs.Knowledge(AiCodecs.FoundryContent(body, model), evidence), ct);
    public Task<AiResult<StructuredText>> StructureAsync(AiContext context, string text, CancellationToken ct) =>
        RunAsync(context, "structure", text, [], [], (body, model, evidence, _) => AiCodecs.Structure(AiCodecs.FoundryContent(body, model), evidence), ct);
    public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext context, string text, CancellationToken ct) =>
        RunAsync(context, "extractSkills", text, [], [], (body, model, evidence, _) => AiCodecs.Skills(AiCodecs.FoundryContent(body, model), evidence), ct);
    public Task<AiResult<GroundedText>> SummarizeAsync(AiContext context, string text, CancellationToken ct) =>
        RunAsync(context, "summarize", text, [], [], (body, model, evidence, _) => AiCodecs.Summary(AiCodecs.FoundryContent(body, model), evidence), ct);
    public Task<AiResult<Ranking>> RerankAsync(AiContext context, string query, CancellationToken ct) =>
        RunAsync(context, "rerank", query, [], [], (body, model, _, candidates) => AiCodecs.Rank(AiCodecs.FoundryContent(body, model), candidates), ct);
    public Task<AiResult<Decisions>> DecideAsync(AiContext context, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct)
    {
        var ids = choices.Select(x => x.Id).Concat(nouls.Select(x => x.Id)).ToArray();
        if (ids.Length is < 1 or > 100 || ids.Any(x => !RecordJson.IsId(x)) || ids.Distinct().Count() != ids.Length ||
            choices.Any(x => string.IsNullOrWhiteSpace(x.Instructions) || x.Options.Count is < 2 or > 255 || x.Options.Any(p => !RecordJson.IsId(p.Key) || string.IsNullOrWhiteSpace(p.Value))) ||
            nouls.Any(x => string.IsNullOrWhiteSpace(x.Instructions))) throw new ArgumentException("Invalid AI questions");
        return RunAsync(context, "decide", text, choices, nouls, (body, model, _, _) => AiCodecs.Decisions(body, model, choices, nouls,context.PromptVersion==SkillChoiceMatching.Version), ct);
    }

    private Task<AiResult<T>> RunAsync<T>(AiContext context, string operation, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls,
        Func<JsonObject, AiModel, HashSet<string>, IReadOnlyDictionary<string, HashSet<string>>, T> parse, CancellationToken ct)
    {
        if (!RecordJson.IsId(context.JobId) || !RecordJson.IsId(context.Actor.TenantId) || !context.Actor.HasRole("member") ||
            context.Actor.TenantId != (!string.IsNullOrWhiteSpace(config["Auth:TenantId"]) ? config["Auth:TenantId"] : config["Data:Mode"] == "Demo" ? "demo" : null) ||
            context.DataVersion < 1 || context.TaxonomyVersion != "taxonomy-v1" || string.IsNullOrWhiteSpace(context.PromptVersion) || context.PromptVersion.Length > 80 ||
            context.Evidence.Length > 100 || context.Candidates.Length > 100 || text.Length > 64000 ||
            context.ConversationEvidenceIds is { Length: > 100 } || context.SessionId is not null && !RecordJson.IsId(context.SessionId)) throw new ArgumentException("Invalid AI context");
        // Protect both a fresh send and a cache hit against concurrent revocation.
        return fence.RunAsync(context.Actor.TenantId, token => RunProtectedAsync(context, operation, text, choices, nouls, parse, token), ct);
    }

    private async Task<AiResult<T>> RunProtectedAsync<T>(AiContext context, string operation, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls,
        Func<JsonObject, AiModel, HashSet<string>, IReadOnlyDictionary<string, HashSet<string>>, T> parse, CancellationToken ct)
    {
        var actor = await authorityProvider.RequireAsync(context.Actor, ct);
        if (context.PromptVersion is SkillSemanticMatching.Version or SkillChoiceMatching.Version)
            await new SkillDictionaryService(repository,fence,new(repository,clock),clock).RequireSemanticInputUnderFenceAsync(context,text,choices,nouls,ct);
        var searchRerank = operation == "rerank" && context.PromptVersion is "search-rerank-v1" or "search-rerank-v2";
        ChatAccess? chat = null;
        ActorAccess[] sharedActors = [];
        if (context.ChatMembershipVersion is not null)
        {
            chat = new(repository, guard, new IdentityResolver(config), config);
            (_, sharedActors) = await chat.RequireAsync(context, ct);
            if (text != await chat.ContextTextAsync(context, ct)) throw new DomainException(409, "SOURCE_REVOKED");
        }
        var conversationEvidence = new JsonArray();
        if (context.SessionId is not null)
        {
            var session = (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, context.SessionId), context.SessionId, ct))?.Value
                ?? throw new DomainException(404, "NOT_FOUND");
            AccessPolicy.RequireRead(actor, session);
            await authorityProvider.RequireSessionAsync(actor, session, ct);
            if (operation == "knowledgeDraft" && context.PromptVersion == "knowledge-draft-v1")
            {
                if (session.Text("ownerId") != actor.PrincipalId || session.Number("sourceVersion") != context.DataVersion || context.ConversationEvidenceIds is not { Length: > 0 }) throw new DomainException(409, "SOURCE_REVOKED");
                foreach (var id in context.ConversationEvidenceIds.Distinct())
                {
                    var record = (await repository.ReadAsync(RecordPartition.Conversation(actor.TenantId, context.SessionId), id, ct))?.Value;
                    if (record is null || record.Text("sessionId") != context.SessionId || record.Text("kind") is not ("message" or "outcome") || record.Text("kind") == "message" && record.Text("authorType") != "human") throw new DomainException(409, "SOURCE_REVOKED");
                    conversationEvidence.Add(new JsonObject { ["id"] = id, ["text"] = record.Text("kind") == "message" ? record.Text("body") : record.Text("resolution") + ": " + record.Text("result") });
                }
            }
            else if (context.PromptVersion == "knowledge-classify-v1")
            {
                var approved = await KnowledgeService.PublishedAsync(repository, actor.TenantId, context.SessionId, context.KnowledgeVersion ?? 0, ct) ?? throw new DomainException(409, "SOURCE_REVOKED");
                var summary = new KnowledgeSummary(approved["sections"]!.Deserialize<SummarySection[]>(AiCodecs.Json)!);
                if (text != summary.Text) throw new DomainException(409, "SOURCE_REVOKED");
            }
            else if (session.Text("status") != "open" || session.Text("aiAssistance") != "enabled") throw new DomainException(409, "SOURCE_REVOKED");
        }
        List<SourceSnapshot> snapshots = [];
        var candidateEvidence = new Dictionary<string, HashSet<string>>();
        var candidates = new JsonArray();
        List<EvidenceCut> cuts = [];
        foreach (var unit in context.Candidates)
        {
            var validated = await guard.RequireAsync(actor, unit, ct);
            foreach (var participant in sharedActors) await guard.RequireAsync(participant, unit, ct);
            if (chat is not null && unit.EntityType != "knowledge") throw new DomainException(409, "SOURCE_REVOKED");
            snapshots.AddRange(validated.Snapshots);
            var bounded = EvidenceBudget.Trim(validated.Unit.SourceRefs, context.EvidenceBytesPerCandidate, unit.Id, cuts);
            if (searchRerank)
                foreach (var source in validated.Unit.SourceRefs)
                {
                    SearchInput.RequireSafe(source.Text);
                    SearchInput.RequireSafe(source.DocumentTitle);
                    SearchInput.RequireSafe(source.Location);
                }
            if (!candidateEvidence.TryAdd(unit.Id, bounded.Select(x => x.Id).ToHashSet())) throw new ArgumentException("Duplicate candidate");
            // Use authoritative evidence text, never the projection's cached summary.
            var fragment = (await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), unit.UnitId, ct))?.Value;
            var role = fragment?.Text("role");
            if (searchRerank && role is not null) SearchInput.RequireSafe(role);
            candidates.Add(new JsonObject { ["id"] = unit.Id, ["entityType"] = unit.EntityType, ["evidence"] = searchRerank ? AiCodecs.Node(bounded.Select(e => new { e.Id, e.Text })) : AiCodecs.Node(bounded),
                ["role"] = string.IsNullOrWhiteSpace(role) ? null : role, ["period"] = fragment?["period"]?.DeepClone() });
        }
        var evidence = conversationEvidence;
        foreach (var reference in context.Evidence.Distinct())
        {
            var source = await guard.EvidenceAsync(actor, reference, ct) ?? throw new DomainException(409, "SOURCE_REVOKED");
            foreach (var participant in sharedActors)
                if (await guard.EvidenceAsync(participant, reference, ct) is null) throw new DomainException(409, "SOURCE_REVOKED");
            var doc = source.Document;
            snapshots.Add(new(doc.Text("id"), doc.Number("sourceVersion"), doc.Number("activeVersion"), doc.Number("accessVersion")));
            evidence.Add(new JsonObject { ["id"] = reference.Id, ["documentVersionId"] = reference.DocumentVersionId,
                ["sourceVersion"] = reference.SourceVersion, ["text"] = source.Span.Text("text") });
        }
        var evidenceIds = context.Evidence.Select(x => x.Id).Concat(conversationEvidence.Select(x => x!["id"]!.GetValue<string>())).Concat(candidateEvidence.Values.SelectMany(x => x)).ToHashSet();
        if (evidenceIds.Count > 100 || snapshots.Distinct().Count() > 100) throw new ArgumentException("Too many AI sources");
        var input = new JsonObject { ["text"] = text, ["evidence"] = evidence, ["candidates"] = candidates };
        if (context.PromptVersion is SkillSemanticMatching.Version or SkillChoiceMatching.Version) input = SkillSemanticBudget.Prepare(input, context.Evidence);
        if(context.PromptVersion==SkillChoiceMatching.Version)input=SkillChoiceWire.Prepare(input,choices.Single());
        if (searchRerank) input["groundingPolicy"] = "Reason must be an exact contiguous quotation from one cited evidence text. Omit unsuitable candidates; an empty items array means all unsuitable. Do not infer missing role or dates.";
        var hashMaterial = new JsonObject { ["input"] = input.DeepClone(), ["choices"] = AiCodecs.Node(choices), ["nouls"] = AiCodecs.Node(nouls),
            ["snapshots"] = AiCodecs.Node(snapshots.Distinct().ToArray()), ["operation"] = operation, ["prompt"] = context.PromptVersion,
            ["taxonomy"] = context.TaxonomyVersion, ["dataVersion"] = context.DataVersion, ["membershipVersion"] = context.ChatMembershipVersion, ["lastSequence"] = context.ChatLastSequence };
        if(choices.Length>0)hashMaterial["choiceProbabilityVersion"]=ChoiceProbabilityRules.Version;
        var inputHash = Hash(RecordJson.Canonical(hashMaterial));
        var limits = AiLimits.Read(config);
        var runId = "ai-run-" + Guid.NewGuid().ToString("N");
        var partition = context.SessionId is null ? RecordPartition.Catalog(actor.TenantId) : RecordPartition.Conversation(actor.TenantId, context.SessionId);
        var run = RecordJson.New("aiRun", runId, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow());
        run["container"] = partition.Container; run["sessionId"] = context.SessionId; run["jobId"] = context.JobId;
        run["operation"] = operation;
        run["status"] = "not_evaluated"; run["errorCode"] = null;
        run["inputSnapshots"] = AiCodecs.Node(snapshots.Distinct().ToArray());
        var trace = new JsonObject
        {
            ["modelId"] = null, ["promptVersion"] = context.PromptVersion, ["taxonomyVersion"] = context.TaxonomyVersion,
            ["dataVersion"] = context.DataVersion, ["inputHash"] = inputHash, ["evidenceIds"] = RecordJson.Array(evidenceIds.Order()),
            ["inputTokens"] = 0L, ["outputTokens"] = 0L, ["requestUnits"] = 0, ["retryCount"] = 0,
            ["estimatedCostJpy"] = limits.AccountingEnabled ? JsonValue.Create(0m) : null, ["pricingVersion"] = string.IsNullOrWhiteSpace(limits.PriceVersion) ? "unapproved" : limits.PriceVersion,
            ["fxVersion"] = limits.FxVersion, ["accountingMode"] = limits.AccountingEnabled ? "Ledger" : "Disabled", ["durationMs"] = 0L, ["attemptCount"] = 0, ["usageEstimated"] = false,
            ["providerRequestIds"] = new JsonArray()
        };
        run["trace"] = trace;
        trace["evidenceCuts"] = AiCodecs.Node(cuts);
        trace["evidenceBytesPerCandidate"] = context.EvidenceBytesPerCandidate;
        await repository.CommitAsync(partition, [RecordWrite.Create(run)], ct);
        async Task Save(CancellationToken token)
        {
            var stored = (await repository.ReadAsync(partition, runId, token))!;
            run["updatedAt"] = clock.GetUtcNow();
            await repository.CommitAsync(partition, [RecordWrite.Replace(run, stored.ETag)], token);
        }
        var start = Stopwatch.GetTimestamp();
        var attempts = 0;
        AiFailure? failure = null;
        var inferenceNotDispatched=true;var providerRejected=false;
        T? result = default;
        var cacheHit = false;
        var status = AiStatus.NotEvaluated;
        string? pendingCacheKey = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(limits.TimeoutSeconds));
        try
        {
            var mock = config["Ai:Mode"] == "Mock";
            if (limits.Stopped || !mock && !limits.LiveOptIn) throw new AiException(AiFailure.Stopped);
            if (!mock && config["Ai:Mode"] != "Live") throw new AiException(AiFailure.Stopped);
            if (operation == "decide" && choices.Length == 0 && nouls.All(x => !x.HasEvidence))
            {
                result = (T)(object)new Decisions(new Dictionary<string, ChoiceAnswer>(), nouls.ToDictionary(x => x.Id, _ => new NoulAnswer(NoulState.Unknown, null)));
            }
            else
            {
                var model = mock ? MockAiTransport.Model(operation) : AiModel.Read(config, operation == "decide" ? "Jev" : "Foundry", limits.OutputTokens);
                var body = operation == "decide" ? AiCodecs.JevRequest(model, input, choices, nouls) : AiCodecs.FoundryRequest(model, operation, input, limits.OutputTokens);
                if (searchRerank)
                {
                    List<EvidenceCut> plannedCuts = [];
                    body = SearchRerankBudget.Prepare(model, input, limits.OutputTokens, limits.InputTokens,
                        context.EvidenceBytesPerCandidate ?? 1200, plannedCuts);
                    var sentBytes = plannedCuts.ToDictionary(c => (c.CandidateId, c.EvidenceId), c => c.SentBytes);
                    cuts = cuts.Select(c => c with { SentBytes = sentBytes.GetValueOrDefault((c.CandidateId, c.EvidenceId)) }).ToList();
                    candidateEvidence.Clear();
                    foreach (var candidate in candidates)
                        candidateEvidence.Add(candidate!["id"]!.GetValue<string>(), candidate!["evidence"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToHashSet());
                    evidenceIds = context.Evidence.Select(e => e.Id).Concat(candidateEvidence.Values.SelectMany(ids => ids)).ToHashSet();
                    hashMaterial["input"] = input.DeepClone();
                    inputHash = Hash(RecordJson.Canonical(hashMaterial));
                    trace["inputHash"] = inputHash;
                    trace["evidenceIds"] = RecordJson.Array(evidenceIds.Order());
                    trace["evidenceCuts"] = AiCodecs.Node(cuts);
                    trace["inputBudgetVersion"] = "search-total-bytes-v1";
                }
                // UTF-8 byte count plus framing allowance is a conservative estimate, not measured tokens.
                var inputBound = Encoding.UTF8.GetByteCount(AiCodecs.SerializeRequest(body)) + 256;
                trace["inputByteBound"] = inputBound;
                if (context.PromptVersion is SkillSemanticMatching.Version or SkillChoiceMatching.Version) trace["inputBudgetVersion"] = SkillSemanticBudget.Version;
                if (inputBound > limits.InputTokens) throw new AiException(AiFailure.InputLimit);
                if (operation == "decide")
                {
                    // Jev has no max-output parameter. Bound its fixed-shape response before sending.
                    var outputBound = context.PromptVersion==SkillChoiceMatching.Version?SkillChoiceWire.OutputBound(model,choices.Single()):AiCodecs.JevOutputBound(choices, nouls);
                    trace["outputByteBound"]=outputBound;
                    if(context.PromptVersion==SkillChoiceMatching.Version){trace["choiceWireVersion"]=SkillChoiceWire.Version;trace["choiceWireMapHash"]=Hash(RecordJson.Canonical(AiCodecs.Node(SkillChoiceWire.IdMap(choices.Single()))));trace["choiceNumericBytes"]=SkillChoiceWire.NumericBytes;}
                    if (outputBound > limits.OutputTokens) throw new AiException(AiFailure.OutputLimit);
                }
                var key = Hash(inputHash + RecordJson.Canonical(AiCodecs.Node(model)) + actor.TenantId + ":" + actor.PrincipalId + ":" + context.SessionId);
                if (cache.TryGetValue<Cached>(key, out var cached) && cached is not null)
                {
                    result = JsonSerializer.Deserialize<T>(cached.Value, AiCodecs.Json)!;
                    trace["cachedFromRunId"] = cached.RunId; cacheHit = true;
                }
                else
                {
                    for (var attempt = 1; attempt <= limits.Attempts; attempt++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var currentLimits = AiLimits.Read(config);
                        if (currentLimits.Stopped || !mock && !currentLimits.LiveOptIn) throw new AiException(AiFailure.Stopped);
                        AiReservation? reservation = mock ? null : await ledger.ReserveAsync(actor.TenantId, runId, model, limits, limits.InputTokens, limits.OutputTokens, deadline.Token);
                        attempts++;
                        trace["modelId"] = model.ModelId; trace["modelVersion"] = model.ModelVersion; trace["region"] = model.Region; trace["sku"] = model.Sku;
                        trace["attemptCount"] = attempts; trace["retryCount"] = attempts - 1;
                        AiUsage? usage = null;
                        AiException? retry = null;
                        var requestSent = false; var responseReceived = false; var uncertain = false; var notDispatched = false;
                        var dispatch = !mock && transport is IDispatchTrackingAiTransport ? new AiDispatchState() : null;
                        try
                        {
                            await Save(deadline.Token);
                            requestSent = true;
                            var request = dispatch is not null
                                ? ((IDispatchTrackingAiTransport)transport).SendAsync(model, body, limits.OutputTokens * 16 + 4096, deadline.Token, dispatch)
                                : (mock ? MockAiTransport.Instance : transport).SendAsync(model, body, limits.OutputTokens * 16 + 4096, deadline.Token);
                            _ = request.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            var response = await request.WaitAsync(deadline.Token);
                            responseReceived = true;
                            if (response.RequestId is not null) trace["providerRequestIds"]!.AsArray().Add(response.RequestId);
                            usage = response.Usage;
                            if (usage is not null && (usage.Input > limits.InputTokens || usage.Output > limits.OutputTokens)) throw new AiException(AiFailure.OutputLimit);
                            result = parse(response.Body, model, evidenceIds, candidateEvidence);
                            if (result is SpecificSkills skillResult)
                                result = (T)(object)SpecificSkillRules.Assess(skillResult, evidence.ToDictionary(e => e!["id"]!.GetValue<string>(), e => e!["text"]!.GetValue<string>()), sourceKind: "unresolved");
                            if (searchRerank && result is Ranking ranking)
                                foreach (var item in ranking.Items)
                                {
                                    var sent = candidates.Single(c => c!["id"]!.GetValue<string>() == item.CandidateId)!["evidence"]!.AsArray();
                                    AiCodecs.Require(sent.Any(e => item.EvidenceIds.Contains(e!["id"]!.GetValue<string>()) &&
                                        e!["text"]!.GetValue<string>().Contains(item.Reason, StringComparison.Ordinal)));
                                }
                            status = AiStatus.Succeeded;
                        }
                        catch (AiException e)
                        {
                            if (e.RequestId is not null) trace["providerRequestIds"]!.AsArray().Add(e.RequestId);
                            retry = e;
                        }
                        catch (OperationCanceledException) { retry = new(ct.IsCancellationRequested ? AiFailure.Cancelled : AiFailure.Timeout); }
                        catch (HttpRequestException) { retry = new(AiFailure.Unavailable, true); }
                        catch (IOException) { retry = new(AiFailure.Unavailable, true); }
                        catch (JsonException) { retry = new(AiFailure.InvalidJson); }
                        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException or NullReferenceException or OverflowException or ArgumentException)
                        { retry = new(AiFailure.SchemaMismatch, validationIssue: AiValidationIssue.ResponseShape); }
                        finally
                        {
                            using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            notDispatched = dispatch?.TryAbandonBeforeDispatch() == true || retry?.InferenceNotDispatched == true;
                            inferenceNotDispatched &= notDispatched;providerRejected |= retry?.ProviderRejected==true;
                            if (notDispatched) usage = new(0, 0, false);
                            uncertain = !notDispatched && retry?.ProviderRejected != true && requestSent && !responseReceived &&
                                (deadline.IsCancellationRequested || retry?.Failure is AiFailure.Timeout or AiFailure.Cancelled or AiFailure.Unavailable or AiFailure.OutputLimit);
                            if (reservation is not null && !uncertain) await ledger.SettleAsync(reservation, usage, model, limits, settlement.Token);
                            var charged = mock ? new AiUsage(0, 0, false) : usage ?? new(limits.InputTokens, limits.OutputTokens, true);
                            trace["inputTokens"] = trace["inputTokens"]!.GetValue<long>() + charged.Input;
                            trace["outputTokens"] = trace["outputTokens"]!.GetValue<long>() + charged.Output;
                            trace["estimatedCostJpy"] = limits.AccountingEnabled ? trace["estimatedCostJpy"]!.GetValue<decimal>() + model.Cost(charged.Input, charged.Output, limits) : null;
                            trace["usageEstimated"] = trace["usageEstimated"]!.GetValue<bool>() || charged.Estimated;
                            await Save(settlement.Token);
                        }
                        if ((uncertain || notDispatched) && retry is not null) throw retry;
                        if (retry is null) break;
                        if (!retry.Retryable || attempt == limits.Attempts) throw retry;
                        var delay = retry.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                        if (delay > TimeSpan.FromSeconds(limits.TimeoutSeconds) - Stopwatch.GetElapsedTime(start)) throw retry;
                        await Task.Delay(delay, deadline.Token);
                    }
                    if (status == AiStatus.Succeeded)
                        pendingCacheKey = key;
                }
            }
            if(result is Decisions decisions && decisions.Choices.Count>0)
                trace["choiceProbabilityValidationVersion"]=ChoiceProbabilityRules.Version;
            if(result is Decisions validDecisions && validDecisions.Choices.Count>0)
                trace["choiceProbabilityValidation"]=AiCodecs.Node(validDecisions.Choices.ToDictionary(p=>p.Key,p=>ChoiceProbabilityRules.Validate(p.Value.Probabilities.Values)));
            await guard.RequireSnapshotsAsync(actor, snapshots, deadline.Token);
            if (pendingCacheKey is not null)
                cache.Set(pendingCacheKey, new Cached(JsonSerializer.SerializeToUtf8Bytes(result, AiCodecs.Json), runId), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
        }
        catch (AiException e) { failure = e.Failure; if (e.ValidationIssue is not null) trace["validationIssue"] = e.ValidationIssue.ToString(); }
        catch (OperationCanceledException) { failure = ct.IsCancellationRequested ? AiFailure.Cancelled : AiFailure.Timeout; }
        catch (Azure.RequestFailedException) { failure = AiFailure.Unavailable; }
        catch (Exception e) when (e is Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException)
        { failure = AiFailure.Unauthorized; }
        catch (Exception e) when (e is InvalidOperationException or FormatException or UriFormatException or OverflowException)
        { failure = AiFailure.Configuration; }
        if (failure is not null)
        {
            result = default;
            cacheHit = false;
            status = attempts == 0 && failure is AiFailure.Stopped or AiFailure.Budget or AiFailure.Busy ? AiStatus.BudgetStopped : AiStatus.Failed;
        }
        run["status"] = status switch { AiStatus.Succeeded => "succeeded", AiStatus.BudgetStopped => "budget_stopped", AiStatus.NotEvaluated => "not_evaluated", _ => "failed" };
        run["errorCode"] = status == AiStatus.BudgetStopped ? "AI_BUDGET_STOPPED" : failure is null ? null : "AI_" + failure.ToString()!.ToUpperInvariant();
        trace["durationMs"] = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        using var finalSave = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Save(finalSave.Token);
        return new(cacheHit ? AiStatus.Succeeded : status, result, runId, failure, cacheHit){InferenceNotDispatched=inferenceNotDispatched,ProviderRejected=providerRejected};
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
