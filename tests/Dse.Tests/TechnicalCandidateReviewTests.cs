using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

/// <summary>保存済みの技術列を、安全に人が確認できることを検証します。</summary>
public sealed class TechnicalCandidateReviewTests
{
    [Theory]
    [InlineData("アプリ開発担当 ／ TypeScript、SQLite、Git", "TypeScript", "SQLite", "Git")]
    [InlineData("通信処理の開発担当 ／ Go、Linux、Git", "Go", "Linux", "Git")]
    public async Task Held_distinct_hints_are_visible_but_not_approved_or_promoted(string source, string first, string second, string third)
    {
        var f = await SkillDictionaryFixture.Create();
        await f.Source("technical", source, source);
        var partition = RecordPartition.Catalog("demo");
        var items = new[] { first, second, third }.Select(h => new SpecificSkill(source, h, "ev-technical", source) { Basis = "knowledge" }).ToArray();
        var row = (await f.Repo.ReadAsync(partition, "fragment-technical", default))!;
        row.Value["specificSkills"] = JsonSerializer.SerializeToNode(SpecificSkillRules.Assess(new(SpecificSkillRules.Version, "extracted", items), new Dictionary<string, string> { ["ev-technical"] = source }), ProjectionRules.Json);
        await f.Repo.CommitAsync(partition, [RecordWrite.Replace(row.Value, row.ETag)], default);
        var before = RecordJson.Canonical((await f.Repo.ReadAsync(partition, "fragment-technical", default))!.Value);
        var key = Guid.NewGuid().ToString("N");
        var collected = await f.Service.CollectAsync(SkillDictionaryFixture.Curator, key, new("fragment-technical"), default);
        Assert.Equal(2, collected.Body!["quarantined"]!.GetValue<int>());
        var id = collected.Body["collectionId"]!.GetValue<string>();
        var detail = await f.Fence.RunAsync("demo", ct => f.Service.SourceUnderFenceAsync(SkillDictionaryFixture.Curator, id, ct), default);
        Assert.Equal(new[] { second, third }, detail["rejected"]!.AsArray().Select(n => n!["normalizationCandidate"]?.GetValue<string>()));
        Assert.All(detail["rejected"]!.AsArray(), n => { Assert.Equal(source, n!["raw"]!.GetValue<string>()); Assert.Equal("knowledge", n["basis"]!.GetValue<string>()); Assert.Equal("duplicate_candidate", n["reason"]!.GetValue<string>()); });
        Assert.False(detail["personalExperienceApproved"]!.GetValue<bool>());
        Assert.Equal(before, RecordJson.Canonical((await f.Repo.ReadAsync(partition, "fragment-technical", default))!.Value));
        Assert.Empty((await f.Service.ReadUnderFenceAsync(SkillDictionaryFixture.Curator, null, default))["concepts"]!.AsArray());
        var replay = await f.Service.CollectAsync(SkillDictionaryFixture.Curator, key, new("fragment-technical"), default);
        Assert.True(JsonNode.DeepEquals(collected.Body, replay.Body));
        var notice = (await f.Repo.ReadAsync(partition, id, default))!;
        Assert.Equal("SKILL_COLLECTION_HELD", (await Assert.ThrowsAsync<DomainException>(() => f.Service.ReviewAsync(SkillDictionaryFixture.Curator, id, Guid.NewGuid().ToString("N"), notice.ETag, new("new", "合成データの保留を勝手に承認しない", 0, SkillDictionaryService.EmptyETag), default))).Code);
    }

    [Fact]
    public async Task Ungrounded_rejected_hint_is_not_returned()
    {
        var f = await SkillDictionaryFixture.Create();
        var partition = RecordPartition.Catalog("demo");
        var row = (await f.Repo.ReadAsync(partition, "fragment-regression", default))!;
        row.Value["specificSkills"]!["items"]![0]!["quote"] = "現在の原文にない引用";
        row.Value["specificSkills"]!["items"]![0]!["normalizationCandidate"] = "表示してはいけない旧資料の技能";
        await f.Repo.CommitAsync(partition, [RecordWrite.Replace(row.Value, row.ETag)], default);
        var collected = await f.Service.CollectAsync(SkillDictionaryFixture.Curator, Guid.NewGuid().ToString("N"), new("fragment-regression"), default);
        var detail = await f.Fence.RunAsync("demo", ct => f.Service.SourceUnderFenceAsync(SkillDictionaryFixture.Curator, collected.Body!["collectionId"]!.GetValue<string>(), ct), default);
        Assert.Null(detail["rejected"]![0]!["normalizationCandidate"]);
        Assert.DoesNotContain("表示してはいけない", detail.ToJsonString());
    }
}
