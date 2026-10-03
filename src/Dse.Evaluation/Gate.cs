namespace Dse.Evaluation;

public sealed record Gate(string Status, string ApprovedBy, DateTimeOffset? FrozenAt,
    string DatasetSha256, string ReplaySha256, double? PrimaryRecallMinimum,
    double? StructuredAccuracyMinimum, double? WarmP95MsMaximum, double? ColdP95MsMaximum,
    double? AiBudgetYenMaximum, string? ExperimentInputsSha256 = null, string? ComparisonsSha256 = null)
{
    public void Validate(string dataset, string replay, string reviewStatus)
    {
        if (Status != "frozen" || string.IsNullOrWhiteSpace(ApprovedBy) || FrozenAt is null ||
            FrozenAt > DateTimeOffset.UtcNow || reviewStatus != "human-approved" ||
            DatasetSha256 != Runner.Hash(dataset) || ReplaySha256 != Runner.Hash(replay) ||
            !Probability(PrimaryRecallMinimum) || !Probability(StructuredAccuracyMinimum) ||
            !Positive(WarmP95MsMaximum) || (ColdP95MsMaximum.HasValue && !Positive(ColdP95MsMaximum)) || !Positive(AiBudgetYenMaximum))
            throw new ArgumentException("人の正解確認・ゲート承認・固定日時・データ/応答ハッシュ・全採用値が必要です。");
    }
    public void ValidateExperiments(string inputsPath, string? comparisonsPath)
    {
        if (ExperimentInputsSha256 != Runner.Hash(inputsPath) ||
            (comparisonsPath is not null && ComparisonsSha256 != Runner.Hash(comparisonsPath)))
            throw new ArgumentException("最終確認には実験入力・比較応答の承認済みhashが必要です。");
    }
    private static bool Probability(double? x) => x.HasValue && double.IsFinite(x.Value) && x is >= 0 and <= 1;
    private static bool Positive(double? x) => x.HasValue && double.IsFinite(x.Value) && x > 0;
}
