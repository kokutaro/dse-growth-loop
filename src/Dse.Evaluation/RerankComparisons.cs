using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dse.Domain;

namespace Dse.Evaluation;

public sealed record ComparisonItem(string EntityId, string[] EvidenceIds, string Reason);
public sealed record ComparisonResponse(string QuestionId, string CandidateSha256, string ModelVersion,
    string PromptVersion, int EvidenceBytes, bool IncludeProbabilities, string Order, int Attempt,
    string Status, ComparisonItem[] Items);
public sealed record ComparisonRow(string QuestionId, string EntityType, string ModelVersion, string PromptVersion,
    int EvidenceBytes, bool IncludeProbabilities, string Order, int Attempt, int K, string Status,
    string FailureCause, string CandidateSha256, string[] Ranking, Scores Scores);
public sealed record ComparisonVariation(string QuestionId, string CandidateSha256, string ModelVersion, string PromptVersion,
    int EvidenceBytes, bool IncludeProbabilities, int K, int SuccessfulRuns, int Pairs, double ExactOrderAgreement, double MeanJaccard);

public static class RerankComparisons
{
    // Includes all evidence, version fields and classifications, independent of presentation order.
    public static string CandidateHash(Candidate[] candidates) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(candidates.OrderBy(c => c.EntityType, StringComparer.Ordinal)
            .ThenBy(c => c.EntityId, StringComparer.Ordinal), Runner.Json)));

    public static string[] Validate(ComparisonResponse response, Candidate[] candidates)
    {
        if (response.CandidateSha256 != CandidateHash(candidates) || string.IsNullOrWhiteSpace(response.ModelVersion) ||
            string.IsNullOrWhiteSpace(response.PromptVersion) || response.EvidenceBytes is < 64 or > 8000 ||
            response.Order is not ("original" or "reversed") || response.Attempt is < 1 or > 3 ||
            response.Status is not ("success" or "failed" or "refused" or "truncated") ||
            response.Items.Select(i => i.EntityId).Distinct().Count() != response.Items.Length ||
            response.Status != "success" && response.Items.Length > 0)
            throw new ArgumentException("同一候補hash・比較条件・応答状態が不正です。");
        foreach (var item in response.Items)
        {
            var candidate = candidates.SingleOrDefault(c => c.EntityId == item.EntityId)
                ?? throw new ArgumentException("架空候補IDです。");
            // Same one-fragment-per-candidate and UTF-8 budget policy as the search service.
            var remaining = response.EvidenceBytes;
            var evidence = candidate.Units[0].SourceRefs.Select(s => {
                var builder = new StringBuilder();
                foreach (var rune in s.Text.EnumerateRunes()) {
                    if (rune.Utf8SequenceLength > remaining) break;
                    builder.Append(rune); remaining -= rune.Utf8SequenceLength;
                }
                return s with { Text = builder.ToString() };
            }).Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToArray();
            if (string.IsNullOrWhiteSpace(item.Reason) || item.EvidenceIds.Length == 0 ||
                item.EvidenceIds.Distinct().Count() != item.EvidenceIds.Length ||
                item.EvidenceIds.Any(id => !evidence.Any(s => s.Id == id)) ||
                !evidence.Any(s => item.EvidenceIds.Contains(s.Id) && s.Text.Contains(item.Reason, StringComparison.Ordinal)))
                throw new ArgumentException("送信根拠にないIDまたは引用です。");
        }
        return response.Items.Select(i => i.EntityId).ToArray();
    }

    public static ComparisonVariation[] Variations(ComparisonRow[] rows) => rows.Where(r => r.Status == "success")
        .GroupBy(r => (r.QuestionId, r.CandidateSha256, r.ModelVersion, r.PromptVersion, r.EvidenceBytes, r.IncludeProbabilities, r.K))
        .Where(g => g.Count() > 1).Select(g => {
            var runs = g.ToArray(); var exact = 0; var pairs = 0; double overlap = 0;
            for (var i = 0; i < runs.Length; i++) for (var j = i + 1; j < runs.Length; j++) {
                var a = runs[i].Ranking.Take(g.Key.K).ToArray(); var b = runs[j].Ranking.Take(g.Key.K).ToArray();
                pairs++; if (a.SequenceEqual(b)) exact++;
                var union = a.Union(b).Count(); overlap += union == 0 ? 1 : (double)a.Intersect(b).Count() / union;
            }
            return new ComparisonVariation(g.Key.QuestionId, g.Key.CandidateSha256, g.Key.ModelVersion, g.Key.PromptVersion,
                g.Key.EvidenceBytes, g.Key.IncludeProbabilities, g.Key.K, runs.Length, pairs, (double)exact / pairs, overlap / pairs);
        }).ToArray();

    public static ComparisonRow[] Run(Fixture fixture, PrimaryRow[] primary, ComparisonResponse[] responses)
    {
        if (responses.Length == 0 || responses.Select(r => (r.QuestionId, r.CandidateSha256, r.ModelVersion, r.PromptVersion,
            r.EvidenceBytes, r.IncludeProbabilities, r.Order, r.Attempt)).Distinct().Count() != responses.Length)
            throw new ArgumentException("空または重複した比較応答です。");
        return responses.SelectMany(response => {
            var row = primary.FirstOrDefault(r => r.QuestionId == response.QuestionId && r.CandidateSha256 == response.CandidateSha256)
                ?? throw new ArgumentException("対応する一次候補がありません。");
            var ranking = Validate(response, row.Candidates);
            var q = fixture.Questions.Single(q => q.Id == response.QuestionId);
            return new[] { 3, 5 }.Select(k => {
                var scores = Metrics.Calculate(q.Judgments, row.Candidates.Select(c => c.EntityId).ToArray(),
                    row.BeforeLimit, row.WithoutNoul, ranking, k);
                if (response.Status != "success") scores = scores with { Precision = null, Ndcg = null, FinalHitRate = null, FalseRecommendation = null };
                var cause = response.Status == "truncated" ? "evidence_truncation_or_output_limit" :
                    response.Status != "success" ? "rerank_failure" : scores.RelevantCount == 0 ? "no_gold" :
                    scores.HitRate == 0 ? "retrieval_miss" : scores.FinalHitRate == 0 ? "rerank_miss" : "none";
                return new ComparisonRow(q.Id, q.EntityType, response.ModelVersion, response.PromptVersion, response.EvidenceBytes,
                    response.IncludeProbabilities, response.Order, response.Attempt, k, response.Status, cause, response.CandidateSha256, ranking, scores);
            });
        }).ToArray();
    }
}
