using System.Diagnostics;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed class NaturalSearchService(IProjectionRepository projections, IPrimaryRepository repository,
    SourceGuard guard, AccessFence fence, IDecisionProvider decisions, IReranker reranker, IConfiguration config, TimeProvider clock)
{
    public RetrievalRules Rules()
    {
        var rules = config.GetSection("Retrieval").Get<RetrievalRules>() ?? new();
        rules.Validate(); return rules;
    }
    private static void RequireMember(ActorAccess actor)
    {
        if (!actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN");
    }
    public async Task<QueryConversion> ConvertAsync(NaturalQuery request, ActorAccess actor, CancellationToken ct, AiContext? shared = null)
    {
        RequireMember(actor); request.Validate(); var rules = Rules();
        if (config["Ai:Mode"] == "Live" && !AiSearchPreflight.Check(config).Ready)
            return new(null, [], "manual_required:configuration", null, rules);
        var result = await decisions.DecideAsync(shared is null ? new(actor, "query-" + Guid.NewGuid().ToString("N"), "search-query-v2", Retriever.TaxonomyVersion, 1, [], []) : shared with { PromptVersion = "search-query-v2" }, request.Query,
            SearchClassification.Choices(), SearchClassification.Nouls(), ct);
        if (result.Status != AiStatus.Succeeded || result.Value is null)
            return new(null, [], "manual_required:" + (result.Failure?.ToString().ToLowerInvariant() ?? "failed"), result.RunId, rules);
        var answer = result.Value.Choices["domain"];
        var distribution = answer.Probabilities.Select(p => new CategoryProbability("domain", p.Key, p.Value)).ToArray();
        if (answer.Choice is "unknown" or "other") return new(null, distribution, "manual_required:unknown", result.RunId, rules);
        var categories = distribution.Where(p => p.CategoryId is not ("unknown" or "other") && p.Probability >= rules.QuestionThreshold)
            .OrderByDescending(p => p.Probability).ThenBy(p => p.CategoryId, StringComparer.Ordinal).Take(rules.QuestionTopCategories)
            .Select(p => new CategoryKey(p.Axis, p.CategoryId)).ToArray();
        var signals = result.Value.Nouls.Where(p => p.Value.State == NoulState.Supported && p.Value.YesProbability >= rules.NoulThreshold).Select(p => p.Key).Order().ToArray();
        var terms = ProfileExtraction.Terms(request.Query).Take(20).ToArray();
        if (categories.Length + signals.Length + terms.Length == 0) return new(null, distribution, "manual_required:unknown", result.RunId, rules);
        return new(new(categories, signals, terms, rules.DocumentThreshold, rules.NoulThreshold, 20), distribution, "converted", result.RunId, rules);
    }

    public async Task SearchAndConsumeAsync(NaturalSearchRequest request, ActorAccess actor, Func<NaturalSearchResponse, CancellationToken, Task> consume, CancellationToken ct, AiContext? shared = null)
    {
        RequireMember(actor); new NaturalQuery(request.Query).Validate();
        if (request.Target is not ("all" or "person" or "knowledge")) throw new ArgumentException("Invalid search target");
        var start = Stopwatch.GetTimestamp(); var runId = "retrieval-" + Guid.NewGuid().ToString("N");
        QueryConversion? conversion = null;
        if (request.Conditions is null) conversion = await ConvertAsync(new(request.Query), actor, ct, shared);
        var supplied = request.Conditions ?? conversion?.Conditions ?? throw new DomainException(422, conversion?.Status switch
        {
            "manual_required:unknown" => "CLASSIFICATION_OUT_OF_SCOPE",
            "manual_required:configuration" => "AI_SEARCH_CONFIGURATION_INVALID",
            _ => "CLASSIFICATION_MANUAL_REQUIRED"
        });
        supplied.Validate();
        SearchInput.RequireSafe(string.Join(" ", supplied.Terms));
        var rules = Rules();
        var conditions = supplied with { ProbabilityThreshold = rules.DocumentThreshold, NoulThreshold = rules.NoulThreshold };
        using var budget = new SearchBudget();
        var fetched = await projections.FindAsync(conditions, actor.Search, ct);
        var chatAccess = new ChatAccess(repository, guard, new IdentityResolver(config), config);
        async Task<SearchUnit[]> Filter(IEnumerable<SearchUnit> units, CancellationToken token) => shared is null
            ? await guard.FilterUnderFenceAsync(actor, units, token) : await chatAccess.FilterAsync(shared, units, token);
        var targeted = fetched.Where(u => request.Target == "all" || (request.Target == "person" ? u.EntityType == "person" : u.EntityType != "person"));
        var primary = await fence.RunAsync(actor.TenantId, async token => Retriever.Search(await Filter(targeted, token), conditions, actor.Search, rules), ct);
        // One relevant fragment per entity bounds evidence fairly; all matching fragments remain in primary cards.
        var inputs = primary.Candidates.Select(c => c.Units[0]).Where(u => u.SourceRefs.Any(s => !string.IsNullOrWhiteSpace(s.Text))).ToArray();
        // Aggregate versions are independent; precise versions travel in validated source snapshots.
        const int inputVersion = 1;
        AiResult<Ranking>? ranked = null;
        string status = primary.Candidates.Length == 0 ? "no_candidates" : "not_performed";
        try
        {
            if (inputs.Length > 0)
            {
                ranked = await reranker.RerankAsync((shared is null ? new AiContext(actor, runId, "search-rerank-v2", Retriever.TaxonomyVersion, inputVersion, [], inputs) : shared with { PromptVersion = "search-rerank-v2", Candidates = inputs })
                    with { EvidenceBytesPerCandidate = rules.EvidenceBytesPerCandidate }, request.Query, ct);
                status = ranked.Status == AiStatus.Succeeded ? ranked.Value?.Items.Length == 0 ? "all_unsuitable" : "succeeded" :
                    ranked.Failure switch { AiFailure.Refused => "refused", AiFailure.Truncated => "truncated", AiFailure.Budget => "budget_stopped", AiFailure.Stopped => "stopped", _ => "not_performed:" + ranked.Failure?.ToString().ToLowerInvariant() };
            }
        }
        catch (DomainException e) when (e.Code == "SOURCE_REVOKED") { status = "source_revoked"; }
        await fence.RunAsync(actor.TenantId, async token =>
        {
            var current = await Filter(primary.Candidates.SelectMany(c => c.Units), token);
            var validIds = current.Select(u => u.Id).ToHashSet();
            // Grounding is checked against the freshly authorized evidence, not the
            // pre-AI copy. A changed span must not leave an old quotation visible.
            var inputChanged = inputs.Any(u => !validIds.Contains(u.Id) ||
                !u.SourceRefs.SequenceEqual(current.Single(v => v.Id == u.Id).SourceRefs));
            var visible = primary.Candidates.Select(c => c with { Units = c.Units.Where(u => validIds.Contains(u.Id)).Select(u => current.Single(v => v.Id == u.Id)).ToArray() }).Where(c => c.Units.Length > 0).ToArray();
            List<CandidateAssessment> assessments = []; List<EvidenceCut> cuts = [];
            var allowed = current.Where(u => inputs.Any(input => input.Id == u.Id))
                .ToDictionary(u => u.Id, u => EvidenceBudget.Trim(u.SourceRefs, rules.EvidenceBytesPerCandidate, u.Id, cuts));
            var items = ranked?.Status == AiStatus.Succeeded ? ranked.Value?.Items ?? [] : [];
            var malformed = items.Select(i => i.CandidateId).Distinct().Count() != items.Length || items.Any(i =>
                !allowed.TryGetValue(i.CandidateId, out var evidence) || string.IsNullOrWhiteSpace(i.Reason) ||
                i.EvidenceIds.Length == 0 || i.EvidenceIds.Any(id => !evidence.Any(e => e.Id == id)) ||
                !evidence.Any(e => i.EvidenceIds.Contains(e.Id) && e.Text.Contains(i.Reason, StringComparison.Ordinal)));
            if (malformed) { status = "ungrounded_output"; items = []; }
            foreach (var item in items.Where(i => validIds.Contains(i.CandidateId)))
            {
                var candidate = visible.Single(c => c.Units.Any(u => u.Id == item.CandidateId));
                assessments.Add(new(candidate.EntityId, candidate.EntityType, item.Reason, item.EvidenceIds, ["役割・期間・現在の対応可否は相談時に確認してください。"]));
            }
            if (visible.Length != primary.Candidates.Length || inputChanged) status = "source_revoked";
            if (status == "succeeded")
            {
                // Successful omission means unsuitable. Keep the complete primary set only in the audit.
                visible = assessments.Select(a => visible.Single(c => c.EntityId == a.EntityId && c.EntityType == a.EntityType)).ToArray();
            }
            else { visible = []; assessments.Clear(); }
            var run = RecordJson.New("retrievalRun", runId, actor.TenantId, actor.PrincipalId, actor.ScopeIds, clock.GetUtcNow());
            run["contractVersion"] = "search-v2";
            run["target"] = request.Target;
            run["finalRanking"] = AiCodecs.Node(visible.Select(c => c.EntityType + ":" + c.EntityId));
            run["rules"] = AiCodecs.Node(rules); run["conditions"] = AiCodecs.Node(conditions);
            run["taxonomyVersion"] = Retriever.TaxonomyVersion; run["normalizationVersion"] = "normalization-v1";
            run["dataVersions"] = AiCodecs.Node(primary.MergedCandidates.SelectMany(c => c.Units).Select(u => new { u.Id, u.EntityId, u.SourceVersion, u.AccessVersion, u.ProjectionVersion }));
            run["routes"] = AiCodecs.Node(new[] { "probability", "noul", "terms" }.ToDictionary(route => route,
                route => primary.MergedCandidates.Where(c => c.Routes.Contains(route)).Select(c => c.EntityType + ":" + c.EntityId).ToArray()));
            run["matchedUnits"] = primary.MatchedUnits; run["mergedBeforeLimit"] = primary.BeforeLimit;
            run["beforeMergeIds"] = AiCodecs.Node(primary.MergedCandidates.SelectMany(c => c.Units).Select(u => u.Id));
            run["mergedIds"] = AiCodecs.Node(primary.MergedCandidates.Select(c => c.EntityType + ":" + c.EntityId));
            run["selectedIds"] = AiCodecs.Node(primary.Candidates.Select(c => c.EntityType + ":" + c.EntityId));
            run["rerankInputIds"] = AiCodecs.Node(inputs.Select(u => u.Id)); run["evidenceCuts"] = AiCodecs.Node(cuts);
            run["cosmosCharges"] = AiCodecs.Node(budget.Charges);
            run["requestUnits"] = budget.RequestUnits; run["ruMeasured"] = config["Data:Mode"] != "Demo";
            run["durationMs"] = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            run["rerankingStatus"] = status; run["aiRunId"] = ranked?.RunId;
            await repository.CommitAsync(RecordPartition.Catalog(actor.TenantId), [RecordWrite.Create(run)], token);
            await consume(new(runId, conditions, primary with { Candidates = visible, RerankingStatus = status }, assessments.ToArray(), "completed", status, ranked?.RunId), token);
            return true;
        }, ct);
    }
}
