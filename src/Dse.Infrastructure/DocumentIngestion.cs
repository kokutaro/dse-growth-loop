using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed class DocumentUploads(IPrimaryRepository repository, AccessFence fence, IdempotencyExecutor idempotency,
    IIngestionBlobs blobs, OfficeExtractor extractor, IngestionOptions options, JobNotifications notifications, TimeProvider clock, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
{
    public async Task<MutationResponse> UploadAsync(ActorAccess actor, string title, string type, string name, string mime, byte[] bytes, string key, CancellationToken ct)
        => await UploadCoreAsync(actor, null, title, type, name, mime, bytes, key, ct);
    public Task<MutationResponse> UploadTestAsync(ActorAccess actor, string personId, string name, string mime, byte[] bytes, string key, CancellationToken ct) =>
        UploadCoreAsync(actor, personId, SyntheticTestAccess.Label + personId, "profile", name, mime, bytes, key, ct);
    private async Task<MutationResponse> UploadCoreAsync(ActorAccess actor, string? testPersonId, string title, string type, string name, string mime, byte[] bytes, string key, CancellationToken ct)
    {
        async Task<JsonObject> Target(CancellationToken token)
        {
            if(testPersonId is null) return await RequireSelfAsync(actor,token);
            actor=await new AuthorityProvider(repository,configuration).RequireAsync(actor,token);
            return await SyntheticTestAccess.RequireAsync(repository,actor,testPersonId,token);
        }
        IdempotencyExecutor.ValidateHeaders(key, null, false);
        if (string.IsNullOrWhiteSpace(title) || title.EnumerateRunes().Count() > 300 || type is not ("profile" or "report" or "case")) throw new DomainException(400, "INVALID_INPUT");
        await Target(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(options.ProcessingSeconds));
        try { await Task.Run(() => extractor.Validate(bytes, name, mime, timeout.Token), timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DomainException(400, "PROCESSING_TIMEOUT"); }
        if(testPersonId is not null)
        {
            if(mime!=OfficeExtractor.ExcelMime)throw new DomainException(400,"TEST_RESUME_REQUIRED");
            var capture=extractor.Extract(bytes,name,mime,timeout.Token);
            if(capture.Workbook is null || capture.Spans.Length == 0)throw new DomainException(422,"TEST_RESUME_REQUIRED");
        }
        var hash = OfficeExtractor.Hash(bytes);
        var input = new JsonObject { ["title"] = title, ["documentType"] = type, ["sha256"] = hash, ["mediaType"] = mime, ["processorVersion"] = IngestionOptions.ProcessorVersion };
        var digest = OfficeExtractor.Hash(Encoding.UTF8.GetBytes(actor.TenantId + "\n" + actor.PrincipalId + "\n" + RecordJson.Canonical(input)));
        var documentId = "doc-" + digest;
        var blobKey = $"{actor.TenantId}/staging/{Guid.NewGuid():N}{(mime == OfficeExtractor.WordMime ? ".docx" : ".xlsx")}";
        // No business acceptance until the original is durable. Orphans are recovered explicitly.
        await blobs.PutAsync("originals", blobKey, bytes, ct);
        var response = await fence.RunAsync(actor.TenantId, async token =>
        {
            var person = await Target(token);
            var partition = RecordPartition.Catalog(actor.TenantId);
            var existing = await repository.ReadAsync(partition, documentId, token);
            if (existing is not null) AccessPolicy.RequireRead(actor, existing.Value);
            if(testPersonId is not null && existing is null && person["documentHeads"]?.AsObject().Count>0)throw new DomainException(409,"TEST_DOCUMENT_LIMIT");
            return await idempotency.ExecuteAsync(partition, actor, "POST", testPersonId is null ? "/api/v1/documents" : "/api/v1/admin/test-people/"+testPersonId+"/documents", key, null, input, async () =>
            {
                if (existing is not null)
                {
                    var oldVersion = await repository.ReadAsync(partition, existing.Value.Text("currentVersionId"), token) ?? throw new DomainException(503, "DEPENDENCY_UNAVAILABLE");
                    return new MutationPlan([], null, Accepted(existing.Value, oldVersion.Value.Text("jobId")));
                }
                var now = clock.GetUtcNow();
                var doc = RecordJson.New("document", documentId, actor.TenantId, actor.PrincipalId, person.Strings("scopeIds"), now);
                var importKey = OfficeExtractor.Hash(Encoding.UTF8.GetBytes(type + "\n" + title));
                doc["importKey"] = importKey;
                doc["title"] = title; doc["documentType"] = type; doc["status"] = "queued"; doc["accessVersion"] = 1;
                doc["personId"] = person.Text("id"); SyntheticTestAccess.Mark(doc,person); doc["departmentId"] = person.Text("departmentId"); doc["extractionState"] = "pending"; doc["aiState"] = "not_started";
                var version = RecordJson.New("documentVersion", documentId + "-v1", actor.TenantId, actor.PrincipalId, doc.Strings("scopeIds"), now);
                SyntheticTestAccess.Mark(version,person);
                doc["currentVersionId"] = version.Text("id");
                var job = Jobs.Create(doc, "ingestion", now);
                version["documentId"] = documentId; version["blobKey"] = blobKey; version["sha256"] = hash; version["mediaType"] = mime;
                version["bytes"] = bytes.Length; version["status"] = "queued"; version["jobId"] = job.Text("id"); version["processorVersion"] = IngestionOptions.ProcessorVersion;
                person["documentHeads"] ??= new JsonObject();
                person["documentHeads"]![importKey] = documentId;
                SyntheticTestAccess.Mark(job,person);
                List<RecordWrite> writes=[RecordWrite.Create(doc), RecordWrite.Create(version), RecordWrite.Create(job), RecordWrite.Replace(person, person.Text("etag"))];
                if(testPersonId is not null)
                {
                    writes.Add(RecordWrite.Create(SyntheticImportBudget.Create(doc,configuration,now)));
                    var audit=RecordJson.New("audit","audit-"+Guid.NewGuid().ToString("N"),actor.TenantId,actor.PrincipalId,doc.Strings("scopeIds"),now);
                    audit["action"]="test_document_accepted";audit["aggregateId"]=documentId;audit["sha256"]=hash;SyntheticTestAccess.Mark(audit,person);writes.Add(RecordWrite.Create(audit));
                }
                return new MutationPlan(writes.ToArray(), null, Accepted(doc, job.Text("id")));
            }, token);
        }, ct);
        // Best effort after commit. Pending remains recoverable if the queue or response is lost.
        await notifications.TrySendAsync(actor.TenantId, response.Body!["jobId"]!.GetValue<string>(), ct);
        return response;
    }
    private async Task<JsonObject> RequireSelfAsync(ActorAccess actor, CancellationToken ct)
    {
        if (!actor.HasRole("member")) throw new DomainException(403, "FORBIDDEN");
        var person = await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), actor.PersonId, ct) ?? throw new DomainException(403, "FORBIDDEN");
        AccessPolicy.RequireSelf(actor, person.Value);
        if (person.Value.Strings("scopeIds").Any(s => !actor.ScopeIds.Contains(s))) throw new DomainException(403, "FORBIDDEN");
        return person.Value;
    }
    private static MutationResponse Accepted(JsonObject doc, string jobId) => new(202, new JsonObject
    { ["documentId"] = doc.Text("id"), ["version"] = doc.Number("sourceVersion"), ["jobId"] = jobId, ["status"] = "queued", ["statusUrl"] = "/api/v1/jobs/" + jobId }, "/api/v1/jobs/" + jobId, null);
}

public sealed class JobNotifications(IPrimaryRepository repository, IIngestionQueue queue, TimeProvider clock)
{
    public async Task<bool> TrySendAsync(string tenant, string id, CancellationToken ct, string? sessionId = null)
    {
        try
        {
            var partition = sessionId is null ? RecordPartition.Catalog(tenant) : RecordPartition.Conversation(tenant, sessionId);
            var job = await repository.ReadAsync(partition, id, ct);
            if (job is null || job.Value.Text("jobType") is not ("ingestion" or "enrichment" or "projection" or "messageCommitted") || job.Value.Text("status") is not ("queued" or "processing")) return true;
            await queue.SendAsync(new(tenant, id, job.Value.Number("aggregateVersion"), sessionId), ct);
            // A fast chat consumer can claim/complete between send and acknowledgment. Never invalidate its ETag.
            if (sessionId is not null)
            {
                var current = await repository.ReadAsync(partition, id, ct);
                if (current?.ETag != job.ETag || current.Value.Text("status") != "queued") return true;
            }
            job.Value["notificationState"] = "sent";
            await repository.CommitAsync(partition, [RecordWrite.Replace(job.Value, job.ETag)], ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }
    public async Task<int> ReconcileAsync(string tenant, CancellationToken ct)
    {
        int count = 0; string? cursor = null;
        do
        {
            var page = await repository.QueryAsync(RecordPartition.Catalog(tenant), "job", null, null, 100, cursor, ct);
            foreach (var job in page.Items.Where(j => j.Value.Text("jobType") is ("ingestion" or "enrichment" or "projection") &&
                (j.Value.Text("status") == "queued" || j.Value.Text("status") == "processing" && j.Value["leaseUntil"]!.Deserialize<DateTimeOffset>() <= clock.GetUtcNow())))
                if (await TrySendAsync(tenant, job.Value.Text("id"), ct)) count++;
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        // Session initialization already stores a catalog challenge; page it to locate durable outboxes.
        cursor = null;
        do
        {
            var challenges = await repository.QueryAsync(RecordPartition.Catalog(tenant), "challenge", null, null, 100, cursor, ct);
            foreach (var challenge in challenges.Items) count += await ReconcileConversationAsync(tenant, challenge.Value.Text("sessionId"), ct);
            cursor = challenges.ContinuationToken;
        } while (cursor is not null);
        return count;
    }
    public async Task<int> ReconcileConversationAsync(string tenant, string sid, CancellationToken ct)
    {
        int count = 0; string? cursor = null;
        do
        {
            var page = await repository.QueryAsync(RecordPartition.Conversation(tenant, sid), "job", null, null, 100, cursor, ct);
            foreach (var job in page.Items.Where(j => j.Value.Text("jobType") == "messageCommitted" &&
                (j.Value.Text("status") == "queued" || j.Value.Text("status") == "processing" && j.Value["leaseUntil"]?.Deserialize<DateTimeOffset>() <= clock.GetUtcNow())))
                if (await TrySendAsync(tenant, job.Value.Text("id"), ct, sid)) count++;
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        return count;
    }
}

public sealed class IngestionRetry(IPrimaryRepository repository, AccessFence fence, IdempotencyExecutor idempotency, JobNotifications notifications, TimeProvider clock)
{
    public async Task<MutationResponse> RetryAsync(ActorAccess actor, string id, string etag, string key, string reason, CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key, etag, true);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new DomainException(400, "INVALID_INPUT");
        var response = await fence.RunAsync(actor.TenantId, async token =>
        {
            var partition = RecordPartition.Catalog(actor.TenantId);
            var job = await repository.ReadAsync(partition, id, token) ?? throw new DomainException(404, "NOT_FOUND");
            AccessPolicy.RequireRead(actor, job.Value);
            if (job.Value.Text("jobType") is not ("ingestion" or "enrichment" or "projection")) throw new DomainException(409, "INVALID_TRANSITION");
            return await idempotency.ExecuteAsync(partition, actor, "POST", $"/api/v1/jobs/{id}/retry", key, etag, new JsonObject { ["reason"] = reason }, async () =>
            {
                if (job.ETag != etag) throw new DomainException(412, "STALE_VERSION");
                var now = clock.GetUtcNow();
                var doc = await repository.ReadAsync(partition, job.Value.Text("aggregateId"), token) ?? throw new DomainException(404, "NOT_FOUND");
                if(doc.Value.Text("generationParentId")!="")throw new DomainException(409,"REEXTRACTION_RETRY_REQUIRES_REVIEW");
                if(SyntheticTestAccess.IsTest(doc.Value))
                {
                    actor=await new AuthorityProvider(repository).RequireAsync(actor,token);
                    await SyntheticTestAccess.RequireLinkedAsync(repository,actor,doc.Value,token);
                }
                if (doc.Value.Number("sourceVersion") != job.Value.Number("aggregateVersion")) throw new DomainException(409, "SOURCE_REVOKED");
                if (job.Value.Text("status") == "ready" || job.Value.Text("status") == "cancelled" && job.Value.Text("jobType") == "ingestion") throw new DomainException(409, "INVALID_TRANSITION");
                if (job.Value.Text("status") == "processing" && (job.Value["leaseUntil"]!.Deserialize<DateTimeOffset>() > now || !actor.HasRole("operator"))) throw new DomainException(409, "INVALID_TRANSITION");
                if (job.Value.Text("jobType") is "enrichment" or "projection")
                {
                    if (job.Value.Text("ownerId") != actor.PrincipalId && !actor.HasRole("operator")) throw new DomainException(403, "FORBIDDEN");
                    if (job.Value.Text("jobType") == "enrichment" && doc.Value.Text("enrichmentJobId") != id) throw new DomainException(409, "SOURCE_REVOKED");
                    if (job.Value.Text("status") == "queued")
                        return new MutationPlan([], null, new(202, new JsonObject { ["jobId"] = id, ["status"] = "queued", ["statusUrl"] = "/api/v1/jobs/" + id }, "/api/v1/jobs/" + id, null));
                    var next = Jobs.Create(doc.Value, job.Value.Text("jobType"), now); next["supersedesJobId"] = id;
                    var recoveredId = next.Text("id");
                    doc.Value[job.Value.Text("jobType") == "enrichment" ? "enrichmentJobId" : "projectionJobId"] = recoveredId;
                    List<RecordWrite> recovery = [RecordWrite.Create(next), RecordWrite.Replace(doc.Value, doc.ETag)];
                    if (job.Value.Text("status") == "processing")
                    {
                        job.Value["status"] = "cancelled"; job.Value["leaseOwner"] = null; job.Value["leaseUntil"] = null;
                        recovery.Add(RecordWrite.Replace(job.Value, job.ETag));
                    }
                    return new MutationPlan(recovery.ToArray(), null,
                        new(202, new JsonObject { ["jobId"] = recoveredId, ["status"] = "queued", ["statusUrl"] = "/api/v1/jobs/" + recoveredId }, "/api/v1/jobs/" + recoveredId, null));
                }
                var version = await repository.ReadAsync(partition, doc.Value.Text("currentVersionId"), token) ?? throw new DomainException(404, "NOT_FOUND");
                if (version.Value.Text("jobId") != id) throw new DomainException(409, "INVALID_TRANSITION");
                List<RecordWrite> writes = [];
                var nextId = id;
                if (job.Value.Text("status") is "failed" or "needs_review" or "budget_stopped")
                {
                    var next = Jobs.Create(doc.Value, "ingestion", now); next["supersedesJobId"] = id;
                    nextId = next.Text("id"); writes.Add(RecordWrite.Create(next));
                    version.Value["jobId"] = nextId; version.Value["status"] = "queued"; doc.Value["status"] = "queued";
                    writes.Add(RecordWrite.Replace(version.Value, version.ETag)); writes.Add(RecordWrite.Replace(doc.Value, doc.ETag));
                }
                else
                {
                    job.Value["status"] = "queued"; job.Value["leaseUntil"] = null; job.Value["leaseOwner"] = null; job.Value["notificationState"] = "pending";
                    writes.Add(RecordWrite.Replace(job.Value, job.ETag));
                }
                return new MutationPlan(writes.ToArray(), null, new(202, new JsonObject { ["jobId"] = nextId, ["status"] = "queued", ["statusUrl"] = "/api/v1/jobs/" + nextId }, "/api/v1/jobs/" + nextId, null));
            }, token);
        }, ct);
        await notifications.TrySendAsync(actor.TenantId, response.Body!["jobId"]!.GetValue<string>(), ct);
        return response;
    }
}
