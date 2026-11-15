using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

/// <summary>本人確認済み項目の通常版更新における根拠引継ぎの境界を検証する。</summary>
public sealed class ConfirmedEvidenceRevisionTests
{
    private const string Text = "Gitで架空の回帰試験を実施した。";
    private static readonly RecordPartition Partition = RecordPartition.Catalog("demo");
    private sealed class ReadFault(IPrimaryRepository inner, string target, Action<JsonObject> change) : IPrimaryRepository
    {
        public async Task<StoredRecord?> ReadAsync(RecordPartition p, string id, CancellationToken ct)
        {
            var row = await inner.ReadAsync(p, id, ct);
            if (id != target || row is null) return row;
            var value = row.Value.Copy(); change(value); return new(value, row.ETag);
        }
        public Task<RecordPage> QueryAsync(RecordPartition p, string k, string? f, string? parent, int n, string? token, CancellationToken ct) => inner.QueryAsync(p, k, f, parent, n, token, ct);
        public Task CommitAsync(RecordPartition p, IReadOnlyList<RecordWrite> writes, CancellationToken ct) => inner.CommitAsync(p, writes, ct);
    }

    private sealed class RevisionCase : IDisposable
    {
        public IngestionHarness Harness { get; } = new();
        public ProfilePipeline Pipeline { get; private set; } = null!;
        public JsonObject Job { get; private set; } = null!;
        public string DocumentId { get; private set; } = "";
        public string OldDocumentId { get; private set; } = "";
        public string ExperienceId { get; private set; } = "";
        public EvidenceReference OldReference { get; private set; } = null!;
        public string AuditId => "audit-evidence-" + OfficeExtractor.Hash(Encoding.UTF8.GetBytes(Job.Text("id") + ":" + ExperienceId + ":" + DocumentId + "-v2"));
        public static async Task<RevisionCase> Create(string action = "confirm", string? first = null, bool otherDocument = false)
        {
            var c = new RevisionCase();
            try
            {
                var h = c.Harness; c.Pipeline = ProfilePipelineTests.Create(h, new());
                var upload = await h.Upload(bytes: Package(Text));
                await h.Processor.RunOnceAsync(default);
                c.DocumentId = c.OldDocumentId = upload.Body!["documentId"]!.GetValue<string>();
                var document = await c.Read(c.DocumentId);
                await c.Pipeline.CommitAsync(await c.Pipeline.BuildAsync(Jobs.Create(document.Value, "enrichment", h.Clock.GetUtcNow()), default), [], default);
                var person = await c.Read("demo-person-01"); c.ExperienceId = Assert.Single(person.Value.Strings("experienceIds"));
                var experience = await c.Read(c.ExperienceId);
                c.OldReference = Assert.Single(experience.Value["evidenceRefs"]!.Deserialize<EvidenceReference[]>(ProjectionRules.Json)!);
                await new ProfileService(h.Repo, new(h.Repo), new(h.Repo, h.Clock)).UpdateAsync(IdentityResolver.Demo, "demo-person-01", Guid.NewGuid().ToString("N"), person.ETag,
                    new(person.Value.Text("displayName"), true, action, [new(c.ExperienceId, Text, experience.Value["period"]!.Deserialize<ExperiencePeriod>(ProjectionRules.Json)!)]), default);
                // This harness publishes plans explicitly. Drain only its local notices before extraction.
                for (var i = 0; i < 20; i++)
                {
                    var notice = await h.Queue.ReceiveAsync(TimeSpan.FromSeconds(30), default);
                    if (notice is null) break;
                    await h.Queue.DeleteAsync(notice, default);
                }
                var bytes = Package(first ?? Text, "追加の架空資料。");
                if (otherDocument)
                {
                    upload = await h.Upload(Guid.NewGuid().ToString("N"), bytes);
                    c.DocumentId = upload.Body!["documentId"]!.GetValue<string>();
                    Assert.NotEqual(c.OldDocumentId, c.DocumentId);
                }
                else
                {
                    document = await c.Read(c.DocumentId);
                    await h.Uploads.ReviseAsync(IdentityResolver.Demo, c.DocumentId, document.ETag, Guid.NewGuid().ToString("N"), "架空資料の版更新", "revision.docx", OfficeExtractor.WordMime, bytes, default);
                }
                Assert.True(await h.Processor.RunOnceAsync(default));
                document = await c.Read(c.DocumentId);
                c.Job = Jobs.Create(document.Value, "enrichment", h.Clock.GetUtcNow());
                return c;
            }
            catch { c.Dispose(); throw; }
        }
        public Task<StoredRecord> Read(string id) => ReadCore(id);
        private async Task<StoredRecord> ReadCore(string id) => (await Harness.Repo.ReadAsync(Partition, id, default))!;
        public void FaultRead(string id, Action<JsonObject> change)
        {
            var repo = new ReadFault(Harness.Repo, id, change);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock" }).Build();
            var ai = new PipelineAi();
            Pipeline = new(repo, Harness.Store, new(repo), ai, ai, new(config), config, Harness.Clock);
        }
        public async Task Change(string id, Action<JsonObject> change)
        {
            var row = await Read(id); change(row.Value);
            await Harness.Store.CommitAsync(Partition, [RecordWrite.Replace(row.Value, row.ETag)], default);
        }
        public async Task RejectBuild()
        {
            var before = await Read(ExperienceId);
            await Assert.ThrowsAsync<DomainException>(() => Pipeline.BuildAsync(Job, default));
            Assert.Equal(before.ETag, (await Read(ExperienceId)).ETag);
            Assert.Null(await Harness.Store.ReadAsync(Partition, AuditId, default));
        }
        public void Dispose() => Harness.Dispose();
    }
    private static byte[] Package(params string[] paragraphs) => OfficeFixtures.Package(body: string.Concat(paragraphs.Select(p => "<w:p><w:r><w:t>" + p + "</w:t></w:r></w:p>")));

    /// <summary>本文と期間が同じ通常訂正だけが確認状態を保持し、現在の引用へ移る。</summary>
    [Theory]
    [InlineData("confirm")]
    [InlineData("correct")]
    public async Task Same_document_unchanged_claim_rebinds_audits_and_invalidates_old_quotes(string action)
    {
        using var c = await RevisionCase.Create(action);
        var before = await c.Read(c.ExperienceId);
        var plan = await c.Pipeline.BuildAsync(c.Job, default);
        await c.Pipeline.CommitAsync(plan, [], default);
        var after = (await c.Read(c.ExperienceId)).Value;
        Assert.Equal(action == "confirm" ? "self_confirmed" : "self_corrected", after.Text("confirmationState"));
        Assert.Equal(Text, after.Text("description"));
        Assert.True(JsonNode.DeepEquals(before.Value["period"], after["period"]));
        Assert.True(JsonNode.DeepEquals(before.Value["correctedBy"], after["correctedBy"]));
        var reference = Assert.Single(after["evidenceRefs"]!.Deserialize<EvidenceReference[]>(ProjectionRules.Json)!);
        Assert.Equal(c.DocumentId + "-v2", reference.DocumentVersionId);
        var guard = new SourceGuard(c.Harness.Repo);
        Assert.Null(await guard.EvidenceAsync(IdentityResolver.Demo, c.OldReference, default));
        Assert.NotNull(await guard.EvidenceAsync(IdentityResolver.Demo, reference, default));
        var audit = (await c.Read(c.AuditId)).Value;
        Assert.True(JsonNode.DeepEquals(before.Value["evidenceRefs"], audit["beforeEvidenceRefs"]));
        Assert.True(JsonNode.DeepEquals(after["evidenceRefs"], audit["afterEvidenceRefs"]));
        // Rebuilding the same plan after publication does not invent another rebind audit.
        var replay = await c.Pipeline.BuildAsync(c.Job, default);
        Assert.DoesNotContain(replay.Writes, w => w.Value.Text("action") == "confirmed_evidence_rebound");
    }

    /// <summary>同じ表題・段落・本文でも別資料を旧項目の根拠として採用しない。</summary>
    [Fact]
    public async Task Another_document_with_same_title_position_and_text_does_not_inherit_confirmation()
    {
        using var c = await RevisionCase.Create(otherDocument: true);
        await c.RejectBuild();
    }

    /// <summary>内容や期間の変更・削除を確認済み内容の自動引継ぎにしない。</summary>
    [Theory]
    [InlineData("text")]
    [InlineData("period")]
    [InlineData("removed")]
    [InlineData("priorUserCorrection")]
    public async Task Changed_claim_or_removed_evidence_is_not_automatically_accepted(string mode)
    {
        using var c = await RevisionCase.Create(first: mode == "text" ? "Gitで別の架空作業を行った。" : mode == "period" ? Text + " 2020-01-01〜2021-01-01" : mode == "removed" ? "元の段落は削除した。" : null);
        if (mode == "priorUserCorrection") await c.Change(c.ExperienceId, r => r["description"] = "本人が訂正した別の内容。");
        await c.RejectBuild();
    }

    /// <summary>履歴の根拠関係と所有・公開境界が異なると引継ぎを拒否する。</summary>
    [Theory]
    [InlineData("spanDocument")][InlineData("versionDocument")][InlineData("spanVersion")][InlineData("validation")]
    [InlineData("spanOwner")][InlineData("versionOwner")][InlineData("tenant")][InlineData("scope")]
    [InlineData("person")][InlineData("documentOwner")][InlineData("withdrawn")][InlineData("role")][InlineData("authorityScope")]
    [InlineData("historicalText")][InlineData("missingSpan")]
    [InlineData("currentSpanOwner")][InlineData("currentVersionOwner")][InlineData("currentSpanScope")]
    public async Task Invalid_lineage_or_access_fails_closed_without_publication(string mode)
    {
        using var c = await RevisionCase.Create();
        var target = mode == "currentSpanOwner" || mode == "currentSpanScope" ? c.DocumentId + "-v2-e00000000" :
            mode == "currentVersionOwner" ? c.DocumentId + "-v2" : mode is "versionDocument" or "versionOwner" ? c.OldReference.DocumentVersionId :
            mode is "person" ? c.ExperienceId : mode is "documentOwner" or "withdrawn" ? c.DocumentId :
            mode is "role" or "authorityScope" ? AuthorityProvider.PersonId("demo-person-01") : c.OldReference.Id;
        // Invalid tenant records cannot be written by BatchRules; inject the bad read instead.
        if (mode == "tenant") c.FaultRead(target, r => r["tenantId"] = "another-tenant");
        else if (mode == "missingSpan")
            await c.Change(c.ExperienceId, r => r["evidenceRefs"]![0]!["id"] = "missing-evidence");
        else await c.Change(target, r =>
        {
            switch (mode)
            {
                case "spanDocument": case "versionDocument": r["documentId"] = "other-document"; break;
                case "spanVersion": r["sourceVersion"] = 9; break;
                case "validation": r["validationState"] = "invalid"; break;
                case "spanOwner": case "versionOwner": case "documentOwner": case "currentSpanOwner": case "currentVersionOwner": r["ownerId"] = "someone-else"; break;
                case "tenant": r["tenantId"] = "another-tenant"; break;
                case "scope": case "currentSpanScope": r["scopeIds"] = RecordJson.Array(["demo", "additional-scope"]); break;
                case "person": r["personId"] = "another-person"; break;
                case "withdrawn": r["activeVersion"] = null; r["status"] = "withdrawn"; break;
                case "role": r["roles"] = RecordJson.Array(["analyst"]); break;
                case "authorityScope": r["scopeIds"] = RecordJson.Array(["other-scope"]); break;
                case "historicalText": r["text"] = "履歴の引用が変わった。"; break;
            }
        });
        await c.RejectBuild();
    }

    /// <summary>履歴を含む読取正本が計画後に更新されると、ETag検査で公開を止める。</summary>
    [Theory]
    [InlineData("oldSpan")][InlineData("oldVersion")][InlineData("currentSpan")][InlineData("document")]
    [InlineData("experience")][InlineData("person")][InlineData("authority")]
    public async Task Etag_change_after_rebind_plan_blocks_the_atomic_commit(string mode)
    {
        using var c = await RevisionCase.Create();
        var plan = await c.Pipeline.BuildAsync(c.Job, default);
        var target = mode switch
        {
            "oldSpan" => c.OldReference.Id, "oldVersion" => c.OldReference.DocumentVersionId,
            "currentSpan" => c.DocumentId + "-v2-e00000000", "document" => c.DocumentId,
            "experience" => c.ExperienceId, "person" => "demo-person-01", _ => AuthorityProvider.PersonId("demo-person-01")
        };
        Assert.Contains(target, plan.Expected.Keys);
        await c.Change(target, r => r["updatedAt"] = "local-conflicting-change");
        var before = (await c.Read(c.ExperienceId)).ETag;
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => c.Pipeline.CommitAsync(plan, [], default))).Code);
        Assert.Equal(before, (await c.Read(c.ExperienceId)).ETag);
        Assert.Null(await c.Harness.Store.ReadAsync(Partition, c.AuditId, default));
    }
}
