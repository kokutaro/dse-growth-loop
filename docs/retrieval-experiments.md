# 検索比較の実行とIssue #14の承認境界

2026-10-03。Issue #14の比較基盤。保存済み応答の比較と実AI評価を段階実施。実Cosmosの投影RU測定は[結果](validation-results/20261003-issue-14-cosmos/results.md)を参照。検索全体の待ち時間・全費用・採用判定は未完了。全入力はダミー。

## 確認済み事項

ユーザーが `evaluation/review.md` のevaluation-v1正解ラベルを承認した。一次Recall最低90%、構造化主要項目90%以上、根拠ID100%、最終HitRate@5の正解あり20問中18問以上を確認基準とする。ウォームp95はユーザーの明示指定により10秒から15秒へ変更した。冷起動は合否上限を設定せず別報告する。予算は8,000円。月1万円税別の旧申請案を承認額と扱わない。

承認済み集合で比較基盤を検証し、不足を未達としてPRへ残す方針をユーザーが承認した。集合ごとの正解あり質問は人物・事例各1問（小集合・大集合は同じ質問内容）であり、重複を数えて20問の達成を主張しない。ラベル承認は合成分類・合成応答を実AI実測へ変えるものではない。

## 入力と構成

既存のdataset/replayに `evaluation/experiment-inputs.json` を追加した。`python3 scripts/generate_experiment_inputs.py` で再生成できる。全体分類、質問分布、語一致条件を明示的に保存し、datasetのSHA-256へ結び付ける。これらは比較器を検証する**合成入力**であり、実Jev分類ではない。

複数職歴の全体分類はmechanical=0.7/software=0.3という比較用の仮定を明示している。断片平均や期間比を実分類と扱わない。他の全体分類は単一職歴の既存合成分布を使う。質問分布も合成値で、上位3/5を選んでも0.1未満のカテゴリは展開しない。この集合では3と5の差を測れない。

| 構成 | 入力・取得経路 |
|---|---|
| A | 全体分類の軸ごとのTop-1、質問Top-1 |
| B | 明示的な全体分類の複数Probability |
| C | 職歴・事例断片の複数Probability |
| D | C＋支持ありNoul |
| E | D＋正規化語一致 |
| F | 認可内・参加可能・現行版の全件。評価器限定、最大200候補、上限超過は失敗 |

A/Bの全体分類が欠落・重複する場合は拒否する。全構成でtenant/scope、利用可否、active、有効版を検査する。Fは公開APIも `SearchRequest.Limit` も変更しない。FのAI投入は実装しておらず、有料試験は別途件数・トークン・費用上限の承認が必要。

一次検索は文書閾値0.05/0.1/0.2/0.3、質問Top-1/3/5、Noul0.3/0.5/0.7、N=20/50/100を探索する。Fは同じ全件集合の重複試験を避けて1条件。人物・事例ごとに指標と分母を保存する。実験は既存Retrieverの全体上限Nと20%救済枠を使い、公開自然文検索のretrieval-v2の人物/事例別枠・RescueSlotsとは異なる。本番枠設定の代替測定ではない。一次検索に最終順位はないのでPrecision/nDCG/最終HitRateはnull。採用設定は自動更新しない。

## 再現コマンド

```sh
# 調整集合。送信0、RU0、live latency=null
python3 scripts/generate_experiment_inputs.py
dotnet run --project src/Dse.Evaluation -- --matrix --out .artifacts/issue14-tuning-smoke
dotnet run --project src/Dse.Evaluation -- --matrix --large --out .artifacts/issue14-tuning-large

# 固定済みゲートのコミット後に確認集合を実行
dotnet run --project src/Dse.Evaluation -- --matrix --split final --gates evaluation/gates.issue14.json --out .artifacts/issue14-final-smoke
dotnet run --project src/Dse.Evaluation -- --matrix --large --split final --gates evaluation/gates.issue14.json --out .artifacts/issue14-final-large

# 同一候補に対する保存済み比較応答。CLIからAIへは送信しない
dotnet run --project src/Dse.Evaluation -- --matrix --comparisons /path/to/comparisons.json --out .artifacts/issue14-comparisons
```

既存runへの上書きは拒否する。`report.json` にrunId、時刻、データ/入力/応答/ゲートhash、版、採用未決定状態を保存する。`primary.json` は全条件の上限前/後ID・根拠・候補hash、`summary.csv` は条件ごとの平均・分母・Noul増分・打切り損失を持つ。`questions.csv`は質問/条件別、`failures.json`は検索漏れと上限損失、`primaryChecks`は承認一次基準へのfixture内合否を出す。大量の全候補JSONは `.artifacts/` で生成し、文書にはCSVと選択条件・失敗例を保存する。

## 同一候補の再順位付け比較

`--comparisons` は次のオブジェクトの配列を読む。

```json
{
  "questionId": "t-smoke-person-software",
  "candidateSha256": "primary.jsonのcandidateSha256",
  "modelVersion": "実際の固定版または明示的なfixtureモデル版",
  "promptVersion": "固定プロンプト版",
  "evidenceBytes": 1200,
  "includeProbabilities": false,
  "order": "original",
  "attempt": 1,
  "status": "success",
  "items": [{"entityId": "t-person-001", "evidenceIds": ["根拠ID"], "reason": "送信根拠本文からの完全一致引用"}]
}
```

候補hashには分類、根拠本文、版を含める。`original/reversed`、再試行1〜3、根拠64〜8000 UTF-8 byte、確率情報あり/なし、モデル/プロンプト版を記録する。提供順と再試行を変えても同じ候補集合に結び付ける。実入力がこの条件を守ったことは送信器の監査で別途照合する。比較器がモデル非決定性を生み出すわけではない。

検索サービスと同じ候補ごとの最初の断片・UTF-8予算を使って根拠IDと完全一致引用を照合する。架空候補、未送信根拠、引用捏造、重複候補、障害時の非空順位を拒否する。K=3/5で指標を計算し、検索失敗・rerank失敗・打切りを区別する。truncatedだけでは根拠不足と出力上限を特定できないため、原因を断定しない。成功応答同士は同じ候補・モデル・プロンプト・根拠量・確率条件で組にし、順位完全一致率と上位K Jaccardを記録する。拒否/失敗/打切りは成功比較へ混ぜない。

## ライブ計測で残る作業

検索監査の `cosmosCharges` にprojection_page別とprimary_read別のRequestChargeを保存した。`requestUnits`は従来と同じ検索/認可読取RUで、監査書込・AI台帳・フェンス書込・索引/保存量・Azure全費用を含む総請求値ではない。`ruMeasured=false` のDemo結果を実RUと扱わない。

実サービス実行前に、対象RG/モデル、最大送信数・input/output/reasoning token、再試行、実行時間、AI試験上限とインフラ費用を示して承認を得る。今回のローカル評価に外部課金はない。承認予算8,000円を、すべての有料操作への包括承認とは扱わない。

ライブでは同じrunIdへページ別RU、索引/保存量、全AiRunのprovider usageと推定usage区別、reasoning token、retry、成功/障害別のcold/warm p50/p95と標本数、単価日付/為替日付、実利用量を固定した月額推計を集める。未測定値はnull。価格や過去runの成功件数を今回の実測に転用しない。追加20問、実構造化の主要項目一致、候補の再順位付け比較、月額との比較、採用理由と不採用理由はまだ完了していない。

最終確認ではdataset/replayに加え実験入力と比較応答のhashを固定する。冷起動上限は承認に従いnullを許容する。ゲートJSONの8,000円は承認予算を記録する値で、送信実装やAzureの強制停止を提供しない。最終集合を見た後の基準変更は禁止し、未達は同じIssueに残す。

今回の確認集合・合否・未達項目は[ローカル検証記録](validation-results/20261003-issue-14-local/results.md)を参照。
