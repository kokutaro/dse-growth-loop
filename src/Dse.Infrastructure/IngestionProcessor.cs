using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Dse.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

public sealed class IngestionProcessor(IPrimaryRepository repository, IIngestionBlobs blobs, IIngestionQueue queue,
    OfficeExtractor extractor, IngestionOptions options, AccessFence fence, TimeProvider clock, ILogger<IngestionProcessor> logger, ProfilePipeline? profiles = null, JobNotifications? notifications = null)
{
    private static readonly TimeSpan Visibility = TimeSpan.FromSeconds(60);
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var delivery = await queue.ReceiveAsync(Visibility, ct);
        if (delivery is null) return false;
        QueueNotice notice;
        try
        {
            notice = JsonSerializer.Deserialize<QueueNotice>(delivery.Body) ?? throw new JsonException();
            if (!RecordJson.IsId(notice.TenantId) || !RecordJson.IsId(notice.JobId) || notice.Version < 1) throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            // Queue has no text-bearing business payload; malformed notifications cannot authorize a job.
            logger.LogWarning("Discarding invalid ingestion notification {MessageId}", delivery.Id);
            await queue.DeleteAsync(delivery, ct); return true;
        }
        var partition = RecordPartition.Catalog(notice.TenantId);
        var job = await repository.ReadAsync(partition, notice.JobId, ct);
        if (job is null || job.Value.Text("jobType") is not ("ingestion" or "enrichment" or "projection") || job.Value.Number("aggregateVersion") != notice.Version)
        { await queue.DeleteAsync(delivery, ct); return true; }
        if (job.Value.Text("status") is "ready" or "cancelled" or "needs_review" or "failed" or "budget_stopped")
        {
            if (job.Value.Text("status") == "failed") await queue.SendAsync(notice, ct, poison: true);
            await queue.DeleteAsync(delivery, ct); return true;
        }
        var now = clock.GetUtcNow();
        if (job.Value.Text("status") == "processing" && job.Value["leaseUntil"]!.Deserialize<DateTimeOffset>() > now) return true;
        if (job.Value["availableAt"]!.Deserialize<DateTimeOffset>() > now)
        {
            await queue.RenewAsync(delivery, job.Value["availableAt"]!.Deserialize<DateTimeOffset>() - now, ct); return true;
        }
        var owner = Guid.NewGuid().ToString("N");
        var exhausted = job.Value.Number("attempt") >= options.Attempts;
        job.Value["status"] = "processing"; job.Value["leaseOwner"] = owner; job.Value["leaseUntil"] = now + Visibility;
        if (!exhausted) job.Value["attempt"] = job.Value.Number("attempt") + 1;
        try { await repository.CommitAsync(partition, [RecordWrite.Replace(job.Value, job.ETag)], ct); }
        catch (StoreConflictException) { return true; }
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        processing.CancelAfter(TimeSpan.FromSeconds(options.ProcessingSeconds));
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(1);
        async Task<StoredRecord> Owned(CancellationToken token)
        {
            var current = await repository.ReadAsync(partition, notice.JobId, token) ?? throw new DomainException(409, "LEASE_LOST");
            if (current.Value.Text("leaseOwner") != owner || current.Value.Text("status") != "processing" || current.Value["leaseUntil"]!.Deserialize<DateTimeOffset>() <= clock.GetUtcNow())
                throw new DomainException(409, "LEASE_LOST");
            return current;
        }
        async Task Renew()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), renewalStop.Token);
                    await gate.WaitAsync(renewalStop.Token);
                    try
                    {
                        var current = await Owned(renewalStop.Token);
                        delivery = await queue.RenewAsync(delivery, Visibility, renewalStop.Token);
                        current.Value["leaseUntil"] = clock.GetUtcNow() + Visibility;
                        await repository.CommitAsync(partition, [RecordWrite.Replace(current.Value, current.ETag)], renewalStop.Token);
                    }
                    finally { gate.Release(); }
                }
            }
            catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
            catch (Exception) { processing.Cancel(); }
        }
        var renewal = Renew();
        bool delete = false;
        try
        {
            if (exhausted) throw new DomainException(400, "RETRY_EXHAUSTED");
            if (job.Value.Text("jobType") is "enrichment" or "projection")
            {
                if (profiles is null) throw new DomainException(422, "PIPELINE_UNAVAILABLE");
                var plan = await profiles.BuildAsync(job.Value, processing.Token);
                await gate.WaitAsync(processing.Token);
                try
                {
                    var current = await Owned(processing.Token);
                    current.Value["status"] = "ready"; current.Value["resultId"] = plan.ResultId;
                    current.Value["leaseUntil"] = null; current.Value["leaseOwner"] = null;
                    current.Value["lastErrorCode"] = null;
                    await profiles.CommitAsync(plan, [RecordWrite.Replace(current.Value, current.ETag)], processing.Token);
                }
                finally { gate.Release(); }
                await queue.DeleteAsync(delivery, ct);
                return true;
            }
            var doc = await repository.ReadAsync(partition, job.Value.Text("aggregateId"), processing.Token) ?? throw new DomainException(409, "SOURCE_REVOKED");
            var ver = await repository.ReadAsync(partition, doc.Value.Text("currentVersionId"), processing.Token) ?? throw new DomainException(409, "SOURCE_REVOKED");
            if (doc.Value.Number("sourceVersion") != notice.Version || ver.Value.Text("jobId") != notice.JobId) throw new DomainException(409, "SOURCE_REVOKED");
            if (ver.Value.Text("processorVersion") != IngestionOptions.ProcessorVersion) throw new DomainException(400, "PROCESSOR_VERSION_UNSUPPORTED");
            var bytes = await blobs.GetAsync("originals", ver.Value.Text("blobKey"), processing.Token);
            if (OfficeExtractor.Hash(bytes) != ver.Value.Text("sha256")) throw new DomainException(400, "HASH_MISMATCH");
            var mime = ver.Value.Text("mediaType");
            var extraction = await Task.Run(() => extractor.Extract(bytes, mime == OfficeExtractor.WordMime ? "source.docx" : "source.xlsx", mime, processing.Token), processing.Token);
            var extractedKey = $"{notice.TenantId}/{ver.Value.Text("id")}/{IngestionOptions.ProcessorVersion}.json";
            await blobs.PutAsync("extracted", extractedKey, JsonSerializer.SerializeToUtf8Bytes(extraction), processing.Token);
            // Stable IDs + job ETag in every batch: stale workers cannot write after takeover.
            // The active pointer stays null until the complete manifest is durable.
            foreach (var chunk in extraction.Spans.Select((span, index) => (span, index)).Chunk(20))
            {
                await gate.WaitAsync(processing.Token);
                try
                {
                    var current = await Owned(processing.Token); List<RecordWrite> writes = [];
                    foreach (var (span, index) in chunk)
                    {
                        var id = $"{ver.Value.Text("id")}-e{index:D8}";
                        var existing = await repository.ReadAsync(partition, id, processing.Token);
                        if (existing is not null)
                        {
                            if (existing.Value.Text("textHash") != OfficeExtractor.Hash(Encoding.UTF8.GetBytes(span.Text))) throw new DomainException(409, "BLOB_CONFLICT");
                            continue;
                        }
                        var evidence = RecordJson.New("evidenceSpan", id, notice.TenantId, doc.Value.Text("ownerId"), doc.Value.Strings("scopeIds"), now);
                        evidence["documentId"] = doc.Value.Text("id"); evidence["documentVersionId"] = ver.Value.Text("id"); evidence["sourceVersion"] = notice.Version;
                        evidence["location"] = new JsonObject { ["type"] = span.Type, ["label"] = span.Label };
                        evidence["text"] = span.Text; evidence["textHash"] = OfficeExtractor.Hash(Encoding.UTF8.GetBytes(span.Text)); evidence["validationState"] = "validated";
                        writes.Add(RecordWrite.Create(evidence));
                    }
                    writes.Add(RecordWrite.Replace(current.Value, current.ETag));
                    await repository.CommitAsync(partition, writes, processing.Token);
                }
                finally { gate.Release(); }
            }
            await gate.WaitAsync(processing.Token);
            try
            {
                await fence.RunAsync(notice.TenantId, async token =>
                {
                    var current = await Owned(token);
                    var latest = await repository.ReadAsync(partition, doc.Value.Text("id"), token) ?? throw new DomainException(409, "SOURCE_REVOKED");
                    var latestVersion = await repository.ReadAsync(partition, ver.Value.Text("id"), token) ?? throw new DomainException(409, "SOURCE_REVOKED");
                    if (latest.ETag != doc.ETag || latestVersion.ETag != ver.ETag || latest.Value.Number("sourceVersion") != notice.Version || latestVersion.Value.Text("jobId") != notice.JobId)
                        throw new DomainException(409, "SOURCE_REVOKED");
                    var status = extraction.Spans.Length == 0 ? "needs_review" : "ready";
                    latest.Value["status"] = status; latest.Value["activeVersion"] = extraction.Spans.Length == 0 ? null : notice.Version;
                    latest.Value["extractionState"] = "complete"; latest.Value["aiState"] = "not_started";
                    latestVersion.Value["status"] = status; latestVersion.Value["extractedBlobKey"] = extractedKey;
                    latestVersion.Value["warnings"] = RecordJson.Array(extraction.Warnings); latest.Value["warnings"] = RecordJson.Array(extraction.Warnings);
                    latestVersion.Value["evidenceCount"] = extraction.Spans.Length;
                    current.Value["status"] = status; current.Value["resultId"] = doc.Value.Text("id"); current.Value["leaseUntil"] = null; current.Value["leaseOwner"] = null;
                    current.Value["lastErrorCode"] = extraction.Spans.Length == 0 ? "EMPTY_DOCUMENT" : null;
                    List<RecordWrite> completion = [];
                    if (profiles is not null && extraction.Spans.Length > 0)
                    {
                        var next = Jobs.Create(latest.Value, "enrichment", clock.GetUtcNow());
                        latest.Value["enrichmentJobId"] = next.Text("id"); latest.Value["aiState"] = "queued";
                        completion.Add(RecordWrite.Create(next));
                    }
                    await repository.CommitAsync(partition, [RecordWrite.Replace(latest.Value, latest.ETag), RecordWrite.Replace(latestVersion.Value, latestVersion.ETag), RecordWrite.Replace(current.Value, current.ETag), .. completion], token);
                    return true;
                }, processing.Token);
                if (notifications is not null)
                {
                    var completed = await repository.ReadAsync(partition, doc.Value.Text("id"), ct);
                    if (completed!.Value.Text("enrichmentJobId") is { Length: > 0 } nextId) await notifications.TrySendAsync(notice.TenantId, nextId, ct);
                }
                delete = true;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await gate.WaitAsync(ct);
            try
            {
                var current = await Owned(ct);
                var invalidPackage = error is System.Xml.XmlException or InvalidDataException or FormatException or ArgumentException or OverflowException;
                var code = error is DomainException de ? de.Code : error is OperationCanceledException ? "PROCESSING_TIMEOUT" : invalidPackage ? "INVALID_PACKAGE" : "DEPENDENCY_UNAVAILABLE";
                var permanent = error is DomainException { Status: < 500 } || invalidPackage;
                var terminal = permanent || current.Value.Number("attempt") >= options.Attempts;
                var profileJob = current.Value.Text("jobType") is "enrichment" or "projection";
                current.Value["status"] = code == "SOURCE_REVOKED" ? "cancelled" : code == "AI_BUDGET_STOPPED" ? "budget_stopped" :
                    profileJob && permanent ? "needs_review" : terminal ? "failed" : "queued";
                current.Value["lastErrorCode"] = code; current.Value["leaseUntil"] = null; current.Value["leaseOwner"] = null;
                var delay = TimeSpan.FromSeconds(10 * Math.Pow(2, current.Value.Number("attempt") - 1));
                if (error is RequestFailedException request && request.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var retry) == true && int.TryParse(retry, out var seconds)) delay = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 604800));
                if (error is DependencyRetryException dependency && dependency.RetryAfter > delay) delay = dependency.RetryAfter;
                if (error is Microsoft.Azure.Cosmos.CosmosException cosmos && cosmos.RetryAfter > delay) delay = cosmos.RetryAfter.Value;
                current.Value["availableAt"] = clock.GetUtcNow() + delay;
                current.Value["notificationState"] = "pending";
                List<RecordWrite> failureWrites = [RecordWrite.Replace(current.Value, current.ETag)];
                var failedDoc = await repository.ReadAsync(partition, current.Value.Text("aggregateId"), ct);
                if (!profileJob && failedDoc is not null && failedDoc.Value.Number("sourceVersion") == notice.Version && code != "SOURCE_REVOKED")
                {
                    var failedVersion = await repository.ReadAsync(partition, failedDoc.Value.Text("currentVersionId"), ct);
                    if (failedVersion is not null && failedVersion.Value.Text("jobId") == notice.JobId)
                    {
                        failedDoc.Value["status"] = terminal ? "failed" : "queued";
                        failedVersion.Value["status"] = terminal ? "failed" : "queued";
                        failureWrites.Add(RecordWrite.Replace(failedDoc.Value, failedDoc.ETag));
                        failureWrites.Add(RecordWrite.Replace(failedVersion.Value, failedVersion.ETag));
                    }
                }
                await repository.CommitAsync(partition, failureWrites, ct);
                if (terminal) { if (current.Value.Text("status") == "failed") await queue.SendAsync(notice, ct, poison: true); delete = true; }
                else delivery = await queue.RenewAsync(delivery, delay, ct);
                logger.LogWarning("Ingestion {JobId} Attempt={Attempt} Code={Code}", notice.JobId, current.Value.Number("attempt"), code);
            }
            catch (Exception e) when (e is StoreConflictException or DomainException { Code: "LEASE_LOST" }) { }
            finally { gate.Release(); }
        }
        finally
        {
            renewalStop.Cancel(); await renewal;
        }
        if (delete) await queue.DeleteAsync(delivery, ct);
        return true;
    }
}

public sealed class IngestionBackgroundService(IngestionProcessor processor, ILogger<IngestionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (!await processor.RunOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Ingestion dependency unavailable; queue notification retained"); await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        }
    }
}
