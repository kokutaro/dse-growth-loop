using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Fixture responses remain separate from the explicitly enabled, budgeted live transport.
public sealed class BusinessAnalysisDrafter(IConfiguration config, BusinessAnalysisLiveDrafter? live = null) : IBusinessHypothesisDrafter, IBusinessFencedHypothesisDrafter
{
    public Task<AiResult<BusinessHypothesis>> DraftAsync(ActorAccess actor, string runId, BusinessAnalysisSource[] sources, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!BusinessAnalysisLiveRuntime.IsFixture(config))
            return live?.DraftAsync(actor, runId, sources, ct) ?? Task.FromResult(new AiResult<BusinessHypothesis>(AiStatus.NotEvaluated, null, runId, AiFailure.Configuration));
        if (!bool.TryParse(config["AiUsage:Stopped"] ?? "true", out var stopped) || stopped)
            return Task.FromResult(new AiResult<BusinessHypothesis>(AiStatus.BudgetStopped, null, runId, AiFailure.Stopped));
        var line = sources[0].Summary.Split('\n').FirstOrDefault(x => x.StartsWith("challenge (", StringComparison.Ordinal));
        var separator = line?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        var text = separator >= 0 ? line![(separator + 2)..] : "承認済み要約の課題本文を確認する（ダミー・要確認）";
        var excerpt = text[..Math.Min(text.Length, 180)];
        return Task.FromResult(new AiResult<BusinessHypothesis>(AiStatus.Succeeded,
            new("共通課題を持つ部署（ダミー・要確認）", excerpt, "承認済み要約に記された対応を個別に確認する",
                "共同で再利用できる支援方法を検討する（ダミー仮説）", "件数だけでは市場性・有効性を確認できない",
                "各部署に困りごとと既存対応を聞き、反証を記録する", sources.Select(s => s.KnowledgeId).ToArray()), runId, null));
    }
    Task<AiResult<BusinessHypothesis>> IBusinessFencedHypothesisDrafter.DraftUnderFenceAsync(ActorAccess actor, string runId, BusinessAnalysisSource[] sources, CancellationToken ct) =>
        BusinessAnalysisLiveRuntime.IsFixture(config) ? DraftAsync(actor, runId, sources, ct) :
            live?.DraftUnderFenceAsync(actor, runId, sources, ct) ?? Task.FromResult(new AiResult<BusinessHypothesis>(AiStatus.NotEvaluated, null, runId, AiFailure.Configuration));
}
