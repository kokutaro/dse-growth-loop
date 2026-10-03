using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Storage.Blobs;
using Dse.Domain;
using Dse.Evaluation;
using Dse.Infrastructure;

var builder = Host.CreateApplicationBuilder([]);
var config = builder.Configuration;
var mode = args.FirstOrDefault() ?? "--prepare";
if (mode is not ("--prepare" or "--run" or "--resume" or "--search" or "--diagnose" or "--export" or "--export-audits" or "--regression" or "--regression-export")) throw new ArgumentException("Invalid mode");
var fixture = Runner.Load<Fixture>("data/dataset.json");
var approval = JsonNode.Parse(File.ReadAllText("data/approval.json"))!.AsObject();
if (fixture.Origin != "synthetic" || fixture.HumanReviewStatus != "human-approved" || approval.Text("datasetSha256") != Runner.Hash("data/dataset.json") ||
    fixture.Questions.Length != 24 || fixture.Units.Length != 60) throw new ArgumentException("Approved independent fixture required");
var run = config["Validation:RunId"] ?? "issue30-independent-20261003-v1";
var scope = run;
var evaluationId = config["Validation:EvaluationId"] ?? run;
var regression = mode is "--regression" or "--regression-export";
if (regression && (evaluationId == run || !RecordJson.IsId(evaluationId) || !evaluationId.StartsWith("issue30-hardening-")))
    throw new ArgumentException("Separate regression evaluation ID required");
if (!RecordJson.IsId(run) || !run.StartsWith("issue30-independent-")) throw new ArgumentException("Dedicated run ID required");
var tenant = config["Auth:TenantId"] ?? throw new ArgumentException("Tenant required");
var owner = "principal-" + run;
var person = "operator-" + run;
var actor = new ActorAccess(tenant, owner, person, ["member", "publisher", "operator"], [scope]);
// Synthetic identities exist only in this dedicated validation process, not public API auth settings.
var settings = new Dictionary<string, string?> { ["Retrieval:CaseLimit"] = "15", ["Retrieval:PersonLimit"] = "10", ["Profile:SecondaryWorkFragments"] = "true" };
var index = config.GetSection("Auth:Bindings").GetChildren().Count();
foreach (var unit in fixture.Units.Where(u => u.Search.EntityType == "person"))
{
    var id = run + "-" + unit.Search.EntityId;
    var principal = "principal-" + unit.Search.EntityId;
    settings[$"Auth:Bindings:{index}:TenantId"] = tenant; settings[$"Auth:Bindings:{index}:Oid"] = Guid.NewGuid().ToString();
    settings[$"Auth:Bindings:{index}:PersonId"] = id; settings[$"Auth:Bindings:{index}:PrincipalId"] = principal;
    settings[$"Auth:Bindings:{index}:Enabled"] = "true"; settings[$"Auth:Bindings:{index}:Roles:0"] = "member";
    index++;
    var grant = config.GetSection("Auth:Grants").GetChildren().Count() + Array.IndexOf(fixture.Units.Where(u => u.Search.EntityType == "person").ToArray(), unit);
    settings[$"Auth:Grants:{grant}:PrincipalId"] = principal; settings[$"Auth:Grants:{grant}:ScopeIds:0"] = scope;
}
config.AddInMemoryCollection(settings);
var safePlan = new { RunId = run, Mode = mode, DatasetSha256 = Runner.Hash("data/dataset.json"), DummyOnly = true, Scope = scope,
    EvaluationId = evaluationId, ObservedRegressionSet = regression, IndependentHoldout = !regression, Arms = regression ? new[] { "all", "targeted" } : new[] { "all" },
    Questions = 24, Documents = 60, Rules = new RetrievalRules(CaseLimit: 15), ProfileFragments = true,
    HttpAuthenticationTested = false, ExecutionScope = "Production Azure services via Managed Identity; public HTTP/JWT and cold scale-out are separate" };
Console.WriteLine("INDEPENDENT_PLAN " + JsonSerializer.Serialize(safePlan, Runner.Json));
if (mode == "--prepare") return;
if (config["Validation:Approved"] != "true" || config["Data:Mode"] != "Azure" || config["Ai:Mode"] != "Live" ||
    mode != "--regression-export" && (config["AiUsage:Stopped"] != "false" || config["AiUsage:LiveOptIn"] != "true") || string.IsNullOrWhiteSpace(config["AZURE_CLIENT_ID"]))
    throw new ArgumentException("Explicit production validation opt-in required");
var limits = AiLimits.Read(config);
if (mode != "--regression-export" && (limits.Attempts != 1 || limits.DailyCalls > 240 || limits.DailyCalls < 1 || limits.DailyTokens > 4500000 ||
    limits.DailyJpy > 200 || limits.DailyJpy <= 0 || limits.InputTokens > 16000 || limits.OutputTokens > 3000 ||
    config["Foundry:Deployment"] != "dse-luna6-eval" || config["Jev:Model"] != "jev-1.13.0"))
    throw new ArgumentException("Frozen validation budget/model contract required");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddCatalog(config, false); builder.Services.AddIngestion(config);
using var host = builder.Build();
var repo = host.Services.GetRequiredService<IPrimaryRepository>(); var store = host.Services.GetRequiredService<IRecordStore>();
var decisions = host.Services.GetRequiredService<IDecisionProvider>(); var pipeline = host.Services.GetRequiredService<ProfilePipeline>();
var search = host.Services.GetRequiredService<NaturalSearchService>(); var guard = host.Services.GetRequiredService<SourceGuard>();
var clock = host.Services.GetRequiredService<TimeProvider>();
if (regression)
{
    var tagged = new TaggedAi(decisions, host.Services.GetRequiredService<IReranker>(), evaluationId);
    search = new NaturalSearchService(host.Services.GetRequiredService<IProjectionRepository>(), repo, guard,
        host.Services.GetRequiredService<AccessFence>(), tagged, tagged, config, clock);
}
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(14)); var ct = deadline.Token;
var partition = RecordPartition.Catalog(tenant);
var blobs = new BlobServiceClient(new Uri(config["Storage:BlobEndpoint"]!), new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config["AZURE_CLIENT_ID"]!)));
var prefix = "validation/issue30/" + evaluationId + "/";
async Task Save(string name, object value) => await blobs.GetBlobContainerClient("extracted").GetBlobClient(prefix + name)
    .UploadAsync(BinaryData.FromString(JsonSerializer.Serialize(value, Runner.Json)), overwrite: false, ct);
if (mode == "--regression-export")
{
    var content = await blobs.GetBlobContainerClient("extracted").GetBlobClient(prefix + "regression-report.json").DownloadContentAsync(ct);
    var bytes = System.Text.Encoding.UTF8.GetBytes(content.Value.Content.ToString());
    using var buffer = new MemoryStream();
    using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, true)) gzip.Write(bytes);
    var encoded = Convert.ToBase64String(buffer.ToArray());
    var count = (encoded.Length + 1999) / 2000;
    for (var i = 0; i < count; i++) Console.WriteLine("REGRESSION_CHUNK " + JsonSerializer.Serialize(new {
        Index = i, Count = count, Sha256 = JevEvaluation.HashText(content.Value.Content.ToString()),
        Data = encoded.Substring(i * 2000, Math.Min(2000, encoded.Length - i * 2000)) }, Runner.Json));
    Console.WriteLine("REGRESSION_EXPORT_COMPLETE " + count); return;
}
if (mode == "--export-audits")
{
    foreach (var kind in new[] { "aiRun", "retrievalRun" })
    {
        string? cursor = null; var pages = 0;
        do
        {
            var page = await repo.QueryAsync(partition, kind, null, null, 100, cursor, ct);
            foreach (var row in page.Items.Where(r => r.Value.Strings("scopeIds").Contains(scope)))
                Console.WriteLine("INDEPENDENT_AUDIT_ROW " + row.Value.ToJsonString());
            cursor = page.ContinuationToken;
            if (++pages > 30) throw new InvalidOperationException("Audit page bound exceeded");
        } while (cursor is not null);
    }
    Console.WriteLine("INDEPENDENT_AUDIT_COMPLETE");
    return;
}
if (mode == "--export")
{
    var content = await blobs.GetBlobContainerClient("extracted").GetBlobClient(prefix + "report.json").DownloadContentAsync(ct);
    var report = JsonNode.Parse(content.Value.Content.ToString())!;
    Console.WriteLine("INDEPENDENT_EXPORT_PLAN " + report["plan"]!.ToJsonString());
    foreach (var row in report["rows"]!.AsArray()) Console.WriteLine("INDEPENDENT_EXPORT_ROW " + row!.ToJsonString());
    Console.WriteLine("INDEPENDENT_EXPORT_COMPLETE " + report["rows"]!.AsArray().Count);
    return;
}
if (mode == "--diagnose")
{
    var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config["AZURE_CLIENT_ID"]!));
    var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["https://vault.azure.net/.default"]), ct);
    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(config["KeyVault:Uri"]!), $"secrets/{config["Jev:SecretName"]}/{config["Jev:SecretVersion"]}?api-version=2025-07-01"));
    request.Headers.Authorization = new("Bearer", token.Token);
    using var response = await http.SendAsync(request, ct);
    Console.WriteLine("INDEPENDENT_DIAGNOSTIC " + JsonSerializer.Serialize(new { KeyVaultStatus = (int)response.StatusCode, InferenceSent = false }));
    using var versionsRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(config["KeyVault:Uri"]!), $"secrets/{config["Jev:SecretName"]}/versions?api-version=2025-07-01"));
    versionsRequest.Headers.Authorization = new("Bearer", token.Token);
    using var versionsResponse = await http.SendAsync(versionsRequest, ct);
    var metadata = JsonNode.Parse(await versionsResponse.Content.ReadAsStringAsync(ct));
    Console.WriteLine("INDEPENDENT_DIAGNOSTIC_VERSIONS " + JsonSerializer.Serialize(new { Status = (int)versionsResponse.StatusCode,
        Versions = metadata?["value"]?.AsArray().Select(v => new { Version = v?["id"]?.GetValue<string>().Split('/').Last(), Enabled = v?["attributes"]?["enabled"]?.GetValue<bool>(), Created = v?["attributes"]?["created"]?.GetValue<long>() }).ToArray() }));
    await foreach (var blob in blobs.GetBlobContainerClient("extracted").GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, prefix, ct))
    {
        if (blob.Name.EndsWith("failure-run.json"))
        {
            var content = await blobs.GetBlobContainerClient("extracted").GetBlobClient(blob.Name).DownloadContentAsync(ct);
            Console.WriteLine("INDEPENDENT_DIAGNOSTIC_RESULT " + content.Value.Content.ToString());
        }
    }
    var page = await repo.QueryAsync(partition, "aiRun", null, null, 100, null, ct);
    foreach (var row in page.Items.Where(r => r.Value.Text("jobId").StartsWith(run)))
        Console.WriteLine("INDEPENDENT_DIAGNOSTIC_AUDIT " + row.Value.ToJsonString());
    return;
}
async Task<object?> LedgerSnapshot()
{
    var day = (await repo.ReadAsync(partition, "ai-day-" + clock.GetUtcNow().ToString("yyyy-MM-dd"), ct))?.Value;
    var gate = (await repo.ReadAsync(partition, "system-ai-gate", ct))?.Value;
    return new { Calls = day?["calls"]?.DeepClone(), Tokens = day?["tokens"]?.DeepClone(), EstimatedJpy = day?["jpy"]?.DeepClone(),
        ActiveReservations = gate?["active"]?.AsArray().Count ?? 0, Blocked = gate?["blocked"]?.DeepClone() };
}
var ledgerBefore = regression ? await LedgerSnapshot() : null;
if (regression) Console.WriteLine("REGRESSION_PREFLIGHT " + JsonSerializer.Serialize(ledgerBefore, Runner.Json));
var stages = new List<object>();
try
{
    await Save(mode == "--regression" ? "regression-start.json" : mode == "--run" ? "start.json" : mode == "--resume" ? "resume-start.json" : "search-start.json", safePlan);
    if (mode is "--run" or "--resume")
    {
        foreach (var entry in fixture.Units)
        {
            var u = entry.Search; var id = run + "-" + u.EntityId; var sourceId = id + "-evidence";
            var primaryOwner = u.EntityType == "person" ? "principal-" + u.EntityId : owner;
            var now = clock.GetUtcNow();
            JsonObject Record(string kind, string name) => RecordJson.New(kind, name, tenant, primaryOwner, [scope], now);
            var primary = Record(u.EntityType, id); primary["activeVersion"] = 1; primary["accessVersion"] = 1;
            if (u.EntityType == "person")
            {
                primary["displayName"] = u.Title; primary["departmentId"] = "independent-department"; primary["available"] = true;
                primary["publicationState"] = "published"; primary["confirmationState"] = "ai_unconfirmed"; primary["correctionState"] = "none";
                primary["experienceIds"] = RecordJson.Array([id + "-experience"]);
            }
            else
            {
                // Existing approved dummy knowledge format; publication workflow is outside this retrieval test.
                primary["title"] = u.Title; primary["summary"] = u.Summary; primary["status"] = "published";
                primary["publicationVersion"] = 1; primary["participantIds"] = RecordJson.Array([]);
                primary["approvedBy"] = new JsonObject { ["type"] = "service", ["id"] = "independent-human-approved-fixture" };
                primary["analysisConsent"] = "not_granted";
            }
            var doc = Record("document", id + "-document"); doc["title"] = u.Title; doc["documentType"] = "profile"; doc["status"] = "ready";
            doc["currentVersionId"] = id + "-version"; doc["activeVersion"] = 1; doc["accessVersion"] = 1;
            var version = Record("documentVersion", id + "-version"); version["documentId"] = doc.Text("id"); version["status"] = "ready";
            version["activeVersion"] = 1; version["blobKey"] = "validation/" + id; version["sha256"] = JevEvaluation.HashText(u.Summary);
            version["mediaType"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"; version["bytes"] = 1; version["jobId"] = run;
            var span = Record("evidenceSpan", sourceId); span["documentId"] = doc.Text("id"); span["documentVersionId"] = version.Text("id");
            span["text"] = u.Summary; span["textHash"] = version["sha256"]!.DeepClone(); span["validationState"] = "validated"; span["activeVersion"] = 1;
            span["location"] = new JsonObject { ["type"] = "paragraph", ["label"] = u.SourceRefs[0].Location };
            var refs = new[] { new EvidenceReference(sourceId, version.Text("id"), 1) };
            List<JsonObject> records = [primary, doc, version, span];
            if (u.EntityType == "person")
            {
                var experience = Record("experience", id + "-experience"); experience["personId"] = id; experience["documentId"] = doc.Text("id");
                experience["title"] = u.Title; experience["description"] = u.Summary; experience["evidenceRefs"] = AiCodecsPublic.Node(refs);
                experience["period"] = AiCodecsPublic.Node(new ExperiencePeriod(null, null, "unknown", null));
                experience["confirmationState"] = "ai_unconfirmed"; experience["correctionState"] = "none"; records.Add(experience);
            }
            var existing = new List<StoredRecord?>();
            foreach (var record in records) existing.Add(await repo.ReadAsync(partition, record.Text("id"), ct));
            if (existing.Any(r => r is not null))
            {
                // Recovery of the first pre-classification seed only. Never overwrite, alter gold, or resend a successful item.
                if (mode != "--resume" || entry != fixture.Units[0] || existing.Any(r => r is null) ||
                    existing.Any(r => r!.Value.Number("sourceVersion") != 1 || r.Value.Text("tenantId") != tenant ||
                        r.Value.Text("ownerId") != primaryOwner || !r.Value.Strings("scopeIds").SequenceEqual([scope])) ||
                    existing.Single(r => r!.Value.Text("kind") == "evidenceSpan")!.Value.Text("text") != u.Summary ||
                    existing.Single(r => r!.Value.Text("kind") == "documentVersion")!.Value.Text("sha256") != version.Text("sha256") ||
                    existing.Single(r => r!.Value.Text("kind") == "experience")!.Value.Text("description") != u.Summary)
                    throw new InvalidOperationException("Existing seed does not match audited failed-first-item recovery");
            }
            else await store.CommitAsync(partition, records.Select(RecordWrite.Create).ToArray(), ct);
            if (u.EntityType == "person")
            {
                var job = Record("job", id + "-job"); job["aggregateId"] = id; job["aggregateVersion"] = 1; job["jobType"] = "projection";
                var plan = await pipeline.BuildAsync(job, ct); await pipeline.CommitAsync(plan, [], ct);
                await Save(id + ".json", new { EntityId = u.EntityId, Kind = u.EntityType, Published = true,
                    ProjectionCount = plan.Writes.Count(w => w.Value.Text("kind") == "searchUnit"), Rules = search.Rules() });
            }
            else
            {
                var answer = await decisions.DecideAsync(new(actor, run, "profile-v1", Retriever.TaxonomyVersion, 1, refs, []), u.Summary,
                    [new("domain", "state.textの経験分野を分類。根拠は訂正前の場合もある。Probabilityは年数・熟練度ではない。", ProfileExtraction.Domains)],
                    ProfileExtraction.Domains.Concat(new[] { new KeyValuePair<string, string>("handover", "引き継ぎ") }).Select(p => new NoulQuestion(p.Key,
                        p.Value + "の経験がstate.textに明記されているか。根拠は本人訂正前の場合もある。", ProfileExtraction.HasDomainEvidence(u.Summary, p.Key))).ToArray(), ct);
                if (answer.Status != AiStatus.Succeeded || answer.Value is null) throw new DomainException(422, "CLASSIFICATION_FAILED");
                var unit = u with { Id = id + "-projection", EntityId = id, TenantId = tenant, UnitId = id, ScopeIds = [scope],
                    Classifications = answer.Value.Choices.SelectMany(x => x.Value.Probabilities.Select(p => new CategoryProbability(x.Key, p.Key, p.Value))).ToArray(),
                    ExperienceSignals = answer.Value.Nouls.Select(p => new ExperienceSignal(p.Key, p.Value.YesProbability,
                        p.Value.State == NoulState.Supported ? "supported" : "unknown")).ToArray(), NormalizedTerms = ProfileExtraction.Terms(u.Summary),
                    SourceVersion = 1, AccessVersion = 1, ProjectionVersion = 1, ProjectionState = "ready", EvidenceRefs = refs, ProcessingMode = "live" };
                var projection = AiCodecsPublic.Node(unit).AsObject();
                foreach (var p in Record("searchUnit", unit.Id)) if (!projection.ContainsKey(p.Key)) projection[p.Key] = p.Value?.DeepClone();
                await store.CommitAsync(partition, [RecordWrite.Create(projection)], ct);
                await guard.RequireAsync(actor, unit, ct);
                await Save(id + ".json", new { EntityId = u.EntityId, Kind = u.EntityType, Published = true, AiRunId = answer.RunId, Decisions = answer.Value });
            }
            Console.WriteLine("INDEPENDENT_SEEDED " + u.EntityId);
        }
    }
    var rows = new List<object>();
    foreach (var arm in regression ? new[] { "all", "targeted" } : new[] { "all" })
    foreach (var q in fixture.Questions)
    {
        var target = arm == "targeted" ? q.EntityType : "all";
        var timer = Stopwatch.StartNew(); NaturalSearchResponse? response = null; string? error = null;
        try { await search.SearchAndConsumeAsync(new(q.Text, Target: target), actor, (r, _) => { response = r; return Task.CompletedTask; }, ct); }
        catch (DomainException e) { error = e.Code; }
        var ms = timer.Elapsed.TotalMilliseconds;
        var audit = response is null ? null : (await repo.ReadAsync(partition, response.RunId, ct))?.Value;
        var selected = audit?.Strings("selectedIds").Select(i => i[(i.IndexOf(':') + 1)..]).Select(i => i.StartsWith(run + "-") ? i[(run.Length + 1)..] : i).ToArray() ?? [];
        var ranked = response?.Primary.Candidates.Select(c => c.EntityId[(run.Length + 1)..]).ToArray() ?? [];
        var targetRanked = ranked.Where(i => q.Judgments.Any(j => j.EntityId == i)).ToArray();
        var targetSelected = selected.Where(i => q.Judgments.Any(j => j.EntityId == i)).ToArray();
        var gold = q.Judgments.Where(j => j.Grade >= 2 && !j.Insufficient).Select(j => j.EntityId).ToArray();
        var record = new { QuestionId = q.Id, Arm = arm, Target = target, q.EntityType, Error = error, RerankingStatus = response?.RerankingStatus,
            DurationMs = ms, RunId = response?.RunId, AiRunId = response?.AiRunId, Primary = selected, Ranking = ranked,
            TargetRanking = targetRanked, OtherTypeRecommendations = ranked.Except(targetRanked).ToArray(),
            PrimaryRecall = gold.Length == 0 ? (double?)null : gold.Intersect(targetSelected).Count() / (double)gold.Length,
            FinalHit = gold.Length == 0 ? (int?)null : gold.Intersect(targetRanked.Take(5)).Any() ? 1 : 0,
            TargetPrecision = targetRanked.Length == 0 ? (double?)null : gold.Intersect(targetRanked.Take(5)).Count() / (double)Math.Min(5, targetRanked.Length),
            LostGold = gold.Except(targetRanked.Take(5)).ToArray(), NoGoldFalseRecommendation = gold.Length == 0 && ranked.Length > 0,
            RequestUnits = audit?["requestUnits"]?.GetValue<double>(), RuMeasured = audit?["ruMeasured"]?.GetValue<bool>(),
            Assessments = response?.Assessments, EvidenceCuts = audit?["evidenceCuts"]?.DeepClone() };
        rows.Add(record); await Save("question-" + q.Id + (regression ? "-" + arm : mode == "--search" ? "-repeat" : "") + ".json", record);
        Console.WriteLine(regression ? "REGRESSION_QUESTION " + q.Id + " " + arm + " " + (response?.RerankingStatus ?? error) : "INDEPENDENT_QUESTION " + JsonSerializer.Serialize(record, Runner.Json));
    }
    List<JsonObject> aiAudits = [];
    if (regression)
    {
        string? cursor = null; var pages = 0;
        do
        {
            var page = await repo.QueryAsync(partition, "aiRun", null, null, 100, cursor, ct);
            aiAudits.AddRange(page.Items.Where(r => r.Value.Text("jobId").StartsWith(evaluationId + "-", StringComparison.Ordinal)).Select(r => r.Value));
            cursor = page.ContinuationToken;
            if (++pages > 30) throw new InvalidOperationException("Audit page bound exceeded");
        } while (cursor is not null);
    }
    await Save(regression ? "regression-report.json" : mode == "--search" ? "repeat-report.json" : "report.json", new { Plan = safePlan, Rows = rows, AiAudits = aiAudits, LedgerBefore = ledgerBefore, LedgerAfter = regression ? await LedgerSnapshot() : null, Completed = true });
    Console.WriteLine("INDEPENDENT_COMPLETE " + rows.Count);
}
catch (Exception e)
{
    await Save("failure-" + mode[2..] + ".json", new { Stage = mode, ErrorType = e.GetType().Name, Code = e is DomainException d ? d.Code : null,
        DataAlreadyWritten = true, AutomaticResend = false });
    Console.WriteLine("INDEPENDENT_FAILED " + e.GetType().Name + (e is DomainException domainError ? ":" + domainError.Code : ""));
    Environment.ExitCode = 1;
}

static class AiCodecsPublic
{
    public static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Runner.Json)!;
}

sealed class TaggedAi(IDecisionProvider decisions, IReranker reranker, string evaluationId) : IDecisionProvider, IReranker
{
    public Task<AiResult<Decisions>> DecideAsync(AiContext context, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct) =>
        decisions.DecideAsync(context with { JobId = evaluationId + "-" + context.JobId }, text, choices, nouls, ct);
    public Task<AiResult<Ranking>> RerankAsync(AiContext context, string text, CancellationToken ct) =>
        reranker.RerankAsync(context with { JobId = evaluationId + "-" + context.JobId }, text, ct);
}
