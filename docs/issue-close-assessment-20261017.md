# 未完了IssueのClose判定（2026-10-17）

main `ca75ded43bd0a93a73ff58059633bc74ed21e8d4`、GitHubのopen Issue本文、マージ済PR38〜42、保存された受入資料を照合した。PR39〜42は各head全25チェック成功・Copilot追加指摘0、マージ後main CI成功。これらの成功は実環境受入・独立品質評価・採用判断を代替しない。新しい試験・実環境操作・Issue変更は行っていない。

**今回CloseできるIssueは0件。** 現在openは次の10件。未チェックのままでも実装済みの部分があるため、Issue全体の完了条件で判断する。

| Issue | 実装・証跡との照合 | Openを維持する残条件 |
| --- | --- | --- |
| [#30](https://github.com/kokutaro/dse-growth-loop/issues/30) | PR36/37の検索改善・独立v3、PR39の安全経路・warm/cold保存結果 | warm p95 15秒未達、cold02 HTTP証跡欠落、cold起動待ちの包含・完全なUI品質確認が未了。遅延改善延期は受入承認ではない |
| [#31](https://github.com/kokutaro/dse-growth-loop/issues/31) | PR29のLuna6順序比較、PR38のオフライン再採点・失敗区分 | 保存比較は1条件。モデル／prompt／根拠量／確率提示の事前固定した少数比較と最終評価が未完。歴史的誤推薦も保持 |
| [#32](https://github.com/kokutaro/dse-growth-loop/issues/32) | PR38の採点器・レビュー票 | humanGoldApproved=false、意味一致の確認分母0。人承認goldと独立90%ゲートは未成立 |
| [#33](https://github.com/kokutaro/dse-growth-loop/issues/33) | PR38/40の分類中・再順位付け・AI入力／HTTP表示／共有のFake回帰 | 実JWT／実Cosmosを含む検索固有の統合受入・最小実行計画と証跡が未完。Memory／fake transport成功だけで最終合格にしない |
| [#34](https://github.com/kokutaro/dse-growth-loop/issues/34) | PR38の部分操作収集、PR41の保存観測→測定契約変換 | 同runの全AI／SDK retry RU／保存・索引／runtime等と実請求の統合不足。state=incomplete、monthlyGate=pending。collector追加改修・追加測定は今回行わない |
| [#35](https://github.com/kokutaro/dse-growth-loop/issues/35) | PR42の6資料・候補設定・hash／再生成一致・未達一覧パケット | 実設定／prompt等の不明、子Issueの未達、総月額・現行価格／請求照合、人による採用判断が残る。not_adopted、本番適用不可 |
| [#15](https://github.com/kokutaro/dse-growth-loop/issues/15) | PR38の手動分析・Live adapter・予算予約・停止証跡付き復旧CLI | 許可少量の実AI・実Job／復旧受入、権威ある部署情報・履歴／失効設計が残る。今回の設計案作成も実装完了とは扱わない |
| [#16](https://github.com/kokutaro/dse-growth-loop/issues/16) | 個別機能のFake／E2E・停止証拠と全体試験の設計資料 | #14/#15等の先行未完、全体シナリオ・故障復旧・隔離バックアップ復元・運用受入が未完。個別CIの合計で全体受入を代替しない |
| [#14](https://github.com/kokutaro/dse-growth-loop/issues/14) | PR29の評価基盤・ベースライン、子#30〜35の追加成果 | 元Todoは複合条件未達の4/12。子Issueの品質・費用・採用判定と正式照合が残る |
| [#1](https://github.com/kokutaro/dse-growth-loop/issues/1) | 各機能の実装と子Issue履歴 | G2/G4を含む検索・分析・全体受入ゲート未完。実装／デモ成功を検証版完成へ昇格しない |

#1→#14→#30〜35、#1→#15/#16は親子・集約関係であり、同じ完了条件の重複Issueではない。#33は検索固有の認可、#16は全体受入、#34は測定、#35は総合採用判断を分担するため重複扱いで閉じない。Issue本文の古い「未実装」や未チェック欄だけで現在のコード状態を判断せず、将来の正式受入時に証跡付きで更新する。
