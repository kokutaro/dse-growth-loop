using Dse.Domain;

namespace Dse.Evaluation;

/// <summary>限定回帰評価で承認された境界。製品の共有辞書や人物実績は更新しない。</summary>
public static class RegressionDictionaryScope
{
    /// <summary>過去の実験定義と区別する評価方針版。</summary>
    public const string Version="regression-migration-development-v2";

    /// <summary>指定された既存IDだけを新版にし、処理開発を含めても移行実行を推認しない。</summary>
    public static SkillDictionarySnapshot IncludeMigrationDevelopment(SkillDictionarySnapshot previous,string conceptId)
    {
        if(previous.Concepts.Count(c=>c.Id==conceptId)!=1)throw new ArgumentException("Unambiguous existing evaluation concept required");
        return previous with{Version=checked(previous.Version+1),Concepts=previous.Concepts.Select(c=>c.Id!=conceptId?c:c with{
            Definition="既存データを移行先へ移すための項目変換・登録処理の設計、開発または検証。移行処理の開発も含み、実際の移行作業を実施したことは必須としない。",
            Boundary="原文にある行為（処理開発、検証、移行実行）を区別して保持する。処理を作成しただけの記述から移行実行経験を認定しない。知識・希望・計画のみは実務経験にしない。"
        }).ToArray()};
    }
}
