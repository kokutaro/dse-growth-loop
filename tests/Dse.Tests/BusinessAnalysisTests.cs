using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Dse.Tests;

public sealed class BusinessAnalysisTests
{
    private static readonly ActorAccess Analyst = IdentityResolver.Demo with { Roles = ["member", "analyst", "publisher"] };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Fake : IBusinessHypothesisDrafter
    {
        public int Calls; public bool Invent; public bool Budget; public Func<Task>? During;
        public async Task<AiResult<BusinessHypothesis>> DraftAsync(ActorAccess actor, string runId, BusinessAnalysisSource[] sources, CancellationToken ct)
        {
            Calls++; if (During is not null) await During();
            if (Budget) return new(AiStatus.BudgetStopped, null, runId, AiFailure.Budget);
            return new(AiStatus.Succeeded, new("対象者", sources[0].Summary[..50], "既存対応", "仮説", "反証", "顧客確認", Invent ? ["invented"] : sources.Select(x => x.KnowledgeId).ToArray()), runId, null);
        }
    }
    private sealed record Setup(IRecordStore Store, IPrimaryRepository Repo, BusinessAnalysisService Service, Fake Ai);
    private static async Task<Setup> Create(Dictionary<string, string?>? options = null)
    {
        var store = new MemoryRecordStore(); var repo = new PrimaryRepository(store); await BusinessAnalysisDemoFixtures.SeedAsync(store, default);
        options ??= []; options["Data:Mode"] = "Demo"; options["Ai:Mode"] = "Mock";
        var config = new ConfigurationBuilder().AddInMemoryCollection(options).Build(); var fake = new Fake();
        return new(store, repo, new(repo, store, new(repo), fake, config, TimeProvider.System), fake);
    }
    private static BusinessAnalysisRequest Request(string? id = null) => new(id ?? Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, BusinessAnalysisService.ConfigVersion);
    private static Task<JsonObject> Start(Setup s, BusinessAnalysisRequest request) => s.Service.StartAsync(Analyst, request, request.RunId, default);
    private static BusinessAnalysisSource Source(string id, string challenge, string session, string dept = "a", int version = 1) =>
        new(id, version, challenge, session, "承認済み要約", ["固有語"], [], new(dept, "unresolved", 2m, 1, 2));
    private static async Task ChangeSource(Setup s, int number, Action<JsonObject, JsonObject> change)
    {
        var sid = "demo-business-session-" + number; var kid = KnowledgeService.Id(sid);
        var cp = RecordPartition.Conversation("demo", sid); var kp = RecordPartition.Catalog("demo");
        var authority = (await s.Repo.ReadAsync(cp, kid, default))!; var catalog = (await s.Repo.ReadAsync(kp, kid, default))!;
        change(authority.Value, catalog.Value);
        await s.Repo.CommitAsync(cp, [RecordWrite.Replace(authority.Value, authority.ETag)], default);
        await s.Repo.CommitAsync(kp, [RecordWrite.Replace(catalog.Value, catalog.ETag)], default);
    }
    [Fact]
    public async Task Code_metrics_match_known_independent_sources_and_no_model_numbers_are_used()
    {
        var s = await Create();
        var fixture = (await s.Repo.QueryAsync(RecordPartition.Catalog("demo"), "knowledge", null, null, 100, null, default)).Items;
        Assert.Equal(3, fixture.Length);
        foreach (var row in fixture)
        {
            Assert.True(AccessPolicy.CanUse(Analyst, row.Value), "fixture must be usable");
            var authority = await KnowledgeService.PublishedAsync(s.Repo, "demo", row.Value.Text("sessionId"), 1, default);
            Assert.NotNull(authority);
            Assert.True(authority!["updatedAt"]!.Deserialize<DateTimeOffset>() < DateTimeOffset.UtcNow, "fixture cutoff must be in the past");
            Assert.Equal(row.Value.Text("summary"), new KnowledgeSummary(authority["sections"]!.Deserialize<SummarySection[]>(Json)!).Text);
        }
        var result = await Start(s, Request());
        Assert.Equal("completed", result.Text("status")); Assert.Equal("fixture", result.Text("processingMode")); Assert.Equal(1, s.Ai.Calls);
        var metrics = result["groups"]![0]!["metrics"]!;
        Assert.Equal(3, metrics["independentChallenges"]!.GetValue<int>()); Assert.Equal(2, metrics["distinctDepartments"]!.GetValue<int>());
        Assert.Equal(1, metrics["recurringChallenges"]!.GetValue<int>()); Assert.Equal(2d / 3, metrics["unresolvedRatio"]!.GetValue<double>());
        Assert.Equal(12m, metrics["reportedHours"]!.GetValue<decimal>()); Assert.Equal(1, metrics["lowRatedChallenges"]!.GetValue<int>());
        Assert.DoesNotContain("sessionId", result.ToJsonString()); Assert.DoesNotContain("principal-demo", result.ToJsonString());
        Assert.DoesNotContain("marketSize", result.ToJsonString()); Assert.DoesNotContain("roi", result.ToJsonString().ToLowerInvariant());
    }
    [Fact]
    public void Duplicated_weekly_reports_revisions_and_transitive_challenge_session_aliases_count_once()
    {
        var sources = new[] { Source("weekly", "c1", "s1"), Source("revision", "c1", "s2", version: 2), Source("alias", "c2", "s2"), Source("other", "c3", "s3", "b") };
        Assert.Equal(2, BusinessAnalysisService.Deduplicate(sources).Length);
        Assert.Equal(4m, BusinessAnalysisService.Metrics(sources).ReportedHours);
        Assert.Equal(2, BusinessAnalysisService.Metrics(sources).IndependentChallenges);
    }
    [Fact]
    public void Secondary_probabilities_join_sources_with_different_top_categories()
    {
        var a = Source("a", "ca", "sa") with { NormalizedTerms = [], Classifications = [new("domain", "primary-a", .9), new("domain", "shared", .4)] };
        var b = Source("b", "cb", "sb") with { NormalizedTerms = [], Classifications = [new("domain", "primary-b", .9), new("domain", "shared", .3)] };
        Assert.Equal(2, Assert.Single(BusinessAnalysisService.Group([a, b])).Length);
        Assert.Equal(2, BusinessAnalysisService.Group([a, b with { Classifications = [new("domain", "primary-b", .9), new("domain", "shared", .29)] }]).Length);
    }
    [Fact]
    public async Task Missing_public_metadata_is_unknown_and_does_not_pass_department_threshold()
    {
        var s = await Create(); await ChangeSource(s, 3, (a, k) => { a.Remove("analysisMetadata"); k.Remove("analysisMetadata"); });
        var result = await Start(s, Request()); Assert.Empty(result["groups"]!.AsArray()); Assert.Equal(0, s.Ai.Calls);
        var excluded = result["exclusions"]![0]!; Assert.Equal("DEPARTMENT_METADATA_MISSING", excluded["reason"]!.GetValue<string>());
        var metrics = excluded["metrics"]!; Assert.Null(metrics["reportedHours"]); Assert.Null(metrics["unresolvedRatio"]); Assert.Null(metrics["lowRatedChallenges"]);
        Assert.Contains("departmentId", metrics["missingMetadata"]!.AsArray().Select(x => x!.GetValue<string>()));
    }
    [Fact]
    public async Task No_analysis_consent_unpublished_or_inaccessible_data_never_reaches_the_model()
    {
        var s = await Create(); await ChangeSource(s, 2, (a, k) => a["consents"]!["demo-person-02"]!["analysis"] = false);
        await ChangeSource(s, 3, (a, k) => { k["scopeIds"] = RecordJson.Array(["outside"]); a["scopeIds"] = k["scopeIds"]!.DeepClone(); });
        var result = await Start(s, Request()); Assert.Equal(1, result.Number("sourceCount")); Assert.Equal(0, s.Ai.Calls); Assert.Empty(result["groups"]!.AsArray());
        await Assert.ThrowsAsync<DomainException>(() => s.Service.StartAsync(Analyst with { Roles = ["member"] }, Request(), "key", default));
    }
    [Fact]
    public async Task Withdrawal_during_drafting_and_after_persistence_hides_all_derived_metrics_and_hypotheses()
    {
        var s = await Create(); s.Ai.During = () => ChangeSource(s, 1, (a, k) => { a["status"] = "withdrawn"; a["activeVersion"] = null; });
        var request = Request(); var result = await Start(s, request);
        Assert.Equal("invalidated", result.Text("status")); Assert.Empty(result["groups"]!.AsArray()); Assert.False(result.ContainsKey("sourceCount"));
        s = await Create(); request = Request(); await Start(s, request);
        await ChangeSource(s, 1, (a, k) => a["consents"]!["demo-person-02"]!["analysis"] = false);
        result = await s.Service.GetAsync(Analyst, request.RunId, default);
        Assert.Equal("invalidated", result.Text("status")); Assert.False(result.ContainsKey("sourceCount")); Assert.False(result.ContainsKey("exclusions"));
    }
    [Fact]
    public async Task Replays_pin_cutoff_config_input_hash_and_do_not_repeat_failed_AI()
    {
        var s = await Create(); var request = Request(); var first = await Start(s, request); var replay = await Start(s, request);
        Assert.Equal(first.Text("inputHash"), replay.Text("inputHash")); Assert.Equal(1, s.Ai.Calls);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => Start(s, request with { Cutoff = request.Cutoff.AddSeconds(-1) }))).Code);
        s = await Create(); s.Ai.Budget = true; request = Request(); first = await Start(s, request); replay = await Start(s, request);
        Assert.Equal("partial_failure", replay.Text("status")); Assert.Equal(1, s.Ai.Calls); Assert.Empty(replay["groups"]!.AsArray());
    }
    [Fact] public async Task Missing_or_default_cutoff_is_rejected_before_saving_a_run_or_drafting()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BusinessAnalysisRequest>("{\"runId\":\"missing-cutoff\",\"configVersion\":\"business-analysis-demo-v1\"}",Json));
        var s = await Create(); var request = Request() with { Cutoff = default };
        await Assert.ThrowsAsync<ArgumentException>(() => Start(s,request)); Assert.Equal(0,s.Ai.Calls);
        Assert.Null(await s.Repo.ReadAsync(RecordPartition.Catalog(Analyst.TenantId),BusinessAnalysisService.StorageId(Analyst.PrincipalId,request.RunId),default));
        Assert.Null(await s.Repo.ReadAsync(RecordPartition.Catalog(Analyst.TenantId),"business-analysis-slot",default));
    }
    [Fact]
    public async Task Forged_evidence_is_rejected_without_automatic_retry()
    {
        var s = await Create(); s.Ai.Invent = true; var result = await Start(s, Request());
        Assert.Equal("partial_failure", result.Text("status")); Assert.Empty(result["groups"]!.AsArray()); Assert.Equal(1, s.Ai.Calls);
        Assert.DoesNotContain("invented", result.ToJsonString());
    }
    [Fact]
    public async Task Input_limits_apply_before_AI_and_configurable_demo_thresholds_are_reported()
    {
        var s = await Create(new() { ["BusinessAnalysis:MaxInputs"] = "2" });
        Assert.Equal("ANALYSIS_INPUT_LIMIT", (await Assert.ThrowsAsync<DomainException>(() => Start(s, Request()))).Code); Assert.Equal(0, s.Ai.Calls);
        s = await Create(new() { ["BusinessAnalysis:MinimumIndependentChallenges"] = "4" }); var result = await Start(s, Request());
        Assert.Empty(result["groups"]!.AsArray()); Assert.Equal(0, s.Ai.Calls);
        Assert.Equal("demo_parameter_not_market_validation", result["limits"]!["thresholdPurpose"]!.GetValue<string>());
    }
    [Fact]
    public async Task Human_decision_requires_owner_scope_current_sources_ETag_and_idempotency()
    {
        var s = await Create(); var request = Request(); var result = await Start(s, request); var etag = result.Text("etag");
        var input = new BusinessAnalysisDecision("investigating"); var key = Guid.NewGuid().ToString("N");
        var changed = await s.Service.DecideAsync(Analyst, request.RunId, input, key, etag, default);
        Assert.Equal("investigating", changed.Text("decision")); Assert.Equal("investigating", changed["groups"]![0]!["decision"]!.GetValue<string>());
        var replay = await s.Service.DecideAsync(Analyst, request.RunId, input, key, etag, default); Assert.Equal(changed.Text("etag"), replay.Text("etag"));
        Assert.Equal("STALE_VERSION", (await Assert.ThrowsAsync<DomainException>(() => s.Service.DecideAsync(Analyst, request.RunId, new("rejected"), Guid.NewGuid().ToString("N"), etag, default))).Code);
        await Assert.ThrowsAsync<DomainException>(() => s.Service.GetAsync(Analyst with { ScopeIds = ["outside"] }, request.RunId, default));
        await Assert.ThrowsAsync<DomainException>(() => s.Service.GetAsync(Analyst with { PrincipalId = "other" }, request.RunId, default));
        await ChangeSource(s, 1, (a, k) => a["status"] = "withdrawn");
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => s.Service.DecideAsync(Analyst, request.RunId, input, Guid.NewGuid().ToString("N"), changed.Text("etag"), default))).Code);
    }
    [Fact]
    public async Task Live_drafter_is_explicitly_unwired_and_makes_no_external_call()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Azure", ["Ai:Mode"] = "Live" }).Build();
        var result = await new BusinessAnalysisDrafter(config).DraftAsync(Analyst, "run", [Source("a", "c", "s")], default);
        Assert.Equal(AiStatus.NotEvaluated, result.Status); Assert.Equal(AiFailure.Configuration, result.Failure);
    }
    [Fact]
    public async Task Concurrent_run_is_rejected_across_service_instances_and_timeout_has_no_AI_retry()
    {
        var s = await Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ai.During = async () => { entered.SetResult(); await release.Task; };
        var first = Start(s, Request()); await entered.Task;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock" }).Build();
        var secondService = new BusinessAnalysisService(s.Repo, s.Store, new(s.Repo), s.Ai, config, TimeProvider.System);
        var second = Request();
        Assert.Equal("ANALYSIS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => secondService.StartAsync(Analyst, second, second.RunId, default))).Code);
        release.SetResult(); await first;
        s = await Create(new() { ["BusinessAnalysis:TimeoutSeconds"] = "1" });
        s.Ai.During = async () => await Task.Delay(1200);
        var request = Request(); var result = await Start(s, request);
        Assert.Equal("partial_failure", result.Text("status")); Assert.Equal(1, s.Ai.Calls);
        Assert.Equal("TIMEOUT_OR_CANCELLED", result["exclusions"]![0]!["reason"]!.GetValue<string>());
        await Start(s, request); Assert.Equal(1, s.Ai.Calls);
        Assert.True(result["recoveryRequired"]!.GetValue<bool>());
        Assert.Equal("ANALYSIS_RECOVERY_REQUIRED", (await Assert.ThrowsAsync<DomainException>(() => Start(s, Request()))).Code);
    }
    [Fact]
    public async Task Live_start_rejects_before_any_repository_or_drafter_operation()
    {
        var store = new MemoryRecordStore(); var repo = new PrimaryRepository(store); var fake = new Fake();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Azure", ["Ai:Mode"] = "Live" }).Build();
        var service = new BusinessAnalysisService(repo, store, new(repo), fake, config, TimeProvider.System); var request = Request();
        Assert.Equal("ANALYSIS_LIVE_NOT_ENABLED", (await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(Analyst, request, request.RunId, default))).Code);
        Assert.Equal(0, fake.Calls); Assert.Empty((await repo.QueryAsync(RecordPartition.Catalog("demo"), "job", null, null, 100, null, default)).Items);
        Assert.Null(await repo.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default));
    }

    [Fact]
    public async Task Individual_human_decisions_leave_other_candidates_unchanged_and_replay_independently()
    {
        var s = await Create(new() { ["BusinessAnalysis:MaxInputBytes"] = "16000", ["BusinessAnalysis:MaxInputTokens"] = "16000" });
        for (var i = 4; i <= 6; i++)
        {
            var templateSid = "demo-business-session-" + (i - 3); var sid = "demo-business-session-" + i; var kid = KnowledgeService.Id(sid);
            var sourcePartition = RecordPartition.Conversation("demo", templateSid); var partition = RecordPartition.Conversation("demo", sid);
            var session = (await s.Repo.ReadAsync(sourcePartition, templateSid, default))!.Value.Copy();
            session["id"] = sid; session["sessionId"] = sid; session["challengeId"] = "separate-challenge-" + i;
            var authority = (await s.Repo.ReadAsync(sourcePartition, KnowledgeService.Id(templateSid), default))!.Value.Copy();
            authority["id"] = kid; authority["sessionId"] = sid;
            var sections = authority["sections"]!.Deserialize<SummarySection[]>(Json)!.Select(x => x.Field == "challenge" ? x with { Text = "架空の砂時計港に固有の検針失敗" } : x).ToArray();
            authority["sections"] = JsonSerializer.SerializeToNode(sections, Json);
            var catalog = (await s.Repo.ReadAsync(RecordPartition.Catalog("demo"), KnowledgeService.Id(templateSid), default))!.Value.Copy();
            catalog["id"] = kid; catalog["sessionId"] = sid; catalog["summary"] = new KnowledgeSummary(sections).Text;
            await s.Repo.CommitAsync(partition, [RecordWrite.Create(session), RecordWrite.Create(authority)], default);
            await s.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Create(catalog)], default);
        }
        var request = Request(); var result = await Start(s, request); Assert.Equal(2, result["groups"]!.AsArray().Count);
        var groupId = result["groups"]![0]!["id"]!.GetValue<string>(); var otherId = result["groups"]![1]!["id"]!.GetValue<string>();
        var key = Guid.NewGuid().ToString("N"); var input = new BusinessAnalysisDecision("investigating", groupId); var etag = result.Text("etag");
        result = await s.Service.DecideAsync(Analyst, request.RunId, input, key, etag, default);
        Assert.Equal("investigating", result["groups"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == groupId)!["decision"]!.GetValue<string>());
        Assert.Equal("candidate", result["groups"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == otherId)!["decision"]!.GetValue<string>());
        var replay = await s.Service.DecideAsync(Analyst, request.RunId, input, key, etag, default); Assert.Equal(result.Text("etag"), replay.Text("etag"));
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => s.Service.DecideAsync(Analyst, request.RunId, input with { GroupId = otherId }, key, etag, default))).Code);
    }
    [Fact]
    public async Task Consent_writer_cannot_enter_between_final_input_validation_and_model_consumption()
    {
        var s = await Create(); var invoked = false;
        s.Ai.During = async () =>
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => new AccessFence(s.Repo).RunAsync("demo", token => { invoked = true; return Task.FromResult(true); }, default));
            Assert.Equal("ACCESS_BUSY", error.Code);
        };
        var result = await Start(s, Request()); Assert.False(invoked); Assert.Equal("completed", result.Text("status"));
    }
    [Fact]
    public async Task Mock_drafter_obeys_default_and_explicit_AI_stop()
    {
        foreach (var stop in new string?[] { null, "true" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = stop }).Build();
            var result = await new BusinessAnalysisDrafter(config).DraftAsync(Analyst, "run", [Source("a", "c", "s")], default);
            Assert.Equal(AiStatus.BudgetStopped, result.Status); Assert.Equal(AiFailure.Stopped, result.Failure); Assert.Null(result.Value);
        }
    }

    [Fact]
    public async Task Manual_analysis_API_enforces_roles_contract_ETag_individual_decision_and_withdrawal_display()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Data:Mode", "Demo"); b.UseSetting("Ai:Mode", "Mock"); b.UseSetting("AiUsage:Stopped", "false");
            b.UseSetting("BusinessAnalysis:DemoFixtures", "true"); b.UseEnvironment("Development");
        });
        using var owner = factory.CreateClient(); using var guest = factory.CreateClient(); guest.DefaultRequestHeaders.Add("X-Demo-Person", "demo-person-02");
        var request = Request();
        async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object input, string key, string? etag = null)
        {
            var message = new HttpRequestMessage(method, path) { Content = JsonContent.Create(input) };
            message.Headers.Add("Idempotency-Key", key); if (etag is not null) message.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(message);
        }
        var capabilities = (await owner.GetFromJsonAsync<JsonObject>("/api/v1/business-analysis/capabilities"))!;
        var ownerKey = capabilities.Text("requestOwnerKey"); Assert.StartsWith("abr-",ownerKey);
        Assert.Equal(ownerKey,(await owner.GetFromJsonAsync<JsonObject>("/api/v1/business-analysis/capabilities"))!.Text("requestOwnerKey"));
        Assert.DoesNotContain(Analyst.PrincipalId,ownerKey); Assert.DoesNotContain(Analyst.PersonId,ownerKey);
        Assert.Null((await guest.GetFromJsonAsync<JsonObject>("/api/v1/business-analysis/capabilities"))!["requestOwnerKey"]);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,(await Send(owner,HttpMethod.Post,"/api/v1/business-analysis",new { runId="omitted-cutoff",configVersion=BusinessAnalysisService.ConfigVersion },"omitted-cutoff")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await Send(guest, HttpMethod.Post, "/api/v1/business-analysis", request, request.RunId)).StatusCode);
        var response = await Send(owner, HttpMethod.Post, "/api/v1/business-analysis", request, request.RunId); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!; Assert.Equal("completed", result.Text("status"));
        var groupId = result["groups"]![0]!["id"]!.GetValue<string>(); var etag = result.Text("etag");
        var sourceId = result["groups"]![0]!["evidenceIds"]![0]!.GetValue<string>(); var sourcePath = "/api/v1/business-analysis/" + request.RunId + "/sources/" + sourceId;
        var sourceResponse = await owner.GetAsync(sourcePath); sourceResponse.EnsureSuccessStatusCode();
        var sourceView = (await sourceResponse.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(6, sourceView["sections"]!.AsArray().Count); Assert.DoesNotContain("evidenceIds", sourceView.ToJsonString());
        Assert.Equal("no-store", sourceResponse.Headers.CacheControl!.ToString());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await owner.GetAsync("/api/v1/business-analysis/" + request.RunId + "/sources/invented-knowledge")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await guest.GetAsync(sourcePath)).StatusCode);
        Assert.Equal(etag, response.Headers.ETag!.ToString());
        var decisionPath = "/api/v1/business-analysis/" + request.RunId + "/decision";
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Send(owner, HttpMethod.Patch, decisionPath, new { status = "investigating", groupId }, Guid.NewGuid().ToString("N"))).StatusCode);
        response = await Send(owner, HttpMethod.Patch, decisionPath, new { status = "investigating", groupId }, Guid.NewGuid().ToString("N"), etag); response.EnsureSuccessStatusCode();
        result = (await response.Content.ReadFromJsonAsync<JsonObject>())!; Assert.Equal("investigating", result["groups"]![0]!["decision"]!.GetValue<string>());
        var repo = factory.Services.GetRequiredService<IPrimaryRepository>(); var sid = "demo-business-session-1"; var cp = RecordPartition.Conversation("demo", sid);
        var authority = (await repo.ReadAsync(cp, KnowledgeService.Id(sid), default))!; authority.Value["consents"]!["demo-person-02"]!["analysis"] = false;
        await repo.CommitAsync(cp, [RecordWrite.Replace(authority.Value, authority.ETag)], default);
        result = (await owner.GetFromJsonAsync<JsonObject>("/api/v1/business-analysis/" + request.RunId))!;
        Assert.Equal("invalidated", result.Text("status")); Assert.Empty(result["groups"]!.AsArray()); Assert.False(result.ContainsKey("sourceCount"));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await owner.GetAsync(sourcePath)).StatusCode);
    }

    private sealed class FailingSourceStore(IRecordStore inner, string targetId) : IRecordStore
    {
        public bool Fail; private int reads;
        public Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct)
        {
            if (Fail && p.Container == "catalog" && id == targetId && ++reads == 2) throw new IOException("lookup failed");
            return inner.ReadAsync(p, id, ct);
        }
        public Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct) => inner.CommitAsync(p, writes, ct);
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) => inner.QueryAsync(p, kind, field, parent, size, token, ct);
    }
    [Fact]
    public async Task Approved_source_view_requires_run_owner_scope_role_and_manifest_and_never_returns_private_ids()
    {
        var s = await Create(); var request = Request(); await Start(s, request); var id = KnowledgeService.Id("demo-business-session-1");
        var body = await s.Service.SourceAsync(Analyst, request.RunId, id, default);
        Assert.Equal(id, body.Text("knowledgeId")); Assert.Equal(1, body.Number("version")); Assert.Equal(6, body["sections"]!.AsArray().Count);
        Assert.Equal(2m, body["analysisMetadata"]!["reportedHours"]!.GetValue<decimal>()); Assert.Null(body["analysisMetadata"]!["departmentId"]);
        var text = body.ToJsonString();
        foreach (var forbidden in new[] { "sessionId", "principal", "participantIds", "evidenceIds", "demo-approved-evidence", "sourceManifest", "ownerId" }) Assert.DoesNotContain(forbidden, text);
        Assert.Equal("FORBIDDEN", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst with { Roles = ["member"] }, request.RunId, id, default))).Code);
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst with { PrincipalId = "other" }, request.RunId, id, default))).Code);
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst with { ScopeIds = ["outside"] }, request.RunId, id, default))).Code);
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst, request.RunId, "invented-knowledge", default))).Code);
        await s.Service.ConsumeSourceAsync(Analyst, request.RunId, id, async (dto, ct) =>
        {
            Assert.Equal("ACCESS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => new AccessFence(s.Repo).RunAsync("demo", _ => Task.FromResult(true), default))).Code);
            return true;
        }, default);
        await ChangeSource(s, 3, (a, k) => a["consents"]!["demo-person-02"]!["analysis"] = false);
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst, request.RunId, id, default))).Code);
    }
    [Fact]
    public async Task Approved_source_lookup_failure_returns_no_body_and_fails_closed()
    {
        var s = await Create(); var request = Request(); await Start(s, request); var id = KnowledgeService.Id("demo-business-session-1");
        var fault = new FailingSourceStore(s.Store, id) { Fail = true }; var repo = new PrimaryRepository(fault);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock" }).Build();
        var service = new BusinessAnalysisService(repo, fault, new(repo), s.Ai, config, TimeProvider.System); var exposed = false;
        var error = await Assert.ThrowsAsync<DomainException>(() => service.ConsumeSourceAsync(Analyst, request.RunId, id, (body, ct) => { exposed = true; return Task.FromResult(true); }, default));
        Assert.Equal(503, error.Status); Assert.Equal("DEPENDENCY_UNAVAILABLE", error.Code); Assert.False(exposed);
        Assert.False(string.IsNullOrEmpty((await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default))!.Value.Text("holder")));
    }
    [Fact]
    public async Task Mock_problem_uses_challenge_body_without_summary_field_or_basis_labels()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false" }).Build();
        var source = Source("a", "c", "s") with { Summary = "result (unknown): 不明\nchallenge (participant_report): 公開済みの困りごと\nattempts (participant_report): 試した対応" };
        var result = await new BusinessAnalysisDrafter(config).DraftAsync(Analyst, "run", [source], default);
        Assert.Equal("公開済みの困りごと", result.Value!.Problem); Assert.DoesNotContain("participant_report", result.Value.Problem); Assert.DoesNotContain("result", result.Value.Problem);
    }

    // These tests exercise the Azure-mode code with a fake provider and local files, never Azure or real AI quality.
    private sealed class WireFake : IAiTransport
    {
        public int Calls; public JsonObject? Sent; public string Variation = "success";
        public TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task>? During;
        public async Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct)
        {
            Calls++; Sent = body.Copy(); if (During is not null) await During();
            var input = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!.AsObject();
            var ids = input["sources"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToArray();
            var h = new JsonObject { ["audience"] = "公開要約の対象者", ["problem"] = "共通課題の仮説", ["existingWorkaround"] = "現行対応", ["valueHypothesis"] = "検証前の価値仮説", ["counterEvidence"] = "共通でない可能性", ["nextCustomerCheck"] = "本人に確認", ["evidenceIds"] = RecordJson.Array(ids) };
            var output = new JsonObject { ["commonProblemConfirmed"] = true, ["hypothesis"] = h };
            if (Variation == "unrelated") { output["commonProblemConfirmed"] = false; output["hypothesis"] = null; }
            if (Variation == "invented") h["evidenceIds"] = RecordJson.Array(["invented"]);
            if (Variation == "extra") h["demand"] = 999;
            if (Variation == "wrongtype") h["problem"] = 123;
            if (Variation == "identity") h["problem"] = Analyst.PersonId;
            if (Variation == "inventedidentity") h["problem"] = "principal-fabricated-999";
            if (Variation == "email") h["problem"] = "alice@example.invalid";
            if (Variation == "financial") h["valueHypothesis"] = "市場規模は100億円";
            var response = new JsonObject { ["model"] = model.ModelId, ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = Variation == "truncated" ? "length" : "stop", ["message"] = new JsonObject { ["content"] = Variation == "invalidjson" ? "not-json" : output.ToJsonString(), ["refusal"] = Variation == "refusal" ? "拒否" : null } }) };
            AiUsage? usage = Variation switch { "missingusage" => null, "estimatedusage" => new(100, 20, true), "negativeusage" => new(-1, 20, false), "excessusage" => new(20000, 20, false), _ => new(100, 20, false) };
            Finished.TrySetResult(); return new(response, usage, "fake-provider-request");
        }
    }
    private sealed record LiveSetup(string Path, IRecordStore Store, IPrimaryRepository Repo, IConfigurationRoot Config, BusinessAnalysisService Service, WireFake Wire) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private static async Task<LiveSetup> CreateLive(string variation = "success")
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dse-analysis-fake-" + Guid.NewGuid().ToString("N"));
        IRecordStore store = new OperationMeasuredRecordStore(new LocalRecordStore(new LocalIngestionFiles(path))); var repo = new PrimaryRepository(store);
        await BusinessAnalysisDemoFixtures.SeedAsync(store, default);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:Mode"] = "Azure", ["Ai:Mode"] = "Live", ["Auth:TenantId"] = "demo", ["BusinessAnalysis:Enabled"] = "true",
            ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "true", ["AiUsage:DailyTokenLimit"] = "300000",
            ["AiUsage:DailyCallLimit"] = "10", ["AiUsage:DailyEstimatedJpyLimit"] = "100", ["AiUsage:UsdToJpy"] = "150",
            ["AiUsage:PriceVersion"] = "fake-test-price", ["AiUsage:FxVersion"] = "fake-test-fx", ["AiUsage:MaxInputTokens"] = "16000",
            ["AiUsage:MaxOutputTokens"] = "1000", ["AiUsage:MaxAttempts"] = "3", ["Foundry:Endpoint"] = "https://dummy.openai.azure.com/",
            ["Foundry:Deployment"] = "dummy", ["Foundry:ModelId"] = "dummy-business-model-v1", ["Foundry:ModelVersion"] = "v1",
            ["Foundry:Region"] = "test", ["Foundry:Sku"] = "GlobalStandard", ["Foundry:InputUsdPerMillion"] = "1",
            ["Foundry:OutputUsdPerMillion"] = "2", ["Foundry:OutputLimit"] = "1000"
        }).Build();
        var wire = new WireFake { Variation = variation }; var live = new BusinessAnalysisLiveDrafter(config, repo, new(store, TimeProvider.System), wire, TimeProvider.System);
        return new(path, store, repo, config, new(repo, store, new(repo), new BusinessAnalysisDrafter(config, live), config, TimeProvider.System), wire);
    }
    private static BusinessAnalysisRequest LiveRequest() => Request() with { ConfigVersion = BusinessAnalysisLiveRuntime.LiveConfigVersion };
    private static Task<JsonObject> StartLive(LiveSetup s, BusinessAnalysisRequest r) => s.Service.StartAsync(Analyst, r, r.RunId, default);
    private static Task<RecordPage> Rows(LiveSetup s, string kind) => s.Store.QueryAsync(RecordPartition.Catalog("demo"), kind, null, null, 100, null, default);
    private static async Task<StoredRecord> Day(LiveSetup s)
    {
        var reservation = Assert.Single((await Rows(s, "aiReservation")).Items);
        return (await s.Store.ReadAsync(RecordPartition.Catalog("demo"), reservation.Value.Text("dayId"), default))!;
    }
    [Fact]
    public async Task Azure_mode_fake_transport_uses_shared_ledger_and_restarts_without_resending_or_private_inputs()
    {
        using var s = await CreateLive(); using var measurement = OperationMeasurements.Begin(MeasurementEvidence.Fixture); var request = LiveRequest(); var result = await StartLive(s, request);
        Assert.Equal("completed", result.Text("status")); Assert.Equal("live", result.Text("processingMode")); Assert.Equal(1, s.Wire.Calls);
        Assert.Equal(12m, result["groups"]![0]!["metrics"]!["reportedHours"]!.GetValue<decimal>());
        var userInput = s.Wire.Sent!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.DoesNotContain("session", userInput); Assert.DoesNotContain("principal", userInput); Assert.DoesNotContain("reportedHours", userInput); Assert.DoesNotContain("department", userInput);
        var reservation = Assert.Single((await Rows(s, "aiReservation")).Items); Assert.Equal("settled", reservation.Value.Text("state"));
        var day = (await Day(s)); Assert.Equal(1, day.Value.Number("calls")); Assert.Equal(120, day.Value.Number("tokens")); Assert.Equal(.021m, day.Value["jpy"]!.GetValue<decimal>());
        var audit = Assert.Single((await Rows(s, "aiRun")).Items).Value; Assert.Equal(1, audit["trace"]!["attemptCount"]!.GetValue<int>()); Assert.Equal(0, audit["trace"]!["retryCount"]!.GetValue<int>()); Assert.Null(audit["trace"]!["requestUnits"]); Assert.Equal("not_collected", audit["trace"]!["requestUnitsStatus"]!.GetValue<string>());
        var measured = measurement.Export(MeasurementKind.SourceRead, MeasurementKind.FenceWrite, MeasurementKind.AuditWrite, MeasurementKind.LedgerReserve, MeasurementKind.LedgerSettle, MeasurementKind.HttpRequest);
        Assert.Empty(measured.MissingExpectedKinds); Assert.All(measured.Operations, op => Assert.Null(op.RequestUnits.Value)); Assert.False(measured.CompleteMeasurement); Assert.False(measured.AcceptanceGatePassed);
        Assert.DoesNotContain("共通課題の仮説", audit.ToJsonString());
        var restartedStore = new LocalRecordStore(new LocalIngestionFiles(s.Path)); var restartedRepo = new PrimaryRepository(restartedStore);
        var service = new BusinessAnalysisService(restartedRepo, restartedStore, new(restartedRepo), new BusinessAnalysisDrafter(s.Config, new(s.Config, restartedRepo, new(restartedStore, TimeProvider.System), s.Wire, TimeProvider.System)), s.Config, TimeProvider.System);
        var replay = await service.StartAsync(Analyst, request, request.RunId, default); Assert.Equal(result.Text("inputHash"), replay.Text("inputHash")); Assert.Equal(1, s.Wire.Calls);
        s.Config["Foundry:InputUsdPerMillion"] = "3";
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, request))).Code); Assert.Equal(1, s.Wire.Calls);
        s.Config["AiUsage:Stopped"] = "true";
        Assert.Equal("AI_BUDGET_STOPPED", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, LiveRequest()))).Code); Assert.Equal(1, s.Wire.Calls);
        var authority = await s.Repo.ReadAsync(RecordPartition.Conversation("demo", "demo-business-session-1"), KnowledgeService.Id("demo-business-session-1"), default);
        authority!.Value["consents"]!["demo-person-02"]!["analysis"] = false;
        await s.Repo.CommitAsync(RecordPartition.Conversation("demo", "demo-business-session-1"), [RecordWrite.Replace(authority.Value, authority.ETag)], default);
        Assert.Equal("invalidated", (await s.Service.GetAsync(Analyst, request.RunId, default)).Text("status")); Assert.Equal(1, s.Wire.Calls);
    }
    [Theory]
    [InlineData("BusinessAnalysis:Enabled", "false", "ANALYSIS_LIVE_NOT_ENABLED")]
    [InlineData("AiUsage:Stopped", "true", "AI_BUDGET_STOPPED")]
    [InlineData("AiUsage:LiveOptIn", "false", "AI_BUDGET_STOPPED")]
    [InlineData("AiUsage:PriceVersion", "unapproved", "ANALYSIS_LIVE_CONFIGURATION_INVALID")]
    [InlineData("AiUsage:FxVersion", "unapproved", "ANALYSIS_LIVE_CONFIGURATION_INVALID")]
    [InlineData("Foundry:ModelVersion", "latest", "ANALYSIS_LIVE_CONFIGURATION_INVALID")]
    [InlineData("Foundry:Endpoint", "http://dummy.openai.azure.com", "ANALYSIS_LIVE_CONFIGURATION_INVALID")]
    [InlineData("AiUsage:UsdToJpy", "0", "ANALYSIS_LIVE_CONFIGURATION_INVALID")]
    public async Task Live_optin_stop_and_contract_validation_happen_before_storage_or_transport(string key, string value, string code)
    {
        using var s = await CreateLive(); s.Config[key] = value;
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, LiveRequest()))).Code);
        Assert.Equal(0, s.Wire.Calls); Assert.Empty((await Rows(s, "aiRun")).Items); Assert.Empty((await Rows(s, "aiReservation")).Items);
        Assert.Null(await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "system-access-fence", default));
    }
    [Theory]
    [InlineData("invented")][InlineData("extra")][InlineData("wrongtype")][InlineData("financial")]
    [InlineData("truncated")][InlineData("invalidjson")][InlineData("refusal")]
    [InlineData("identity")][InlineData("inventedidentity")][InlineData("email")]
    [InlineData("missingusage")][InlineData("estimatedusage")][InlineData("negativeusage")][InlineData("excessusage")]
    public async Task Fake_live_invalid_output_is_rejected_once_with_conservative_cost_accounting(string variation)
    {
        using var s = await CreateLive(variation); var request = LiveRequest(); var result = await StartLive(s, request);
        Assert.Equal("partial_failure", result.Text("status")); Assert.Empty(result["groups"]!.AsArray()); Assert.Equal(1, s.Wire.Calls);
        await StartLive(s, request); Assert.Equal(1, s.Wire.Calls);
        var reservation = Assert.Single((await Rows(s, "aiReservation")).Items).Value;
        Assert.Equal(variation is "missingusage" or "estimatedusage" or "negativeusage" ? "uncertain" : "settled", reservation.Text("state"));
        if (variation is "missingusage" or "estimatedusage" or "negativeusage") Assert.Equal(2.7m, reservation["chargedEstimateJpy"]!.GetValue<decimal>());
        if (variation == "excessusage") Assert.True((await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "system-ai-gate", default))!.Value["blocked"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Common_problem_denial_is_an_exclusion_and_input_body_bound_prevents_provider_send()
    {
        using var s = await CreateLive("unrelated"); var result = await StartLive(s, LiveRequest());
        Assert.Equal("completed", result.Text("status")); Assert.Empty(result["groups"]!.AsArray()); Assert.Equal("NO_COMMON_PROBLEM", result["exclusions"]![0]!["reason"]!.GetValue<string>());
        s.Config["AiUsage:MaxInputTokens"] = "1";
        result = await StartLive(s, LiveRequest()); Assert.Equal("partial_failure", result.Text("status")); Assert.Equal(1, s.Wire.Calls); Assert.Single((await Rows(s, "aiReservation")).Items);
    }
    [Fact]
    public async Task Shared_budget_exhaustion_after_one_fake_call_prevents_a_second_provider_send()
    {
        using var s = await CreateLive(); s.Config["AiUsage:DailyCallLimit"] = "0";
        Assert.Equal("ANALYSIS_LIVE_CONFIGURATION_INVALID", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, LiveRequest()))).Code); Assert.Equal(0, s.Wire.Calls);
        s.Config["AiUsage:DailyCallLimit"] = "1";
        await StartLive(s, LiveRequest()); var result = await StartLive(s, LiveRequest());
        Assert.Equal("partial_failure", result.Text("status")); Assert.Equal(1, s.Wire.Calls); Assert.Single((await Rows(s, "aiReservation")).Items);
    }
    private static readonly ActorAccess Operator = Analyst with { Roles = ["member", "analyst", "operator"] };
    private static BusinessAnalysisRecoveryRequest RecoveryRequest(JsonObject inspection, string runId) => new(runId, Analyst.PrincipalId, inspection.Text("runEtag"), inspection.Text("slotEtag"),
        "approval-local-fake", true, true, true, "stop-evidence-local-fake", new string('a', 64), inspection.Strings("pendingReservationIds"));
    [Fact]
    public async Task Timeout_retains_pending_cost_and_slot_until_evidence_based_offline_recovery_without_retry()
    {
        using var s = await CreateLive(); s.Config["AiUsage:TimeoutSeconds"] = "1";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Wire.During = async () => { entered.SetResult(); await release.Task; }; // Ignores cancellation, just like an uncertain remote request.
        var request = LiveRequest(); var task = StartLive(s, request); await entered.Task; var result = await task;
        Assert.Equal("partial_failure", result.Text("status")); Assert.True(result["recoveryRequired"]!.GetValue<bool>());
        Assert.Equal("ANALYSIS_RECOVERY_REQUIRED", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, LiveRequest()))).Code);
        Assert.Equal(1, s.Wire.Calls); var pending = Assert.Single((await Rows(s, "aiReservation")).Items); Assert.Equal("pending", pending.Value.Text("state"));
        var gate = (await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "system-ai-gate", default))!; Assert.Contains(pending.Value.Text("id"), gate.Value.Strings("active"));
        release.SetResult(); await s.Wire.Finished.Task; // Tests terminate their fake; real offline termination requires independently reviewed evidence.
        s.Config["AiUsage:Stopped"] = "true"; s.Config["AiUsage:LiveOptIn"] = "false";
        var recovery = new BusinessAnalysisRecovery(s.Store, s.Config, TimeProvider.System);
        Assert.Equal("FORBIDDEN", (await Assert.ThrowsAsync<DomainException>(() => recovery.InspectAsync(Analyst, Analyst.PrincipalId, request.RunId, default))).Code);
        var inspection = await recovery.InspectAsync(Operator, Analyst.PrincipalId, request.RunId, default); var input = RecoveryRequest(inspection, request.RunId);
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RecoverAsync(Operator, input with { ProvidersStopped = false }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RecoverAsync(Operator, input with { StopEvidenceSha256 = "invalid" }, default));
        Assert.Equal("STALE_VERSION", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(Operator, input with { SlotETag = "\"stale-version\"" }, default))).Code);
        Assert.Equal("RECOVERY_RESERVATION_MISMATCH", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(Operator, input with { PendingReservationIds = [] }, default))).Code);
        var dayBefore = (await Day(s)).Value.ToJsonString();
        var recovered = await recovery.RecoverAsync(Operator, input, default); Assert.Equal("aborted", recovered.Text("status")); Assert.Equal(0m, recovered["refundedJpy"]!.GetValue<decimal>());
        Assert.Equal(dayBefore, (await Day(s)).Value.ToJsonString()); Assert.Equal("uncertain", Assert.Single((await Rows(s, "aiReservation")).Items).Value.Text("state"));
        Assert.Empty((await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "system-ai-gate", default))!.Value.Strings("active"));
        Assert.Equal("ready", (await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "business-analysis-slot", default))!.Value.Text("status"));
        var saved = (await s.Service.GetAsync(Analyst, request.RunId, default)); Assert.Equal("aborted", saved.Text("status")); Assert.Empty(saved["groups"]!.AsArray()); Assert.Null(saved["sourceCount"]);
        Assert.Equal(recovered.ToJsonString(), (await recovery.RecoverAsync(Operator, input, default)).ToJsonString()); Assert.Equal(1, s.Wire.Calls);
        Assert.Equal("RECOVERY_REQUEST_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(Operator, input with { StopEvidenceReference = "different-proof" }, default))).Code);
    }
    [Fact]
    public async Task Offline_recovery_never_releases_another_run_and_requires_explicit_stop()
    {
        var s = await Create(new() { ["BusinessAnalysis:TimeoutSeconds"] = "1" }); s.Ai.During = () => Task.Delay(1100);
        var request = Request(); await Start(s, request); await Task.Delay(150);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["AiUsage:Stopped"] = "true", ["AiUsage:LiveOptIn"] = "false" }).Build();
        var recovery = new BusinessAnalysisRecovery(s.Store, config, TimeProvider.System); var inspection = await recovery.InspectAsync(Operator, Analyst.PrincipalId, request.RunId, default);
        var slot = (await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "business-analysis-slot", default))!; slot.Value["analysisRunId"] = "another-run";
        await s.Store.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(slot.Value, slot.ETag)], default);
        var input = RecoveryRequest(inspection, request.RunId) with { SlotETag = (await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "business-analysis-slot", default))!.ETag };
        Assert.Equal("RECOVERY_SLOT_MISMATCH", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(Operator, input, default))).Code);
        Assert.Equal("another-run", (await s.Store.ReadAsync(RecordPartition.Catalog("demo"), "business-analysis-slot", default))!.Value.Text("analysisRunId"));
        config["AiUsage:LiveOptIn"] = "true";
        Assert.Equal("RECOVERY_REQUIRES_AI_STOP", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(Operator, input, default))).Code);
    }

    [Theory]
    [InlineData("demo-person-02")][InlineData("principal-demo-01")][InlineData("demo-department-a")][InlineData("alice@example.invalid")][InlineData("principal-unseen-999")]
    public async Task Public_source_identifiers_are_rejected_without_private_lookup_or_model_send_and_saved_results_fail_closed(string identifying)
    {
        var s = await Create(); var request = Request(); await Start(s, request);
        await ChangeSource(s, 1, (a, k) =>
        {
            var sections = a["sections"]!.Deserialize<SummarySection[]>(Json)!.Select(x => x.Field == "challenge" ? x with { Text = x.Text + " " + identifying } : x).ToArray();
            a["sections"] = JsonSerializer.SerializeToNode(sections, Json); k["summary"] = new KnowledgeSummary(sections).Text;
        });
        Assert.Equal("invalidated", (await s.Service.GetAsync(Analyst, request.RunId, default)).Text("status"));
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst, request.RunId, KnowledgeService.Id("demo-business-session-1"), default))).Code);
        Assert.Equal("IDENTIFYING_CONTENT", (await Assert.ThrowsAsync<DomainException>(() => Start(s, Request()))).Code); Assert.Equal(1, s.Ai.Calls);
    }

    private const string RecoveryTargetAi = "recovery-target-ai", RecoveryOtherAi = "recovery-other-ai";
    private const string RecoveryTargetReservation = "zz-target-pending", RecoveryOtherReservation = "zz-other-pending";
    private static JsonObject RecoveryAi(string id, string jobId)
    {
        var row = RecordJson.New("aiRun", id, "demo", Analyst.PrincipalId, Analyst.ScopeIds, DateTimeOffset.UtcNow);
        row["jobId"] = jobId; row["container"] = "catalog"; row["sessionId"] = null; row["operation"] = BusinessAnalysisLiveDrafter.PromptVersion;
        row["status"] = "failed"; row["errorCode"] = "AI_TIMEOUT"; row["inputSnapshots"] = new JsonArray();
        row["trace"] = new JsonObject { ["modelId"] = "dummy", ["promptVersion"] = BusinessAnalysisLiveDrafter.PromptVersion, ["taxonomyVersion"] = "dummy-v1", ["dataVersion"] = 1,
            ["inputHash"] = "dummy-hash", ["evidenceIds"] = new JsonArray(), ["inputTokens"] = 0, ["outputTokens"] = 0, ["requestUnits"] = null,
            ["requestUnitsStatus"] = "not_collected", ["attemptCount"] = 1, ["retryCount"] = 0, ["estimatedCostJpy"] = 0m, ["pricingVersion"] = "dummy-price" };
        return row;
    }
    private static JsonObject RecoveryReservation(string id, string aiRun, string state)
    {
        var row = RecordJson.New("aiReservation", id, "demo", "system", ["system"], DateTimeOffset.UtcNow);
        row["runId"] = aiRun; row["state"] = state; row["dayId"] = "ai-day-recovery-fixture";
        row["input"] = 100L; row["output"] = 20L; row["jpy"] = 1m; return row;
    }
    private sealed class RecoveryStore(IRecordStore inner) : IRecordStore
    {
        public string? MissingId; public bool ChangeGateBeforeRecovery;
        public List<(string Kind, string? Field, string? Parent)> Queries = [];
        public Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct) => id == MissingId ? Task.FromResult<StoredRecord?>(null) : inner.ReadAsync(p, id, ct);
        public async Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            if (ChangeGateBeforeRecovery && writes.Any(w => w.Value.Text("operation") == "businessAnalysisOfflineRecovery"))
            {
                ChangeGateBeforeRecovery = false;
                var gate = (await inner.ReadAsync(p, "system-ai-gate", ct))!; gate.Value["blocked"] = false;
                await inner.CommitAsync(p, [RecordWrite.Replace(gate.Value, gate.ETag)], ct);
            }
            await inner.CommitAsync(p, writes, ct);
        }
        public Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? token, CancellationToken ct)
        {
            Queries.Add((kind, field, parent));
            if (kind == "aiReservation" && field != "runId") throw new InvalidOperationException("Tenant-wide reservation scan is forbidden in this regression");
            return inner.QueryAsync(p, kind, field, parent, size, token, ct);
        }
    }
    private static async Task<(Setup Setup, BusinessAnalysisRequest Run, RecoveryStore Guard, BusinessAnalysisRecovery Recovery)> RecoveryFixture(int history = 0)
    {
        var s = await Create(); var request = Request(); await Start(s, request); var p = RecordPartition.Catalog("demo");
        var id = BusinessAnalysisService.StorageId(Analyst.PrincipalId, request.RunId);
        var run = (await s.Store.ReadAsync(p, id, default))!; run.Value["analysisStatus"] = "partial_failure"; run.Value["recoveryRequired"] = true;
        var slot = (await s.Store.ReadAsync(p, "business-analysis-slot", default))!; slot.Value["status"] = "blocked"; slot.Value["analysisRunId"] = id;
        var gate = RecordJson.New("aiUsageGate", "system-ai-gate", "demo", "system", ["system"], DateTimeOffset.UtcNow);
        gate["active"] = RecordJson.Array([RecoveryTargetReservation, RecoveryOtherReservation]); gate["blocked"] = true;
        var day = RecordJson.New("aiUsageDay", "ai-day-recovery-fixture", "demo", "system", ["system"], DateTimeOffset.UtcNow);
        day["calls"] = history + 2; day["tokens"] = 120L * (history + 2); day["jpy"] = (decimal)history + 2;
        await s.Store.CommitAsync(p, [RecordWrite.Replace(run.Value, run.ETag), RecordWrite.Replace(slot.Value, slot.ETag),
            RecordWrite.Create(RecoveryAi(RecoveryTargetAi, id)), RecordWrite.Create(RecoveryAi(RecoveryOtherAi, "other-analysis")),
            RecordWrite.Create(RecoveryReservation(RecoveryTargetReservation, RecoveryTargetAi, "pending")), RecordWrite.Create(RecoveryReservation(RecoveryOtherReservation, RecoveryOtherAi, "pending")),
            RecordWrite.Create(gate), RecordWrite.Create(day)], default);
        foreach (var batch in Enumerable.Range(0, history).Chunk(100))
            await s.Store.CommitAsync(p, batch.Select(n => RecordWrite.Create(RecoveryReservation("ai-res-history-" + n.ToString("D5"), RecoveryOtherAi, "settled"))).ToArray(), default);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["AiUsage:Stopped"] = "true", ["AiUsage:LiveOptIn"] = "false" }).Build();
        var guard = new RecoveryStore(s.Store); return (s, request, guard, new(guard, config, TimeProvider.System));
    }
    [Fact]
    public async Task Recovery_ignores_more_than_a_week_of_unrelated_history_and_keeps_other_pending_reservations()
    {
        var (s, run, guard, recovery) = await RecoveryFixture(1680); var p = RecordPartition.Catalog("demo");
        var otherBefore = (await s.Store.ReadAsync(p, RecoveryOtherReservation, default))!;
        var dayBefore = (await s.Store.ReadAsync(p, "ai-day-recovery-fixture", default))!;
        var inspection = await recovery.InspectAsync(Operator, Analyst.PrincipalId, run.RunId, default);
        Assert.Equal([RecoveryTargetReservation], inspection.Strings("pendingReservationIds"));
        var result = await recovery.RecoverAsync(Operator, RecoveryRequest(inspection, run.RunId), default);
        Assert.Equal(1, result.Number("pendingReservationsMarkedUncertain")); Assert.Equal(0m, result["refundedJpy"]!.GetValue<decimal>());
        Assert.Equal("uncertain", (await s.Store.ReadAsync(p, RecoveryTargetReservation, default))!.Value.Text("state"));
        var otherAfter = (await s.Store.ReadAsync(p, RecoveryOtherReservation, default))!;
        Assert.Equal(otherBefore.ETag, otherAfter.ETag); Assert.Equal(otherBefore.Value.ToJsonString(), otherAfter.Value.ToJsonString());
        Assert.Equal(dayBefore.ETag, (await s.Store.ReadAsync(p, "ai-day-recovery-fixture", default))!.ETag);
        var gate = (await s.Store.ReadAsync(p, "system-ai-gate", default))!.Value;
        Assert.Equal([RecoveryOtherReservation], gate.Strings("active")); Assert.True(gate["blocked"]!.GetValue<bool>());
        Assert.Equal("ready", (await s.Store.ReadAsync(p, "business-analysis-slot", default))!.Value.Text("status"));
        Assert.All(guard.Queries.Where(q => q.Kind == "aiReservation"), q => { Assert.Equal("runId", q.Field); Assert.Equal(RecoveryTargetAi, q.Parent); });
        Assert.Equal(2, guard.Queries.Count(q => q.Kind == "aiReservation"));
    }
    [Theory]
    [InlineData("gate_missing")][InlineData("active_row_missing")][InlineData("active_ai_missing")]
    [InlineData("target_not_active")][InlineData("active_duplicate")][InlineData("active_settled")]
    public async Task Recovery_unknown_or_incomplete_gate_fails_closed_before_any_derived_result_or_slot_write(string damage)
    {
        var (s, run, guard, recovery) = await RecoveryFixture(); var p = RecordPartition.Catalog("demo");
        var inspection = await recovery.InspectAsync(Operator, Analyst.PrincipalId, run.RunId, default); var input = RecoveryRequest(inspection, run.RunId);
        if (damage == "gate_missing") guard.MissingId = "system-ai-gate";
        else if (damage == "active_row_missing") guard.MissingId = RecoveryOtherReservation;
        else if (damage == "active_ai_missing") guard.MissingId = RecoveryOtherAi;
        else if (damage == "active_settled")
        {
            var row = (await s.Store.ReadAsync(p, RecoveryTargetReservation, default))!; row.Value["state"] = "settled";
            await s.Store.CommitAsync(p, [RecordWrite.Replace(row.Value, row.ETag)], default);
        }
        else
        {
            var gate = (await s.Store.ReadAsync(p, "system-ai-gate", default))!;
            gate.Value["active"] = RecordJson.Array(damage == "target_not_active" ? [RecoveryOtherReservation] : [RecoveryTargetReservation, RecoveryTargetReservation, RecoveryOtherReservation]);
            await s.Store.CommitAsync(p, [RecordWrite.Replace(gate.Value, gate.ETag)], default);
        }
        var id = BusinessAnalysisService.StorageId(Analyst.PrincipalId, run.RunId); var before = (await s.Store.ReadAsync(p, id, default))!;
        Assert.Equal("RECOVERY_LEDGER_INCONSISTENT", (await Assert.ThrowsAsync<DomainException>(() => recovery.InspectAsync(Operator, Analyst.PrincipalId, run.RunId, default))).Code);
        Assert.Equal("RECOVERY_LEDGER_INCONSISTENT", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(Operator, input, default))).Code);
        Assert.Equal(before.ETag, (await s.Store.ReadAsync(p, id, default))!.ETag);
        Assert.Equal("blocked", (await s.Store.ReadAsync(p, "business-analysis-slot", default))!.Value.Text("status"));
    }
    [Fact]
    public async Task Recovery_gate_changed_after_validation_is_rejected_by_the_atomic_batch_ETag()
    {
        var (s, run, guard, recovery) = await RecoveryFixture(); var p = RecordPartition.Catalog("demo");
        var inspection = await recovery.InspectAsync(Operator, Analyst.PrincipalId, run.RunId, default);
        guard.ChangeGateBeforeRecovery = true;
        await Assert.ThrowsAsync<StoreConflictException>(() => recovery.RecoverAsync(Operator, RecoveryRequest(inspection, run.RunId), default));
        Assert.Equal("pending", (await s.Store.ReadAsync(p, RecoveryTargetReservation, default))!.Value.Text("state"));
        Assert.Equal("blocked", (await s.Store.ReadAsync(p, "business-analysis-slot", default))!.Value.Text("status"));
        Assert.Equal("partial_failure", (await s.Store.ReadAsync(p, BusinessAnalysisService.StorageId(Analyst.PrincipalId, run.RunId), default))!.Value.Text("analysisStatus"));
    }

}
