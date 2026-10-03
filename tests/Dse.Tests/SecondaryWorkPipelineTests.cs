using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class SecondaryWorkPipelineTests
{
    [Theory]
    [InlineData("主業務は機械設計。副業務として営業を共同実施した。", "副業務として営業を共同実施した。")]
    [InlineData("主業務は機械設計。\n  兼務：営業を補助した。", "兼務：営業を補助した。")]
    [InlineData("主業務は機械設計。副業務は担当していない。", "副業務は担当していない。")]
    [InlineData("副業務として営業を共同実施した。", null)]
    [InlineData("機械設計と営業を実施した。", null)]
    [InlineData("主業務は機械設計。副業務として ", null)]
    public void OnlyExplicitSecondarySectionsBecomeVerbatimFragments(string text, string? expected) =>
        Assert.Equal(expected, ProfileExtraction.SecondaryWorkText(text));

    private const string Text = "主業務は機械設計。副業務として次の対応を共同実施した。営業で、顧客の要求を必須項目と将来項目に分けた。予算と納期に合う提案範囲を合意した。";
    private static ProfilePipeline Create(IngestionHarness h, SecondaryAi ai, bool enabled = true) => new(h.Repo, h.Store,
        new AccessFence(h.Repo), ai, ai, new IdentityResolver(new ConfigurationBuilder().Build()),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Data:Mode"] = "Demo", ["Ai:Mode"] = "Mock", ["Profile:SecondaryWorkFragments"] = enabled.ToString()
        }).Build(), h.Clock);
    private static async Task<JsonObject> Import(IngestionHarness h, string text = Text)
    {
        var upload = await h.Upload(bytes: OfficeFixtures.Package(body: $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"));
        await h.Processor.RunOnceAsync(default);
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), upload.Body!["documentId"]!.GetValue<string>(), default))!.Value;
        return Jobs.Create(doc, "enrichment", h.Clock.GetUtcNow());
    }
    private static async Task<SearchUnit[]> Units(IngestionHarness h)
    {
        var rows = await h.Store.QueryAsync(RecordPartition.Catalog("demo"), "searchUnit", "entityId", "demo-person-01", 100, null, default);
        return await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,
            rows.Items.Select(r => r.Value.Deserialize<SearchUnit>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!), default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondaryWorkIsSearchableWithoutChangingExperienceOrEvidence(bool enabled)
    {
        using var h = new IngestionHarness(); var ai = new SecondaryAi(); var pipeline = Create(h, ai, enabled);
        var plan = await pipeline.BuildAsync(await Import(h), default); await pipeline.CommitAsync(plan, [], default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value;
        Assert.Single(person.Strings("experienceIds"));
        var experience = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), person.Strings("experienceIds")[0], default))!.Value;
        Assert.Equal(Text, experience.Text("description"));
        var units = await Units(h);
        Assert.Equal(enabled ? 3 : 2, units.Length);
        var result = Retriever.Search(units, new([new("domain", "sales")], [], [], .2), IdentityResolver.Demo.Search, new RetrievalRules());
        if (enabled)
        {
            Assert.Single(result.Candidates);
            var secondary = Assert.Single(units, u => u.Summary.StartsWith("副業務として", StringComparison.Ordinal));
            Assert.Contains("共同実施", secondary.Summary);
            Assert.DoesNotContain("主業務", secondary.Summary);
            Assert.All(secondary.SourceRefs, s => Assert.Contains(secondary.Summary, s.Text));
            Assert.Equal(experience["evidenceRefs"]!.ToJsonString(), JsonSerializer.Serialize(secondary.EvidenceRefs, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Contains(ai.Texts, t => t == secondary.Summary);
            var calls = ai.Texts.Count;
            await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(person, "projection", h.Clock.GetUtcNow()), default), [], default);
            Assert.Equal(calls, ai.Texts.Count);
        }
        else Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task CorrectionRemovingSecondaryWorkDoesNotResurrectItOnReimport()
    {
        using var h = new IngestionHarness(); var pipeline = Create(h, new()); var job = await Import(h);
        await pipeline.CommitAsync(await pipeline.BuildAsync(job, default), [], default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var profiles = new ProfileService(h.Repo, new AccessFence(h.Repo), new IdempotencyExecutor(h.Repo, h.Clock));
        await profiles.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "secondary-correction-01", person.ETag,
            new("本人訂正", true, "correct", [new(person.Value.Strings("experienceIds")[0], "機械設計のみを実施した。", new(null, null, "unknown", null))]), default);
        await pipeline.CommitAsync(await pipeline.BuildAsync(job, default), [], default);
        Assert.DoesNotContain(await Units(h), u => u.Summary.Contains("副業務", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithdrawalAfterSecondaryBuildPreventsAtomicPublication()
    {
        using var h = new IngestionHarness(); var pipeline = Create(h, new()); var plan = await pipeline.BuildAsync(await Import(h), default);
        Assert.Contains(plan.Writes, w => w.Value.Text("kind") == "searchUnit" && w.Value.Text("summary").StartsWith("副業務", StringComparison.Ordinal));
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var profiles = new ProfileService(h.Repo, new AccessFence(h.Repo), new IdempotencyExecutor(h.Repo, h.Clock));
        await profiles.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "secondary-withdrawal-01", person.ETag,
            new("撤回", true, "correct", [], "withdrawn"), default);
        Assert.Equal("SOURCE_REVOKED", (await Assert.ThrowsAsync<DomainException>(() => pipeline.CommitAsync(plan, [], default))).Code);
        Assert.Empty(await Units(h));
    }

    [Fact]
    public async Task SecondaryFragmentLimitStopsBeforeClassificationAndPublication()
    {
        using var h = new IngestionHarness(); var ai = new SecondaryAi(); var pipeline = Create(h, ai);
        var paragraphs = string.Concat(Enumerable.Range(0, 11).Select(i => $"<w:p><w:r><w:t>{Text}</w:t></w:r></w:p>"));
        var upload = await h.Upload(bytes: OfficeFixtures.Package(body: paragraphs));
        await h.Processor.RunOnceAsync(default);
        var doc = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), upload.Body!["documentId"]!.GetValue<string>(), default))!.Value;
        var before = await Units(h);
        var error = await Assert.ThrowsAsync<DomainException>(() => pipeline.BuildAsync(Jobs.Create(doc, "enrichment", h.Clock.GetUtcNow()), default));
        Assert.Equal("PROFILE_FRAGMENT_LIMIT", error.Code);
        Assert.Empty(ai.Texts);
        Assert.Equal(before.Select(u => u.Id), (await Units(h)).Select(u => u.Id));
    }

    [Fact]
    public async Task SecondaryTextFromCorrectionIsNotPassedOffAsAnOriginalSourceQuote()
    {
        using var h = new IngestionHarness(); var ai = new SecondaryAi(); var pipeline = Create(h, ai);
        await pipeline.CommitAsync(await pipeline.BuildAsync(await Import(h), default), [], default);
        var person = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!;
        var profiles = new ProfileService(h.Repo, new AccessFence(h.Repo), new IdempotencyExecutor(h.Repo, h.Clock));
        const string corrected = "主業務は機械設計。副業務として未記載の製造経験を本人が追記した。";
        await profiles.UpdateAsync(IdentityResolver.Demo, "demo-person-01", "secondary-new-claim-01", person.ETag,
            new("本人訂正", true, "correct", [new(person.Value.Strings("experienceIds")[0], corrected, new(null, null, "unknown", null))]), default);
        var updated = (await h.Repo.ReadAsync(RecordPartition.Catalog("demo"), "demo-person-01", default))!.Value;
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(updated, "projection", h.Clock.GetUtcNow()), default), [], default);
        var units = await Units(h);
        Assert.Contains(units, u => u.Summary == corrected);
        Assert.DoesNotContain(units, u => u.Summary.StartsWith("副業務", StringComparison.Ordinal));
    }

    internal sealed class SecondaryAi : ITextStructurer, IDecisionProvider
    {
        public List<string> Texts { get; } = [];
        public Task<AiResult<StructuredText>> StructureAsync(AiContext context, string text, CancellationToken ct) =>
            Task.FromResult(new AiResult<StructuredText>(AiStatus.Succeeded, new([new(text, context.Evidence.Select(e => e.Id).ToArray())]), "fake-structure", null));
        public Task<AiResult<Decisions>> DecideAsync(AiContext context, string text, ChoiceQuestion[] choices, NoulQuestion[] nouls, CancellationToken ct)
        {
            Texts.Add(text);
            var secondary = text.StartsWith("副業務", StringComparison.Ordinal);
            var category = secondary ? "sales" : "mechanical";
            return Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded,
                new(choices.ToDictionary(q => q.Id, q => new ChoiceAnswer(category, q.Options.Keys.ToDictionary(k => k,
                    k => secondary ? k == "sales" ? 1d : 0 : k == "mechanical" ? .93 : k == "sales" ? .07 : 0), .9)),
                    nouls.ToDictionary(q => q.Id, q => new NoulAnswer(NoulState.NotEvaluated, null))), "fake-decision", null));
        }
    }
}
