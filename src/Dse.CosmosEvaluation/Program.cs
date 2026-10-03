using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using Dse.Domain;
using Dse.Evaluation;
using Dse.Infrastructure;
using Microsoft.Azure.Cosmos;

// Dedicated evaluation partition. No inference, secrets, production writes or role changes.
var mode = args.FirstOrDefault() ?? "--prepare";
if (mode is not ("--prepare" or "--probe" or "--run")) throw new ArgumentException("--prepare / --probe / --run");
var output = args.Length > 1 ? args[1] : ".artifacts/cosmos-evaluation";
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new ArgumentException("Use a new output directory; existing evidence cannot be overwritten.");
Directory.CreateDirectory(output);
const string endpoint = "https://dse-cosmos-ypbunss3xh66m.documents.azure.com:443/";
var tenant = "issue14-cosmos-v2-" + Guid.NewGuid().ToString("N");
const string data = "evaluation/expansion-v2";
const string capturePath = "docs/validation-results/20261003-issue-14-jev-v2/responses.json";
const string expectedPath = "docs/validation-results/20261003-issue-14-jev-v2/final-primary.json";
var fixture = Runner.Load<Fixture>($"{data}/dataset.json");
if (fixture.Origin != "synthetic" || fixture.HumanReviewStatus != "human-approved")
    throw new ArgumentException("Approved synthetic fixture required");
var applied = JevEvaluation.Apply(fixture, Runner.Load<JevPlan>($"{data}/jev-plan.json"),
    Runner.Load<JevCapture>(capturePath), Runner.Hash($"{data}/jev-plan.json"));
var setting = Runner.Load<ExperimentSetting[]>($"{data}/selected-settings.json").Single(s => s.Variant == "E");
var units = applied.Fixture.Units.Where(u => u.Split == "final" && u.SourceVersion == u.CurrentActiveVersion).Select(u => u.Search).ToArray();
var questions = fixture.Questions.Where(q => q.Split == "final").ToArray();
var expected = Runner.Load<PrimaryRow[]>(expectedPath).Where(r => r.Setting == setting).ToDictionary(r => r.QuestionId);
if (units.Length != 60 || questions.Length != 24 || expected.Count != 24) throw new ArgumentException("Frozen workload changed");
SearchRequest Request(Question q) {
    var query = applied.Inputs.Queries.Single(x => x.QuestionId == q.Id);
    var categories = query.Distribution.Where(p => p.CategoryId is not ("unknown" or "other") && p.Probability >= .1)
        .OrderByDescending(p => p.Probability).ThenBy(p => p.Axis, StringComparer.Ordinal).ThenBy(p => p.CategoryId, StringComparer.Ordinal)
        .Take(setting.QuestionTop).Select(p => new CategoryKey(p.Axis, p.CategoryId)).ToArray();
    return q.Request with { Categories = categories, Terms = query.Terms,
        SignalIds = query.Signals!.Where(s => s.EvidenceState == "supported" && s.YesProbability >= setting.NoulThreshold).Select(s => s.SignalId).ToArray(),
        ProbabilityThreshold = setting.DocumentThreshold, NoulThreshold = setting.NoulThreshold, Limit = setting.N };
}
foreach (var q in questions) {
    var candidates = Retriever.Search(units.Where(u => u.EntityType == q.EntityType), Request(q), new("demo-tenant", ["demo"])).Candidates;
    if (RerankComparisons.CandidateHash(candidates) != expected[q.Id].CandidateSha256) throw new ArgumentException("Local baseline mismatch");
}
void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, Runner.Json) + "\n");
Save("plan.json", new { createdAt = DateTimeOffset.UtcNow, mode, endpoint, database = "dse-growth-loop", container = "catalog", tenant,
    dummyOnly = true, aiRequests = 0, documents = units.Length, searches = questions.Length * 2, repeats = 2,
    assumedMonthlySearches = 100, setting, datasetSha256 = Runner.Hash($"{data}/dataset.json"), captureSha256 = Runner.Hash(capturePath),
    expectedSha256 = Runner.Hash(expectedPath), plannedRuStopThreshold = 5000,
    scope = "Projection SQL queries only; excludes source authorization reads, fences, audit writes, storage and full end-to-end latency",
    localCandidateHashesMatched = questions.Length });
if (mode == "--prepare") { Console.WriteLine("Prepared: 60 dummy projections, 48 queries, 24 local candidate hashes matched, zero AI calls."); return 0; }
using var client = new CosmosClient(endpoint, new AzureCliCredential(), new CosmosClientOptions {
    ConnectionMode = ConnectionMode.Gateway, MaxRetryAttemptsOnRateLimitedRequests = 0, RequestTimeout = TimeSpan.FromSeconds(30),
    UseSystemTextJsonSerializerWithOptions = new(JsonSerializerDefaults.Web)
});
var container = client.GetContainer("dse-growth-loop", "catalog");
var events = new List<object>(); double totalRu = 0;
void Record(string operation, string id, double ru, double ms, object? detail = null) {
    totalRu += ru;
    var entry = new { operation, id, requestCharge = ru, elapsedMs = ms, detail };
    events.Add(entry);
    File.AppendAllText(Path.Combine(output, "operations.jsonl"), JsonSerializer.Serialize(entry, new JsonSerializerOptions(Runner.Json) { WriteIndented = false }) + "\n");
    if (totalRu > 5000) throw new InvalidOperationException("RU stop threshold exceeded; no further operations");
}
try {
    var timer = Stopwatch.StartNew();
    using (var probe = await container.ReadItemStreamAsync("issue14-access-probe", new PartitionKey(tenant))) {
        Record("access_probe", "issue14-access-probe", probe.Headers.RequestCharge, timer.Elapsed.TotalMilliseconds,
            new { httpStatus = (int)probe.StatusCode });
        // 404 is expected and proves data-plane authorization without writing a record.
        if (probe.StatusCode != System.Net.HttpStatusCode.NotFound) probe.EnsureSuccessStatusCode();
    }
    if (mode == "--probe") { Save("status.json", new { status = "authorized", aiRequests = 0, totalRu }); return 0; }
    foreach (var unit in units) {
        timer.Restart();
        // Immutable create: never overwrite another run's records or an existing partition.
        var response = await container.CreateItemAsync(unit with { TenantId = tenant }, new PartitionKey(tenant));
        Record("create", unit.Id, response.RequestCharge, timer.Elapsed.TotalMilliseconds);
    }
    var rows = new List<object>();
    var queryCharges = new List<double>(); var queryTimes = new List<double>();
    for (var repeat = 1; repeat <= 2; repeat++) foreach (var q in questions) {
        var request = Request(q);
        var baseQuery = CosmosCatalog.BuildQuery(request, new(tenant, ["demo"]));
        var sql = new QueryDefinition(baseQuery.QueryText + " AND u.entityType = @entityType");
        foreach (var parameter in baseQuery.GetQueryParameters()) sql.WithParameter(parameter.Name, parameter.Value);
        sql.WithParameter("@entityType", q.EntityType);
        using var iterator = container.GetItemQueryIterator<SearchUnit>(sql, requestOptions: new QueryRequestOptions {
            PartitionKey = new PartitionKey(tenant), MaxItemCount = 100 });
        var fetched = new List<SearchUnit>(); double ru = 0; var pages = 0; timer.Restart();
        while (iterator.HasMoreResults) {
            var pageTimer = Stopwatch.StartNew(); var page = await iterator.ReadNextAsync();
            Record("query_page", q.Id, page.RequestCharge, pageTimer.Elapsed.TotalMilliseconds, new { repeat, count = page.Count });
            ru += page.RequestCharge; pages++; fetched.AddRange(page);
        }
        var ms = timer.Elapsed.TotalMilliseconds;
        // Restore original partition identity solely for hash comparison with the saved AI input.
        var candidates = Retriever.Search(fetched.Select(u => u with { TenantId = "demo-tenant" }), request, new("demo-tenant", ["demo"])).Candidates;
        var hash = RerankComparisons.CandidateHash(candidates);
        var matched = hash == expected[q.Id].CandidateSha256;
        rows.Add(new { questionId = q.Id, repeat, requestCharge = ru, elapsedMs = ms, pages, fetched = fetched.Count,
            candidates = candidates.Length, candidateSha256 = hash, matched });
        queryCharges.Add(ru); queryTimes.Add(ms);
        Save("queries.json", rows);
        if (!matched) throw new InvalidOperationException("Cosmos candidates differ from frozen baseline; stopped");
    }
    Save("summary.json", new { queryCount = rows.Count, meanQueryRu = queryCharges.Average(), queryRuTotal = queryCharges.Sum(),
        meanQueryMs = queryTimes.Average(), p95QueryMs = queryTimes.Order().ElementAt((int)Math.Ceiling(queryTimes.Count * .95) - 1),
        p95Method = "nearest-rank, all 48 SDK query durations; includes journal IO, excludes AI and authorization reads",
        assumedMonthlySearches = 100, estimatedMonthlyQueryRu = queryCharges.Average() * 100,
        measuredTotalRu = totalRu, aiRequests = 0 });
    Save("status.json", new { status = "completed", aiRequests = 0, totalRu, queries = rows.Count, candidateHashesMatched = rows.Count });
    Console.WriteLine($"Completed {rows.Count} queries. Total recorded RU: {totalRu:F3}; zero AI calls.");
    return 0;
} catch (CosmosException e) {
    Save("status.json", new { status = "failed", httpStatus = (int)e.StatusCode, subStatus = e.SubStatusCode, requestCharge = e.RequestCharge, totalRu, aiRequests = 0 });
    Console.WriteLine($"Cosmos HTTP {(int)e.StatusCode}, substatus {e.SubStatusCode}. See status.json; no exception body exported.");
    return 1;
} catch (Exception e) when (e is not OutOfMemoryException) {
    Save("status.json", new { status = "failed", errorType = e.GetType().Name, totalRu, aiRequests = 0 });
    Console.WriteLine($"Stopped: {e.GetType().Name}. See journal; no exception body exported."); return 1;
}
