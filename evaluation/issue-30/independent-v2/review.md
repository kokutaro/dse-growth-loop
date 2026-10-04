# Issue 30 独立集合の正解ラベル案

状態：人による確認待ち。AI分類・LLM応答は未取得。

新規10シナリオ、人物／事例各10問、正解なし4問。各種別30候補、計60原本。旧集合の課題・対応文は再利用していない。
分類体系と一般的な主業務・副業務書式は共通。シナリオ内容の独立性であり実社員の無作為抽出ではない。
各問の同シナリオdirectを3、supportを2、negativeと他シナリオを0とする案。正解なし4問は全候補0。
副業務の共同実施は正解だが、案内配布のみは非正解。ラベルに不一致があればモデル実行前に訂正する。

[全原文](sources.md)・[全候補の判定](dataset.json)を確認してください。

| 質問ID | 質問 | 正解（候補ID末尾） |
|---|---|---|
| `i30n-software-deadlock-person-query` | 同時更新のロック順序の違いで取引処理が停止する問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-software-deadlock-knowledge-query` | 同時更新のロック順序の違いで取引処理が停止する問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-software-pagination-person-query` | 更新が続く一覧でページ移動時に項目が重複または欠落する問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-software-pagination-knowledge-query` | 更新が続く一覧でページ移動時に項目が重複または欠落する問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-mechanical-resonance-person-query` | 駆動周波数付近の共振で装置の振動が増幅する問題を抑える経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-mechanical-resonance-knowledge-query` | 駆動周波数付近の共振で装置の振動が増幅する問題を抑える実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-mechanical-corrosion-person-query` | 異種金属の接触部が屋外で腐食する問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-mechanical-corrosion-knowledge-query` | 異種金属の接触部が屋外で腐食する問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-sales-renewal-person-query` | 契約更新の連絡が遅れて継続顧客を失う問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-sales-renewal-knowledge-query` | 契約更新の連絡が遅れて継続顧客を失う問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-sales-tender-person-query` | 入札で必要な提出書類の不足により失格となる問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-sales-tender-knowledge-query` | 入札で必要な提出書類の不足により失格となる問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-manufacturing-moisture-person-query` | 樹脂材料の吸湿で成形品に気泡が発生する問題を減らす経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-manufacturing-moisture-knowledge-query` | 樹脂材料の吸湿で成形品に気泡が発生する問題を減らす実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-manufacturing-setup-person-query` | 品種切替時の段取り作業で生産停止が長引く問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-manufacturing-setup-knowledge-query` | 品種切替時の段取り作業で生産停止が長引く問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-operations-visitor-person-query` | 来訪者の退館記録が欠けて災害時の所在確認ができない問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-operations-visitor-knowledge-query` | 来訪者の退館記録が欠けて災害時の所在確認ができない問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-operations-expense-person-query` | 出張精算の費目誤りで差戻しが続く問題を減らす経験のある担当者を探したい。 | `direct`=3、`support`=2 |
| `i30n-operations-expense-knowledge-query` | 出張精算の費目誤りで差戻しが続く問題を減らす実施済みの事例を探したい。 | `direct`=3、`support`=2 |
| `i30n-no-answer-01` | 音声認識の専門用語の誤変換を辞書学習で減らした経験者を探したい。 | 正解なし |
| `i30n-no-answer-02` | 真空容器の微小漏れをヘリウム検査で特定した実施済み事例を探したい。 | 正解なし |
| `i30n-no-answer-03` | 古典文学の写本の系統を比較研究した担当者を探したい。 | 正解なし |
| `i30n-no-answer-04` | 遠方銀河の赤方偏移を観測して距離を推定した研究事例を探したい。 | 正解なし |

正解IDは質問IDの`-query`を`-direct`／`-support`に置換した値。各原文の対応はsources.mdに明記。

## 固定する検証条件

人物15／事例15、救済2、Top-3、質問閾値0.1、文書確率0.2、Noul0.5。既存本番実装のプロンプト・根拠予算1,200バイトを使う。
一次Recallは人物／事例各90%以上、最終HitRate@5とPrecision@5は90%以上、正解なし誤推薦0、根拠ID／原文整合100%。ウォームp95は15秒以下、coldは別報告。
独立確認結果を見て閾値・モデル・プロンプト・ラベルを調整しない。未達なら原因を記録し、次の調整・別独立集合へ進む。
