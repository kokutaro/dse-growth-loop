# Issue #9 Azure実環境の検証結果

実施日：2026-10-01 UTC。run ID：`20261001-issue-9-live`。実行者：Codex、開発者本人の既存Azure CLIログイン。基準commit：`eea1df3dc4267dd8d002e0a399405c70a787d398`＋本PRの未コミット変更で構築したimage。Issue全体は**未完了**。全体受入は #16。先行する[実行準備の記録](../20261001-issue-9-preparation/results.md)は承認前の履歴であり、本記録の実測と区別する。

## 承認・対象と配置

ユーザーは本会話で現在のCLI接続先subscription／`dse-growth-loop-rg`／Japan East、月額8,000円、今回4,000円、構築に必要な作業、最大3推論試行と検証後停止を承認した。Key Vaultへの本人の`Key Vault Secrets Officer`一時付与は対象Vault・本人・登録後削除を別途明示承認。登録後に追加assignmentを削除済み。Jevキー作成とOfficeファイル選択は本人が操作した。実社員データは使用していない。

subscription・tenant・本人oid・Entra client ID・秘密版・予算通知先の対応表、全ARM出力と原ログはgit対象外の`.artifacts/azure-validation/20261001-live/`に保管する。公開成果物は許可した測定項目のみ。キー、JWT、入力本文、生AI応答、実ユーザー名は含めない。

| リソース | 実構成 |
|---|---|
| Container Apps | `dse-api`／`dse-worker`、`dse-environment`、Japan East、Consumptionのみ。API 0.5 vCPU/1 GiB、Worker 0.25 vCPU/0.5 GiB、最小0／最大1、cooldown 300秒 |
| ACR | `dseacrypbunss3xh66m`、Basic、admin無効。ローカルlinux/amd64 build、ACR Tasks未使用 |
| Cosmos | `dse-cosmos-ypbunss3xh66m`、Serverless、`dse-growth-loop` DB、catalog／conversations単位のMI権限 |
| Storage | `dsestypbunss3xh66m`、Standard LRS、共有キー無効、originals／extracted、ingestion／ingestion-poison |
| Key Vault | `dse-kv-ypbunss3xh66m`、Standard、purge protection有効、Jevキーの固定版をMI取得 |
| Foundry | `dse-foundry-ypbunss3xh66m`、AIServices S0、Japan East、local auth無効。`gpt-5.6-luna`／`2026-07-09`、GlobalStandard、capacity 1、NoAutoUpgrade |
| ログ | `dse-logs-ypbunss3xh66m`、Log Analytics PerGB2018、Application Insights。保持・収集capはBicep／停止時構成記録に従う |
| 認証 | 新規Entra単一テナントアプリ、v2、委任`access_as_user`、HTTPS `/redirect.html`、許可本人1名。demo本人・demo scopeへの明示Binding。部署別の本番ACLを実証したものではない |

初回読取で対象RGは不存在。既存の別RGに触れず、必要Provider6種を承認後登録。`20261001-1013-registered` preflightは全条件成立。地域ManagedEnvironmentCountはlimit 20／current 0、残1以上を確認。選択モデル・版・SKU・capacityの配置と実応答で利用可否を確認した。Provider未登録時に観測したquota 0を利用可能として通さないよう、残quota／有限値のガードを修正した。

## 実行経路と失敗記録

識別子を伏せた再現コマンド。`.env`は非公開、既存承認なしに再実行してはならない。

```sh
python3 scripts/azure_validation.py preflight --run-id <unique-id> \
  --subscription <approved-subscription> --location japaneast --foundry-location japaneast \
  --out .artifacts/azure-validation
python3 scripts/deploy.py validate
python3 scripts/deploy.py what-if --env-file <private-live.env>
python3 scripts/deploy.py foundation --env-file <private-live.env> --confirm-cost
python3 scripts/deploy.py build --env-file <private-live.env> --build-method local
python3 scripts/deploy.py push --env-file <private-live.env> --confirm-cost
python3 scripts/deploy.py what-if --env-file <private-live.env> --include-apps
python3 scripts/deploy.py apps --env-file <private-live.env> --confirm-cost
az containerapp job start --name <approved-manual-job> --resource-group dse-growth-loop-rg \
  --subscription <approved-subscription>
python3 scripts/azure_validation.py replicas --run-id <unique-id> \
  --subscription <approved-subscription> --app <dse-api-or-dse-worker> --out .artifacts/azure-validation
```

基盤what-ifを確認して配置。初回はFoundry project／modelの並行更新が`RequestConflict`となり、一部38リソースが作成済みになった。modelにprojectへの`dependsOn`を追加し、再what-if（1 create／30 modify／8 same）後の同じIncremental配置が成功。既存データの削除・再seedによる上書きはしていない。初回アプリはWorkerなし、AI Mock／Stopped=true／LiveOptIn=falseで配置。通知復旧時に実装済み取込Workerだけを明示追加した。

標準pushはmacOS keychainの`User interaction is not allowed (-25308)`で失敗。代替は`az acr login --expose-token`の応答をプロセス内だけで処理し、0600の一時Docker auth設定で同じACRにtag/push、終了時削除。credential helperやACR adminを有効にして回避していない。この代替手順は今回の運用操作で、`deploy.py push`本体の修正ではない。

image履歴は[実digest](image-digests.json)を参照（配列順API、Worker）。`issue9-live3`が実AI測定、`issue9-logout4`が初回実ログアウト、`issue9-final5`が最終ソースの認証同時要求修正。途中の`issue9-live2`は測定traceの参照先が誤りusageを出せず、AI送信前に修正してbuildし直した。JobログAPIは保持終了後に`No replicas found`となったため、保持されたLog AnalyticsからJob状態とKEDAイベントを取得した。これは処理失敗とは区別する。

## MI・実AI・認証

MI試験は本人資格情報によるデータ操作で代替せず、MIを明示した専用Manual Jobで行った。`--connection-smoke`は承認／ダミー／run ID／固定秘密版／Azure endpoint／MI必須、推論0回。SDKの結果だけを記録し例外本文と秘密値を出さない。

| 実行 | UTC開始→終了 | 結果 |
|---|---|---|
| API MI `dse-validation-api-qx63ygs` | 10:38:34→10:39:04 | [10/10成功](api-mi-results.json)、Cosmos create/read合計16.48 RU。originals upload/read、ingestion send、固定秘密版取得。extracted read／ingestion receiveは403 |
| Worker MI `dse-validation-worker-qrhxdar` | 10:40:08→10:40:45 | [11/11成功](worker-mi-results.json)、Cosmos create/read合計16.48 RU。originals read、extracted upload/read、ingestion receive/延長/delete、poison send、固定秘密版取得。originals uploadは403 |
| AI `dse-validation-ai-32icu3d` | 10:55:21→10:55:56 | Worker MI、3/3成功、retry 0、各attempt 1、timeout 300秒、replicaRetryLimit 0、並列1。終了後Stopped=true／LiveOptIn=falseに変更 |

RBACの実試験で伝播待ちの403は観測しなかった。403を許可ロール拡大で解決していない。API MIでのFoundry実推論、catalog／conversations以外のCosmos拒否、付与直後の伝播時間の定量測定は未実施。MI Todoの全条件を満たしたとは判定しない。

以下は[保存AiRunと提供元usageの許可項目](ai-results.json)。入力hash・prompt・生応答は除外。提供元usageは全件`usageEstimated=false`。予約の最終状態はこの公開測定に含めていない。

| 操作 | 実応答モデル | input/output tokens | ms | 単価・仮為替による推定円 |
|---|---|---:|---:|---:|
| Jev Choice＋Noul（1要求） | `jev-1.13.0` | 352 / 49 | 856 | 0.0029568 |
| Foundry構造化 | `gpt-5.6-luna-2026-07-09` | 164 / 33 | 2,360 | 0.01448 |
| 固定候補1件の再順位付け | 同上 | 366 / 134 | 2,113 | 0.0468 |
| 合計 | 固定版一致、成功3件 | 882 / 216 | 操作ごとに測定 | 0.0642368 |

再順位付け前にseed済み正本と固定候補の根拠を照合し、再seedして通していない。日次上限3回／27,000 tokens／10円予約上限、各MaxAttempts=1。承認済み3試行を消費済み。通常API／WorkerはAI停止。上表は請求額ではなく、[公式Foundry単価](https://azure.microsoft.com/en-us/blog/gpt-5-6-now-available-in-microsoft-foundry/)と[Jev単価](https://docs.typesafe.ai/models)に基づく台帳推定。為替200円/USDは計画上限の仮定であり、実為替ではない。単価版`azure-blog-20260730-typesafe-20261001`、為替版`validation-planning-ceiling-200JPY-per-USD`。

実ブラウザMSALで本人ログインし、scope付きAPIで検索・アップロード・課題／会話操作を実行。新しいredirectからもログイン成功。`issue9-logout4`で接続中の会話を持った状態でログアウトし、「接続を終了しました」だけになり保護画面が消えた。APIは発行済みJWTの失効機能を持たず、ログアウトはローカルのtoken取得・接続を停止する。ログアウト後の既存JWT再送拒否は実証していない。

別テナント／許可外ユーザーは、本人から既存検証アカウントなしとの回答を受領。実試験は`not_run`。scopeなし／誤audienceの実トークンも未実行。署名破壊やローカルfakeを実試験としない。複数パネルを同時に開くと`interaction_in_progress`が発生したため、最終ソースは進行中のtoken取得を共有する。回帰テストを追加した。

## 縮退・通知回復・永続化

API `20261001-api-zero-before-http`は全active revisionレプリカ0。単発GET `/api/config`は10:45:45.486556Z開始、35,750.99 ms／200。次のwarm要求は10:46:21.237676Z開始、62.8 ms／200。その後のレプリカ読取は1。最初のstartup中の1台観測を、0台からの起動と呼んでいない。ログアウト後、`probe-restored9`は11:45:07.8000688Zに1→0。11:46:03.110246Zの制御プレーン読取でも0。最終API要求を契機に起動させる前に確認した。縮退までの時間をcooldownの300秒だけで断定していない。

通知失敗はAPIだけの一時revisionでQueue endpointを`https://127.0.0.1:1/`へ変更して注入。Blob／Cosmos／認証は維持。本人が固定fixture `web/tests/fixtures/profile.docx`を1件アップロード。11:05:51.6706625ZのDB読取でingestion Jobは`queued`／attempt 0／`notification=pending`。その後元のQueue endpointへ戻し、`--reconcile`が11:22:26.982964Zに`Notified: 1`を出力した。

Workerの[実KEDAイベント](worker-scale-events.json)は、空Queueで11:16:44.7853131Zに1→0、再通知後11:22:44.7897103Zに0→1（通知から17.8067秒）、11:27:44.7211415Zに1→0。11:29:46.635042Zの制御プレーン読取も0。[復旧後DB](jobs-retained-final.json)でingestionは`ready`／attempt 1／notification sent、後続enrichmentは`budget_stopped`／`AI_BUDGET_STOPPED`。AI停止を解除して追加課金せず、UIも機械抽出済み・AI保留と表示した。空QueueのManual `--once`は11:17:44→11:18:22に正常終了。

プローブ試験は専用API revision `probe-failure9`に認証必須URLをプローブ先として設定。ARM PUTの最初の2回は読取専用フィールド混入で400となり変更されず、allowlistの要求本文へ修正後に成功。revisionは`ActivationFailed`／レプリカ0となったが、保持ログには`ScaledObjectCheckFailed: Target resource doesn't exist`等のみで、HTTP401プローブ失敗の直接証拠を得られなかった。**プローブによる失敗・回復を合格にしない**。元のhealthパスに戻した`probe-restored9`はHealthy。新プロセスから保存した課題・メッセージ#1・Office文書版1・抽出版1を再読取できた。原本／DB削除なし。回復Todoはプローブ原因確定が残るため未チェック。

## 再適用・旧image復帰・停止

自然縮退を確認した後、`az containerapp update --image <retained-initial-image> --revision-suffix rollback9`で旧`issue9-eea1df3`へ復帰。[旧revision](rollback-revisions.json)はHealthy。認証・接続先・原本／DBを維持し、imageだけを変更した。次に`issue9-final5`へ戻し、同じimage指定で再適用も成功。[最終revision](final-image-revisions.json)はHealthy。最終UI起動、複数の初回認証要求が一つのpopupを使うこと、保護画面のログアウト終了を確認した。再ログインには本人デバイス確認が必要だったため要求を送信せず試験を終了し、最終imageでの再ログイン完了は未実施として残す。

最終の[HTTP読取](http-final-results.json)は11:53 UTC、`/api/config`が200／77.03 ms、正しい保護URL `/api/v1/people/demo-person-01`の匿名要求が401／77.93 ms。初期測定の`/api/people/...`は版付きURLではないため、それだけを正本API拒否の証拠にしていない。

11:53:54〜11:54:10 UTCに全active revisionをdeactivateし、全Job実行を読取確認。[停止証跡](final-stop.json)でAPI `final5`／Worker `wql4iqz`はactive=false／Stopped／0台。アプリ本体のARM `runningStatus=Running`表示はrevision稼働とは異なり、revision自身を確認した。7種類のManual Job、8実行は全てSucceeded、Runningなし、合計実行時間275秒。今回実行中Jobの強制stopは不要だったため未試験。停止手順は別途文書化済み。全アプリ／JobはAiUsage Stopped=true／LiveOptIn=false。Workerのtagは`issue9-logout4`で、Worker digestは最終tagと同じ。Foundry model、ACR旧image、Cosmos／Blobのダミー、poison通知、Jev秘密版、ログ、Budgetは保管した。

## 費用照合・残存費用

[実測集計](cost-metrics.json)の期間は10:13:00〜11:55:50 UTC。Cosmos `TotalRequestUnits`は6,390.713489688621 RU（非nullの1分sample 102）。MI診断の応答ヘッダー計32.96 RUとは集計範囲が異なり、加算しない。Console/System/AppRequests/AppTracesの`_BilledSize`合計2,030,875 bytes（約2.03 MB）。後着ログとmetric遅延で増える可能性がある。

10月1日分のRG ActualCostを11:46:45 UTCに読取成功したが、rowsは空、JPY実請求は`null`／pending。Jevの請求・残高画面は未取得で`not_observed`。実usageがあっても請求額0円とはしない。AiRun予約の最終状態と請求照合も未測定で、差分額を算出しない。

JPY単価は2026-10-01に[Azure Retail Prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices)でJapan EastのConsumption／Serverless／Basicを照合した税別の小売見積。契約割引・税・無料枠の適用は実請求で確認する。

| 分離した見積 | 計算・条件 |
|---|---|
| ACR Basicの残存固定費 | 26.2478円/日、31日なら813.6818円。アプリ0台でも残る |
| Cosmos RU | 44.9017円/100万RUなら観測6,390.71 RUは約0.287円。保存量・他期間を含めない |
| ログ収集 | Analytics ingestion 526.217円/GBを全観測bytesに仮適用すると約1.069円。無料枠・対象table・後着分・契約適用は未照合 |
| Manual Jobリソース | 実275秒×(0.25 vCPU×0.0038円/秒＋0.5 GiB×0.0005円/秒)=0.33円の小売換算。請求確定値ではない |
| API/Workerリソースの計画余裕 | API最大2 revisionの重複を各2時間、Worker1台2時間と仮定すると43.2円。実稼働時間は全revisionを通して積算未了。この仮定を実測値／厳密な費用上限と呼ばない |
| AI | 実882/216 tokensに上記単価と仮為替200円/USDで0.0642368円。円建て実請求ではない |

[Container Apps公式料金](https://azure.microsoft.com/en-us/pricing/details/container-apps/)のsubscription共通無料枠（180,000 vCPU秒／360,000 GiB秒／200万要求）の残量は未検証のため控除していない。本環境はConsumptionのみで、Dedicatedプランの管理料メーターを合算していない。課金停止の意味を「RG全体0円」としない。Blob容量・版、Cosmos容量、ACR保管、Key Vault操作、保持期間を超えるログ、Queue監視・操作などの残存費は別途実請求で照合する。Budget 8,000円の80/100%通知は硬い停止上限ではない。月の断続利用としてAPI/Workerを各5時間使うならリソース64.8円＋ACR813.68円という部分見積になるが、全月額の確定額ではない。

後日、新しいrun IDで同じ期間／RGのActualCostとJev請求を読取し、通貨・税・無料枠・単価・実為替・RU／usage／予約・後着ログを照合する。未確定項目の差分を0とせず、費用Todoは未チェックを維持する。

## 最終確認と未完了項目

最終のローカル検証：locked-mode restore、Release build、.NET215件（失敗／skipなし、カバレッジ収集）、Python設定/Bicep41件、契約13件、評価fixture4件、認証4件、TypeScript/Vite build、Playwright logout1件成功。既存ASPIRE010警告1件。Bicep試験の最初のsandbox実行はAzure CLI設定への書込制限で失敗し、許可された再実行で成功した。ローカル結果は実サービス合否と区別する。最終CIはPRの最新commitに対する6 jobsの結果を参照する。

IssueのTodoは行全体の証拠が揃ったものだけ更新する。Entraの拒否ケース、MIの残りの範囲、プローブの原因確定、実請求の照合は完了扱いにしない。Issueをopen、PRを`Refs #9`とする。[機械可読集計](report.json)と[最終ソースのSHA-256](source-sha256.json)を併用する。未コミットworktreeでのbuildという実行条件を、後からcommit済みのbuildとして書き換えない。

| Issue Todo | 判定 |
|---|---|
| 現状・SKU・モデル・quota・権限事前点検 | 完了。登録後のpreflight・配置・固定版実応答 |
| validate→what-if→基盤→image→apps、認証必須1名 | 完了。未実装常駐なし、初期Workerなし、後から実装済み取込のみ追加 |
| Entra実トークンと各拒否、redirect/logout | 未完了。拒否ケース／最終image再ログイン・既存JWT再送拒否未実施 |
| 役割別MI、スコープと伝播 | 一部成功。未測定のAPI Foundry等を残す |
| Jev Choice/Noul＋Foundry構造化/再順位付け | 完了。3試行／全成功、実ID・版・provider usageを記録 |
| API/Worker/Manual Jobの起動・終了・空Queue | 完了。制御プレーンと実KEDA時刻で0→1→0 |
| 通知失敗・reconcile・プローブ・再起動永続化 | 一部成功。プローブ失敗の原因確定が残る |
| 再実行・旧image復帰・停止の手順 | 完了。手順とIncremental定義、実image復帰・再適用・停止確認 |
| run成果物・日時・commit・実RU/tokens/time | 完了。本ディレクトリに公開可能な証跡のみ保存 |
| 失敗・未承認を未完了に残す | 完了。not_run／partial／pendingを維持 |
| 見積と実費差・保管費・ログ量 | 整理済み、実請求差分はpendingのため未完了 |
| 初期接続リスク解消・全体受入は後続 | 主要リスクを実測。上記残条件があるため全体完了にはしない。受入は #16 |
