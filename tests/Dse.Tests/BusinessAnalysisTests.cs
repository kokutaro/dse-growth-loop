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
    private static readonly ActorAccess Operator = new("demo","principal-dummy-operator","dummy-operator",["operator","analyst"],["demo"]);
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
    private static async Task<Setup> Create(Dictionary<string, string?>? options = null, bool recoveryOperator = false)
    {
        var store = new MemoryRecordStore(); var repo = new PrimaryRepository(store);
        if (recoveryOperator)
        {
            var actors=Enumerable.Range(1,3).Select(i=>IdentityResolver.DemoPerson($"demo-person-{i:D2}")!).Append(Operator).ToArray();
            await AuthorityProvider.InitializeAsync(store,"demo",actors.Select(a=>new IdentityBinding("demo","",a.PrincipalId,a.PersonId,a.Roles,true)).ToArray(),
                actors.Select(a=>new AccessGrant(a.PrincipalId,a.ScopeIds)).ToArray(),default);
        }
        await BusinessAnalysisDemoFixtures.SeedAsync(store, default);
        options ??= []; options.TryAdd("AiUsage:Stopped","false"); options["Data:Mode"] = "Demo"; options["Ai:Mode"] = "Mock";
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
        var s = await Create(); await ChangeSource(s, 3, (a, k) => { a.Remove("analysisMetadata"); k.Remove("analysisMetadata"); a["departmentSnapshot"] = null; k["departmentSnapshot"] = null; });
        var person = (await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId("demo-person-02"),default))!;
        person.Value["departmentTerms"] = new JsonArray(); await s.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(person.Value,person.ETag)],default);
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
    [Theory] [InlineData(null)] [InlineData("true")] [InlineData("invalid")]
    public async Task Fixture_stop_rejects_before_any_run_slot_fence_or_drafter(string? stopped)
    {
        var s = await Create(new() { ["AiUsage:Stopped"] = stopped }); var request = Request();
        Assert.Equal("AI_BUDGET_STOPPED",(await Assert.ThrowsAsync<DomainException>(() => Start(s,request))).Code);
        Assert.Equal(0,s.Ai.Calls); var p=RecordPartition.Catalog(Analyst.TenantId);
        foreach (var id in new[] { BusinessAnalysisService.StorageId(Analyst.PrincipalId,request.RunId),"business-analysis-slot","system-access-fence" })
            Assert.Null(await s.Repo.ReadAsync(p,id,default));
    }
    [Theory] [InlineData(null)] [InlineData("true")] [InlineData("invalid")]
    public async Task Fixture_capabilities_and_post_obey_stop_before_persistence(string? stopped)
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Data:Mode","Demo"); b.UseSetting("Ai:Mode","Mock"); b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_,config) => config.AddInMemoryCollection(new Dictionary<string,string?> { ["AiUsage:Stopped"]=stopped }));
        });
        using var client=factory.CreateClient(); var caps=(await client.GetFromJsonAsync<JsonObject>("/api/v1/business-analysis/capabilities"))!;
        Assert.True(caps["allowed"]!.GetValue<bool>()); Assert.False(caps["enabled"]!.GetValue<bool>()); Assert.Equal("AI_BUDGET_STOPPED",caps.Text("reason"));
        var input=Request("fixture-stopped-http"); using var message=new HttpRequestMessage(HttpMethod.Post,"/api/v1/business-analysis") { Content=JsonContent.Create(input) };
        message.Headers.Add("Idempotency-Key",input.RunId); var response=await client.SendAsync(message); Assert.Equal(System.Net.HttpStatusCode.Conflict,response.StatusCode);
        Assert.Equal("AI_BUDGET_STOPPED",(await response.Content.ReadFromJsonAsync<JsonObject>())!.Text("code"));
        using var scope=factory.Services.CreateScope(); var repo=scope.ServiceProvider.GetRequiredService<IPrimaryRepository>();var p=RecordPartition.Catalog("demo");
        foreach (var id in new[] { BusinessAnalysisService.StorageId(Analyst.PrincipalId,input.RunId),"business-analysis-slot","system-access-fence" })
            Assert.Null(await repo.ReadAsync(p,id,default));
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
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false" }).Build();
        var secondService = new BusinessAnalysisService(s.Repo, s.Store, new(s.Repo), s.Ai, config, TimeProvider.System);
        var second = Request();
        Assert.Equal("ANALYSIS_BUSY", (await Assert.ThrowsAsync<DomainException>(() => secondService.StartAsync(Analyst, second, second.RunId, default))).Code);
        release.SetResult(); await first;
        s = await Create(new() { ["BusinessAnalysis:TimeoutSeconds"] = "1" });
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Ai.During = () => stalled.Task;
        var request = Request(); var result = await Start(s, request);
        stalled.SetResult();
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
            var templateChallenge = (await s.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-business-challenge-"+(i-3), default))!.Value.Copy();
            templateChallenge["id"] = session.Text("challengeId"); templateChallenge["sessionId"] = sid;
            await s.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Create(catalog),RecordWrite.Create(templateChallenge)], default);
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
        Assert.Equal("AUTHORITY_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(Analyst with { PrincipalId = "other" }, request.RunId, id, default))).Code);
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
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false" }).Build();
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
            if (Variation == "credential-failure") throw new AiException(AiFailure.Unavailable) { InferenceNotDispatched = true };
            if (Variation is "rejected400" or "rejected404") throw new AiException(AiFailure.Unavailable) { ProviderRejected = true };
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
    private const string LiveTenant = "11111111-1111-4111-8111-111111111111";
    private static readonly ActorAccess LiveAnalyst = Analyst with { TenantId = LiveTenant };
    private static readonly ActorAccess LiveOperator = Operator with { TenantId = LiveTenant };
    private static readonly ActorAccess LiveObserver = new(LiveTenant,"principal-observing-analyst","observing-analyst",["member","analyst"],["demo"]);
    private sealed record LiveSetup(string Path, IRecordStore Store, IPrimaryRepository Repo, IConfigurationRoot Config, BusinessAnalysisService Service, WireFake Wire) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private static async Task<LiveSetup> CreateLive(string variation = "success", bool recoveryOperator = false, bool observingAnalyst = false)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dse-analysis-fake-" + Guid.NewGuid().ToString("N"));
        IRecordStore store = new OperationMeasuredRecordStore(new LocalRecordStore(new LocalIngestionFiles(path))); var repo = new PrimaryRepository(store);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:Mode"] = "Azure", ["Ai:Mode"] = "Live", ["Auth:TenantId"] = LiveTenant, ["BusinessAnalysis:Enabled"] = "true",
            ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "true", ["AiUsage:DailyTokenLimit"] = "300000",
            ["AiUsage:DailyCallLimit"] = "10", ["AiUsage:DailyEstimatedJpyLimit"] = "100", ["AiUsage:UsdToJpy"] = "150",
            ["AiUsage:PriceVersion"] = "fake-test-price", ["AiUsage:FxVersion"] = "fake-test-fx", ["AiUsage:MaxInputTokens"] = "16000",
            ["AiUsage:MaxOutputTokens"] = "1000", ["AiUsage:MaxAttempts"] = "3", ["Foundry:Endpoint"] = "https://dummy.openai.azure.com/",
            ["Foundry:Deployment"] = "dummy", ["Foundry:ModelId"] = "dummy-business-model-v1", ["Foundry:ModelVersion"] = "v1",
            ["Foundry:Region"] = "test", ["Foundry:Sku"] = "GlobalStandard", ["Foundry:InputUsdPerMillion"] = "1",
            ["Foundry:OutputUsdPerMillion"] = "2", ["Foundry:OutputLimit"] = "1000"
        }).Build();
        for (var i=0;i<2;i++)
        {
            var suffix = $"{i+1:D2}"; config[$"Auth:Bindings:{i}:TenantId"] = LiveTenant;
            config[$"Auth:Bindings:{i}:Oid"] = $"33333333-3333-4333-8333-{i+1:D12}";
            config[$"Auth:Bindings:{i}:PrincipalId"] = "principal-demo-"+suffix; config[$"Auth:Bindings:{i}:PersonId"] = "demo-person-"+suffix;
            config[$"Auth:Bindings:{i}:Roles:0"] = "member"; config[$"Auth:Bindings:{i}:Enabled"] = "true";
            config[$"Auth:Grants:{i}:PrincipalId"] = "principal-demo-"+suffix; config[$"Auth:Grants:{i}:ScopeIds:0"] = "demo";
        }
        config["Auth:Bindings:0:Roles:1"] = "analyst";
        if(recoveryOperator || observingAnalyst)
        {
            Assert.False(recoveryOperator && observingAnalyst);
            var additional=recoveryOperator?LiveOperator:LiveObserver;
            config["Auth:Bindings:2:TenantId"]=LiveTenant;config["Auth:Bindings:2:Oid"]="33333333-3333-4333-8333-000000000003";
            config["Auth:Bindings:2:PrincipalId"]=additional.PrincipalId;config["Auth:Bindings:2:PersonId"]=additional.PersonId;
            config["Auth:Bindings:2:Enabled"]="true";config["Auth:Bindings:2:Roles:0"]=additional.Roles[0];config["Auth:Bindings:2:Roles:1"]=additional.Roles[1];
            config["Auth:Grants:2:PrincipalId"]=additional.PrincipalId;config["Auth:Grants:2:ScopeIds:0"]="demo";
        }
        await AuthorityFixture.InitializeAsync(store,config,LiveTenant);
        await BusinessAnalysisDemoFixtures.SeedAsync(store, default, LiveTenant);
        var wire = new WireFake { Variation = variation }; var live = new BusinessAnalysisLiveDrafter(config, repo, new(store, TimeProvider.System), wire, TimeProvider.System);
        return new(path, store, repo, config, new(repo, store, new(repo), new BusinessAnalysisDrafter(config, live), config, TimeProvider.System), wire);
    }
    private static string FindRootForTrace()
    {
        var d=new DirectoryInfo(AppContext.BaseDirectory);
        while(!File.Exists(Path.Combine(d!.FullName,"DseGrowthLoop.slnx")))d=d.Parent;
        return d.FullName;
    }
    private static BusinessAnalysisRequest LiveRequest() => Request() with { ConfigVersion = BusinessAnalysisLiveRuntime.LiveConfigVersion };
    private static Task<JsonObject> StartLive(LiveSetup s, BusinessAnalysisRequest r) => s.Service.StartAsync(LiveAnalyst, r, r.RunId, default);
    private static Task<RecordPage> Rows(LiveSetup s, string kind) => s.Store.QueryAsync(RecordPartition.Catalog(LiveTenant), kind, null, null, 100, null, default);
    private static async Task<StoredRecord> Day(LiveSetup s)
    {
        var reservation = Assert.Single((await Rows(s, "aiReservation")).Items);
        return (await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant), reservation.Value.Text("dayId"), default))!;
    }
    [Theory] [InlineData("rebound")] [InlineData("missing_pin")] [InlineData("profile_owner")] [InlineData("disabled")] [InlineData("scope")]
    public async Task Current_consent_principal_is_required_before_live_send_and_saved_result_read(string change)
    {
        using var s = await CreateLive(); var request = LiveRequest(); await StartLive(s,request); Assert.Equal(1,s.Wire.Calls);
        if (change == "missing_pin")
        {
            for (var i=1;i<=3;i++)
            {
                var sid="demo-business-session-"+i; var p=RecordPartition.Conversation(LiveTenant,sid); var source=(await s.Repo.ReadAsync(p,KnowledgeService.Id(sid),default))!;
                source.Value["consents"]!["demo-person-02"]!.AsObject().Remove("principalId"); await s.Repo.CommitAsync(p,[RecordWrite.Replace(source.Value,source.ETag)],default);
            }
        }
        else if (change == "disabled") s.Config["Auth:Bindings:1:Enabled"] = "false";
        else if (change == "scope") s.Config["Auth:Grants:1:ScopeIds:0"] = "outside";
        else
        {
            var p=RecordPartition.Catalog(LiveTenant); var profile=(await s.Repo.ReadAsync(p,"demo-person-02",default))!;
            profile.Value["ownerId"] = "principal-replacement"; await s.Repo.CommitAsync(p,[RecordWrite.Replace(profile.Value,profile.ETag)],default);
            if (change == "rebound") { s.Config["Auth:Bindings:1:PrincipalId"] = "principal-replacement"; s.Config["Auth:Grants:1:PrincipalId"] = "principal-replacement"; }
        }
        var saved=await s.Service.GetAsync(LiveAnalyst,request.RunId,default); Assert.Equal("invalidated",saved.Text("status")); Assert.Empty(saved["groups"]!.AsArray()); Assert.Null(saved["sourceCount"]);
        Assert.Equal("SOURCE_REVOKED",(await Assert.ThrowsAsync<DomainException>(() => s.Service.SourceAsync(LiveAnalyst,request.RunId,KnowledgeService.Id("demo-business-session-1"),default))).Code);
        var next=await StartLive(s,LiveRequest()); Assert.Equal(0,next.Number("sourceCount")); Assert.Empty(next["groups"]!.AsArray()); Assert.Equal(1,s.Wire.Calls);
    }
    [Fact]
    public async Task Historical_Ledger_analysis_replays_after_upgrade_without_another_provider_call()
    {
        using var s=await CreateLive();s.Config["AiUsage:MaxAttempts"]="1";var request=LiveRequest();var first=await StartLive(s,request);
        var partition=RecordPartition.Catalog(LiveTenant);var saved=(await s.Store.ReadAsync(partition,BusinessAnalysisService.StorageId(LiveAnalyst.PrincipalId,request.RunId),default))!;
        var l=AiLimits.Read(s.Config);var model=AiModel.Read(s.Config,"Foundry",l.OutputTokens);
        // Explicit pre-upgrade shape: no AccountingEnabled property existed.
        var limits=new {l.Stopped,l.LiveOptIn,l.DailyTokens,l.DailyCalls,l.DailyJpy,l.UsdToJpy,l.PriceVersion,l.FxVersion,l.InputTokens,l.OutputTokens,l.Concurrency,l.Attempts,l.TimeoutSeconds,l.Weekly,l.WeeklyCalls,l.WeeklyTotalJpy,l.InfrastructureReserveJpy,l.ExternalAiReserveJpy};
        string Hash(string value)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        var historical=Hash(RecordJson.Canonical(JsonSerializer.SerializeToNode(new {limits,model,promptVersion=BusinessAnalysisLiveDrafter.PromptVersion,configVersion=BusinessAnalysisLiveRuntime.LiveConfigVersion},Json)));
        saved.Value["configurationHash"]=historical;
        saved.Value["requestHash"]=Hash(RecordJson.Canonical(JsonSerializer.SerializeToNode(request,Json))+RecordJson.Canonical(saved.Value["limits"])+historical);
        await s.Store.CommitAsync(partition,[RecordWrite.Replace(saved.Value,saved.ETag)],default);
        var replay=await StartLive(s,request);Assert.Equal(first.Text("inputHash"),replay.Text("inputHash"));Assert.Equal(1,s.Wire.Calls);
        s.Config["AiUsage:AccountingMode"]="Disabled";s.Config["AiUsage:MaxAttempts"]="1";
        Assert.NotEqual(historical,BusinessAnalysisLiveRuntime.ConfigurationHash(s.Config));
        Assert.Equal("IDEMPOTENCY_CONFLICT",(await Assert.ThrowsAsync<DomainException>(()=>StartLive(s,request))).Code);Assert.Equal(1,s.Wire.Calls);
    }
    private sealed class DeferredCredentials : IAiCredentials
    {
        public TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> BearerAsync(string provider,CancellationToken ct)=>Result.Task;
    }
    private sealed class NoInferenceHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;throw new InvalidOperationException("Inference must never be reached");}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Analysis_deadline_during_credentials_releases_both_slots_and_forbids_late_dispatch(bool disabled)
    {
        using var s=await CreateLive();s.Config["BusinessAnalysis:TimeoutSeconds"]="1";s.Config["AiUsage:TimeoutSeconds"]="30";
        if(disabled){s.Config["AiUsage:AccountingMode"]="Disabled";s.Config["AiUsage:MaxAttempts"]="1";}
        var credentials=new DeferredCredentials();var handler=new NoInferenceHandler();
        using var transport=new AiHttpTransport(new HttpClient(handler),credentials,TimeProvider.System,s.Config);
        var live=new BusinessAnalysisLiveDrafter(s.Config,s.Repo,new(s.Store,TimeProvider.System),transport,TimeProvider.System);
        var service=new BusinessAnalysisService(s.Repo,s.Store,new(s.Repo),new BusinessAnalysisDrafter(s.Config,live),s.Config,TimeProvider.System);
        try
        {
            var request=LiveRequest();var failed=await service.StartAsync(LiveAnalyst,request,request.RunId,default).WaitAsync(TimeSpan.FromSeconds(16));
            Assert.Equal("partial_failure",failed.Text("status"));Assert.False(failed["recoveryRequired"]!.GetValue<bool>());Assert.Equal(0,handler.Calls);
            var partition=RecordPartition.Catalog(LiveTenant);
            Assert.Equal("ready",(await s.Store.ReadAsync(partition,"business-analysis-slot",default))!.Value.Text("status"));
            Assert.Empty((await s.Store.ReadAsync(partition,"system-ai-gate",default))!.Value.Strings("active"));
            Assert.Equal("completed",(await StartLive(s,LiveRequest())).Text("status"));Assert.Equal(1,s.Wire.Calls);
            credentials.Result.TrySetResult("late-fake-credential");await Task.Delay(50);Assert.Equal(0,handler.Calls);
        }
        finally {credentials.Result.TrySetResult("late-fake-credential");}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Azure_mode_fake_transport_uses_shared_admission_and_restarts_without_resending_or_private_inputs(bool accountingDisabled)
    {
        using var s = await CreateLive();
        if (accountingDisabled)
        {
            s.Config["AiUsage:AccountingMode"]="Disabled";s.Config["AiUsage:MaxAttempts"]="1";
            s.Config["AiUsage:DailyTokenLimit"]="0";s.Config["AiUsage:DailyCallLimit"]="0";s.Config["AiUsage:DailyEstimatedJpyLimit"]="0";
            s.Config["AiUsage:UsdToJpy"]="0";s.Config["AiUsage:PriceVersion"]="";s.Config["AiUsage:FxVersion"]="";
        }
        using var measurement = OperationMeasurements.Begin(MeasurementEvidence.Fixture); var request = LiveRequest(); var result = await StartLive(s, request);
        Assert.Equal("completed", result.Text("status")); Assert.Equal("live", result.Text("processingMode")); Assert.Equal(1, s.Wire.Calls);
        Assert.Equal(12m, result["groups"]![0]!["metrics"]!["reportedHours"]!.GetValue<decimal>());
        var userInput = s.Wire.Sent!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.DoesNotContain("session", userInput); Assert.DoesNotContain("principal", userInput); Assert.DoesNotContain("reportedHours", userInput); Assert.DoesNotContain("department", userInput);
        var reservation = Assert.Single((await Rows(s, accountingDisabled ? "aiExecution" : "aiReservation")).Items); Assert.Equal(accountingDisabled ? "completed" : "settled", reservation.Value.Text("state"));
        if (!accountingDisabled) { var day = (await Day(s)); Assert.Equal(1, day.Value.Number("calls")); Assert.Equal(120, day.Value.Number("tokens")); Assert.Equal(.021m, day.Value["jpy"]!.GetValue<decimal>()); }
        var audit = Assert.Single((await Rows(s, "aiRun")).Items).Value; Assert.Equal(1, audit["trace"]!["attemptCount"]!.GetValue<int>()); Assert.Equal(0, audit["trace"]!["retryCount"]!.GetValue<int>()); Assert.Null(audit["trace"]!["requestUnits"]); Assert.Equal("not_collected", audit["trace"]!["requestUnitsStatus"]!.GetValue<string>());
        var traceOutput=Path.Combine(FindRootForTrace(),"tests/Dse.Tests/TestResults");Directory.CreateDirectory(traceOutput);
        await File.WriteAllTextAsync(Path.Combine(traceOutput,accountingDisabled?"business-ai-run-disabled.json":"business-ai-run-ledger.json"),audit.ToJsonString());
        var measured = accountingDisabled ? measurement.Export(MeasurementKind.SourceRead, MeasurementKind.FenceWrite, MeasurementKind.AuditWrite, MeasurementKind.HttpRequest) : measurement.Export(MeasurementKind.SourceRead, MeasurementKind.FenceWrite, MeasurementKind.AuditWrite, MeasurementKind.LedgerReserve, MeasurementKind.LedgerSettle, MeasurementKind.HttpRequest);
        Assert.Empty(measured.MissingExpectedKinds); Assert.All(measured.Operations, op => Assert.Null(op.RequestUnits.Value)); Assert.False(measured.CompleteMeasurement); Assert.False(measured.AcceptanceGatePassed);
        Assert.DoesNotContain("共通課題の仮説", audit.ToJsonString());
        var restartedStore = new LocalRecordStore(new LocalIngestionFiles(s.Path)); var restartedRepo = new PrimaryRepository(restartedStore);
        var service = new BusinessAnalysisService(restartedRepo, restartedStore, new(restartedRepo), new BusinessAnalysisDrafter(s.Config, new(s.Config, restartedRepo, new(restartedStore, TimeProvider.System), s.Wire, TimeProvider.System)), s.Config, TimeProvider.System);
        var replay = await service.StartAsync(LiveAnalyst, request, request.RunId, default); Assert.Equal(result.Text("inputHash"), replay.Text("inputHash")); Assert.Equal(1, s.Wire.Calls);
        s.Config["Foundry:InputUsdPerMillion"] = "3";
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, request))).Code); Assert.Equal(1, s.Wire.Calls);
        s.Config["AiUsage:Stopped"] = "true";
        Assert.Equal("AI_BUDGET_STOPPED", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, LiveRequest()))).Code); Assert.Equal(1, s.Wire.Calls);
        var authority = await s.Repo.ReadAsync(RecordPartition.Conversation(LiveTenant, "demo-business-session-1"), KnowledgeService.Id("demo-business-session-1"), default);
        authority!.Value["consents"]!["demo-person-02"]!["analysis"] = false;
        await s.Repo.CommitAsync(RecordPartition.Conversation(LiveTenant, "demo-business-session-1"), [RecordWrite.Replace(authority.Value, authority.ETag)], default);
        Assert.Equal("invalidated", (await s.Service.GetAsync(LiveAnalyst, request.RunId, default)).Text("status")); Assert.Equal(1, s.Wire.Calls);
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
        Assert.Null(await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant), "system-access-fence", default));
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
        if (variation == "excessusage") Assert.True((await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant), "system-ai-gate", default))!.Value["blocked"]!.GetValue<bool>());
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
    private static BusinessAnalysisRecoveryRequest RecoveryRequest(JsonObject inspection, string runId) => new(runId, Analyst.PrincipalId, inspection.Text("runEtag"), inspection.Text("slotEtag"),
        "approval-local-fake", true, true, true, "stop-evidence-local-fake", new string('a', 64), inspection.Strings("pendingReservationIds"));
    [Theory] [InlineData("rejected400")] [InlineData("rejected404")] [InlineData("credential-failure")]
    public async Task Definite_provider_rejection_releases_business_slot_and_allows_a_corrected_new_analysis(string variation)
    {
        using var s=await CreateLive(variation:variation);s.Config["AiUsage:AccountingMode"]="Disabled";s.Config["AiUsage:MaxAttempts"]="1";
        var failed=await StartLive(s,LiveRequest());Assert.Equal("partial_failure",failed.Text("status"));Assert.Equal(1,s.Wire.Calls);
        Assert.Equal("ready",(await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant),"business-analysis-slot",default))!.Value.Text("status"));
        Assert.Empty((await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant),"system-ai-gate",default))!.Value.Strings("active"));
        s.Wire.Variation="success";var corrected=await StartLive(s,LiveRequest());Assert.Equal("completed",corrected.Text("status"));Assert.Equal(2,s.Wire.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Timeout_retains_pending_cost_or_execution_slot_until_evidence_based_offline_recovery_without_retry(bool accountingDisabled)
    {
        using var s = await CreateLive(recoveryOperator:true); s.Config["AiUsage:TimeoutSeconds"] = "1";
        if (accountingDisabled) { s.Config["AiUsage:AccountingMode"] = "Disabled"; s.Config["AiUsage:MaxAttempts"] = "1"; }
        var reservationKind = accountingDisabled ? "aiExecution" : "aiReservation";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Wire.During = async () => { entered.SetResult(); await release.Task; }; // Ignores cancellation, just like an uncertain remote request.
        var request = LiveRequest(); var task = StartLive(s, request); await entered.Task; var result = await task;
        Assert.Equal("partial_failure", result.Text("status")); Assert.True(result["recoveryRequired"]!.GetValue<bool>());
        Assert.Equal("ANALYSIS_RECOVERY_REQUIRED", (await Assert.ThrowsAsync<DomainException>(() => StartLive(s, LiveRequest()))).Code);
        Assert.Equal(1, s.Wire.Calls); var pending = Assert.Single((await Rows(s, reservationKind)).Items); Assert.Equal("pending", pending.Value.Text("state"));
        var gate = (await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant), "system-ai-gate", default))!; Assert.Contains(pending.Value.Text("id"), gate.Value.Strings("active"));
        release.SetResult(); await s.Wire.Finished.Task; // Tests terminate their fake; real offline termination requires independently reviewed evidence.
        s.Config["AiUsage:Stopped"] = "true"; s.Config["AiUsage:LiveOptIn"] = "false";
        var recovery = new BusinessAnalysisRecovery(s.Store, s.Config, TimeProvider.System);
        Assert.Equal("FORBIDDEN", (await Assert.ThrowsAsync<DomainException>(() => recovery.InspectAsync(LiveAnalyst, Analyst.PrincipalId, request.RunId, default))).Code);
        var inspection = await recovery.InspectAsync(LiveOperator, Analyst.PrincipalId, request.RunId, default); var input = RecoveryRequest(inspection, request.RunId);
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RecoverAsync(LiveOperator, input with { ProvidersStopped = false }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.RecoverAsync(LiveOperator, input with { StopEvidenceSha256 = "invalid" }, default));
        Assert.Equal("STALE_VERSION", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(LiveOperator, input with { SlotETag = "\"stale-version\"" }, default))).Code);
        Assert.Equal("RECOVERY_RESERVATION_MISMATCH", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(LiveOperator, input with { PendingReservationIds = [] }, default))).Code);
        var dayBefore = accountingDisabled ? null : (await Day(s)).Value.ToJsonString();
        var recovered = await recovery.RecoverAsync(LiveOperator, input, default); Assert.Equal("aborted", recovered.Text("status")); Assert.Equal(0m, recovered["refundedJpy"]!.GetValue<decimal>());
        if (!accountingDisabled) Assert.Equal(dayBefore, (await Day(s)).Value.ToJsonString());
        else { Assert.Empty((await Rows(s,"aiReservation")).Items); Assert.Null(await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant),"ai-day-"+DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),default)); } Assert.Equal("uncertain", Assert.Single((await Rows(s, reservationKind)).Items).Value.Text("state"));
        Assert.Empty((await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant), "system-ai-gate", default))!.Value.Strings("active"));
        Assert.Equal("ready", (await s.Store.ReadAsync(RecordPartition.Catalog(LiveTenant), "business-analysis-slot", default))!.Value.Text("status"));
        var saved = (await s.Service.GetAsync(LiveAnalyst, request.RunId, default)); Assert.Equal("aborted", saved.Text("status")); Assert.Empty(saved["groups"]!.AsArray()); Assert.Null(saved["sourceCount"]);
        Assert.Equal(recovered.ToJsonString(), (await recovery.RecoverAsync(LiveOperator, input, default)).ToJsonString()); Assert.Equal(1, s.Wire.Calls);
        Assert.Equal("RECOVERY_REQUEST_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => recovery.RecoverAsync(LiveOperator, input with { StopEvidenceReference = "different-proof" }, default))).Code);
    }
    [Fact]
    public async Task Offline_recovery_never_releases_another_run_and_requires_explicit_stop()
    {
        var s = await Create(new() { ["BusinessAnalysis:TimeoutSeconds"] = "1" },recoveryOperator:true);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); s.Ai.During = () => stalled.Task;
        var request = Request(); await Start(s, request); stalled.SetResult();
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
    public async Task Public_source_identifiers_are_rejected_without_model_send_and_saved_results_fail_closed(string identifying)
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
        public string? ChangeGuardBeforeRecovery;
        public bool MismatchedFenceTenant;
        public Func<Task>? AfterTargetReservationQuery;
        public Func<Task>? AfterRecoveryReceiptRead;
        public List<(string Kind, string? Field, string? Parent)> Queries = [];
        public async Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct)
        {
            var row=id==MissingId?null:await inner.ReadAsync(p,id,ct);
            if(row is not null && id=="system-access-fence" && MismatchedFenceTenant)row.Value["tenantId"]="other-tenant";
            if(row is not null && id.StartsWith("analysis-recovery-",StringComparison.Ordinal) && AfterRecoveryReceiptRead is { } change)
            {AfterRecoveryReceiptRead=null;await change();}
            return row;
        }
        public async Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
        {
            if(ChangeGuardBeforeRecovery is { } guardId && writes.Any(w=>w.Value.Text("operation")=="businessAnalysisOfflineRecovery"))
            {
                ChangeGuardBeforeRecovery=null;
                var row=(await inner.ReadAsync(p,guardId,ct))!;
                if(guardId==AuthorityProvider.ControlId)row.Value["revision"]=row.Value.Number("revision")+1;
                else if(guardId=="system-access-fence")row.Value["holder"]="another-active-operation";
                else {row.Value["enabled"]=false;row.Value["accessEpoch"]=Guid.NewGuid().ToString("N");}
                await inner.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],ct);
            }
            if (ChangeGateBeforeRecovery && writes.Any(w => w.Value.Text("operation") == "businessAnalysisOfflineRecovery"))
            {
                ChangeGateBeforeRecovery = false;
                var gate = (await inner.ReadAsync(p, "system-ai-gate", ct))!; gate.Value["blocked"] = false;
                await inner.CommitAsync(p, [RecordWrite.Replace(gate.Value, gate.ETag)], ct);
            }
            await inner.CommitAsync(p, writes, ct);
        }
        public async Task<RecordPage> QueryAsync(RecordPartition p, string kind, string? field, string? parent, int size, string? token, CancellationToken ct)
        {
            Queries.Add((kind, field, parent));
            if (kind == "aiReservation" && field != "runId") throw new InvalidOperationException("Tenant-wide reservation scan is forbidden in this regression");
            var page=await inner.QueryAsync(p, kind, field, parent, size, token, ct);
            if(kind=="aiReservation" && parent==RecoveryTargetAi && AfterTargetReservationQuery is { } change)
            {AfterTargetReservationQuery=null;await change();}
            return page;
        }
    }
    private static async Task<(Setup Setup, BusinessAnalysisRequest Run, RecoveryStore Guard, BusinessAnalysisRecovery Recovery)> RecoveryFixture(int history = 0)
    {
        var s = await Create(recoveryOperator:true); var request = Request(); await Start(s, request); var p = RecordPartition.Catalog("demo");
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

    private static async Task<Dictionary<string,StoredRecord>> RecoveryState(Setup s,string runId)
    {
        var p=RecordPartition.Catalog("demo");var snapshot=new Dictionary<string,StoredRecord>();
        foreach(var id in new[]{BusinessAnalysisService.StorageId(Analyst.PrincipalId,runId),"business-analysis-slot",RecoveryTargetReservation,RecoveryOtherReservation,"system-ai-gate","ai-day-recovery-fixture"})
            snapshot[id]=(await s.Store.ReadAsync(p,id,default))!;
        return snapshot;
    }
    private static async Task RecoveryUnchanged(Setup s,Dictionary<string,StoredRecord> snapshot)
    {
        foreach(var(id,before) in snapshot)
        {
            var after=(await s.Store.ReadAsync(RecordPartition.Catalog("demo"),id,default))!;
            Assert.Equal(before.ETag,after.ETag);Assert.True(JsonNode.DeepEquals(before.Value,after.Value));
        }
    }
    private static async Task RevokeRecoveryOperator(Setup s)
    {
        var p=RecordPartition.Catalog("demo");var person=(await s.Store.ReadAsync(p,AuthorityProvider.PersonId(Operator.PersonId),default))!;
        var control=(await s.Store.ReadAsync(p,AuthorityProvider.ControlId,default))!;
        person.Value["enabled"]=false;person.Value["accessEpoch"]=Guid.NewGuid().ToString("N");control.Value["revision"]=control.Value.Number("revision")+1;
        await s.Store.CommitAsync(p,[RecordWrite.Replace(person.Value,person.ETag),RecordWrite.Replace(control.Value,control.ETag)],default);
    }
    [Fact] public async Task Recovery_rejects_forged_operator_claims_without_canonical_operator_role()
    {
        var(s,run,_,recovery)=await RecoveryFixture();var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);
        var forged=Analyst with{Roles=["operator","analyst"]};var before=await RecoveryState(s,run.RunId);
        Assert.Equal("FORBIDDEN",(await Assert.ThrowsAsync<DomainException>(()=>recovery.InspectAsync(forged,Analyst.PrincipalId,run.RunId,default))).Code);
        Assert.Equal("FORBIDDEN",(await Assert.ThrowsAsync<DomainException>(()=>recovery.RecoverAsync(forged,RecoveryRequest(inspection,run.RunId),default))).Code);
        await RecoveryUnchanged(s,before);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Recovery_rechecks_operator_revocation_during_ledger_scan(bool inspect)
    {
        var(s,run,guard,recovery)=await RecoveryFixture();var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);
        var before=await RecoveryState(s,run.RunId);guard.AfterTargetReservationQuery=()=>RevokeRecoveryOperator(s);
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>inspect
            ?recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default)
            :recovery.RecoverAsync(Operator,RecoveryRequest(inspection,run.RunId),default))).Code);
        await RecoveryUnchanged(s,before);
    }
    [Theory][InlineData("control")][InlineData("person")][InlineData("fence")]
    public async Task Recovery_guards_authority_and_fence_changes_after_final_validation_in_the_batch(string race)
    {
        var(s,run,guard,recovery)=await RecoveryFixture();var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);
        var before=await RecoveryState(s,run.RunId);
        guard.ChangeGuardBeforeRecovery=race=="control"?AuthorityProvider.ControlId:race=="person"?AuthorityProvider.PersonId(Operator.PersonId):"system-access-fence";
        await Assert.ThrowsAsync<StoreConflictException>(()=>recovery.RecoverAsync(Operator,RecoveryRequest(inspection,run.RunId),default));
        await RecoveryUnchanged(s,before);
        if(race=="fence")Assert.Equal("another-active-operation",(await s.Store.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value.Text("holder"));
    }
    [Theory]
    [InlineData("disabled",false)][InlineData("operator_role",false)][InlineData("analyst_role",false)][InlineData("principal",false)][InlineData("scope",false)]
    [InlineData("disabled",true)][InlineData("operator_role",true)][InlineData("analyst_role",true)][InlineData("principal",true)][InlineData("scope",true)]
    public async Task Recovery_checks_current_grants_before_inspection_apply_and_completed_receipt_replay(string change,bool replay)
    {
        var(s,run,_,recovery)=await RecoveryFixture();var p=RecordPartition.Catalog("demo");
        var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);var input=RecoveryRequest(inspection,run.RunId);
        if(replay)await recovery.RecoverAsync(Operator,input,default);
        var before=await RecoveryState(s,run.RunId);var person=(await s.Store.ReadAsync(p,AuthorityProvider.PersonId(Operator.PersonId),default))!;
        if(change=="disabled")person.Value["enabled"]=false;
        else if(change=="principal")person.Value["principalId"]="principal-replacement-operator";
        else if(change=="scope")person.Value["scopeIds"]=RecordJson.Array(["outside"]);
        else person.Value["roles"]=RecordJson.Array(change=="operator_role"?["analyst"]:["operator"]);
        await s.Store.CommitAsync(p,[RecordWrite.Replace(person.Value,person.ETag)],default);
        var expected=change=="scope"?"NOT_FOUND":change.EndsWith("_role",StringComparison.Ordinal)?"FORBIDDEN":"AUTHORITY_REVOKED";
        Assert.Equal(expected,(await Assert.ThrowsAsync<DomainException>(()=>recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default))).Code);
        Assert.Equal(expected,(await Assert.ThrowsAsync<DomainException>(()=>recovery.RecoverAsync(Operator,input,default))).Code);
        await RecoveryUnchanged(s,before);
    }
    [Fact] public async Task Recovery_rejects_the_old_operator_epoch_even_after_grants_are_restored()
    {
        var(s,run,_,recovery)=await RecoveryFixture();var p=RecordPartition.Catalog("demo");var provider=new AuthorityProvider(s.Repo);
        var old=(await provider.PersonAsync("demo",Operator.PersonId,default))!;var inspection=await recovery.InspectAsync(old,Analyst.PrincipalId,run.RunId,default);
        var before=await RecoveryState(s,run.RunId);await RevokeRecoveryOperator(s);
        var person=(await s.Store.ReadAsync(p,AuthorityProvider.PersonId(Operator.PersonId),default))!;
        person.Value["enabled"]=true;person.Value["accessEpoch"]=Guid.NewGuid().ToString("N");await s.Store.CommitAsync(p,[RecordWrite.Replace(person.Value,person.ETag)],default);
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>recovery.InspectAsync(old,Analyst.PrincipalId,run.RunId,default))).Code);
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>recovery.RecoverAsync(old,RecoveryRequest(inspection,run.RunId),default))).Code);
        var current=(await provider.PersonAsync("demo",Operator.PersonId,default))!;
        await recovery.InspectAsync(current,Analyst.PrincipalId,run.RunId,default);await RecoveryUnchanged(s,before);
    }
    [Theory][InlineData("missing")][InlineData("kind")][InlineData("tenant")][InlineData("holder_missing")][InlineData("holder_nonstring")][InlineData("held")]
    public async Task Recovery_requires_a_known_empty_fence_and_never_releases_an_existing_holder(string damage)
    {
        var(s,run,guard,recovery)=await RecoveryFixture();var p=RecordPartition.Catalog("demo");
        var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);var before=await RecoveryState(s,run.RunId);
        if(damage=="missing")guard.MissingId="system-access-fence";
        else if(damage=="tenant")guard.MismatchedFenceTenant=true;
        else
        {
            var row=(await s.Store.ReadAsync(p,"system-access-fence",default))!;
            if(damage=="kind")row.Value["kind"]="job";
            else if(damage=="holder_missing")row.Value.Remove("holder");
            else row.Value["holder"]=damage=="held"?JsonValue.Create("stopped-holder-needs-separate-recovery"):JsonValue.Create(true);
            await s.Store.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);
        }
        Assert.Equal(damage=="held"?"RECOVERY_REQUIRES_FENCE_RECOVERY":"RECOVERY_FENCE_INCONSISTENT",
            (await Assert.ThrowsAsync<DomainException>(()=>recovery.RecoverAsync(Operator,RecoveryRequest(inspection,run.RunId),default))).Code);
        await RecoveryUnchanged(s,before);
        if(damage=="held")Assert.Equal("stopped-holder-needs-separate-recovery",(await s.Store.ReadAsync(p,"system-access-fence",default))!.Value.Text("holder"));
    }
    [Fact] public async Task Recovery_receipt_read_rechecks_a_revocation_before_returning_the_saved_result()
    {
        var(s,run,guard,recovery)=await RecoveryFixture();var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);
        var input=RecoveryRequest(inspection,run.RunId);await recovery.RecoverAsync(Operator,input,default);
        var before=await RecoveryState(s,run.RunId);guard.AfterRecoveryReceiptRead=()=>RevokeRecoveryOperator(s);
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>recovery.RecoverAsync(Operator,input,default))).Code);
        await RecoveryUnchanged(s,before);
    }
    [Fact] public async Task Recovery_guard_writes_preserve_authority_values_and_read_only_paths_preserve_ETags()
    {
        var(s,run,_,recovery)=await RecoveryFixture();var p=RecordPartition.Catalog("demo");
        var ids=new[]{AuthorityProvider.ControlId,AuthorityProvider.PersonId(Operator.PersonId),"system-access-fence"};
        var before=new Dictionary<string,StoredRecord>();foreach(var id in ids)before[id]=(await s.Store.ReadAsync(p,id,default))!;
        var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);
        foreach(var id in ids)Assert.Equal(before[id].ETag,(await s.Store.ReadAsync(p,id,default))!.ETag);
        var input=RecoveryRequest(inspection,run.RunId);await recovery.RecoverAsync(Operator,input,default);
        var after=new Dictionary<string,StoredRecord>();
        foreach(var id in ids)
        {
            after[id]=(await s.Store.ReadAsync(p,id,default))!;Assert.NotEqual(before[id].ETag,after[id].ETag);
            Assert.True(JsonNode.DeepEquals(RecordJson.ToStorage(before[id].Value),RecordJson.ToStorage(after[id].Value)));
        }
        await recovery.RecoverAsync(Operator,input,default);
        foreach(var id in ids)Assert.Equal(after[id].ETag,(await s.Store.ReadAsync(p,id,default))!.ETag);
    }
    [Fact] public async Task Recovery_rejects_an_Azure_binding_config_change_at_the_service_boundary()
    {
        using var s=await CreateLive(recoveryOperator:true);var run=LiveRequest();await StartLive(s,run);
        s.Config["AiUsage:Stopped"]="true";s.Config["AiUsage:LiveOptIn"]="false";
        var recovery=new BusinessAnalysisRecovery(s.Store,s.Config,TimeProvider.System);
        var inspection=await recovery.InspectAsync(LiveOperator,Analyst.PrincipalId,run.RunId,default);
        s.Config["Auth:Bindings:2:Roles:0"]="member";
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>recovery.InspectAsync(LiveOperator,Analyst.PrincipalId,run.RunId,default))).Code);
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>recovery.RecoverAsync(LiveOperator,RecoveryRequest(inspection,run.RunId),default))).Code);
        Assert.Equal(1,s.Wire.Calls);
    }
    [Fact] public async Task Recovery_rejects_a_stop_digest_with_a_trailing_newline_without_writes()
    {
        var(s,run,_,recovery)=await RecoveryFixture();var inspection=await recovery.InspectAsync(Operator,Analyst.PrincipalId,run.RunId,default);
        var before=await RecoveryState(s,run.RunId);var input=RecoveryRequest(inspection,run.RunId);
        await Assert.ThrowsAsync<ArgumentException>(()=>recovery.RecoverAsync(Operator,input with{StopEvidenceSha256=input.StopEvidenceSha256+"\n"},default));
        await RecoveryUnchanged(s,before);
    }
    [Theory]
    [InlineData("roles","result")][InlineData("roles","source")][InlineData("roles","decision")][InlineData("roles","decision_receipt")]
    [InlineData("scope","result")][InlineData("scope","source")][InlineData("scope","decision")][InlineData("scope","decision_receipt")]
    [InlineData("principal","result")][InlineData("principal","source")][InlineData("principal","decision")][InlineData("principal","decision_receipt")]
    [InlineData("oid","result")][InlineData("oid","source")][InlineData("oid","decision")][InlineData("oid","decision_receipt")]
    [InlineData("disabled","result")][InlineData("disabled","source")][InlineData("disabled","decision")][InlineData("disabled","decision_receipt")]
    public async Task Saved_analysis_reader_rejects_config_mismatch_when_not_a_source_participant(string change,string action)
    {
        using var s=await CreateLive(observingAnalyst:true);var request=LiveRequest();
        var actor=(await new AuthorityProvider(s.Repo,s.Config).PersonAsync(LiveTenant,LiveObserver.PersonId,default))!;
        var result=await s.Service.StartAsync(actor,request,request.RunId,default);
        Assert.Equal("completed",result.Text("status"));Assert.NotEmpty(result["groups"]!.AsArray());
        var before=await s.Service.GetAsync(actor,request.RunId,default);Assert.Equal("completed",before.Text("status"));
        var knowledge=KnowledgeService.Id("demo-business-session-1");await s.Service.SourceAsync(actor,request.RunId,knowledge,default);
        var input=new BusinessAnalysisDecision("on_hold");var key="reader-decision-"+Guid.NewGuid().ToString("N");var etag=before.Text("etag");
        if(action=="decision_receipt")await s.Service.DecideAsync(actor,request.RunId,input,key,etag,default);
        var p=RecordPartition.Catalog(LiveTenant);var id=BusinessAnalysisService.StorageId(actor.PrincipalId,request.RunId);
        var saved=(await s.Store.ReadAsync(p,id,default))!;var receipts=(await Rows(s,"idempotency")).Items;
        var control=(await s.Store.ReadAsync(p,AuthorityProvider.ControlId,default))!;
        var person=(await s.Store.ReadAsync(p,AuthorityProvider.PersonId(actor.PersonId),default))!;
        Dictionary<string,string?> original=[];
        void Set(string setting,string value){original[setting]=s.Config[setting];s.Config[setting]=value;}
        if(change=="roles")Set("Auth:Bindings:2:Roles:1","publisher");
        else if(change=="scope")Set("Auth:Grants:2:ScopeIds:0","outside");
        else if(change=="disabled")Set("Auth:Bindings:2:Enabled","false");
        else if(change=="oid")Set("Auth:Bindings:2:Oid","44444444-4444-4444-8444-000000000004");
        else{Set("Auth:Bindings:2:PrincipalId","principal-rebound-observer");Set("Auth:Grants:2:PrincipalId","principal-rebound-observer");}
        var consumed=false;
        Task<JsonObject> Consume(JsonObject body,CancellationToken _){consumed=true;return Task.FromResult(body);}
        Task<JsonObject> Attempt()=>action switch
        {
            "result"=>s.Service.ConsumeAsync(actor,request.RunId,Consume,default),
            "source"=>s.Service.ConsumeSourceAsync(actor,request.RunId,knowledge,Consume,default),
            _=>s.Service.DecideAsync(actor,request.RunId,input,key,etag,default)
        };
        Assert.Equal("AUTHORITY_REVOKED",(await Assert.ThrowsAsync<DomainException>(Attempt)).Code);Assert.False(consumed);
        var after=(await s.Store.ReadAsync(p,id,default))!;Assert.Equal(saved.ETag,after.ETag);Assert.True(JsonNode.DeepEquals(saved.Value,after.Value));
        Assert.Equal(receipts.Select(x=>x.ETag),(await Rows(s,"idempotency")).Items.Select(x=>x.ETag));
        Assert.Equal(control.ETag,(await s.Store.ReadAsync(p,AuthorityProvider.ControlId,default))!.ETag);
        Assert.Equal(person.ETag,(await s.Store.ReadAsync(p,AuthorityProvider.PersonId(actor.PersonId),default))!.ETag);
        foreach(var(setting,value) in original)s.Config[setting]=value;
        Assert.Equal("completed",(await s.Service.GetAsync(actor,request.RunId,default)).Text("status"));
        Assert.Equal(1,s.Wire.Calls);
    }
    [Fact] public async Task Valid_separate_analyst_still_cannot_read_or_decide_another_owners_analysis()
    {
        using var s=await CreateLive(observingAnalyst:true);var request=LiveRequest();var result=await StartLive(s,request);
        var viewer=(await new AuthorityProvider(s.Repo,s.Config).PersonAsync(LiveTenant,LiveObserver.PersonId,default))!;
        var consumed=false;Task<JsonObject> Consume(JsonObject body,CancellationToken _){consumed=true;return Task.FromResult(body);}
        Assert.Equal("NOT_FOUND",(await Assert.ThrowsAsync<DomainException>(()=>s.Service.ConsumeAsync(viewer,request.RunId,Consume,default))).Code);
        Assert.Equal("NOT_FOUND",(await Assert.ThrowsAsync<DomainException>(()=>s.Service.ConsumeSourceAsync(viewer,request.RunId,KnowledgeService.Id("demo-business-session-1"),Consume,default))).Code);
        Assert.Equal("NOT_FOUND",(await Assert.ThrowsAsync<DomainException>(()=>s.Service.DecideAsync(viewer,request.RunId,new("on_hold"),Guid.NewGuid().ToString("N"),result.Text("etag"),default))).Code);
        Assert.False(consumed);Assert.Equal(1,s.Wire.Calls);
        Assert.Equal(result.Text("etag"),(await s.Service.GetAsync(LiveAnalyst,request.RunId,default)).Text("etag"));
    }

}
