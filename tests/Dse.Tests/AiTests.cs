using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public partial class AiTests
{
    private static string Root => FindRoot();
    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d!.FullName, "DseGrowthLoop.slnx"))) d = d.Parent;
        return d.FullName;
    }
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(Root, "tests/Dse.Tests/Fixtures/jev-" + name + ".json"));
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Live", ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "true",
        ["AiUsage:DailyTokenLimit"] = "100000", ["AiUsage:DailyCallLimit"] = "10", ["AiUsage:DailyEstimatedJpyLimit"] = "100",
        ["AiUsage:UsdToJpy"] = "150", ["AiUsage:PriceVersion"] = "test-price-v1", ["AiUsage:FxVersion"] = "test-fx-v1",
        ["Jev:Model"] = "jev-1.13.0", ["Jev:InputUsdPerMillion"] = "0.042", ["Jev:OutputUsdPerMillion"] = "0", ["Jev:OutputLimit"] = "1000",
        ["Foundry:Endpoint"] = "https://dummy.openai.azure.com/", ["Foundry:Deployment"] = "dummy-deployment",
        ["Foundry:ModelId"] = "dummy-model-2026-09-28", ["Foundry:ModelVersion"] = "2026-09-28", ["Foundry:Region"] = "test", ["Foundry:Sku"] = "GlobalStandard",
        ["Foundry:InputUsdPerMillion"] = "1", ["Foundry:OutputUsdPerMillion"] = "2", ["Foundry:OutputLimit"] = "1000"
    }).Build();
    private static AiContext Context() => new(IdentityResolver.Demo, "job-ai-test", "prompt-v1", "taxonomy-v1", 1, [], []);
    private static ChoiceQuestion[] Questions() => [new("department", "Select a department", new Dictionary<string, string> { ["billing"] = "Billing", ["technical"] = "Technical", ["sales"] = "Sales" })];
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
    private sealed class Credentials : IAiCredentials
    {
        public Task<string> BearerAsync(string provider, CancellationToken ct) => Task.FromResult("dummy-secret");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return send(request, ct); }
    }
    private static AiService Service(IConfiguration config, IRecordStore store, HttpMessageHandler handler)
    {
        AuthorityProvider.SeedDemoAsync(store,"demo",default).GetAwaiter().GetResult();
        var repo = new PrimaryRepository(store);
        return new(config, repo, new(repo), new(repo), new(store, TimeProvider.System), new AiHttpTransport(new HttpClient(handler), new Credentials(), TimeProvider.System, config), TimeProvider.System);
    }
    private static Task<AiResult<Decisions>> Decide(AiService service, AiContext? context = null, CancellationToken ct = default) =>
        service.DecideAsync(context ?? Context(), "Dummy billing request", Questions(), [], ct);

    public static IEnumerable<object?[]> AuthorityConfigurationCases() =>
        from mode in new string?[] { null, "Azure" }
        from mismatch in new[] { "role", "scope", "principal", "oid", "disabled" }
        from cached in new[] { false, true }
        select new object?[] { mode, mismatch, cached };

    [Theory]
    [MemberData(nameof(AuthorityConfigurationCases))]
    public async Task Current_configuration_mismatch_blocks_classification_before_send_cache_or_usage_writes(string? mode, string mismatch, bool cached)
    {
        const string tenant = "11111111-1111-4111-8111-111111111111", oid = "22222222-2222-4222-8222-222222222222";
        var actor = new ActorAccess(tenant, "principal-ai-authority", "person-ai-authority", ["member", "analyst"], ["demo"]);
        var store = new MemoryRecordStore();
        await AuthorityProvider.InitializeAsync(store, tenant, [new(tenant, oid, actor.PrincipalId, actor.PersonId, actor.Roles, true)], [new(actor.PrincipalId, actor.ScopeIds)], default);
        var config = Config(); config["Data:Mode"] = mode; config["Auth:TenantId"] = tenant;
        config["Auth:Bindings:0:TenantId"] = tenant; config["Auth:Bindings:0:Oid"] = oid;
        config["Auth:Bindings:0:PrincipalId"] = actor.PrincipalId; config["Auth:Bindings:0:PersonId"] = actor.PersonId;
        config["Auth:Bindings:0:Roles:0"] = "member"; config["Auth:Bindings:0:Roles:1"] = "analyst"; config["Auth:Bindings:0:Enabled"] = "true";
        config["Auth:Grants:0:PrincipalId"] = actor.PrincipalId; config["Auth:Grants:0:ScopeIds:0"] = "demo";
        var repo = new PrimaryRepository(store); actor = await new AuthorityProvider(repo, config).RequireAsync(actor, default);
        var handler = new Handler((_, _) => Task.FromResult(Response(Fixture("choice"))));
        using var service = new AiService(config, repo, new(repo, config), new(repo), new(store, TimeProvider.System),
            new AiHttpTransport(new HttpClient(handler), new Credentials(), TimeProvider.System, config), TimeProvider.System);
        var context = Context() with { Actor = actor, PromptVersion = "search-query-v2" };
        if (cached) Assert.Equal(AiStatus.Succeeded, (await Decide(service, context)).Status);
        var partition = RecordPartition.Catalog(tenant);
        async Task<string> Snapshot()
        {
            var records = new List<object>();
            foreach (var kind in new[] { "aiRun", "aiReservation", "authorityControl", "personAuthority" })
            {
                var page = await store.QueryAsync(partition, kind, null, null, 100, null, default);
                Assert.Null(page.ContinuationToken);
                records.AddRange(page.Items.Select(row => new { row.ETag, Value = row.Value.ToJsonString() }));
            }
            foreach (var id in new[] { "ai-day-" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), "system-ai-gate" })
            {
                var row = await store.ReadAsync(partition, id, default);
                records.Add(new { Id = id, row?.ETag, Value = row?.Value.ToJsonString() });
            }
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records)));
        }
        var before = await Snapshot();
        if (mismatch == "role") config["Auth:Bindings:0:Roles:1"] = "publisher";
        if (mismatch == "scope") config["Auth:Grants:0:ScopeIds:0"] = "other";
        if (mismatch == "principal") { config["Auth:Bindings:0:PrincipalId"] = "replacement"; config["Auth:Grants:0:PrincipalId"] = "replacement"; }
        if (mismatch == "oid") config["Auth:Bindings:0:Oid"] = "33333333-3333-4333-8333-333333333333";
        if (mismatch == "disabled") config["Auth:Bindings:0:Enabled"] = "false";
        var error = await Assert.ThrowsAsync<DomainException>(() => Decide(service, context));
        Assert.Equal("AUTHORITY_REVOKED", error.Code);
        Assert.Equal(cached ? 1 : 0, handler.Calls);
        Assert.Equal(before, await Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_rerank_bounds_the_actual_HTTP_body_and_rejects_unsent_quotations(bool unsent)
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var unit = (await new MemoryProjectionRepository(store).FindAsync(new([], [], ["dummy"]), IdentityResolver.Demo.Search, default))[0];
        var evidenceId = unit.EvidenceRefs![0].Id;
        var partition = RecordPartition.Catalog("demo");
        var span = (await store.ReadAsync(partition, evidenceId, default))!;
        var original = string.Concat(Enumerable.Repeat("副業務の運用改善を共同実施した。😀", 100)) + "末尾の未送信情報";
        span.Value["text"] = original;
        await store.CommitAsync(partition, [RecordWrite.Replace(span.Value, span.ETag)], default);
        var handler = new Handler(async (request, ct) =>
        {
            var wire = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.True(wire.Length + 256 <= 3000);
            var body = JsonNode.Parse(wire)!;
            var input = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            var candidate = input["candidates"]![0]!;
            Assert.Equal(unit.EntityType, candidate["entityType"]!.GetValue<string>());
            var sent = candidate["evidence"]![0]!["text"]!.GetValue<string>();
            Assert.StartsWith(sent, original); Assert.DoesNotContain("末尾の未送信情報", sent);
            Assert.Null(candidate["evidence"]![0]!["documentTitle"]);
            return Response(Foundry(JsonSerializer.Serialize(new { items = new[] { new {
                candidateId = unit.Id, reason = unsent ? "末尾の未送信情報" : sent, evidenceIds = new[] { evidenceId }
            } } })));
        });
        var config = Config(); config["AiUsage:MaxInputTokens"] = "3000";
        using var service = Service(config, store, handler);
        var result = await service.RerankAsync(Context() with { PromptVersion = "search-rerank-v2", Candidates = [unit], EvidenceBytesPerCandidate = 1200 }, "運用改善", default);
        Assert.Equal(unsent ? AiStatus.Failed : AiStatus.Succeeded, result.Status);
        Assert.Equal(1, handler.Calls);
        var run = (await store.ReadAsync(partition, result.RunId, default))!.Value;
        Assert.Equal("search-total-bytes-v1", run["trace"]!["inputBudgetVersion"]!.GetValue<string>());
        var cut = run["trace"]!["evidenceCuts"]![0]!;
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(original), cut["originalBytes"]!.GetValue<int>());
        Assert.True(cut["sentBytes"]!.GetValue<int>() < 1200);
        if (!unsent)
        {
            Assert.True((await service.RerankAsync(Context() with { PromptVersion = "search-rerank-v2", Candidates = [unit], EvidenceBytesPerCandidate = 1200 }, "運用改善", default)).Cached);
            Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public async Task Official_choice_keeps_distribution_confidence_usage_and_versioned_cache()
    {
        var store = new MemoryRecordStore(); var config = Config();
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("https://api.typesafe.ai/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("choice", body["questions"]!["department"]!["type"]!.GetValue<string>());
            Assert.NotNull(body["questions"]!["department"]!["criteria"]!["sales"]);
            var response = Response(Fixture("choice")); response.Headers.Add("x-request-id", "dummy-request-id"); return response;
        });
        using var service = Service(config, store, handler);
        var first = await Decide(service);
        Assert.Equal(AiStatus.Succeeded, first.Status);
        Assert.Equal(3, first.Value!.Choices["department"].Probabilities.Count);
        Assert.Equal(0, first.Value.Choices["department"].Probabilities["sales"]);
        Assert.Equal(.81, first.Value.Choices["department"].Confidence);
        var run = (await store.ReadAsync(RecordPartition.Catalog("demo"), first.RunId, default))!.Value;
        Assert.Equal(318, run["trace"]!["inputTokens"]!.GetValue<long>());
        Assert.Equal("dummy-request-id", run["trace"]!["providerRequestIds"]![0]!.GetValue<string>());
        Assert.DoesNotContain("Dummy billing request", run.ToJsonString());
        Assert.DoesNotContain("dummy-secret", run.ToJsonString());
        Assert.True((await Decide(service)).Cached);
        Assert.False((await Decide(service, Context() with { PromptVersion = "prompt-v2" })).Cached);
        Assert.Equal(2, handler.Calls);
        var output = Path.Combine(Root, "tests/Dse.Tests/TestResults"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "ai-run.json"), run.ToJsonString());
    }

    [Fact]
    public async Task Noul_zero_is_supported_and_missing_evidence_is_unknown_without_call()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Fixture("noul").Replace("0.95", "0.0"))));
        using var service = Service(Config(), new MemoryRecordStore(), handler);
        var result = await service.DecideAsync(Context(), "dummy", [], [new("is_urgent", "Urgent?", true), new("missing", "Unknown?", false)], default);
        Assert.Equal(new NoulAnswer(NoulState.Supported, 0), result.Value!.Nouls["is_urgent"]);
        Assert.Equal(new NoulAnswer(NoulState.Unknown, null), result.Value.Nouls["missing"]);
        var unknown = await service.DecideAsync(Context(), "dummy", [], [new("missing", "Unknown?", false)], default);
        Assert.Equal(AiStatus.NotEvaluated, unknown.Status); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("missing", AiFailure.SchemaMismatch)]
    [InlineData("range", AiFailure.SchemaMismatch)]
    [InlineData("sum", AiFailure.SchemaMismatch)]
    [InlineData("choice", AiFailure.SchemaMismatch)]
    [InlineData("model", AiFailure.ModelMismatch)]
    [InlineData("json", AiFailure.InvalidJson)]
    [InlineData("duplicate", AiFailure.InvalidJson)]
    [InlineData("usage", AiFailure.SchemaMismatch)]
    public async Task Invalid_Jev_response_never_succeeds(string change, AiFailure expected)
    {
        var json = JsonNode.Parse(Fixture("choice"))!;
        var answer = json["answers"]!["department"]!;
        switch (change)
        {
            case "missing": answer["probabilities"]!.AsObject().Remove("sales"); break;
            case "range": answer["confidence"] = 1.1; break;
            case "sum": answer["probabilities"]!["sales"] = .5; break;
            case "choice": answer["choice"] = "sales"; break;
            case "model": json["model"] = "jev-latest"; break;
            case "usage": json["usage"]!["input_tokens"] = -1; break;
        }
        var payload = change switch { "json" => "{broken", "duplicate" => json.ToJsonString().Replace("{\"model\":", "{\"model\":\"unexpected\",\"model\":"), _ => json.ToJsonString() };
        var handler = new Handler((_, _) => Task.FromResult(Response(payload)));
        using var service = Service(Config(), new MemoryRecordStore(), handler);
        var result = await Decide(service);
        Assert.Equal(AiStatus.Failed, result.Status); Assert.Equal(expected, result.Failure); Assert.Null(result.Value); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("missing", "Keys")]
    [InlineData("range", "Probability")]
    [InlineData("sum", "DistributionSum")]
    [InlineData("choice", "SelectedMaximum")]
    public async Task Classification_schema_failure_records_only_a_bounded_validation_tag(string change, string tag)
    {
        var payload = JsonNode.Parse(Fixture("choice"))!;
        var answer = payload["answers"]!["department"]!;
        switch (change)
        {
            case "missing": answer["probabilities"]!.AsObject().Remove("sales"); break;
            case "range": answer["confidence"] = 1.1; break;
            case "sum": answer["probabilities"]!["sales"] = .5; break;
            case "choice": answer["choice"] = "sales"; break;
        }
        var store = new MemoryRecordStore();
        var handler = new Handler((_, _) => Task.FromResult(Response(payload.ToJsonString())));
        using var service = Service(Config(), store, handler);
        var result = await Decide(service);
        Assert.Equal(AiFailure.SchemaMismatch, result.Failure); Assert.Null(result.Value);
        var audit = (await store.ReadAsync(RecordPartition.Catalog("demo"), result.RunId, default))!.Value;
        Assert.Equal(tag, audit["trace"]!["validationIssue"]!.GetValue<string>());
        Assert.DoesNotContain("Dummy billing request", audit.ToJsonString());
        Assert.DoesNotContain("dummy-secret", audit.ToJsonString());
        Assert.Null(audit["answers"]);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, 1, AiFailure.Unauthorized)]
    [InlineData(403, 1, AiFailure.Unauthorized)]
    [InlineData(429, 2, AiFailure.RateLimited)]
    [InlineData(500, 1, AiFailure.Unavailable)]
    [InlineData(529, 1, AiFailure.Unavailable)]
    public async Task Http_errors_are_bounded_and_each_retry_reserves_spend(int status, int expectedCalls, AiFailure failure)
    {
        var store = new MemoryRecordStore();
        var handler = new Handler((_, _) =>
        {
            var response = Response("SECRET BODY", (HttpStatusCode)status);
            response.Headers.RetryAfter = new(TimeSpan.Zero); return Task.FromResult(response);
        });
        using var service = Service(Config(), store, handler);
        var result = await Decide(service);
        Assert.Equal(failure, result.Failure); Assert.Equal(expectedCalls, handler.Calls);
        var run = (await store.ReadAsync(RecordPartition.Catalog("demo"), result.RunId, default))!.Value;
        Assert.Equal(expectedCalls, run["trace"]!["attemptCount"]!.GetValue<int>());
        Assert.True(run["trace"]!["usageEstimated"]!.GetValue<bool>());
        Assert.DoesNotContain("SECRET BODY", run.ToJsonString());
    }

    [Fact]
    public async Task Retry_after_larger_than_deadline_does_not_send_early()
    {
        var handler = new Handler((_, _) =>
        {
            var response = Response("", HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromMinutes(10)); return Task.FromResult(response);
        });
        using var service = Service(Config(), new MemoryRecordStore(), handler);
        Assert.Equal(AiFailure.RateLimited, (await Decide(service)).Failure); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("disconnect", AiFailure.Unavailable)]
    [InlineData("timeout", AiFailure.Timeout)]
    [InlineData("stream-disconnect", AiFailure.Unavailable)]
    public async Task Disconnect_and_timeout_keep_uncertain_charge(string mode, AiFailure expected)
    {
        var config = Config(); config["AiUsage:TimeoutSeconds"] = "1";
        var store = new MemoryRecordStore();
        var handler = new Handler(async (_, ct) =>
        {
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, ct);
            if (mode == "stream-disconnect") throw new IOException("Secret stream exception text");
            throw new HttpRequestException("Secret exception text");
        });
        using var service = Service(config, store, handler);
        Assert.Equal(expected, (await Decide(service)).Failure);
        var day = (await store.ReadAsync(RecordPartition.Catalog("demo"), "ai-day-" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), default))!;
        Assert.True(day.Value["tokens"]!.GetValue<long>() >= 9000);
    }

    private static string Foundry(string content, string finish = "stop", bool refusal = false) => JsonSerializer.Serialize(new
    {
        model = "dummy-model-2026-09-28", choices = new[] { new { finish_reason = finish, message = new { content, refusal = refusal ? "no" : null } } },
        usage = new { prompt_tokens = 20, completion_tokens = 30 }
    });
    [Theory]
    [InlineData("refusal", AiFailure.Refused)]
    [InlineData("length", AiFailure.Truncated)]
    [InlineData("content_filter", AiFailure.Refused)]
    [InlineData("id", AiFailure.InventedId)]
    [InlineData("schema", AiFailure.SchemaMismatch)]
    [InlineData("json", AiFailure.InvalidJson)]
    public async Task Foundry_rejects_refusal_truncation_invalid_JSON_schema_and_invented_evidence(string mode, AiFailure failure)
    {
        var content = mode switch { "schema" => "{}", "json" => "broken", "id" => "{\"text\":\"dummy\",\"evidenceIds\":[\"invented\"]}", _ => "{\"text\":\"dummy\",\"evidenceIds\":[]}" };
        var handler = new Handler((request, _) =>
        {
            Assert.EndsWith("/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response(Foundry(content, mode is "length" or "content_filter" ? mode : "stop", mode == "refusal")));
        });
        using var service = Service(Config(), new MemoryRecordStore(), handler);
        var result = await service.SummarizeAsync(Context(), "dummy", default);
        Assert.Equal(AiStatus.Failed, result.Status); Assert.Equal(failure, result.Failure); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cache_hit_rechecks_authoritative_source_and_revocation_blocks_send()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var units = await new MemoryProjectionRepository(store).FindAsync(new([], [], ["dummy"]), IdentityResolver.Demo.Search, default);
        var unit = units.Single(x => x.EntityId == "demo-person-01");
        var config = Config(); config["Ai:Mode"] = "Mock";
        var handler = new Handler((_, _) => throw new Xunit.Sdk.XunitException("External call"));
        using var service = Service(config, store, handler);
        var context = Context() with { Candidates = [unit] };
        Assert.Equal(AiStatus.Succeeded, (await service.RerankAsync(context, "dummy", default)).Status);
        Assert.True((await service.RerankAsync(context, "dummy", default)).Cached);
        var partition = RecordPartition.Catalog("demo"); var source = (await store.ReadAsync(partition, unit.EntityId, default))!;
        source.Value["available"] = false; await store.CommitAsync(partition, [RecordWrite.Replace(source.Value, source.ETag)], default);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.RerankAsync(context, "dummy", default));
        Assert.Equal("SOURCE_REVOKED", error.Code); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("AiUsage:Stopped", "true", AiFailure.Stopped)]
    [InlineData("AiUsage:LiveOptIn", "false", AiFailure.Stopped)]
    [InlineData("AiUsage:DailyCallLimit", "0", AiFailure.Budget)]
    [InlineData("AiUsage:MaxInputTokens", "1", AiFailure.InputLimit)]
    [InlineData("AiUsage:MaxOutputTokens", "1", AiFailure.OutputLimit)]
    public async Task Guards_block_HTTP(string key, string value, AiFailure failure)
    {
        var config = Config(); config[key] = value;
        var handler = new Handler((_, _) => throw new Xunit.Sdk.XunitException("External call"));
        using var service = Service(config, new MemoryRecordStore(), handler);
        Assert.Equal(failure, (await Decide(service)).Failure); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Missing_usage_charges_full_reservation_and_restart_cannot_bypass_budget()
    {
        var config = Config(); config["AiUsage:DailyTokenLimit"] = "9000";
        var handler = new Handler((_, _) =>
        {
            var body = JsonNode.Parse(Fixture("choice"))!.AsObject(); body.Remove("usage"); return Task.FromResult(Response(body.ToJsonString()));
        });
        var store = new MemoryRecordStore();
        using (var service = Service(config, store, handler)) Assert.Equal(AiStatus.Succeeded, (await Decide(service)).Status);
        using (var service = Service(config, store, handler)) Assert.Equal(AiFailure.Budget, (await Decide(service)).Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Durable_ledger_competition_crash_recovery_and_UTC_rollover()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-ledger-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock(); var config = Config(); var limits = AiLimits.Read(config); var model = AiModel.Read(config, "Jev", limits.OutputTokens);
        try
        {
            AiLedger Ledger() => new(new LocalRecordStore(new LocalIngestionFiles(root)), clock);
            var tasks = Enumerable.Range(0, 12).Select(async i =>
            {
                try { return await Ledger().ReserveAsync("demo", "run-" + i, model, limits, 8000, 1000, default); }
                catch (AiException e) { Assert.Equal(AiFailure.Busy, e.Failure); return null; }
            });
            var reserved = Assert.Single(await Task.WhenAll(tasks), x => x is not null)!;
            var restarted = Ledger();
            await Assert.ThrowsAsync<AiException>(() => restarted.ReserveAsync("demo", "run-new", model, limits, 8000, 1000, default));
            await Assert.ThrowsAsync<ArgumentException>(() => restarted.RecoverSlotAsync("demo", reserved.Id, false, default));
            await Assert.ThrowsAsync<ArgumentException>(()=>restarted.RecoverSlotAsync("demo",reserved.Id,true,default));
            await restarted.RecoverSlotAsync("demo", reserved.Id, true, default,new(true,true,true,"reviewed-approval","provider-stop-proof",new string('a',64)));
            var strict = limits with { DailyTokens = 9000 };
            Assert.Equal(AiFailure.Budget, (await Assert.ThrowsAsync<AiException>(() => restarted.ReserveAsync("demo", "run-next", model, strict, 8000, 1000, default))).Failure);
            clock.Now = clock.Now.AddDays(1);
            var next = await restarted.ReserveAsync("demo", "run-next", model, strict, 8000, 1000, default);
            await restarted.SettleAsync(next, new(318, 34, false), model, strict, default);
            await restarted.SettleAsync(next, new(318, 34, false), model, strict, default);
            var day = (await new LocalRecordStore(new LocalIngestionFiles(root)).ReadAsync(RecordPartition.Catalog("demo"), next.DayId, default))!;
            Assert.Equal(352, day.Value["tokens"]!.GetValue<long>()); Assert.Equal(1, day.Value["calls"]!.GetValue<long>());
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("tokens")]
    [InlineData("money")]
    public async Task Parallel_reservations_cannot_exceed_any_daily_ceiling(string ceiling)
    {
        var store = new MemoryRecordStore(); var config = Config(); var model = AiModel.Read(config, "Jev", 1000);
        var limits = AiLimits.Read(config) with { Concurrency = 8 };
        limits = ceiling switch { "calls" => limits with { DailyCalls = 1 }, "tokens" => limits with { DailyTokens = 9000 }, _ => limits with { DailyJpy = model.Cost(8000, 1000, limits) } };
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await new AiLedger(store, TimeProvider.System).ReserveAsync("demo", "run-" + i, model, limits, 8000, 1000, default); return true; }
            catch (AiException e) { Assert.Equal(AiFailure.Budget, e.Failure); return false; }
        }));
        Assert.Single(outcomes, x => x);
    }

    [Fact]
    public async Task Provider_overrun_blocks_following_calls_even_next_day()
    {
        var clock = new Clock(); var store = new MemoryRecordStore(); var config = Config(); var limits = AiLimits.Read(config);
        var model = AiModel.Read(config, "Jev", 1000); var ledger = new AiLedger(store, clock);
        var reservation = await ledger.ReserveAsync("demo", "run-test", model, limits, 8000, 1000, default);
        await ledger.SettleAsync(reservation, new(8001, 1000, false), model, limits, default);
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(AiFailure.Budget, (await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "run-next", model, limits, 8000, 1000, default))).Failure);
    }

    [Fact]
    public async Task Ledger_cancellation_retains_shared_slot_until_provider_quiescence_before_disabled_admission()
    {
        using var cancel=new CancellationTokenSource();
        var pending=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=new Handler((_,_)=>{cancel.Cancel();return pending.Task;});
        var store=new MemoryRecordStore();using var service=Service(Config(),store,handler);
        try
        {
            var result=await Decide(service,ct:cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(AiFailure.Cancelled,result.Failure);Assert.Equal(1,handler.Calls);
            var gate=(await store.ReadAsync(RecordPartition.Catalog("demo"),"system-ai-gate",default))!;var id=Assert.Single(gate.Value.Strings("active"));
            var ledger=new AiLedger(store,TimeProvider.System);var disabled=AiLimits.Read(AccountingOff());var model=AiModel.Read(AccountingOff(),"Jev",1000);
            Assert.Equal(AiFailure.Busy,(await Assert.ThrowsAsync<AiException>(()=>ledger.ReserveAsync("demo","new-mode-call",model,disabled,100,100,default))).Failure);
            await Assert.ThrowsAsync<ArgumentException>(()=>ledger.RecoverSlotAsync("demo",id,true,default));
            pending.TrySetResult(Response(Fixture("choice"))); // Provider completion is independently confirmed before recovery.
            await ledger.RecoverSlotAsync("demo",id,true,default,new(true,true,true,"offline-reviewed","provider-stop-proof",new string('a',64)));
            var next=await ledger.ReserveAsync("demo","new-mode-call",model,disabled,100,100,default);Assert.True(next.AccountingDisabled);
            await ledger.SettleAsync(next,new(90,90,false),model,disabled,default);Assert.Equal(1,handler.Calls);
        }
        finally {pending.TrySetResult(Response(Fixture("choice")));}
    }

    [Theory]
    [InlineData("Jev:Endpoint", "https://intermediary.invalid/v1/systemone")]
    [InlineData("Jev:Model", "jev-latest")]
    [InlineData("Foundry:ModelVersion", "latest")]
    [InlineData("Foundry:Sku", "ProvisionedManaged")]
    [InlineData("Foundry:Endpoint", "http://dummy.openai.azure.com/")]
    public void Model_contract_rejects_alias_unapproved_endpoint_and_non_metered_SKU(string key, string value)
    {
        var config = Config(); config[key] = value;
        Assert.Throws<InvalidOperationException>(() => AiModel.Read(config, key.StartsWith("Jev") ? "Jev" : "Foundry", 1000));
    }

    [Fact]
    public void Smoke_requires_all_explicit_gates()
    {
        var config = Config(); Assert.Throws<InvalidOperationException>(() => AiSmoke.Validate(config));
        config["Data:Mode"] = "Azure"; config["AiSmoke:Approved"] = "true"; config["AiSmoke:DummyOnly"] = "true";
        config["AiSmoke:OperatorPersonId"] = "demo-person-01"; config["AiSmoke:ApprovalReference"] = "issue-9-approval";
        config["AiSmoke:Count"] = "2"; config["AiSmoke:MaxInputTokens"] = "8000"; config["AiSmoke:MaxOutputTokens"] = "1000";
        config["AiUsage:MaxAttempts"] = "1"; config["APPROVED_MONTHLY_BUDGET_JPY"] = "100";
        AiSmoke.Validate(config);
        foreach (var key in new[] { "AiSmoke:Approved", "AiSmoke:DummyOnly", "AiSmoke:OperatorPersonId", "AiSmoke:ApprovalReference", "AiSmoke:Count", "AiSmoke:MaxInputTokens", "AiSmoke:MaxOutputTokens", "APPROVED_MONTHLY_BUDGET_JPY" })
        {
            var value = config[key]; config[key] = ""; Assert.Throws<InvalidOperationException>(() => AiSmoke.Validate(config)); config[key] = value;
        }
    }

    private sealed class Token : Azure.Core.TokenCredential
    {
        public string[] Scopes = [];
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext context, CancellationToken ct) => throw new NotSupportedException();
        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext context, CancellationToken ct)
        { Scopes = context.Scopes; return ValueTask.FromResult(new Azure.Core.AccessToken("dummy-mi-token", DateTimeOffset.UtcNow.AddMinutes(10))); }
    }
    [Fact]
    public async Task Jev_secret_comes_from_pinned_Key_Vault_version_and_Foundry_uses_Entra()
    {
        var config = Config(); config["KeyVault:Uri"] = "https://dummy.vault.azure.net/";
        config["Jev:SecretVersion"] = new string('a', 32); var token = new Token();
        var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://dummy.vault.azure.net/secrets/jev-api-key/" + new string('a', 32) + "?api-version=2025-07-01", request.RequestUri!.AbsoluteUri);
            Assert.Equal("dummy-mi-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Response("{\"value\":\"dummy-vault-secret\"}"));
        });
        using var http = new HttpClient(handler); var credentials = new AzureAiCredentials(http, token, config);
        Assert.Equal("dummy-vault-secret", await credentials.BearerAsync("Jev", default));
        Assert.Equal("https://vault.azure.net/.default", Assert.Single(token.Scopes));
        Assert.Equal("dummy-mi-token", await credentials.BearerAsync("Foundry", default));
        Assert.Equal("https://ai.azure.com/.default", Assert.Single(token.Scopes)); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Wrong_tenant_or_session_participant_is_rejected_before_HTTP()
    {
        var store = new MemoryRecordStore(); var context = Context();
        var session = RecordJson.New("session", "session-test", "demo", "other", ["demo"], DateTimeOffset.UtcNow);
        session["sessionId"] = "session-test"; session["participantIds"] = RecordJson.Array(["other"]);
        session["status"] = "open"; session["aiAssistance"] = "enabled";
        await store.CommitAsync(RecordPartition.Conversation("demo", "session-test"), [RecordWrite.Create(session)], default);
        var handler = new Handler((_, _) => throw new Xunit.Sdk.XunitException("External call"));
        using var service = Service(Config(), store, handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Decide(service, context with { Actor = context.Actor with { TenantId = "other" } }));
        await Assert.ThrowsAsync<DomainException>(() => Decide(service, context with { SessionId = "session-test" }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Oversized_stream_response_fails_without_body_logging()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(new string('x', 100000))));
        using var service = Service(Config(), new MemoryRecordStore(), handler);
        Assert.Equal(AiFailure.OutputLimit, (await Decide(service)).Failure);
    }

    [Fact]
    public async Task Foundry_success_uses_strict_schema_output_cap_and_grounded_candidate_ids()
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var unit = (await new MemoryProjectionRepository(store).FindAsync(new([], [], ["dummy"]), IdentityResolver.Demo.Search, default))[0];
        var evidenceId = unit.EvidenceRefs![0].Id;
        var handler = new Handler(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("dummy-deployment", body["model"]!.GetValue<string>());
            Assert.Equal(1000, body["max_completion_tokens"]!.GetValue<int>());
            Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
            var operation = body["response_format"]!["json_schema"]!["name"]!.GetValue<string>();
            var item = new JsonObject { ["text"] = "Dummy", ["evidenceIds"] = RecordJson.Array([evidenceId]) };
            if (operation == "rerank") { item.Remove("text"); item["candidateId"] = unit.Id; item["reason"] = "Dummy reason"; }
            var content = operation == "summarize" ? item : new JsonObject { ["items"] = new JsonArray(item) };
            return Response(Foundry(content.ToJsonString()));
        });
        using var service = Service(Config(), store, handler);
        var context = Context() with { Candidates = [unit], Evidence = unit.EvidenceRefs };
        Assert.Equal(AiStatus.Succeeded, (await service.StructureAsync(context, "Dummy", default)).Status);
        Assert.Equal(AiStatus.Succeeded, (await service.SummarizeAsync(context, "Dummy", default)).Status);
        var ranking = await service.RerankAsync(context, "Dummy", default);
        Assert.Equal(unit.Id, Assert.Single(ranking.Value!.Items).CandidateId);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("evidence")]
    public async Task Reranker_rejects_invented_candidate_or_cross_candidate_evidence(string field)
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, "demo", default);
        var units = await new MemoryProjectionRepository(store).FindAsync(new([], [], ["dummy"]), IdentityResolver.Demo.Search, default);
        var unit = units.Single(x => x.EntityId == "demo-person-01");
        var other = units.Single(x => x.EntityId == "demo-person-02");
        var content = JsonSerializer.Serialize(new { items = new[] { new {
            candidateId = field == "candidate" ? "invented" : unit.Id, reason = "Dummy reason",
            evidenceIds = new[] { field == "evidence" ? other.EvidenceRefs![0].Id : unit.EvidenceRefs![0].Id }
        } } });
        var handler = new Handler((_, _) => Task.FromResult(Response(Foundry(content))));
        using var service = Service(Config(), store, handler);
        var result = await service.RerankAsync(Context() with { Candidates = [unit, other] }, "Dummy", default);
        Assert.Equal(AiFailure.InventedId, result.Failure); Assert.Null(result.Value); Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("invented")]
    [InlineData("missing-evidence")]
    [InlineData("duplicate-field")]
    public async Task Knowledge_draft_uses_authoritative_conversation_evidence_and_rejects_bad_sections(string mode)
    {
        var store = new MemoryRecordStore(); await AuthorityProvider.SeedDemoAsync(store,"demo",default); var config = Config(); var repo = new PrimaryRepository(store);
        var chat = new ConversationService(repo, new(), new(config), TimeProvider.System);
        var challenge = await chat.CreateAsync(IdentityResolver.Demo, "knowledge-draft-0001", new("架空", "ダミー"), default);
        var sid = challenge.Text("sessionId"); var session = await chat.SessionAsync(IdentityResolver.Demo, sid, default);
        var message = await chat.SendAsync(IdentityResolver.Demo, sid, "knowledge-message-01", session.ETag, new("message-test", "前提は不明"), default);
        session = await chat.SessionAsync(IdentityResolver.Demo, sid, default);
        var handler = new Handler(async (request, ct) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Assert.Equal("knowledgeDraft", body["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
            Assert.Contains("reuseConditions", body["messages"]![0]!["content"]!.GetValue<string>());
            var input = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal("前提は不明", input["evidence"]![0]!["text"]!.GetValue<string>());
            var content = new JsonObject { ["sections"] = new JsonArray(KnowledgeSummary.Fields.Select((f, i) => (JsonNode)new JsonObject
            { ["field"] = mode == "duplicate-field" && i == 5 ? "challenge" : f, ["text"] = "前提は不明", ["basis"] = "participant_report",
              ["evidenceIds"] = RecordJson.Array(mode == "missing-evidence" ? [] : [mode == "invented" ? "invented-id" : message.Text("id")]) }).ToArray()) };
            return Response(Foundry(content.ToJsonString()));
        });
        using var service = Service(config, store, handler);
        var context = new AiContext(IdentityResolver.Demo, "draft-job-0001", "knowledge-draft-v1", Retriever.TaxonomyVersion, session.Value.Number("sourceVersion"), [], [], sid) { ConversationEvidenceIds = [message.Text("id")] };
        var result = await service.DraftAsync(context, default);
        Assert.Equal(mode == "valid" ? AiStatus.Succeeded : AiStatus.Failed, result.Status);
        if (mode == "valid") Assert.Equal(6, result.Value!.Sections.Length);
        else { Assert.Null(result.Value); Assert.Equal(mode == "duplicate-field" ? AiFailure.SchemaMismatch : AiFailure.InventedId, result.Failure); }
        Assert.Equal(1, handler.Calls);
        var run = (await repo.ReadAsync(RecordPartition.Conversation("demo", sid), result.RunId, default))!.Value;
        Assert.Equal("knowledge-draft-v1", run["trace"]!["promptVersion"]!.GetValue<string>());
        Assert.DoesNotContain("前提は不明", run.ToJsonString());
        await Assert.ThrowsAsync<DomainException>(() => service.DraftAsync(context with { Actor = IdentityResolver.DemoPerson("demo-person-02")! }, default));
        await Assert.ThrowsAsync<DomainException>(() => service.DraftAsync(context with { ConversationEvidenceIds = ["forged-message-id"] }, default));
        Assert.Equal(1, handler.Calls);
    }
}
