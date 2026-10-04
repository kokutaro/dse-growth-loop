# Issue #30 独立 v3 の原文・ラベル案

人による確認待ち。AI分類・再順位付けは未実行（0回）。v2の観測後修正をこの集合では調整しない。

[架空原本60件](sources.md)・[全正解ラベル案](dataset.json)・[実行・稼働上限案](execution-plan.md)。

人物・事例各10問と正解なし4問。同じシナリオの直接実施を3、共同実施・副業務を2、案内配布のみと他シナリオを0とする案。実社員の無作為標本ではなく、架空シナリオの確認集合。以前の質問・対応本文と完全一致は0件。

| 質問ID | 質問 | 正解ラベル案 |
|---|---|---|
| `i30p-software-event-dedup-person-query` | 同じ決済イベントが再送されて売上を二重計上する問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-software-event-dedup-knowledge-query` | 同じ決済イベントが再送されて売上を二重計上する問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-software-unicode-cut-person-query` | 絵文字を含む文章の切り出しで表示が壊れる問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-software-unicode-cut-knowledge-query` | 絵文字を含む文章の切り出しで表示が壊れる問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-mechanical-thermal-joint-person-query` | 温度変化で伸びる配管が支持部を押して損傷する問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-mechanical-thermal-joint-knowledge-query` | 温度変化で伸びる配管が支持部を押して損傷する問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-mechanical-cable-bend-person-query` | 可動部のケーブルが繰り返し曲げられて断線する問題を減らす経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-mechanical-cable-bend-knowledge-query` | 可動部のケーブルが繰り返し曲げられて断線する問題を減らす実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-sales-quote-scope-person-query` | 顧客へ提示した見積の作業範囲が曖昧で受注後に認識違いが起きる問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-sales-quote-scope-knowledge-query` | 顧客へ提示した見積の作業範囲が曖昧で受注後に認識違いが起きる問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-sales-channel-owner-person-query` | 代理店と自社が同じ顧客へ別々に提案して商談が衝突する問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-sales-channel-owner-knowledge-query` | 代理店と自社が同じ顧客へ別々に提案して商談が衝突する問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-manufacturing-lot-label-person-query` | 製造途中で部品のロット表示が混ざり追跡できなくなる問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-manufacturing-lot-label-knowledge-query` | 製造途中で部品のロット表示が混ざり追跡できなくなる問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-manufacturing-furnace-drift-person-query` | 熱処理炉の温度の偏りで製品の硬さがばらつく問題を減らす経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-manufacturing-furnace-drift-knowledge-query` | 熱処理炉の温度の偏りで製品の硬さがばらつく問題を減らす実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-operations-room-booking-person-query` | 社内会議室の予約が重なり利用者同士の調整が繰り返される問題を改善する経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-operations-room-booking-knowledge-query` | 社内会議室の予約が重なり利用者同士の調整が繰り返される問題を改善する実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-operations-return-assets-person-query` | 退職時に会社貸与物の回収漏れが発生する問題を防ぐ経験のある担当者を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-operations-return-assets-knowledge-query` | 退職時に会社貸与物の回収漏れが発生する問題を防ぐ実施済みの事例を探したい。 | `direct`=3、`support`=2、他=0 |
| `i30p-no-answer-01` | 画像の文字領域を検出して傾き補正した経験者を探したい。 | 全候補0 |
| `i30p-no-answer-02` | 油圧回路の急激な圧力上昇を測定して抑えた実施済み事例を探したい。 | 全候補0 |
| `i30p-no-answer-03` | 古代貨幣の刻印を比較して鋳造地を研究した担当者を探したい。 | 全候補0 |
| `i30p-no-answer-04` | 中世音楽の写譜を比較して旋律の変遷を研究した事例を探したい。 | 全候補0 |

正解なし01・02は分野内だが該当原本なし、03・04は分類体系の対象外の想定。対象外は検索未実行と区別する。ラベル変更があれば実AI送信前に再固定する。

人物15／事例15／救済2、質問Top3・閾値0.1、文書0.2、Noul0.5、根拠1,200バイト、`search-query-v2`／`search-rerank-v2`、Jev 1.13.0／Luna6を固定する。一次Recall・種別ごとのHit@5・Precision@5は各90%以上、正解なし誤推薦0、根拠整合100%、warm HTTP p95は15秒以下。coldは別報告。
