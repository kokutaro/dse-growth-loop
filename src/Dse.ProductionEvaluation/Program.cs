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
if (mode is not ("--prepare" or "--run" or "--resume" or "--seed" or "--search" or "--diagnose" or "--export" or "--export-audits" or "--regression" or "--regression-export" or "--report-export" or "--seed-status" or "--ledger-status" or "--resume-seed" or "--weekly-budget-initialize")) throw new ArgumentException("Invalid mode");
var dataDirectory = config["Validation:DataDirectory"] ?? "data";
var datasetPath = Path.Combine(dataDirectory, "dataset.json");
var approvalPath = Path.Combine(dataDirectory, "approval.json");
var fixture = Runner.Load<Fixture>(datasetPath);
var approval = File.Exists(approvalPath) ? JsonNode.Parse(File.ReadAllText(approvalPath))!.AsObject() : null;
ProductionEvaluationPlan.RequireFixture(fixture, Runner.Hash(datasetPath), approval?.Text("datasetSha256"), mode == "--prepare");
var rules = ProductionEvaluationPlan.Rules(config["Retrieval:PersonLimit"]);
var bothTargets = config.GetValue<bool>("Validation:BothTargets");
var run = config["Validation:RunId"] ?? "issue30-independent-20261003-v1";
var scope = run;
var evaluationId = config["Validation:EvaluationId"] ?? run;
var attempt = config.GetValue<int>("Validation:AttemptId", 1);
if (attempt is < 1 or > 16) throw new ArgumentException("Invalid manual recovery attempt");
var readOnlyExport = mode is "--regression-export" or "--report-export" or "--seed-status" or "--ledger-status" or "--weekly-budget-initialize";
var regression = mode is "--regression" or "--regression-export";
if (regression && (evaluationId == run || !RecordJson.IsId(evaluationId) || !evaluationId.StartsWith("issue30-hardening-")))
    throw new ArgumentException("Separate regression evaluation ID required");
if (!RecordJson.IsId(run) || !run.StartsWith("issue30-independent-")) throw new ArgumentException("Dedicated run ID required");
var regressionAllOnly = config.GetValue<bool>("Validation:RegressionAllOnly");
var arms = ProductionEvaluationPlan.Arms(regression, bothTargets, regressionAllOnly);
var tenant = config["Auth:TenantId"] ?? throw new ArgumentException("Tenant required");
var owner = "principal-" + run;
var person = "operator-" + run;
var actor = new ActorAccess(tenant, owner, person, ["member", "publisher", "operator"], [scope]);
// Synthetic identities exist only in this dedicated validation process, not public API auth settings.
var settings = new Dictionary<string, string?> { ["Retrieval:CaseLimit"] = "15", ["Retrieval:PersonLimit"] = rules.PersonLimit.ToString(), ["Profile:SecondaryWorkFragments"] = "true" };
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
var safePlan = new { RunId = run, Mode = mode, DatasetSha256 = Runner.Hash(datasetPath), DummyOnly = true, Scope = scope,
    EvaluationId = evaluationId, ObservedRegressionSet = regression, IndependentHoldout = !regression, Arms = arms,
    Questions = 24, Documents = 60, Rules = rules, LabelApprovalStatus = fixture.HumanReviewStatus, ExecutionEnabled = mode != "--prepare", ProfileFragments = true,
    HttpAuthenticationTested = false, ExecutionScope = "Production Azure services via Managed Identity; public HTTP/JWT and cold scale-out are separate" };
Console.WriteLine("INDEPENDENT_PLAN " + JsonSerializer.Serialize(safePlan, Runner.Json));
if (mode == "--prepare") return;
if (config["Validation:Approved"] != "true" || config["Data:Mode"] != "Azure" || config["Ai:Mode"] != "Live" ||
    !readOnlyExport && (config["AiUsage:Stopped"] != "false" || config["AiUsage:LiveOptIn"] != "true") || string.IsNullOrWhiteSpace(config["AZURE_CLIENT_ID"]))
    throw new ArgumentException("Explicit production validation opt-in required");
var limits = AiLimits.Read(config);
if (!readOnlyExport && (limits.Attempts != 1 || (!limits.Weekly && (limits.DailyCalls > 240 || limits.DailyCalls < 1)) || limits.DailyTokens > 4500000 ||
    (!limits.Weekly && (limits.DailyJpy > 200 || limits.DailyJpy <= 0)) || (limits.Weekly && (limits.WeeklyCalls is < 1 or > 1680 || limits.WeeklyTotalJpy is <= 0 or > 2000 || limits.UsdToJpy != 162)) || limits.InputTokens > 16000 || limits.OutputTokens > 3000 ||
    config["Foundry:Deployment"] != "dse-luna6-eval" || config["Jev:Model"] != "jev-1.13.0"))
    throw new ArgumentException("Frozen validation budget/model contract required");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddCatalog(config, false); builder.Services.AddIngestion(config);
using var host = builder.Build();
var repo = host.Services.GetRequiredService<IPrimaryRepository>(); var store = host.Services.GetRequiredService<IRecordStore>();
var decisions = host.Services.GetRequiredService<IDecisionProvider>(); var pipeline = host.Services.GetRequiredService<ProfilePipeline>();
var search = host.Services.GetRequiredService<NaturalSearchService>(); var guard = host.Services.GetRequiredService<SourceGuard>();
var clock = host.Services.GetRequiredService<TimeProvider>();
ObservedDecisions? observedDecisions = null;
if (!regression && bothTargets)
{
    observedDecisions = new(decisions);
    search = new NaturalSearchService(host.Services.GetRequiredService<IProjectionRepository>(), repo, guard,
        host.Services.GetRequiredService<AccessFence>(), observedDecisions, host.Services.GetRequiredService<IReranker>(), config, clock);
}
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
if (readOnlyExport)
{
    string exported;
    if (mode == "--weekly-budget-initialize")
    {
        var weeklyApproval = JsonNode.Parse(File.ReadAllText(Path.Combine(dataDirectory, "weekly-budget-approval.json")))!.AsObject();
        if (!limits.Stopped || limits.LiveOptIn || !limits.Weekly || limits.UsdToJpy != 162 || limits.WeeklyTotalJpy > 2000 ||
            weeklyApproval.Text("reference") != config["Validation:WeeklyApprovalReference"] || weeklyApproval["totalJpy"]!.GetValue<int>() != 2000)
            throw new ArgumentException("Stopped operator initialization matching weekly human approval required");
        var week = await host.Services.GetRequiredService<AiLedger>().InitializeWeekAsync(tenant, limits, weeklyApproval.Text("reference"), true, ct);
        exported = JsonSerializer.Serialize(new { Plan = safePlan, Week = week, Ledger = await LedgerSnapshot(), InferenceSent = false }, Runner.Json);
    }
    else if (mode == "--ledger-status")
    {
        var audits = new List<object>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await repo.QueryAsync(partition, "aiRun", null, null, 100, cursor, ct);
            audits.AddRange(page.Items.Where(r => r.Value.Strings("scopeIds").Contains(scope)).Select(r => (object)new {
                Id = r.Value.Text("id"), JobId = r.Value.Text("jobId"), Operation = r.Value.Text("operation"),
                Status = r.Value.Text("status"), ErrorCode = r.Value["errorCode"]?.DeepClone(),
                CreatedAt = r.Value["createdAt"]?.DeepClone(), Trace = r.Value["trace"]?.DeepClone() }));
            cursor = page.ContinuationToken;
            if (++pages > 30) throw new InvalidOperationException("Audit page bound exceeded");
        } while (cursor is not null);
        exported = JsonSerializer.Serialize(new { Plan = safePlan, AiAudits = audits, Ledger = await LedgerSnapshot(), InferenceSent = false }, Runner.Json);
    }
    else if (mode == "--seed-status")
    {
        var units = new List<object>();
        foreach (var entry in fixture.Units)
        {
            var id = run + "-" + entry.Search.EntityId;
            var primary = await repo.ReadAsync(partition, id, ct);
            var receipt = blobs.GetBlobContainerClient("extracted").GetBlobClient(prefix + id + ".json");
            units.Add(new { EntityId = entry.Search.EntityId, PrimaryExists = primary is not null, ReceiptExists = (await receipt.ExistsAsync(ct)).Value });
        }
        var audits = new List<JsonObject>(); string? cursor = null; var pages = 0;
        do
        {
            var page = await repo.QueryAsync(partition, "aiRun", null, null, 100, cursor, ct);
            audits.AddRange(page.Items.Where(r => r.Value.Strings("scopeIds").Contains(scope)).Select(r => r.Value));
            cursor = page.ContinuationToken;
            if (++pages > 30) throw new InvalidOperationException("Audit page bound exceeded");
        } while (cursor is not null);
        var firstId = run + "-" + fixture.Units[0].Search.EntityId;
        var firstRecords = new List<JsonObject>();
        foreach (var id in new[] { firstId, firstId + "-document", firstId + "-version", firstId + "-evidence", firstId + "-experience" })
        {
            var record = await repo.ReadAsync(partition, id, ct);
            if (record is not null) firstRecords.Add(record.Value);
        }
        exported = JsonSerializer.Serialize(new { Plan = safePlan, Units = units, FirstRecords = firstRecords, AiAudits = audits, Ledger = await LedgerSnapshot(), InferenceSent = false }, Runner.Json);
    }
    else
    {
    var reportName = config["Validation:ReportName"] ?? "regression-report.json";
    if (reportName is not ("regression-report.json" or "repeat-report.json" or "report.json" or "seed-complete.json")) throw new ArgumentException("Invalid export report name");
    var content = await blobs.GetBlobContainerClient("extracted").GetBlobClient(prefix + reportName).DownloadContentAsync(ct);
    exported = content.Value.Content.ToString();
    }
    var bytes = System.Text.Encoding.UTF8.GetBytes(exported);
    using var buffer = new MemoryStream();
    using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, true)) gzip.Write(bytes);
    var encoded = Convert.ToBase64String(buffer.ToArray());
    var count = (encoded.Length + 1999) / 2000;
    var startChunk = config.GetValue<int>("Validation:ExportStartChunk", 0);
    var chunkLimit = config.GetValue<int>("Validation:ExportChunkCount", count);
    if (startChunk < 0 || startChunk >= count || chunkLimit < 1) throw new ArgumentException("Invalid export chunk range");
    for (var i = startChunk; i < Math.Min(count, startChunk + chunkLimit); i++) Console.WriteLine("REGRESSION_CHUNK " + JsonSerializer.Serialize(new {
        Index = i, Count = count, Sha256 = JevEvaluation.HashText(exported),
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
    var day = (await store.ReadAsync(partition, "ai-day-" + clock.GetUtcNow().ToString("yyyy-MM-dd"), ct))?.Value;
    var gate = (await store.ReadAsync(partition, "system-ai-gate", ct))?.Value;
    return new { Calls = day?["calls"]?.DeepClone(), Tokens = day?["tokens"]?.DeepClone(), EstimatedJpy = day?["jpy"]?.DeepClone(),
        Week = (await store.ReadAsync(partition, AiLedger.WeekId(clock.GetUtcNow()), ct))?.Value,
        ActiveReservations = gate?["active"]?.AsArray().Count ?? 0, Blocked = gate?["blocked"]?.DeepClone() };
}
var ledgerBefore = await LedgerSnapshot();
var ledgerNode = JsonSerializer.SerializeToNode(ledgerBefore, Runner.Json)!;
var stageCalls = mode is "--run" or "--resume" ? 196 : mode is "--seed" or "--resume-seed" ? 100 : mode is "--search" or "--regression" ? (arms.Length * 48) : 0;
if (stageCalls > 0 && !limits.Weekly) ProductionEvaluationPlan.RequireCallBudget(ledgerNode["calls"]?.GetValue<int>() ?? 0, limits.DailyCalls, stageCalls,
    ledgerNode["activeReservations"]!.GetValue<int>());
if (stageCalls > 0 && limits.Weekly)
{
    var w = ledgerNode["week"]?.AsObject() ?? throw new InvalidOperationException("Weekly approval missing");
    ProductionEvaluationPlan.RequireWeeklyBudget(w["calls"]!.GetValue<int>(), limits.WeeklyCalls, stageCalls, ledgerNode["activeReservations"]!.GetValue<int>(),
        w["usd"]!.GetValue<decimal>() * limits.UsdToJpy + w["reservedNonRuntimeJpy"]!.GetValue<decimal>(), limits.WeeklyTotalJpy,
        ledgerNode["tokens"]?.GetValue<long>() ?? 0, limits.DailyTokens);
}
if (regression) Console.WriteLine("REGRESSION_PREFLIGHT " + JsonSerializer.Serialize(ledgerBefore, Runner.Json));
var stages = new List<object>();
try
{
    await Save(mode == "--regression" ? "regression-start.json" : mode == "--run" ? "start.json" : mode == "--resume" ? "resume-start.json" : mode == "--seed" ? "seed-start.json" : mode == "--resume-seed" ? "resume-seed-start-" + attempt + ".json" : "search-start.json", safePlan);
    if (mode is "--run" or "--resume" or "--seed" or "--resume-seed")
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
                var receiptBlob = blobs.GetBlobContainerClient("extracted").GetBlobClient(prefix + id + ".json");
                var completed = (await receiptBlob.ExistsAsync(ct)).Value;
                if (mode == "--resume-seed")
                {
                    ProductionEvaluationPlan.RequireSeedRecovery(records.ToArray(), existing.Select(r => r?.Value).ToArray(), completed);
                    if (completed)
                    {
                        var receipt = JsonNode.Parse((await receiptBlob.DownloadContentAsync(ct)).Value.Content.ToString())!;
                        if (receipt["entityId"]?.GetValue<string>() != u.EntityId || receipt["kind"]?.GetValue<string>() != u.EntityType ||
                            receipt["published"]?.GetValue<bool>() != true) throw new InvalidOperationException("Invalid completed seed receipt");
                        var projectionIds = u.EntityType == "person" ? existing[0]!.Value.Strings("projectionIds") : [id + "-projection"];
                        if (projectionIds.Length == 0 || u.EntityType == "person" && projectionIds.Length != receipt["projectionCount"]!.GetValue<int>())
                            throw new InvalidOperationException("Incomplete completed seed projections");
                        foreach (var projectionId in projectionIds)
                        {
                            var projection = await store.ReadAsync(partition, projectionId, ct) ?? throw new InvalidOperationException("Missing completed seed projection");
                            var unit = projection.Value.Deserialize<SearchUnit>(Runner.Json)!;
                            if (unit.EntityId != id || unit.ProcessingMode != "live") throw new InvalidOperationException("Invalid completed seed projection");
                            await guard.RequireAsync(actor, unit, ct);
                        }
                        Console.WriteLine("INDEPENDENT_REUSED " + u.EntityId); continue;
                    }
                    if (u.EntityType == "person" && existing[0]!.Value.Number("sourceVersion") != 1)
                        throw new InvalidOperationException("Partial person pipeline cannot be recovered");
                    var recoveryAudits = new List<JsonObject>(); string? recoveryCursor = null; var recoveryPages = 0;
                    do
                    {
                        var page = await repo.QueryAsync(partition, "aiRun", null, null, 100, recoveryCursor, ct);
                        recoveryAudits.AddRange(page.Items.Where(r => r.Value.Strings("scopeIds").Contains(scope)).Select(r => r.Value));
                        recoveryCursor = page.ContinuationToken;
                        if (++recoveryPages > 30) throw new InvalidOperationException("Audit page bound exceeded");
                    } while (recoveryCursor is not null);
                    ProductionEvaluationPlan.RequireUnsentRecovery(recoveryAudits.ToArray(), sourceId);
                    if (u.EntityType == "knowledge" && await store.ReadAsync(partition, id + "-projection", ct) is not null)
                        throw new InvalidOperationException("Unreceipted knowledge projection cannot be recovered");
                    Console.WriteLine("INDEPENDENT_RESUMING_UNSENT " + u.EntityId);
                }
                else
                {
                    // Historical recovery of the first pre-classification seed only.
                    if (mode != "--resume" || entry != fixture.Units[0] || completed)
                        throw new InvalidOperationException("Existing seed requires an explicit verified recovery mode");
                    ProductionEvaluationPlan.RequireSeedRecovery(records.ToArray(), existing.Select(r => r?.Value).ToArray(), false);
                }
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
    if (mode is "--seed" or "--resume-seed")
    {
        await Save("seed-complete.json", new { Plan = safePlan, Completed = true, LedgerAfter = await LedgerSnapshot() });
        Console.WriteLine("INDEPENDENT_SEED_COMPLETE " + fixture.Units.Length); return;
    }
    var rows = new List<object>();
    foreach (var arm in arms)
    foreach (var q in fixture.Questions)
    {
        var target = arm == "targeted" ? q.EntityType : "all";
        observedDecisions?.Reset();
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
            QueryClassification = observedDecisions?.Last is {} decision ? new { Status = decision.Status.ToString(), Choice = decision.Value?.Choices["domain"].Choice, Failure = decision.Failure?.ToString(), decision.RunId, decision.Cached } : null,
            DurationMs = ms, RunId = response?.RunId, AiRunId = response?.AiRunId, Primary = selected, Ranking = ranked,
            TargetRanking = targetRanked, OtherTypeRecommendations = ranked.Except(targetRanked).ToArray(),
            PrimaryRecall = gold.Length == 0 ? (double?)null : gold.Intersect(targetSelected).Count() / (double)gold.Length,
            FinalHit = gold.Length == 0 ? (int?)null : gold.Intersect(targetRanked.Take(5)).Any() ? 1 : 0,
            TargetPrecision = targetRanked.Length == 0 ? (double?)null : gold.Intersect(targetRanked.Take(5)).Count() / (double)Math.Min(5, targetRanked.Length),
            LostGold = gold.Except(targetRanked.Take(5)).ToArray(), NoGoldFalseRecommendation = gold.Length == 0 && ranked.Length > 0,
            RequestUnits = audit?["requestUnits"]?.GetValue<double>(), RuMeasured = audit?["ruMeasured"]?.GetValue<bool>(),
            Assessments = response?.Assessments, EvidenceCuts = audit?["evidenceCuts"]?.DeepClone(), RetrievalAudit = audit?.DeepClone() };
        rows.Add(record); await Save("question-" + q.Id + (regression || bothTargets ? "-" + arm + (mode == "--search" ? "-repeat" : "") : mode == "--search" ? "-repeat" : "") + ".json", record);
        Console.WriteLine(regression ? "REGRESSION_QUESTION " + q.Id + " " + arm + " " + (response?.RerankingStatus ?? error) : "INDEPENDENT_QUESTION " + JsonSerializer.Serialize(record, Runner.Json));
    }
    List<JsonObject> aiAudits = [];
    if (regression || bothTargets)
    {
        string? cursor = null; var pages = 0;
        do
        {
            var page = await repo.QueryAsync(partition, "aiRun", null, null, 100, cursor, ct);
            aiAudits.AddRange(page.Items.Where(r => regression ? r.Value.Text("jobId").StartsWith(evaluationId + "-", StringComparison.Ordinal) : r.Value.Strings("scopeIds").Contains(scope)).Select(r => r.Value));
            cursor = page.ContinuationToken;
            if (++pages > 30) throw new InvalidOperationException("Audit page bound exceeded");
        } while (cursor is not null);
    }
    await Save(regression ? "regression-report.json" : mode == "--search" ? "repeat-report.json" : "report.json", new { Plan = safePlan, Rows = rows, AiAudits = aiAudits, LedgerBefore = ledgerBefore, LedgerAfter = await LedgerSnapshot(), Completed = true });
    Console.WriteLine("INDEPENDENT_COMPLETE " + rows.Count);
}
catch (Exception e)
{
    await Save("failure-" + mode[2..] + (mode == "--resume-seed" ? "-" + attempt : "") + ".json", new { Stage = mode, ErrorType = e.GetType().Name, Code = e is DomainException d ? d.Code : null,
        DataAlreadyWritten = true, AutomaticResend = false, Diagnostic = e is InvalidOperationException ? e.Message : null });
    Console.WriteLine("INDEPENDENT_FAILED " + e.GetType().Name + (e is DomainException domainError ? ":" + domainError.Code : e is InvalidOperationException ? ":" + e.Message : ""));
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

// Read-only observation of the normal query adapter response; context and input are passed unchanged.
sealed class ObservedDecisions(IDecisionProvider inner) : IDecisionProvider
{
    public AiResult<Decisions>? Last { get; private set; }
    public void Reset() => Last = null;
    public async Task<AiResult<Decisions>> DecideAsync(AiContext context, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct)
    {
        Last = await inner.DecideAsync(context, text, choices, nouls, ct); return Last;
    }
}
