using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dse.Tests;

public static class OfficeFixtures
{
    public static byte[] Package(bool excel = false, string? body = null, Dictionary<string, string>? extra = null)
    {
        var main = excel ? "xl/workbook.xml" : "word/document.xml";
        var content = excel ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/{main}\" ContentType=\"{content}\"/></Types>",
            ["_rels/.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"{main}\"/></Relationships>",
        };
        if (excel)
        {
            parts[main] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"架空週報\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>";
            parts["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"s1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>";
            parts["xl/worksheets/sheet1.xml"] = body ?? "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>架空セル</t></is></c><c r=\"B1\"><f>1+2</f><v>3</v></c><c r=\"C1\"/><c r=\"D1\"><f>4+5</f></c><c r=\"E1\" s=\"1\"><v>46100</v></c></row></sheetData><mergeCells><mergeCell ref=\"A1:A2\"/></mergeCells></worksheet>";
            parts["xl/styles.xml"] = "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>";
        }
        else parts[main] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + (body ?? "<w:p><w:r><w:t>架空の職歴</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>架空の表</w:t></w:r></w:p></w:tc></w:tr></w:tbl>") + "</w:body></w:document>";
        foreach (var (key, value) in extra ?? []) parts[key] = value;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (key, value) in parts) { var entry = zip.CreateEntry(key, CompressionLevel.NoCompression); entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(value); }
        return stream.ToArray();
    }
}
public sealed class OfficeExtractionTests
{
    [Fact]
    public void Zip_bomb_compression_ratio_is_rejected_before_expansion()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        { using var entry = zip.CreateEntry("bomb.xml", CompressionLevel.SmallestSize).Open(); entry.Write(new byte[200000]); }
        Assert.Equal("PAYLOAD_TOO_LARGE", Assert.Throws<DomainException>(() => Extractor().Validate(bytes.ToArray(), "x.docx", OfficeExtractor.WordMime, default)).Code);
    }
    [Fact]
    public void Encryption_flag_is_not_accepted_as_plain_office()
    {
        var bytes = OfficeFixtures.Package();
        for (var i = 0; i < bytes.Length - 10; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 3 && bytes[i + 3] == 4) bytes[i + 6] |= 1;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2) bytes[i + 8] |= 1;
        }
        Assert.Throws<DomainException>(() => Extractor().Validate(bytes, "x.docx", OfficeExtractor.WordMime, default));
    }

    private static OfficeExtractor Extractor(IngestionOptions? options = null) => new(options ?? new());
    [Fact]
    public void Word_paragraph_and_table_positions_match_original()
    {
        var result = Extractor().Extract(OfficeFixtures.Package(), "sample.docx", OfficeExtractor.WordMime, default);
        Assert.Equal(["架空の職歴", "架空の表"], result.Spans.Select(s => s.Text));
        Assert.Contains("paragraph 1", result.Spans[0].Label);
        Assert.Contains("table 1 row 1 cell 1", result.Spans[1].Label);
    }
    [Fact]
    public void Excel_keeps_cell_range_and_cached_value_and_skips_empty_cells()
    {
        var result = Extractor().Extract(OfficeFixtures.Package(true), "sample.xlsx", OfficeExtractor.ExcelMime, default);
        Assert.Equal(3, result.Spans.Length); Assert.EndsWith("架空週報!A1:A2", result.Spans[0].Label);
        Assert.Equal("3", result.Spans[1].Text); Assert.Matches("^2026-", result.Spans[2].Text);
        Assert.Contains("FORMULA_CACHE_MISSING", result.Warnings); Assert.Contains("FORMULA_CACHED_VALUES_ONLY", result.Warnings);
    }
    [Fact]
    public void Empty_documents_and_images_have_explicit_warnings()
    {
        var result = Extractor().Extract(OfficeFixtures.Package(body: "", extra: new() { ["word/media/image1.png"] = "image" }), "sample.docx", OfficeExtractor.WordMime, default);
        Assert.Empty(result.Spans); Assert.Contains("EMPTY_DOCUMENT", result.Warnings); Assert.Contains("IMAGES_NOT_OCR", result.Warnings);
    }
    [Theory]
    [InlineData("../outside.xml", "x")]
    [InlineData("word/vbaProject.bin", "code")]
    [InlineData("word/embeddings/object.bin", "code")]
    [InlineData("word/document.xml", "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><x>&x;</x>")]
    public void Dangerous_packages_are_rejected(string path, string content) => Assert.Throws<DomainException>(() => Extractor().Validate(OfficeFixtures.Package(extra: new() { [path] = content }), "x.docx", OfficeExtractor.WordMime, default));
    [Fact]
    public void Invalid_format_mime_zip_size_and_entry_limits_are_rejected()
    {
        var bytes = OfficeFixtures.Package();
        Assert.Throws<DomainException>(() => Extractor().Validate(bytes, "x.docm", OfficeExtractor.WordMime, default));
        Assert.Throws<DomainException>(() => Extractor().Validate(bytes, "x.docx", OfficeExtractor.ExcelMime, default));
        Assert.Throws<DomainException>(() => Extractor().Validate([1, 2, 3], "x.docx", OfficeExtractor.WordMime, default));
        Assert.Equal(413, Assert.Throws<DomainException>(() => Extractor(new() { OriginalBytes = 1 }).Validate(bytes, "x.docx", OfficeExtractor.WordMime, default)).Status);
        Assert.Equal(413, Assert.Throws<DomainException>(() => Extractor(new() { ExpandedBytes = 10 }).Validate(bytes, "x.docx", OfficeExtractor.WordMime, default)).Status);
        Assert.Equal(413, Assert.Throws<DomainException>(() => Extractor(new() { Entries = 1 }).Validate(bytes, "x.docx", OfficeExtractor.WordMime, default)).Status);
    }
    [Fact]
    public void External_links_are_not_followed_and_unicode_chunks_preserve_text()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 8001));
        var bytes = OfficeFixtures.Package(body: $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>", extra: new() { ["word/_rels/document.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"x\" TargetMode=\"External\" Target=\"https://invalid.example/private\"/></Relationships>" });
        var result = Extractor().Extract(bytes, "x.docx", OfficeExtractor.WordMime, default);
        Assert.Equal(2, result.Spans.Length); Assert.Equal(text, string.Concat(result.Spans.Select(x => x.Text))); Assert.Contains("EXTERNAL_LINKS_IGNORED", result.Warnings);
    }
}

public sealed class IngestionClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
public sealed class FaultQueue(IIngestionQueue inner) : IIngestionQueue
{
    public bool FailSend { get; set; }
    public int PoisonCount { get; private set; }
    public TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task SendAsync(QueueNotice notice, CancellationToken ct, bool poison = false)
    { if (FailSend) throw new IOException(); if (poison) PoisonCount++; return inner.SendAsync(notice, ct, poison); }
    public Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility, CancellationToken ct) => inner.ReceiveAsync(visibility, ct);
    public async Task<QueueDelivery> RenewAsync(QueueDelivery delivery, TimeSpan visibility, CancellationToken ct) { var result = await inner.RenewAsync(delivery, visibility, ct); Renewed.TrySetResult(); return result; }
    public Task DeleteAsync(QueueDelivery delivery, CancellationToken ct) => inner.DeleteAsync(delivery, ct);
}
public sealed class IngestionHarness : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dse-test-" + Guid.NewGuid().ToString("N"));
    public IPrimaryRepository Repo { get; }
    public IRecordStore Store { get; }
    public IngestionClock Clock { get; } = new();
    public IIngestionBlobs Blobs { get; }
    public FaultQueue Queue { get; }
    public DocumentUploads Uploads { get; }
    public JobNotifications Notifications { get; }
    public IngestionProcessor Processor { get; }
    public IngestionRetry Retry { get; }
    public IngestionHarness()
    {
        var files = new LocalIngestionFiles(Path);
        var store = new LocalRecordStore(files); Store = store; Repo = new PrimaryRepository(store);
        DemoRecords.SeedAsync(store, "demo", default).GetAwaiter().GetResult();
        Blobs = new LocalIngestionBlobs(files); Queue = new(new LocalIngestionQueue(files, Clock));
        var fence = new AccessFence(Repo); var idempotency = new IdempotencyExecutor(Repo, Clock);
        var options = new IngestionOptions(); var extractor = new OfficeExtractor(options);
        Notifications = new(Repo, Queue, Clock);
        Uploads = new(Repo, fence, idempotency, Blobs, extractor, options, Notifications, Clock);
        Processor = new(Repo, Blobs, Queue, extractor, options, fence, Clock, NullLogger<IngestionProcessor>.Instance);
        Retry = new(Repo, fence, idempotency, Notifications, Clock);
    }
    public Task<MutationResponse> Upload(string key = "upload-key-00001", byte[]? bytes = null, bool excel = false) => Uploads.UploadAsync(IdentityResolver.Demo, "架空資料", "profile", excel ? "x.xlsx" : "x.docx", excel ? OfficeExtractor.ExcelMime : OfficeExtractor.WordMime, bytes ?? OfficeFixtures.Package(excel), key, default);
    public async Task<StoredRecord> Job(MutationResponse response) => (await Repo.ReadAsync(RecordPartition.Catalog("demo"), response.Body!["jobId"]!.GetValue<string>(), default))!;
    public void Dispose() => Directory.Delete(Path, true);
}
public sealed class IngestionFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_extracts_once_and_replays_across_repository_restart(bool excel)
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(excel: excel);
        Assert.Equal(202, accepted.Status); var job = await h.Job(accepted);
        await h.Queue.SendAsync(new("demo", job.Value.Text("id"), 1), default);
        await h.Processor.RunOnceAsync(default); await h.Processor.RunOnceAsync(default);
        Assert.Equal("ready", (await h.Job(accepted)).Value.Text("status"));
        var docId = accepted.Body!["documentId"]!.GetValue<string>();
        var restarted = new LocalRecordStore(new LocalIngestionFiles(h.Path));
        var doc = await restarted.ReadAsync(RecordPartition.Catalog("demo"), docId, default);
        Assert.Equal(1, doc!.Value.Number("activeVersion")); Assert.Equal("not_started", doc.Value.Text("aiState"));
        var page = await restarted.QueryAsync(RecordPartition.Catalog("demo"), "evidenceSpan", "documentId", docId, 100, null, default);
        Assert.Equal(excel ? 3 : 2, page.Items.Length);
        var replay = await h.Upload(excel: excel); Assert.True(JsonNode.DeepEquals(accepted.Body, replay.Body));
        var duplicate = await h.Upload("different-key-001", excel: excel); Assert.True(JsonNode.DeepEquals(accepted.Body, duplicate.Body));
        Assert.Equal(1, (await h.Job(accepted)).Value.Number("attempt"));
    }
    [Fact]
    public async Task Notify_failure_remains_durable_and_manual_reconcile_recovers()
    {
        using var h = new IngestionHarness(); h.Queue.FailSend = true;
        var accepted = await h.Upload(); Assert.Equal("pending", (await h.Job(accepted)).Value.Text("notificationState"));
        h.Queue.FailSend = false; Assert.Equal(1, await h.Notifications.ReconcileAsync("demo", default));
        await h.Processor.RunOnceAsync(default); Assert.Equal("ready", (await h.Job(accepted)).Value.Text("status"));
    }
    [Fact]
    public async Task Unexpired_lease_blocks_competitor_and_abandoned_work_resumes_after_expiry()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(); var job = await h.Job(accepted);
        job.Value["status"] = "processing"; job.Value["leaseUntil"] = h.Clock.GetUtcNow().AddSeconds(60); job.Value["leaseOwner"] = "stopped-worker"; job.Value["attempt"] = 1;
        await h.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(job.Value, job.ETag)], default);
        await h.Processor.RunOnceAsync(default); Assert.Equal("processing", (await h.Job(accepted)).Value.Text("status"));
        h.Clock.Advance(61); await h.Processor.RunOnceAsync(default);
        Assert.Equal("ready", (await h.Job(accepted)).Value.Text("status")); Assert.Equal(2, (await h.Job(accepted)).Value.Number("attempt"));
    }
    [Fact]
    public async Task Three_interrupted_attempts_poison_then_explicit_retry_creates_new_job()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(); var job = await h.Job(accepted);
        job.Value["status"] = "processing"; job.Value["leaseUntil"] = h.Clock.GetUtcNow().AddSeconds(-1); job.Value["leaseOwner"] = "stopped"; job.Value["attempt"] = 3;
        await h.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(job.Value, job.ETag)], default);
        await h.Processor.RunOnceAsync(default); job = await h.Job(accepted);
        Assert.Equal("failed", job.Value.Text("status")); Assert.Equal(1, h.Queue.PoisonCount);
        var next = await h.Retry.RetryAsync(IdentityResolver.Demo, job.Value.Text("id"), job.ETag, "retry-key-000001", "明示再実行", default);
        Assert.NotEqual(job.Value.Text("id"), next.Body!["jobId"]!.GetValue<string>());
        await h.Processor.RunOnceAsync(default);
        Assert.Equal("ready", (await h.Job(next)).Value.Text("status")); Assert.Equal(job.Value.Text("id"), (await h.Job(next)).Value.Text("supersedesJobId"));
    }
    [Fact]
    public async Task Old_version_notification_cannot_activate_document()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(); var id = accepted.Body!["documentId"]!.GetValue<string>();
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), id, default))!; doc.Value["sourceVersion"] = 2;
        await h.Repo.CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(doc.Value, doc.ETag)], default);
        await h.Processor.RunOnceAsync(default); Assert.Equal("cancelled", (await h.Job(accepted)).Value.Text("status"));
        Assert.Null((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), id, default))!.Value["activeVersion"]);
    }
    [Fact]
    public async Task Empty_doc_needs_review_and_changed_idempotent_input_conflicts()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(bytes: OfficeFixtures.Package(body: ""));
        await h.Processor.RunOnceAsync(default); Assert.Equal("needs_review", (await h.Job(accepted)).Value.Text("status"));
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<DomainException>(() => h.Upload())).Code);
    }
    [Fact]
    public async Task Unauthorized_upload_is_rejected_before_blob_or_job_creation()
    {
        using var h = new IngestionHarness();
        await Assert.ThrowsAsync<DomainException>(() => h.Uploads.UploadAsync(IdentityResolver.Demo with { PersonId = "demo-person-02" }, "架空", "profile", "x.docx", OfficeExtractor.WordMime, OfficeFixtures.Package(), "upload-key-00001", default));
        Assert.Empty((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"), "job", null, null, 100, null, default)).Items);
    }
}

public sealed class DocumentUploadApiTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b => { b.UseEnvironment("Development"); b.UseSetting("Data:Mode", "Demo"); });
    private static HttpRequestMessage Upload(byte[] bytes, string? forbidden = null)
    {
        var form = new MultipartFormDataContent(); form.Add(new StringContent("架空文書"), "title"); form.Add(new StringContent("profile"), "documentType");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue(OfficeExtractor.WordMime); form.Add(file, "file", "sample.docx");
        if (forbidden is not null) form.Add(new StringContent("other"), forbidden);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/documents") { Content = form }; request.Headers.Add("Idempotency-Key", "api-upload-key-01"); return request;
    }
    [Fact]
    public async Task Multipart_acceptance_status_evidence_and_authorized_download()
    {
        using var factory = Factory(); using var client = factory.CreateClient(); var bytes = OfficeFixtures.Package();
        var response = await client.SendAsync(Upload(bytes)); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        await factory.Services.GetRequiredService<IngestionProcessor>().RunOnceAsync(default);
        var id = accepted.Text("documentId");
        var evidence = await client.GetAsync($"/api/v1/documents/{id}/evidence"); evidence.EnsureSuccessStatusCode();
        Assert.Contains("paragraph", await evidence.Content.ReadAsStringAsync());
        var original = await client.GetAsync($"/api/v1/documents/{id}/original"); Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync());
        Assert.Contains("attachment", original.Content.Headers.ContentDisposition!.ToString());
        var doc = (await factory.Services.GetRequiredService<IPrimaryRepository>().ReadAsync(RecordPartition.Catalog("demo"), id, default))!;
        doc.Value["scopeIds"] = RecordJson.Array(["private"]);
        await factory.Services.GetRequiredService<IPrimaryRepository>().CommitAsync(RecordPartition.Catalog("demo"), [RecordWrite.Replace(doc.Value, doc.ETag)], default);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/documents/{id}/original")).StatusCode);
    }
    [Theory]
    [InlineData("ownerId")]
    [InlineData("scopeIds")]
    [InlineData("personId")]
    public async Task Authorization_metadata_cannot_be_supplied(string field)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Upload(OfficeFixtures.Package(), field))).StatusCode);
    }
    [Fact]
    public async Task Bad_zip_and_oversized_request_are_rejected()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Upload([1, 2, 3]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(Upload(new byte[11 * 1024 * 1024]))).StatusCode);
    }
}

public sealed class InterruptingRepository(IPrimaryRepository inner, CancellationTokenSource stop) : IPrimaryRepository
{
    public Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct) => inner.ReadAsync(partition, id, ct);
    public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? cursor, CancellationToken ct) => inner.QueryAsync(partition, kind, field, parent, size, cursor, ct);
    public async Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct)
    {
        await inner.CommitAsync(partition, writes, ct);
        if (writes.Any(w => w.Value.Text("kind") == "evidenceSpan")) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
    }
}
public sealed class TransientBlobs(IIngestionBlobs inner) : IIngestionBlobs
{
    public bool FailRead { get; set; } = true;
    public Task? ReadGate { get; set; }
    public async Task<byte[]> GetAsync(string container, string key, CancellationToken ct) { if (FailRead) throw new IOException("simulated"); if (ReadGate is not null) await ReadGate.WaitAsync(ct); return await inner.GetAsync(container, key, ct); }
    public Task PutAsync(string container, string key, byte[] bytes, CancellationToken ct) => inner.PutAsync(container, key, bytes, ct);
    public IAsyncEnumerable<BlobEntry> ListAsync(string container, string prefix, CancellationToken ct) => inner.ListAsync(container, prefix, ct);
    public Task DeleteAsync(string container, string key, string etag, CancellationToken ct) => inner.DeleteAsync(container, key, etag, ct);
}
public sealed class IngestionRecoveryTests
{
    [Fact]
    public async Task Slow_processing_renews_queue_receipt_and_database_lease_before_completion()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blobs = new TransientBlobs(h.Blobs) { FailRead = false, ReadGate = release.Task };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var processing = Processor(h, blobs: blobs).RunOnceAsync(timeout.Token);
        try
        {
            await h.Queue.Renewed.Task.WaitAsync(timeout.Token);
            var job = await h.Job(accepted); Assert.Equal("processing", job.Value.Text("status"));
            Assert.NotNull(job.Value["leaseUntil"]);
        }
        finally { release.TrySetResult(); }
        await processing;
        Assert.Equal("ready", (await h.Job(accepted)).Value.Text("status"));
        Assert.Null(await h.Queue.ReceiveAsync(TimeSpan.FromSeconds(60), default));
    }

    [Fact]
    public async Task Export_real_ingestion_records_for_shared_schema_validation()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(); await h.Processor.RunOnceAsync(default);
        var documentId = accepted.Body!["documentId"]!.GetValue<string>();
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<LocalRecordSnapshot>(await File.ReadAllTextAsync(System.IO.Path.Combine(h.Path, "records.json")))!;
        var rows = snapshot.Records.Select(x => x.Record.Value).Where(x => x.Text("id") == documentId || x.Text("documentId") == documentId || x.Text("aggregateId") == documentId)
            .Where(x => RecordJson.ContractKinds.ContainsKey(x.Text("kind"))).Select(x => new { schema = RecordJson.ContractKinds[x.Text("kind")], value = x }).ToArray();
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "DseGrowthLoop.slnx"))) root = root.Parent;
        Assert.NotNull(root); var output = System.IO.Path.Combine(root.FullName, "tests/Dse.Tests/TestResults"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(System.IO.Path.Combine(output, "ingestion-contract-fixture.json"), System.Text.Json.JsonSerializer.Serialize(rows));
    }

    private static IngestionProcessor Processor(IngestionHarness h, IPrimaryRepository? repo = null, IIngestionBlobs? blobs = null) => new(repo ?? h.Repo, blobs ?? h.Blobs, h.Queue, new OfficeExtractor(new()), new(), new AccessFence(repo ?? h.Repo), h.Clock, NullLogger<IngestionProcessor>.Instance);
    [Fact]
    public async Task Stop_after_first_evidence_batch_keeps_output_hidden_and_restart_finishes_without_duplicates()
    {
        using var h = new IngestionHarness();
        var bytes = OfficeFixtures.Package(body: string.Concat(Enumerable.Range(0, 45).Select(i => $"<w:p><w:r><w:t>架空段落{i}</w:t></w:r></w:p>")));
        var accepted = await h.Upload(bytes: bytes); var id = accepted.Body!["documentId"]!.GetValue<string>();
        using var stop = new CancellationTokenSource(); var interrupted = new InterruptingRepository(h.Repo, stop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor(h, interrupted).RunOnceAsync(stop.Token));
        Assert.Null((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), id, default))!.Value["activeVersion"]);
        Assert.Equal(20, (await h.Repo.QueryAsync(RecordPartition.Catalog("demo"), "evidenceSpan", "documentId", id, 100, null, default)).Items.Length);
        h.Clock.Advance(61); await h.Processor.RunOnceAsync(default);
        Assert.Equal("ready", (await h.Job(accepted)).Value.Text("status"));
        Assert.Equal(45, (await h.Repo.QueryAsync(RecordPartition.Catalog("demo"), "evidenceSpan", "documentId", id, 100, null, default)).Items.Length);
    }
    [Fact]
    public async Task Transient_dependency_failure_obeys_backoff_and_poison_limit()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload();
        var processor = Processor(h, blobs: new TransientBlobs(h.Blobs));
        await processor.RunOnceAsync(default); Assert.Equal("queued", (await h.Job(accepted)).Value.Text("status"));
        Assert.False(await processor.RunOnceAsync(default));
        h.Clock.Advance(11); await processor.RunOnceAsync(default); h.Clock.Advance(21); await processor.RunOnceAsync(default);
        Assert.Equal("failed", (await h.Job(accepted)).Value.Text("status")); Assert.Equal(3, (await h.Job(accepted)).Value.Number("attempt")); Assert.Equal(1, h.Queue.PoisonCount);
    }
    [Fact]
    public async Task Concurrent_consumers_and_renewed_pop_receipts_do_not_duplicate_results()
    {
        using var h = new IngestionHarness(); var accepted = await h.Upload(); var job = await h.Job(accepted);
        await h.Queue.SendAsync(new("demo", job.Value.Text("id"), 1), default);
        await Task.WhenAll(h.Processor.RunOnceAsync(default), Processor(h).RunOnceAsync(default));
        Assert.Equal("ready", (await h.Job(accepted)).Value.Text("status")); Assert.Equal(1, (await h.Job(accepted)).Value.Number("attempt"));
        h.Clock.Advance(61); while (await h.Processor.RunOnceAsync(default)) { }
        await h.Queue.SendAsync(new("demo", job.Value.Text("id"), 1), default);
        var delivery = (await h.Queue.ReceiveAsync(TimeSpan.FromSeconds(60), default))!;
        var renewed = await h.Queue.RenewAsync(delivery, TimeSpan.FromSeconds(60), default);
        Assert.NotEqual(delivery.Receipt, renewed.Receipt);
        await Assert.ThrowsAsync<DomainException>(() => h.Queue.RenewAsync(delivery, TimeSpan.FromSeconds(60), default));
        await h.Queue.DeleteAsync(renewed, default); Assert.Null(await h.Queue.ReceiveAsync(TimeSpan.FromSeconds(60), default));
    }
    [Fact]
    public async Task Cleanup_only_selects_old_unreferenced_staging_and_requires_explicit_keys()
    {
        using var h = new IngestionHarness(); await h.Upload();
        const string orphan = "demo/staging/orphan.docx"; await h.Blobs.PutAsync("originals", orphan, [1, 2], default);
        await h.Blobs.PutAsync("originals", "other/staging/orphan.docx", [1, 2], default);
        h.Clock.Advance(86401);
        var cleanup = new StagingCleanup(h.Repo, h.Blobs, h.Clock);
        Assert.Equal([orphan], await cleanup.RunAsync("demo", false, [], default));
        await Assert.ThrowsAsync<ArgumentException>(() => cleanup.RunAsync("demo", true, [], default));
        Assert.Equal([orphan], await cleanup.RunAsync("demo", true, [orphan], default));
        Assert.Empty(await cleanup.RunAsync("demo", false, [], default));
    }
}
