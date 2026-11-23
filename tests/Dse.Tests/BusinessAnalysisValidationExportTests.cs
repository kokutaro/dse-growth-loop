using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BusinessAnalysisValidationExportTests
{
    private sealed class Drafter : IBusinessHypothesisDrafter
    {
        public int Calls;
        public Task<AiResult<BusinessHypothesis>> DraftAsync(ActorAccess actor, string id, BusinessAnalysisSource[] sources, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AiResult<BusinessHypothesis>(AiStatus.Succeeded,
                new("秘密の対象", "秘密の課題", "秘密の対応", "秘密の仮説", "未検証", "本人による確認が必要", sources.Select(x => x.KnowledgeId).ToArray()), id, null));
        }
    }
    private sealed class Setup : IDisposable
    {
        public readonly string Root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "validation-export-tests-" + Guid.NewGuid().ToString("N"))).FullName;
        public readonly MemoryRecordStore Store = new();
        public readonly Drafter Draft = new();
        public required IConfigurationRoot Config;
        public required PrimaryRepository Repo;
        public required AuthorityProvider Authority;
        public required BusinessAnalysisService Analysis;
        public required ActorAccess Actor;
        public required string Run;
        public string Output => Path.Combine(Root, "out", "report.json");
        public BusinessAnalysisValidationExport Export => new(Analysis, Authority);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static async Task<Setup> Create()
    {
        var actor = IdentityResolver.Demo with { Roles = ["member", "analyst", "publisher", "operator"] };
        var store = new MemoryRecordStore(); var repo = new PrimaryRepository(store);
        var actors = Enumerable.Range(1, 3).Select(i => i == 1 ? actor : IdentityResolver.DemoPerson($"demo-person-{i:D2}")!).ToArray();
        await AuthorityProvider.InitializeAsync(store, "demo", actors.Select(a => new IdentityBinding("demo", "", a.PrincipalId, a.PersonId, a.Roles, true)).ToArray(),
            actors.Select(a => new AccessGrant(a.PrincipalId, a.ScopeIds)).ToArray(), default);
        await BusinessAnalysisDemoFixtures.SeedAsync(store, default);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["AiUsage:Stopped"] = "false", ["AiUsage:LiveOptIn"] = "false",
            ["ValidationExport:Enabled"] = "true", ["ValidationExport:Synthetic"] = "true", ["ValidationExport:PersonId"] = actor.PersonId,
            ["ValidationExport:RunId"] = "synthetic-export-test", ["ValidationExport:FileName"] = "report.json" }).Build();
        var draft = new Drafter(); var analysis = new BusinessAnalysisService(repo, store, new(repo), draft, config, TimeProvider.System);
        await analysis.StartAsync(actor, new("synthetic-export-test", DateTimeOffset.UtcNow, BusinessAnalysisService.ConfigVersion), "synthetic-export-test", default);
        var s = new Setup { Config = config, Repo = repo, Authority = new(repo), Actor = actor, Run = "synthetic-export-test", Analysis = analysis };
        // Use the same backing store/drafter for the prepared result and export; no network is registered.
        s.Store.Restore(store.Snapshot()); s.Draft.Calls = draft.Calls;
        s.Repo = new(s.Store); s.Authority = new(s.Repo);
        s.Analysis = new(s.Repo, s.Store, new(s.Repo), s.Draft, config, TimeProvider.System);
        Directory.CreateDirectory(Path.Combine(s.Root, "data")); Directory.CreateDirectory(Path.Combine(s.Root, "out"));
        File.SetUnixFileMode(Path.Combine(s.Root, "out"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(s.Root, "data", "records.json"), "{}");
        config["Data:LocalPath"] = Path.Combine(s.Root, "data"); config["ValidationExport:Directory"] = Path.Combine(s.Root, "out");
        config["AiUsage:Stopped"] = "true";
        return s;
    }
    private static string BusinessState(Setup s) => JsonSerializer.Serialize(s.Store.Snapshot().Records.Where(x => x.Record.Value.Text("kind") != "accessFence"));

    [Fact]
    public async Task Export_is_private_redacted_no_dispatch_and_does_not_modify_business_records()
    {
        using var s = await Create(); var before = BusinessState(s); var calls = s.Draft.Calls;
        await s.Export.ExportAsync(s.Config, true, s.Actor, default);
        Assert.Equal(before, BusinessState(s)); Assert.Equal(calls, s.Draft.Calls);
        var text = await File.ReadAllTextAsync(s.Output); var report = JsonNode.Parse(text)!;
        Assert.True(report["synthetic"]!.GetValue<bool>()); Assert.False(report["modelDispatchedByExport"]!.GetValue<bool>());
        Assert.Equal(3, report["groups"]![0]!["independentChallenges"]!.GetValue<int>());
        Assert.Equal(2, report["groups"]![0]!["distinctDepartments"]!.GetValue<int>());
        Assert.Equal(12, report["groups"]![0]!["reportedHours"]!.GetValue<decimal>());
        foreach (var forbidden in new[] { "秘密", "demo-person", "principal", "knowledge-", s.Run, "hypothesis", "tenant", "etag" }) Assert.DoesNotContain(forbidden, text);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(s.Output));
        var again = await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.Equal("VALIDATION_EXPORT_ALREADY_ATTEMPTED", again.Code); Assert.Equal(text, await File.ReadAllTextAsync(s.Output));
        s.Config["ValidationExport:FileName"] = "other.json";
        await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.False(File.Exists(Path.Combine(s.Root, "out", "other.json"))); Assert.Equal(calls, s.Draft.Calls);
    }

    [Theory]
    [InlineData("ValidationExport:Enabled", null)] [InlineData("ValidationExport:Synthetic", "false")]
    [InlineData("Data:Mode", "Azure")] [InlineData("Ai:Mode", "Live")]
    [InlineData("AiUsage:Stopped", "false")] [InlineData("AiUsage:LiveOptIn", "true")]
    [InlineData("ValidationExport:FileName", "../report.json")]
    public async Task Unsafe_configuration_is_rejected_before_any_store_or_dispatch_work(string key, string? value)
    {
        using var s = await Create(); s.Config[key] = value; var before = JsonSerializer.Serialize(s.Store.Snapshot());
        await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.Equal(before, JsonSerializer.Serialize(s.Store.Snapshot())); Assert.False(File.Exists(s.Output));
    }

    [Fact]
    public async Task Production_default_is_inaccessible_and_store_or_private_directory_is_never_created()
    {
        using var s = await Create();
        Assert.Throws<DomainException>(() => BusinessAnalysisValidationExport.Validate(s.Config, false));
        File.Delete(Path.Combine(s.Root, "data", "records.json"));
        Assert.Throws<DomainException>(() => BusinessAnalysisValidationExport.Validate(s.Config, true));
        Assert.False(File.Exists(Path.Combine(s.Root, "data", "records.json")));
    }

    [Theory]
    [InlineData("operator")] [InlineData("member")] [InlineData("analyst")] [InlineData("scope")] [InlineData("disabled")]
    public async Task Canonical_authority_revocation_is_not_replaced_by_forged_claims(string damage)
    {
        using var s = await Create(); var p = RecordPartition.Catalog("demo");
        var row = (await s.Repo.ReadAsync(p, AuthorityProvider.PersonId(s.Actor.PersonId), default))!;
        if (damage == "disabled") row.Value["enabled"] = false;
        else if (damage == "scope") row.Value["scopeIds"] = RecordJson.Array(["other"]);
        else row.Value["roles"] = RecordJson.Array(s.Actor.Roles.Where(x => x != damage));
        await s.Repo.CommitAsync(p, [RecordWrite.Replace(row.Value, row.ETag)], default);
        await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.False(File.Exists(s.Output));
    }

    [Theory]
    [InlineData("revoked")] [InlineData("processing")] [InlineData("live")] [InlineData("unknown-citation")]
    public async Task Noncurrent_or_unfinished_or_untrusted_results_are_not_exported(string damage)
    {
        using var s = await Create(); var p = RecordPartition.Catalog("demo");
        if (damage == "revoked")
        {
            var source = (await s.Repo.QueryAsync(p, "knowledge", null, null, 100, null, default)).Items[0];
            source.Value["status"] = "withdrawn"; await s.Repo.CommitAsync(p, [RecordWrite.Replace(source.Value, source.ETag)], default);
        }
        else
        {
            var row = (await s.Repo.QueryAsync(p, "job", null, null, 100, null, default)).Items.Single(x => x.Value.Text("jobType") == "businessAnalysis");
            if (damage == "processing") row.Value["analysisStatus"] = "processing";
            if (damage == "live") row.Value["processingMode"] = "live";
            if (damage == "unknown-citation") row.Value["groups"]![0]!["hypothesis"]!["evidenceIds"] = new JsonArray("unknown-private-id");
            await s.Repo.CommitAsync(p, [RecordWrite.Replace(row.Value, row.ETag)], default);
        }
        await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.False(File.Exists(s.Output));
    }

    [Fact]
    public async Task Existing_output_and_symlink_and_public_directory_are_rejected_without_overwrite()
    {
        using var s = await Create(); await File.WriteAllTextAsync(s.Output, "existing");
        await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.Equal("existing", await File.ReadAllTextAsync(s.Output));
        File.Delete(s.Output); Directory.CreateSymbolicLink(Path.Combine(s.Root, "link"), Path.Combine(s.Root, "out"));
        s.Config["ValidationExport:Directory"] = Path.Combine(s.Root, "link");
        Assert.Throws<DomainException>(() => BusinessAnalysisValidationExport.Validate(s.Config, true));
        s.Config["ValidationExport:Directory"] = Path.Combine(s.Root, "out");
        File.SetUnixFileMode(Path.Combine(s.Root, "out"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        Assert.Throws<DomainException>(() => BusinessAnalysisValidationExport.Validate(s.Config, true));
    }

    [Fact]
    public async Task An_unknown_previous_claim_blocks_export_even_with_no_report_and_never_dispatches()
    {
        using var s = await Create();
        var claim = Path.Combine(s.Root, "out", ".validation-" + ConversationService.Hash("demo:" + s.Actor.PrincipalId + ":" + s.Run) + ".claim");
        await File.WriteAllTextAsync(claim, "unknown previous file write"); var calls = s.Draft.Calls;
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.Equal("VALIDATION_EXPORT_ALREADY_ATTEMPTED", error.Code);
        Assert.False(File.Exists(s.Output)); Assert.Equal(calls, s.Draft.Calls);
    }

    [Fact]
    public async Task Held_fence_is_not_recovered_or_bypassed_for_export()
    {
        using var s = await Create(); var p = RecordPartition.Catalog("demo");
        var fence = (await s.Repo.ReadAsync(p, "system-access-fence", default))!;
        fence.Value["holder"] = "unresolved-owner"; await s.Repo.CommitAsync(p, [RecordWrite.Replace(fence.Value, fence.ETag)], default);
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Export.ExportAsync(s.Config, true, s.Actor, default));
        Assert.Equal("ACCESS_BUSY", error.Code); Assert.False(File.Exists(s.Output));
        Assert.Equal("unresolved-owner", (await s.Repo.ReadAsync(p, "system-access-fence", default))!.Value.Text("holder"));
    }
}
