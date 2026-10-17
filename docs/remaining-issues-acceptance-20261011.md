# 残Issueの受入条件・計測準備（2026-10-11）

## 2026-10-17：#15/#16のオフライン復旧を優先

main `2f780b1` でopen PRは0件、open Issueは #1/#14/#15/#16/#30〜#35。既存のJob通知漏れ/reconcile・重複通知・リース切れ・明示retry試験と、事業分析の停止/uncertain予約回収を再利用し、[復旧serviceの管理者再照合と保存直前競合の不足](validation-results/20261017-analysis-recovery-authority/results.md)を修正する。偽role、走査中の失効、検査後のcontrol/person/fence変更をローカル故障注入で再現した。新しい復旧経路を作り直さず、既存batchに認可とCASを追加する。

前段で報告したクラウド実AI3操作は汎用Choice/Noul・構造化・再順位付けの接続smokeであり、事業仮説の共通課題確認・集計・人の判断のライブ受入ではない。#15の実分析/復旧には、確認済みanalyst管理主体と新しい分析同意・独立資料、別途レビューした停止証拠が必要。現在有効なダミー1名へ権限を足したり、残り92名や旧同意を有効化したりしない。

#16は先行Issue未完了のため全体受入を完了としない。今回のローカル故障注入を、実JWT/実Cosmos競合・隔離バックアップ復元・恒久image展開の成功へ置き換えない。人の安全レビュー・業務判断も代理で完了せず、#15/#16は全条件が満たされるまでOpenを維持する。Azureの起動・変更・有料推論を行わず、初回検索遅延/p95最適化と作業台帳の再導入も行わない。

## 2026-10-17：#35の採用判断用パケット

[保存資料と候補設定をhashで固定するパケット](validation-results/20261017-issue35-adoption-readiness/results.md)を追加した。#30〜#34の依存を保ち、warm未達・cold02欠落・構造化gold未承認・実認可受入pending・総月額不明を列挙する。出力は判断待ち/未採用/本番適用不可に固定し、fixture成功や改善延期を受入承認に転用しない。コードのマージと本番採用・展開は別である。追加計測・価格照会・台帳照合・遅延改善は行わない。

## 2026-10-17：#34の保存観測変換

#34は引き続き#30/#33に依存し、実環境受入の完了待ちは残る。一方、[保存済み数値観測を既存測定契約へ変換するオフラインCLI](validation-results/20261017-issue34-saved-evidence/results.md)を追加した。warm全24件p95=17.403440秒、固定22件p95=18.002211秒で15秒未達。cold02 HTTP欠落、RU/AI/保存量/総月額不明を保持し、部分変換を#34/#35の合格へ昇格しない。初回遅延の追加計測・最適化・追加Azure計測・台帳照合・監視基盤改修は行わない。

## 2026-10-17：#33のquery-only経路回帰

最新main `e61f7a3` を基点に、[query-only分類からAI入力・HTTP応答・共有確定までのローカル回帰](validation-results/20261017-issue33-query-boundaries/results.md)を追加した。分類中のscope/撤回/参加可否/版/根拠変更と分類後の原本読取障害を検証する。既存の条件指定済み検索・再順位付け中の再照合回帰を維持し、全体の実JWT/実Cosmos受入はpending。#33は先行完了待ち不要、#34は#30/#33、#35は#30〜#34に依存するため#33から進める。

初回検索遅延はユーザーの06:06:41 UTCの指示で既知課題として残す。正式p95未達・cold証跡制約を合格へ変更せず、段階別計測追加・最適化・Azure追加稼働・台帳照合・監視基盤改修は今回行わない。以下は保存当時の履歴として区別する。

## 2026-10-17 の更新

[Issue30認証付き測定結果](validation-results/20261017-issue30-measurement-summary/results.md)：warm24/cold3のUI実行は終了。warm HTTP p95は全24件17.403440秒／200のみ18.002211秒で15秒未達。cold02のHTTP/HAR等欠落、coldの起動待ち包含未確認、完全なUI品質再検証未了により受入pending。全46revisionの復元・actual0を05:56:53 UTCに二重確認した。ユーザーは06:06:41 UTCに初回検索遅延の追加計測・改善を延期し、既知課題を残して他の未完了Issueへ進むと判断した。閾値緩和・受入承認ではない。Azure停止維持、追加実測・台帳照合・監視改修は今回行わない。

最新GitHubでは#33は先行完了待ち不要、#34は#30/#33、#35は#30〜#34に依存する。次は#33の既存ローカル認可回帰を照合し、query-only分類からAI入力・最終表示・共有確定までの不足境界を偽AIで検証する。以下の棚卸しは保存当時の履歴として残す。

今回のローカル作業は受入に必要な実装・回帰・資料の準備であり、Azure検証版の最終受入ではない。GitHub mainは `78f182a30f106a8d34f7989d39d23f622e5d5c08`、open Issueは #1、#14、#15、#16、#30〜#35、open PRは0件という読み取り結果を基点にした。GitHubのチェック欄、Issue状態、Azure設定は変更していない。

前段のDemo実装から継続し、匿名DTO/API、独立した表示名開示同意、Live分析アダプター・共有AiLedger連携・オフライン復旧CLI、操作の部分収集器をローカルコードへ追加した。これらは**実装済み・最終ローカル検証成功（.NET535、E2E49件）**。最新段階の結果は [20261011-production-contracts/results.md](validation-results/20261011-production-contracts/results.md) で判定し、前段の成功件数を追加後の最終成功件数として再利用しない。この継続段階の実Azure接続/データプレーン操作は0回、有料モデル送信は0回で、設定有効化や展開も行っていない。

## 判定と作業順

| Issue | 既存証拠で確認できる範囲 | 今回安全に進める範囲 | 受入に残る条件 |
| --- | --- | --- | --- |
| [#30](https://github.com/kokutaro/dse-growth-loop/issues/30) | 承認済み独立v3の実サービス48検索。人物/事例の一次Recall100%、指定補助正解損失0、原文/根拠整合60/60、サービス品質合格 | 保存captureを現行採点器で再採点。正常分類・service48を再送しない | 認証付きwarm24のHTTP/UI、cold3、稼働/台帳/停止証跡。総合ゲートはpending |
| [#33](https://github.com/kokutaro/dse-growth-loop/issues/33) | 原本再照合・検索/共有回帰の既存実装とMockテスト | scope違い、撤回、旧版、参加不可、AI処理中の版変更・認可読取失敗の追加回帰、匿名allowlist DTO/API、旧raw APIの本番operator制限、独立同意を実装。最終ローカル検証成功 | 実JWT/実Cosmos/複数replicaの認可・撤回回帰は計画/承認後。構造化IDの除去と自由文の完全匿名化を分ける。Mock成功を実サービス成功にしない |
| [#31](https://github.com/kokutaro/dse-growth-loop/issues/31) | 旧Luna6の元順序19/20、逆順一致23/24、逆順のみ二重請求負例推薦1件 | 同一候補hashの保存応答比較、欠損・切詰め・不正JSON/根拠の回帰、送信計画固定 | モデル/プロンプト/根拠量/確率有無の未測定比較、変更候補の必要最小再評価 |
| [#32](https://github.com/kokutaro/dse-growth-loop/issues/32) | 構造化JSON・引用検証基盤。意味的一致90%の独立合格証拠はない | 項目別分母・欠落/unknown/誤分類・根拠誤りを定義した採点器とレビュー票 | 新しい項目別goldの人による承認。承認済み正解がない状態で90%達成とは判定しない |
| [#34](https://github.com/kokutaro/dse-growth-loop/issues/34) | 旧投影48取得の平均7.1212 RU、取得p95 49.14ms、旧LLM単体p95 1.86秒 | 同run計測schema/未承認planに加え、原本/投影/フェンス/監査/台帳等の操作収集とoperator専用exportを実装。最終ローカル検証成功 | SDK内部retryの試行別RU、全AI usage/失敗/留保との統合、保存/索引、runtime/転送/ログ、請求照合とHTTP/UI冷温実測が未完了。部分exportのacceptanceGatePassedはfalse |
| [#35](https://github.com/kokutaro/dse-growth-loop/issues/35) | 部分計測と月100検索の投影RU約0.032円推計 | 各ゲートと不明値を集約、月額式・採用候補の版/hash整理 | #30〜#34の合否、全Azure月額と8,000円比較、採用/制限の明示決定。恒久適用は別作業 |
| [#15](https://github.com/kokutaro/dse-growth-loop/issues/15) | #12の許可済みKnowledgeが入口。前段Demoで独立課題の重複排除・数値集計・人の判断・撤回/冪等/上限を確認 | Live drafter、共有AiLedger予約/精算、版とモデル/価格/FX/limits hash固定、持続スロットのinspect/recover CLIを実装。最終ローカル検証成功 | Live入口は既定501。明示Enabled・LiveOptIn・停止解除・承認価格/FX/正の上限で実装経路へ進む。許可済み少量実AI評価、実Job/復旧受入、部署の権威ある正本/API設計は未完了。LLMに集計値を生成させない |
| [#16](https://github.com/kokutaro/dse-growth-loop/issues/16) | 個別機能のローカル証拠、過去の接続/停止証拠 | 仕様確認・故障注入ケースと操作台本の設計準備 | #9/#13/#12/#15/#14完了後に全体実装・結合。実環境の復旧/停止/バックアップは別承認 |
| [#14](https://github.com/kokutaro/dse-growth-loop/issues/14)、[#1](https://github.com/kokutaro/dse-growth-loop/issues/1) | 検索親4/12、実働化親の段階ゲートは未完了 | 子の証拠と元Todoの対応を維持 | 子ゲートの全達成、または未達を具体化してユーザーが明示承認した検証版制限。親を自動完了にしない |

推奨順は #30保存応答確認と#33認可境界 → #31/#32評価準備と#15ローカル実装 → #34統合計測 → #35採用判断 → #16全体受入。#16本文は「先行Issueの完了後に実装・結合へ着手」としており、今回準備した試験設計を全体結合完了と呼ばない。

## 過去実測と今回の再採点

[2026-10-04独立v3結果](validation-results/20261004-issue-30-independent-v3-production/results.md)は実サービスの保存済み証拠。新しく承認された60原本/24問を、全対象/対象指定各24件で評価した。正解ありは20問、正解なしは4問で、そのうち2問は分類対象外。人によるラベル承認は [approval.json](../evaluation/issue-30/independent-v3/approved/approval.json) を参照。合成ダミー集合の実務代表性・統計的独立性は未検証。

2026-10-11に有料送信なしで次を実行した。

```sh
python3 scripts/issue30_independent_v3_report.py \
  --capture docs/validation-results/20261004-issue-30-independent-v3-production/production-report.json \
  --output docs/validation-results/20261011-acceptance-inventory/issue30-v3-replay-summary.json
```

[再採点JSON](validation-results/20261011-acceptance-inventory/issue30-v3-replay-summary.json)は `serviceQualityPassed=true`、`overallGate=pending-authenticated-http-and-timing`。表示される138試行・151,620入力/28,399出力token・3.270987036円は**旧captureに記録された使用量**で、今回の新しい呼出し・請求・現在残高ではない。再採点は実モデル品質を新たに測った結果でもない。

[旧Cosmos結果](validation-results/20261003-issue-14-cosmos/results.md)の7.1212 RU/検索と49.14msは投影ページ取得のみで、原本認可、フェンス、監査、台帳、UI、coldを含まない。旧Luna6の1.86秒もAPI単体。旧v2の18.622秒は未達履歴として維持し、v3の合格根拠へ転用しない。

## 基準変更の履歴

| 対象 | 初期の案/目標 | 記録された承認後の基準 | 使い方 |
| --- | --- | --- | --- |
| ウォーム検索説明 | #14の10秒目標 | #14「2026-10-03 対応状況」にユーザー承認済みp95 15秒と変更理由を記録、#34も15秒 | warm24全応答と22実検索の線形p95を双方15秒以下で判定。coldは別報告、合否なし |
| 月額 | #1の1万円税別は予算申請案 | #14/#35の承認予算8,000円 | 税/契約条件と実利用量を確認して全Azure月額を比較。支出開始の包括承認ではない |
| 月100検索 | 検証用途の仮定 | 実利用回数としては未確認 | 入力変数として明示し、観測実績に更新。約0.032円は投影RU部分だけ |

この資料は既存の基準を緩めない。新しいgoldや期間への過去承認の転用、予算残量の自動リセットも行わない。

## #34：同runの測定契約

[measurement-record.schema.json](evaluation/issue-34/measurement-record.schema.json) と [measurement-plan-20261011.json](evaluation/issue-34/measurement-plan-20261011.json) は**総合測定の設計準備**。その保存時点の「collector未実装」は前段の履歴で、継続コードでは部分収集器を追加した。planは実行承認・実サービス計測を表さず、観測済みexportで上書きしない。未観測値は `null`、集合は空、状態は `planned` とし、0円/0 RU/0秒の実測値に変えない。

既存 [SearchBudget](../src/Dse.Infrastructure/SearchBudget.cs) は `projection_page` と `primary_read` の検索予算用集計で、フェンスなど検索全体の費用を網羅する契約には変更していない。継続追加の [OperationMeasurements](../src/Dse.Infrastructure/OperationMeasurements.cs)、[RecordStore](../src/Dse.Infrastructure/RecordStore.cs)、[HTTP収集middleware](../src/Dse.Api/OperationMeasurementsMiddleware.cs) が、生成run/request/correlation IDの同じ収集境界で投影ページ・原本/根拠・フェンス・監査・台帳・HTTP操作を固定kindへ記録する。Demo/LocalのRUはnull。実Cosmos経路もSDKの公開応答単位の取得機構を実装した段階で、今回実Cosmosの測定値は取得していない。

`GET /api/v1/operation-measurements` はoperator限定、`Cache-Control:no-store` の有界process内buffer export。schemaは `issue34-operation-export-v1` で総合run契約 `issue34-measurement-v1` と別。tenant/principal/source ID・本文・route・任意例外messageを保持せず、未取得component/欠落kindを残し、`completeRequestUnits=null`、`completeMeasurement=false`、`acceptanceGatePassed=false` を返す。SDK responseの `attempt=1` は内部wire retry数ではなく、batch全体chargeも各項目へ分割できたとは扱わない。SDK内部再試行の試行別RU、全AI token/cache/reasoning/失敗予約の収集・請求、索引/保存/runtime/転送/ログ/UI冷温との統合は残る。Live分析のAiRunに保存するusage/留保と部分exportが存在しても、#34全件測定完了や総月額合格を意味しない。[計測仕様](operation-measurements.md) を参照。

| 区分 | 必要な記録 | 欠落/重複を防ぐ照合 |
| --- | --- | --- |
| 検索経路 | runId、requestId、correlationId、question/target、候補hash、原本/アクセス/投影版、開始終了とHTTP/UI完了 | 422の対象外2件と22検索を区別、未完了や再送を除外して黙って成功件数を増やさない |
| Cosmos | 投影各ページ、原本/根拠認可読取、フェンス読取/更新、監査書込、AI台帳の予約/精算/read、再試行ごとのRU | SDK response単位のoperationId/attemptを一意化。batch全体RUと各項目RUを二重加算しない。429/失敗chargeも保存 |
| AI | 分類/再順位付け/補助等の全モデル、論理request/HTTP試行、正常/失敗/再試行、入力/出力/cache/reasoning、予約上界、単価/FX | cache tokenとreasoning tokenのモデル固有包含関係を明記、usage不明の失敗を0にせず予約を留保、正常保存応答は再送しない |
| 保存/索引 | container/scope、観測期間、データ/索引/バックアップ/Blob/Queueの量と取得方法 | 分離できない値はtotalのみとunseparated範囲を記録。共用container値をこのrunの増分と言わない |
| runtime/転送/観測 | 全revisionのreplica-seconds、Job秒、CPU/GiB、要求/転送、ACR、ログ/AppInsights、既存固定費 | 前累計から継続し、停止/復元窓・時刻欠落・同時revisionを含める。無料枠適用を推測しない |
| 費用 | meter/SKU/region、取得日/有効日/通貨、FX日付、税/契約/割引、未請求推計と確定請求、留保 | 計測・推計・請求照合を分ける。unknown componentがある総額はnull、全Azure月額ゲートpending |

warmはSPA/認証を先に用意した状態で24原文を各1回、target=`all`。coldはSPAを保持してAPI実replica0の証拠を先に取り、起動後にGETで温めず計画3問を最初の検索として送る。HTTP開始/終了・UI表示完了・サービス内部処理を別々に測り、coldをwarmの標本へ混ぜない。p50/p95は計算法と母集団を保存し、旧nearest-rankの投影値と新しい線形HTTP値を比較する際は差を明記する。

CSVはschemaの各operationを1行へ平坦化し、runId/operationId/attemptを主キーにする。生HAR/JWT/認証cache、原本本文、実社員情報、秘密envは公開証跡へ含めない。sanitized要求/応答/時刻/版/hashと集計だけを保存する。

## 実環境再開が止まる理由と必要な準備

[2026-10-08の再開条件](validation-results/20261008-workspace-ui/resume-issue30.md)と [2026-10-04引継ぎ](validation-results/20261004-issue-30-independent-v3-production/handoff.md)を参照。今回Azure ARM、Cosmos、Job、モデルへ状態確認/実行を送っておらず、下表の数値は**過去資料**である。

| 不足 | 保存された履歴と今回の判定 | 再開前に具体化するもの |
| --- | --- | --- |
| 共有台帳 | 10/4の703試行・留保1,200円・残779.880264524円は旧週。最新reservation/blocked/日次token/週次費用/承認reference未取得 | 読取経路自体の課金/Job実行範囲を含む承認、最新台帳と実請求未照合分の留保 |
| 累計runtime | 10/8再構成Job6249/9000秒（6151+98）、API14340/18000秒は親引継ぎ値で独立再構成なし | API累計原本、全実行追加分、失敗/欠落を保守計上、復元込みの壁時計枠。週が変わってもサイクル累計をリセットしない |
| 適用価格/総上界 | 旧FX162・旧Luna6単価による54試行27.1188円/1,026,000token、予備込み66試行33.1452円/1,254,000tokenは条件付き。RU/保存/ログ/転送等未上界 | Azure該当meter/SKU契約価格、現在FXと税、全項目の上界、今回の明示費用承認。小さいAI/compute見積もりだけで総額保証しない |
| 現行配置との差 | 10/8読取ではAPI issue9-final5、Job issue30-hardening1へ復元・推論停止。dse-luna（旧5.6）とdse-luna6-eval（v3対象）も別 | v3 dataset/scope、API/Runner digest、source hash、モデル/設定の差分固定。起動/権限/認証変更が必要なら別承認 |
| 独立停止/復元 | checkoutに停止watchdogの実装/回帰なし。restoration.jsonは一致判定で元テンプレート本体ではない | private復元snapshot、別操作経路の停止担当、親終了/CLI失敗/認証失効/上限超過/復元失敗を含むMock検証、期限前の復元余裕と失敗手順 |
| 過去追加修正 | `issue30-v3-local-resume-20261005.zip` / manifestをMacで取得していない | 親が安全に回収し、基点/hash/差分を照合。現行採点器と重複適用しない。watchdog検証済みと推測しない |
| 本人認証と観測 | 過去に短時間API起動→通知→ログイン待ちで失敗 | 本人が操作可能なブラウザー、認証/記録/停止担当を**課金起動前**に用意。復元込みの単一承認枠。短時間方式を繰り返さない |

`--ledger-status` はCosmos監査/台帳読取であり、`--weekly-budget-initialize` は名前や `readOnlyExport` 変数によらず正本を更新する。今回どちらも実行しない。Budget表示は共有AI台帳や確定請求の代わりにならず、Cost Management 429や価格検索0行も料金0円を意味しない。

別タスクのTailscale/Foundry Private Endpoint接続成功は、この台帳・費用・停止・認証・採用条件の証拠を追加しない。匿名UI向けDTOは継続コードで実装したが、実JWT/実Cosmos/通知/ログ/運用経路を含む匿名性の受入は別課題。既存の固定検索検証経路でのSLOと匿名性の合格を混同しない。

2026-10-11のVNet移行後に、[Issue30のローカル検証準備](issue30-migration-local-preparation.md)を追加した。全revision/実replicaの停止snapshotと移行watchdogのMock回帰、共通期限の独立supervisor/実OS故障回帰、snapshot保護、warm24/cold3の保存記録・根拠採点を整備。独立レビューで再現したAPI確認によるWorker停止要求の遅延、成功CLIの子process残留、cold応答runIdの相互/warm流用を修正し回帰を追加。Copilot指摘の準備期限によるWorker要求の枯渇、snapshot取得中のrevision競合、HTTPより早いwarm UI時間の受理も回帰を追加して修正した。さらにv3候補IDのscope、成功deactivateの空応答、preflightのApp/Environment組を検証した。上表の「checkoutにwatchdog実装/回帰なし」はこの追加前の状態であり、独立した実停止/復元・最新台帳・本人認証・費用承認は引き続き未確認。今回Azureを起動せず、合格へ変更しない。

## 追加実装の状態と具体的な未完了

| 追加領域 | ローカルに実装した境界 | 残る受入条件 |
| --- | --- | --- |
| 匿名業務DTO/API | `anonymous-workspace-v1`、room局所aliasと課題/招待/候補/会話/根拠/公開範囲/部署/事例のopaque token、現原本/参加資格の再照合、本人acceptedで即時参加。旧raw検索/会話APIは本番operator制限、本人プロフィール/文書は所有者制限 | 構造化IDのallowlist除去は自由文・引用・識別文脈の完全匿名化ではない。`freeTextAnonymized=false`。実JWT/実Cosmosの並行操作・通知・ログ・ブラウザー保存内容を含む受入が必要 |
| 独立表示名開示 | `/workspace/rooms/{roomId}/identity-consent`、現全参加者が同じprofile/binding/grant/membership版へ本人同意。未成立は自分含む全sharedName=null。撤回/指紋不一致を観測したGET/POSTは全grantを永続失効 | 本人profileのdisplayNameは自己申告で公的氏名の照合ではない。単調なAuthPolicy版がなく、未観測の設定変更→復元履歴を検出できない。停止下の設定変更/バックアップ復元では同意一括失効または単調な認可版を設計・検証する。既に見られた名前/画像の回収は保証しない |
| Live手動分析 | Demo/MockとAzure/Liveの設定版を分離、Enabled/停止/opt-in/正のtoken・call・JPY/価格FX版で入口を制限。共有AiLedger上界予約/usage精算、group送信1回・自動retry0、全入力の最新版/双方分析同意を送信前後に照合 | 新しい実AI送信は0。最新共有週/日台帳と承認価格/FXの実設定、許可少量評価、実Job実行と停止/復元の検証は未実施。既定501は維持するが、adapterそのものを未実装とは呼ばない |
| オフライン復旧 | `--inspect-business-analysis-recovery` / `--recover-business-analysis`。operator+analyst、run/slot ETag、正確なpending予約集合、明示承認、独立停止証拠のreference/hashを要求。対象を1batchでabort・slot解除、留保をuncertainに保ち返金0 | flag/操作者の申告/取消tokenは遠隔provider停止の証明ではない。独立したAPI/Worker/provider終結証拠と実環境の停止・復元試験が必要。HTTP復旧経路や無限再送は追加しない |
| 部署を含む分析集計 | 公開要約の分析metadataは編集と双方再同意に結び付け、独立課題数/別部署数などをコードで集計。公開根拠DTOでは部署IDをnull、ワークスペースではopaque部署tokenへ置換 | 現在はダミーの同意済みmetadataであり、実組織部署の権威ある正本・有効日/異動/履歴・入力者権限・検証APIは未設計。別部署数を実組織の確定需要として採用しない |

各領域の実装と最終ローカル検証は [今回の継続結果](validation-results/20261011-production-contracts/results.md)、APIの正式shapeは [workspace OpenAPI](specs/openapi-workspace-v1.json) と [business-analysis OpenAPI](specs/openapi-business-analysis-v1.json)、詳細は [表示名同意](identity-disclosure.md) と [手動分析](business-analysis.md) を参照。公開・分析同意と表示名開示同意は互いを代替しない。公開要約の編集は公開・分析同意を失効させ、変更した最新版への再同意が必要。

## #35：月額と採用判断の未確定値

`月額 = 利用量×適用単価` を項目別に計算する。初期入力は検索100回/月、AI呼出し数/分類とrerank token/RU/保存量/runtime/転送/観測/ACRなどは実測に更新し、未取得はnullまたは出典付き範囲。既存固定費と他Azureサービスも含めた合計が求まるまで8,000円以内とは判定しない。試験週2,000円・非runtime留保1,200円は旧実行枠の内訳であり、月額と加算/減算して残額を作らない。

採用候補にはモデル版、prompt/evidence長、確率提示、分類/辞書/検索規則、人物/事例上限・救済枠、source/config/dataset/gold hashと失敗例を記録する。#30サービス合格だけで本番採用を決定しない。#31の順序依存、#32の独立gold、#33の実経路認可、#34統合計測が未達なら、該当未達と適用制限を残す。ユーザーが制限を明示承認する前に受入へ置き換えない。

## #16：準備する全体試験と停止後の確認

先行Issueの完了後に使う台本は Office投入 → 構造化/分類 → 本人訂正 → 固定自然文検索 → 匿名依頼/即時参加 → 永続会話/有限AI事例投稿 → 解決/評価 → 要約レビュー/双方公開同意 → 別質問の知識再利用 → 分析同意された独立課題の手動分析 → 人の判断。公開同意と独立した表示名開示同意を分離し、編集時の公開同意失効を確認する。サーバーの匿名DTOと同意APIは実装済みだが、Demo UI/Fake store確認だけで実JWT/サーバー側匿名性の実環境受入を完成とはしない。

| 故障/境界 | 必須の期待結果 | 安全な準備と残る実試験 |
| --- | --- | --- |
| Jev/Foundry停止、429、予算停止 | DB正常なら会話保存を継続、AI失敗/停止を区別、有限回、正常応答再送なし | Mock障害ケースを設計。実停止/有料再送は個別承認 |
| DB停止・応答喪失 | 未保存を表示、同じkeyで照合/再送、訂正を失わず二重投稿0 | Mock応答喪失と競合。実DB/再配置は隔離計画後 |
| Queue通知漏れ/重複、Worker lease切れ | reconcileとlease回復、同じイベントの二重AI投稿0 | ダミー/Fake clockの設計。実Job/Queue操作は範囲承認 |
| 公開撤回・参加者変更・処理中版変更 | AI入力/表示/共有を再認可、根拠と集計の漏洩0 | #33/#15の回帰を入口にする。実JWT/Cosmosは別評価 |
| 再配置/cold・バックアップ復元 | 旧版復帰、業務Job停止、撤回状態の再適用、実replica0/Running Job0、schedule無効 | private元設定と隔離復元先・保存対象を先に定義。デプロイ/削除/復元は今回未実行 |
| 運用の失敗/poison/利用量 | correlationIdで追跡、AI停止/再実行/reconcile/手動分析、通常ログに本文/秘密0 | CLI/UI契約と観測設計。権限追加・実操作は別承認 |

終了時は全旧/新revisionのinactiveと明示replica0、実行中Job0、元image/env/scope/CPU/memory/scale/args一致、分析schedule無効、失敗/欠落も含む利用量、保持対象/消し方を記録する。削除は実行者承認後のみ。過去の停止JSONを今回の停止保証には使わない。

#1のG2は#14未完了、G4は#15/#16未完了として残る。G0/G1/G3も旧ロードマップのチェック欄を推測で更新せず、最新子Issue証拠との正式な照合時に判定する。実データ移行はこのダミー検証の承認を転用せず、別レビューとする。

## 前段のローカル結果と継続後の判定先

[前段2026-10-11の結果・実画面証跡](validation-results/20261011-remaining-issues/results.md)は変更前の履歴として保持する。.NET423件、Python121件、契約18件、fixture5件、認証4件、E2E重複除外44件の成功はその段階の結果。そこにある「Live実行・復旧CLI未実装」「匿名DTO/独立同意未実装」等の記載は保存時点の説明で、旧結果/ZIPを継続実装の状態へ書き換えない。

継続コードはLive/共有台帳/復旧CLI、匿名DTO/独立同意API、部分収集器を実装済みで、最終統合ローカル検証の合否・件数・実画面は [20261011-production-contracts/results.md](validation-results/20261011-production-contracts/results.md) へ保存する。最終件数は.NET535、Python121、契約20、fixture5、認証4、E2E49（重複除外）、全成功。実画面の判定はリンク先を参照。実Azure操作0・有料モデル送信0を維持し、保存応答再採点/Mockやfake transportの成功だけでopen Issue全体を受入完了とはしない。
