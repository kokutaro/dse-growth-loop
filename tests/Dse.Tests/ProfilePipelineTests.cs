using System.Text.Json;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class ProfilePipelineTests
{
    [Fact]
    public void Dates_are_computed_and_overlapping_periods_are_counted_once()
    {
        var mechanical = ProfileExtraction.Period("機械設計 2019-01-01〜2023-01-01");
        var software = ProfileExtraction.Period("ソフトウェア 2023-01-01〜2026-01-01");
        Assert.Equal(48, mechanical.Months); Assert.Equal(36, software.Months);
        Assert.Equal(84, ProfileExtraction.TotalMonths([mechanical, software, software]));
        Assert.Null(ProfileExtraction.Period("経験7年ほど").Months);
        Assert.Null(ProfileExtraction.Period("2020-01-01〜2021-01-01 2022-01-01").Months);
        Assert.Null(ProfileExtraction.TotalMonths([mechanical, new(null, null, "unknown", null)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_office_evidence_produces_searchable_profile_and_keeps_secondary_low_confidence(bool excel)
    {
        using var h = new IngestionHarness();
        var accepted = await h.Upload(excel: excel); await h.Processor.RunOnceAsync(default);
        var ai = new PipelineAi(); var pipeline = Create(h, ai);
        var job = Jobs.Create((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), accepted.Body!["documentId"]!.GetValue<string>(), default))!.Value, "enrichment", h.Clock.GetUtcNow());
        var plan = await pipeline.BuildAsync(job, default);
        await pipeline.CommitAsync(plan, [], default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        Assert.NotEmpty(person.Value.Strings("experienceIds"));
        var units = (await h.Store.QueryAsync(RecordPartition.Catalog("demo"), "searchUnit", "entityId", "demo-person-01", 100, null, default)).Items.Select(x => x.Value.Deserialize<SearchUnit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        var valid = await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo, units, default);
        var result = Retriever.Search(valid, new([new("domain", "software")], [], []), IdentityResolver.Demo.Search);
        Assert.Single(result.Candidates); Assert.All(valid, u => Assert.Equal("ai_unconfirmed", u.ConfirmationState));
        Assert.Contains(valid, u => u.ExperienceSignals.Any(s => s.EvidenceState == "not_evaluated" && s.YesProbability is null));
        Assert.Contains(valid, u => u.ExperienceSignals.Any(s => s.EvidenceState == "unknown" && s.YesProbability is null));
        Assert.Contains(valid, u => u.ExperienceSignals.Any(s => s.EvidenceState == "supported" && s.YesProbability == 0));
        var calls = ai.Calls;
        var projection = Jobs.Create(person.Value, "projection", h.Clock.GetUtcNow());
        await pipeline.CommitAsync(await pipeline.BuildAsync(projection, default), [], default);
        Assert.Equal(calls, ai.Calls);
    }

    private static async Task<System.Text.Json.Nodes.JsonObject> Enrichment(IngestionHarness h, MutationResponse accepted)
    {
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), accepted.Body!["documentId"]!.GetValue<string>(), default))!;
        return Jobs.Create(doc.Value, "enrichment", h.Clock.GetUtcNow());
    }
    [Fact]
    public async Task Correction_survives_reimport_and_conflict_is_recorded_without_overwriting()
    {
        using var h = new IngestionHarness(); var ai = new PipelineAi(); var pipeline = Create(h, ai);
        var upload = await h.Upload(); await h.Processor.RunOnceAsync(default);
        await pipeline.CommitAsync(await pipeline.BuildAsync(await Enrichment(h, upload), default), [], default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var experience = person.Value.Strings("experienceIds")[0];
        var profiles = new ProfileService(h.Repo, new AccessFence(h.Repo), new IdempotencyExecutor(h.Repo, h.Clock));
        await profiles.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "correction-key-001", person.ETag,
            new("本人訂正", true, "correct", [new(experience, "本人のソフトウェア経験", new(null, null, "unknown", null))]), default);
        await pipeline.CommitAsync(await pipeline.BuildAsync(await Enrichment(h, upload), default), [], default);
        var corrected = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), experience, default))!.Value;
        Assert.Equal("本人のソフトウェア経験", corrected.Text("description"));
        Assert.Equal("conflict", corrected.Text("correctionState")); Assert.NotNull(corrected["pendingExtraction"]);
        Assert.Equal("本人訂正", (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value.Text("displayName"));
    }
    [Fact]
    public async Task Withdrawal_after_build_blocks_activation_and_old_job_replay()
    {
        using var h = new IngestionHarness(); var pipeline = Create(h, new()); var upload = await h.Upload(); await h.Processor.RunOnceAsync(default);
        var job = await Enrichment(h, upload); var plan = await pipeline.BuildAsync(job, default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var service = new ProfileService(h.Repo, new AccessFence(h.Repo), new IdempotencyExecutor(h.Repo, h.Clock));
        await service.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "withdrawal-key-01", person.ETag, new("撤回", true, "correct", [], "withdrawn"), default);
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => pipeline.CommitAsync(plan, [], default))).Code);
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => pipeline.BuildAsync(job, default))).Code);
        Assert.Null((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value["activeVersion"]);
    }
    [Fact]
    public async Task Interrupted_build_has_no_partially_published_profile()
    {
        using var h = new IngestionHarness(); var upload = await h.Upload(); await h.Processor.RunOnceAsync(default);
        var before = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var pipeline = Create(h, new()); var job = await Enrichment(h, upload);
        await pipeline.BuildAsync(job, default); // simulate process termination before publication
        Assert.Equal(before.ETag, (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.ETag);
        await pipeline.CommitAsync(await pipeline.BuildAsync(job, default), [], default);
        Assert.Equal(2, (await h.Repo.QueryAsync(RecordPartition.Catalog("demo"), "experience", "personId", "demo-person-01", 100, null, default)).Items.Length);
    }
    [Fact]
    public async Task Department_report_never_becomes_employee_experience()
    {
        using var h = new IngestionHarness();
        var upload = await h.Uploads.UploadAsync(IdentityResolver.Demo, "部署週報", "report", "x.docx", OfficeExtractor.WordMime, OfficeFixtures.Package(), "department-key-01", default);
        await h.Processor.RunOnceAsync(default); var pipeline = Create(h, new());
        var plan = await pipeline.BuildAsync(await Enrichment(h, upload), default); await pipeline.CommitAsync(plan, [], default);
        Assert.Empty((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value.Strings("experienceIds"));
        Assert.Equal("departmentActivity", (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), plan.ResultId, default))!.Value.Text("kind"));
    }
    [Fact]
    public async Task Ungrounded_AI_and_budget_stop_do_not_publish_values()
    {
        using var h = new IngestionHarness(); var upload = await h.Upload(); await h.Processor.RunOnceAsync(default);
        var ai = new PipelineAi { Ungrounded = true }; var pipeline = Create(h, ai); var job = await Enrichment(h, upload);
        Assert.Equal("UNGROUNDED_EXTRACTION", (await Assert.ThrowsAsync<DomainException>(() => pipeline.BuildAsync(job, default))).Code);
        ai.Ungrounded = false; ai.Stopped = true;
        Assert.Equal("AI_BUDGET_STOPPED", (await Assert.ThrowsAsync<DomainException>(() => pipeline.BuildAsync(job, default))).Code);
        Assert.Empty((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value.Strings("experienceIds"));
    }

    [Fact]
    public async Task Worker_chains_extraction_classification_and_projection_and_preserves_contracts()
    {
        using var h = new IngestionHarness(); var ai = new PipelineAi(); var pipeline = Create(h, ai);
        var worker = new IngestionProcessor(h.Repo, h.Blobs, h.Queue, new OfficeExtractor(new()), new(), new AccessFence(h.Repo), h.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestionProcessor>.Instance, pipeline, h.Notifications);
        var upload = await h.Upload(); await worker.RunOnceAsync(default); await worker.RunOnceAsync(default);
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), upload.Body!["documentId"]!.GetValue<string>(), default))!;
        var job = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), doc.Value.Text("enrichmentJobId"), default))!;
        Assert.Equal("ready", job.Value.Text("status")); Assert.Equal("complete", doc.Value.Text("aiState"));
        await h.Queue.SendAsync(new("demo", job.Value.Text("id"), 1), default); await worker.RunOnceAsync(default);
        Assert.Equal(1, (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), job.Value.Text("id"), default))!.Value.Number("attempt"));
        var all = new List<object>();
        foreach (var kind in new[] { "person", "experience", "classificationResult", "searchUnit", "job", "document" })
        {
            var rows = (await h.Store.QueryAsync(RecordPartition.Catalog("demo"), kind, null, null, 100, null, default)).Items;
            all.AddRange(rows.Where(r => r.Value.Text("processingMode") == "fixture" || r.Value.Text("jobType") == "enrichment" || r.Value.Text("kind") == "experience")
                .Select(r => (object)new { schema = RecordJson.ContractKinds[r.Value.Text("kind")], value = r.Value }));
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "DseGrowthLoop.slnx"))) root = root.Parent;
        var output = Path.Combine(root!.FullName, "tests/Dse.Tests/TestResults"); Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "profile-contract-fixture.json"), JsonSerializer.Serialize(all));
    }
    [Fact]
    public async Task Worker_budget_stop_is_visible_and_explicit_retry_recovers()
    {
        using var h = new IngestionHarness(); var ai = new PipelineAi { Stopped = true }; var pipeline = Create(h, ai);
        var worker = new IngestionProcessor(h.Repo, h.Blobs, h.Queue, new OfficeExtractor(new()), new(), new AccessFence(h.Repo), h.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestionProcessor>.Instance, pipeline, h.Notifications);
        var upload = await h.Upload(); await worker.RunOnceAsync(default); await worker.RunOnceAsync(default);
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), upload.Body!["documentId"]!.GetValue<string>(), default))!;
        var job = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), doc.Value.Text("enrichmentJobId"), default))!;
        Assert.Equal("budget_stopped", job.Value.Text("status"));
        ai.Stopped = false;
        var retry = await h.Retry.RetryAsync(IdentityResolver.Demo, job.Value.Text("id"), job.ETag, "retry-profile-0001", "予算再開後", default);
        await worker.RunOnceAsync(default);
        Assert.Equal("ready", (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), retry.Body!["jobId"]!.GetValue<string>(), default))!.Value.Text("status"));
    }
    [Fact]
    public async Task New_upload_blocks_older_same_title_job_and_removes_obsolete_uncorrected_fragments()
    {
        using var h = new IngestionHarness(); var pipeline = Create(h, new());
        var first = await h.Upload(); await h.Processor.RunOnceAsync(default); var oldJob = await Enrichment(h, first);
        await pipeline.CommitAsync(await pipeline.BuildAsync(oldJob, default), [], default);
        var second = await h.Upload("new-document-0001", OfficeFixtures.Package(body: "<w:p><w:r><w:t>新しいソフトウェア職歴</w:t></w:r></w:p>"));
        await h.Processor.RunOnceAsync(default);
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => pipeline.BuildAsync(oldJob, default))).Code);
        await pipeline.CommitAsync(await pipeline.BuildAsync(await Enrichment(h, second), default), [], default);
        Assert.Single((await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value.Strings("experienceIds"));
    }

    [Fact]
    public async Task Spreadsheet_row_dates_preserve_cell_evidence_and_compute_four_plus_three_years()
    {
        using var h = new IngestionHarness();
        var sheet = "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>機械設計</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>2019-01-01</t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>2023-01-01</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>ソフトウェア開発</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>2023-01-01</t></is></c><c r=\"C2\" t=\"inlineStr\"><is><t>2026-01-01</t></is></c></row></sheetData></worksheet>";
        var upload = await h.Upload(bytes: OfficeFixtures.Package(true, sheet), excel: true); await h.Processor.RunOnceAsync(default);
        var pipeline = Create(h, new()); await pipeline.CommitAsync(await pipeline.BuildAsync(await Enrichment(h, upload), default), [], default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value;
        Assert.Equal(84, person.Number("totalExperienceMonths")); Assert.Equal(2, person.Strings("experienceIds").Length);
        foreach (var id in person.Strings("experienceIds"))
        {
            var row = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), id, default))!.Value;
            Assert.Equal(3, row["evidenceRefs"]!.AsArray().Count);
            Assert.Contains(row["period"]!["months"]!.GetValue<int>(), new[] { 48, 36 });
        }
    }

    internal static ProfilePipeline Create(IngestionHarness h, PipelineAi ai) => new(h.Repo, h.Store, new AccessFence(h.Repo), ai, ai,
        new IdentityResolver(new ConfigurationBuilder().Build()), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock" }).Build(), h.Clock);
}

public sealed class PipelineAi : ITextStructurer, IDecisionProvider
{
    public int Calls { get; private set; }
    public bool Ungrounded { get; set; }
    public bool Stopped { get; set; }
    public Task<AiResult<StructuredText>> StructureAsync(AiContext c, string text, CancellationToken ct) => Task.FromResult(new AiResult<StructuredText>(AiStatus.Succeeded, new([new(Ungrounded ? "捏造した主張" : text, c.Evidence.Select(x => x.Id).ToArray())]), "fake-structure", null));
    public Task<AiResult<Decisions>> DecideAsync(AiContext c, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct)
    {
        Calls++;
        if (Stopped) return Task.FromResult(new AiResult<Decisions>(AiStatus.BudgetStopped, null, "fake-stopped", AiFailure.Stopped));
        return Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded, new(choices.ToDictionary(q => q.Id, q => new ChoiceAnswer("mechanical", q.Options.Keys.ToDictionary(k => k, k => k == "mechanical" ? .7 : k == "software" ? .3 : 0), .05)),
            nouls.ToDictionary(q => q.Id, q => q.Id == "software" ? new NoulAnswer(NoulState.Supported, 0) : q.Id == "mechanical" ? new NoulAnswer(NoulState.Unknown, null) : new NoulAnswer(NoulState.NotEvaluated, null))), "fake-decision", null));
    }
}
