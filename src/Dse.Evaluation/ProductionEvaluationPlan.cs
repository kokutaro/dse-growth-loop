using Dse.Domain;
using System.Text.Json.Nodes;

namespace Dse.Evaluation;

public static class ProductionEvaluationPlan
{
    public static void RequireFixture(Fixture fixture, string datasetHash, string? approvedHash, bool prepare)
    {
        if (fixture.Origin != "synthetic" || fixture.Questions.Length != 24 || fixture.Units.Length != 60)
            throw new ArgumentException("Dedicated synthetic 24-question fixture required");
        if (prepare && fixture.HumanReviewStatus == "pending") return;
        if (fixture.HumanReviewStatus != "human-approved" || approvedHash != datasetHash)
            throw new ArgumentException("Human approval matching the exact dataset hash required");
    }
    public static RetrievalRules Rules(string? personLimit)
    {
        if (personLimit is not (null or "10" or "15")) throw new ArgumentException("Only frozen person10/person15 arms supported");
        var rules = new RetrievalRules(PersonLimit: personLimit == "15" ? 15 : 10, CaseLimit: 15);
        rules.Validate(); return rules;
    }
    public static string[] Arms(bool regression, bool bothTargets, bool regressionAllOnly = false)
    {
        if (regressionAllOnly && !regression) throw new ArgumentException("Reduced arms are only permitted for observed regression");
        return regressionAllOnly ? ["all"] : regression || bothTargets ? ["all", "targeted"] : ["all"];
    }

    public static void RequireCallBudget(int callsUsed, int dailyLimit, int stageUpperBound, int activeReservations)
    {
        if (callsUsed < 0 || dailyLimit > 240 || stageUpperBound < 1 || activeReservations != 0 ||
            callsUsed > dailyLimit - stageUpperBound)
            throw new InvalidOperationException("Insufficient remaining shared call budget for the complete stage");
    }

    public static void RequireWeeklyBudget(int callsUsed, int weeklyLimit, int stageUpperBound, int activeReservations,
        decimal committedJpy, decimal totalJpy, long dailyTokensUsed, long dailyTokenLimit)
    {
        // Jev classification + Luna rerank use at most 0.5022 JPY per billable call at FX162.
        if (callsUsed < 0 || weeklyLimit > 1680 || stageUpperBound < 1 || activeReservations != 0 ||
            callsUsed > weeklyLimit - stageUpperBound || totalJpy > 2000 || committedJpy < 0 ||
            committedJpy + stageUpperBound * .5022m > totalJpy || dailyTokensUsed < 0 || dailyTokenLimit > 4500000 ||
            dailyTokensUsed > dailyTokenLimit - stageUpperBound * 19000L)
            throw new InvalidOperationException("Insufficient weekly money/call or daily token budget for the complete stage");
    }

    public static void RequireSeedRecovery(JsonObject[] expected, JsonObject?[] existing, bool completed)
    {
        if (expected.Length != existing.Length || existing.Any(r => r is null))
            throw new InvalidOperationException("Partial seed records cannot be recovered");
        for (var i = 0; i < expected.Length; i++)
        {
            var person = expected[i].Text("kind") == "person";
            foreach (var pair in expected[i])
            {
                if (pair.Key is "createdAt" or "updatedAt") continue;
                if (person && pair.Key is "sourceVersion" or "activeVersion")
                {
                    if (existing[i]!.Number(pair.Key) != (completed ? 2 : 1))
                        throw new InvalidOperationException("Unexpected person seed version");
                    continue;
                }
                if (!JsonNode.DeepEquals(pair.Value, existing[i]![pair.Key]))
                    throw new InvalidOperationException("Existing seed differs from the approved source: " + expected[i].Text("kind") + "." + pair.Key);
            }
        }
    }

    public static void RequireUnsentRecovery(JsonObject[] audits, string evidenceId)
    {
        var matching = audits.Where(a => a["trace"] is JsonObject trace && trace.Strings("evidenceIds").Contains(evidenceId)).ToArray();
        if (matching.Length != 1 || matching[0].Text("status") != "budget_stopped" ||
            matching[0].Text("errorCode") != "AI_BUDGET_STOPPED" || matching[0]["trace"]!["attemptCount"]!.GetValue<int>() != 0)
            throw new InvalidOperationException("Only an audited unsent budget stop can be resumed");
    }
}
