# Issue 30 独立集合の正解ラベル案

状態：2026-10-03にユーザーが質問・正解ラベルを確認し、使用を承認済み。ラベルを変更せず初回AI評価を実行した。承認記録は`approval.json`、実測は[独立本番検証](../../../docs/validation-results/20261003-issue-30-independent-production/results.md)。原文のモデル実行前レビュー案を保持する。

新規10シナリオ、人物／事例各10問、正解なし4問。各種別30候補、計60原本。旧集合の課題・対応文は再利用していない。
分類体系と一般的な主業務・副業務書式は共通。シナリオ内容の独立性であり実社員の無作為抽出ではない。
各問の同シナリオdirectを3、supportを2、negativeと他シナリオを0とする案。正解なし4問は全候補0。
副業務の共同実施は正解だが、案内配布のみは非正解。ラベルに不一致があればモデル実行前に訂正する。

[全原文](sources.md)・[全候補の判定](dataset.json)を確認してください。

| 質問ID | 質問 | 正解（候補ID末尾） |
|---|---|---|
| `i30i-software-cache-person-query` | 人気商品へのアクセスが集中してキャッシュ更新時にDB負荷が跳ね上がる問題を抑える経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-software-cache-knowledge-query` | 人気商品へのアクセスが集中してキャッシュ更新時にDB負荷が跳ね上がる問題を抑える実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-software-timezone-person-query` | 海外拠点の予約時刻が夏時間の切替日にずれる問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-software-timezone-knowledge-query` | 海外拠点の予約時刻が夏時間の切替日にずれる問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-mechanical-thermal-person-query` | 温度変化で軸と穴のはめ合いがきつくなる問題を設計で防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-mechanical-thermal-knowledge-query` | 温度変化で軸と穴のはめ合いがきつくなる問題を設計で防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-mechanical-fatigue-person-query` | 繰り返し荷重でブラケットの角から亀裂が発生する問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-mechanical-fatigue-knowledge-query` | 繰り返し荷重でブラケットの角から亀裂が発生する問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-sales-discount-person-query` | 担当者ごとの無秩序な値引きで粗利が下がる商談運用を改める経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-sales-discount-knowledge-query` | 担当者ごとの無秩序な値引きで粗利が下がる商談運用を改める実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-sales-channel-person-query` | 代理店同士が同じ見込み顧客に重複提案して揉める問題を整理する経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-sales-channel-knowledge-query` | 代理店同士が同じ見込み顧客に重複提案して揉める問題を整理する実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-manufacturing-calibration-person-query` | 検査ゲージのずれによって不良品を合格にしてしまう問題を減らす経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-manufacturing-calibration-knowledge-query` | 検査ゲージのずれによって不良品を合格にしてしまう問題を減らす実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-manufacturing-traceability-person-query` | 不良が発覚した際に対象ロットを特定できない問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-manufacturing-traceability-knowledge-query` | 不良が発覚した際に対象ロットを特定できない問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-operations-permit-person-query` | 交代勤務の切替時に保守作業の許可と危険区域の情報が抜ける問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-operations-permit-knowledge-query` | 交代勤務の切替時に保守作業の許可と危険区域の情報が抜ける問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-operations-spares-person-query` | 交換部品の欠品で設備復旧が遅れる問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30i-operations-spares-knowledge-query` | 交換部品の欠品で設備復旧が遅れる問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30i-no-answer-01` | コード署名鍵の漏えい時に署名を失効し再発行した経験者を探したい。 | 正解なし |
| `i30i-no-answer-02` | 光学センサーの迷光を遮光構造で減らした実施済み事例を探したい。 | 正解なし |
| `i30i-no-answer-03` | 深海生物の分類学の研究で新種を記載した研究者を探したい。 | 正解なし |
| `i30i-no-answer-04` | 古代碑文を解読して年代を確定した学術調査の事例を探したい。 | 正解なし |

正解IDは質問IDの`-query`を`-direct`／`-support`に置換した値。各原文の対応はsources.mdに明記。

## 固定する検証条件

人物10／事例15、救済2、Top-3、質問閾値0.1、文書確率0.2、Noul0.5。既存本番実装のプロンプト・根拠予算1,200バイトを使う。
一次Recallは人物／事例各90%以上、最終HitRate@5とPrecision@5は90%以上、正解なし誤推薦0、根拠ID／原文整合100%。ウォームp95は15秒以下、coldは別報告。
独立確認結果を見て閾値・モデル・プロンプト・ラベルを調整しない。未達なら原因を記録し、次の調整・別独立集合へ進む。
