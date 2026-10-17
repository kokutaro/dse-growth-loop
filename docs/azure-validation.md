# Azure接続・権限・起動停止の検証

CLI停止snapshot説明の更新日：2026-10-12。実環境記録は2026-10-01のまま。Issue #9。全件ダミー・許可ユーザー1名の初期接続検証。[展開](deployment.md)、[永続化とRBAC](persistence.md)、[取込と回復](document-ingestion.md)、[AI台帳](ai-adapters.md)を併用する。アプリ全体の受入は #16。

## 実行ゲートと証跡

対象subscription／`dse-growth-loop-rg`／アプリ・Foundryのリージョン、実行者、承認参照、月次・今回・日次AIの費用上限、許可する変更を実行前に確定する。環境作成、Provider登録、Entra変更、秘密投入、推論、停止操作は承認した対象と範囲だけで行う。`.env`の承認額やコマンドのフラグだけで、この会話の承認を代替しない。未承認・接続失敗は未完了のまま残す。

`scripts/azure_validation.py` は読み取り専用。配置、登録、ログイン変更、秘密取得、データ書込、推論、HTTP pingをしない。外部コマンドの標準出力／エラー本文は記録せず、許可した構成項目だけを保存する。CLIエラーは固定コードと終了値だけになるため、失敗詳細は実行者が秘密を除いて別途調査する。

```sh
# Azureログインを使わない構文検証。既存run-idへの追記・上書きは禁止
python3 scripts/azure_validation.py local --run-id YYYYMMDD-local-01

# subscriptionは既定アカウントへ暗黙依存せず、読み取り先を明示
python3 scripts/azure_validation.py preflight --run-id YYYYMMDD-preflight-01 \
  --subscription <subscription-id> --location japaneast --foundry-location <region>

# アプリへのHTTP要求なし。全revision・各実replica一覧・最後の全revision読戻しを保存
python3 scripts/azure_validation.py replicas --run-id YYYYMMDD-api-zero-01 \
  --subscription <subscription-id> --app dse-api

# VNet移行後の新API。preflightでもApp/Environmentの組を一致させる
python3 scripts/azure_validation.py replicas --run-id YYYYMMDD-api-vnet-zero-01 \
  --subscription <subscription-id> --app dse-api-vnet --environment dse-environment-vnet-jpe
```

終了0はコマンドの成功／読み取り点検の条件成立だけを表し、停止確認とは区別する。全revision一覧が取得できればactiveがなくても読取成功になり、activeや実replicaが残る場合でもCLI読取が正常ならexit0・`stopSnapshotConfirmed=false`になり得る。停止確認はreportの `stopSnapshotConfirmed=true` を別途確認する。これは全revisionがinactive/Stopped/metadata replica0、各実replica一覧が空、最終読戻しの名前/状態が初回と一致する場合だけ成立する。一覧が空・未知値・最終取得中の追加/削除/状態変化・CLI失敗は停止未確認。空の全revision一覧、未登録Provider、CLI失敗、読取前提不成立は終了1、引数不正は終了2。preflight/replicasのAppとEnvironmentの混在も引数段階で拒否する。

`report.json`の`liveAcceptance`とライブscenarioは必ず`not_run`で作成される。欠けた測定値は`null`で、0 RU／0秒と解釈しない。RG不存在は「不存在を読み取れた」という結果で、基盤構築済みを意味しない。モデル一覧・クォータ・ARM permissionsの読取成功も、選択モデルの利用可能性やMIのデータ権限の合格にはならない。停止snapshotは取得時点のcontrol-plane情報であり、将来の停止継続・請求hard cap・実環境受入を保証しない。

各段階で別run-idを使用し、`docs/validation-results/<run-id>/`へ次を保存する。個人ID・subscription等の識別子を伏せる場合は、実行者が保管する対応表の参照を残す。原ログ、トークン、秘密、アプリの全env／全ARM出力はコミットしない。`--out .artifacts/azure-validation`ならローカル保管だけになる。

| 記録 | 必須項目 |
|---|---|
| 実行 | UTC日時、commit、未コミット変更の有無、実行者、承認参照、対象環境 |
| 構成 | リソース種別、SKU、実モデルID／版／リージョン、image tagとdigest、active revision、MIの役割とスコープ |
| シナリオ | 下表のID、実コマンド、期待結果、観測結果、件数、passed／failed／not_run、未実行理由 |
| 測定 | Cosmosの実RU、提供元usageと推定usageの区別、cold／warm時間、0復帰時間、ログ量、費用の期間・通貨・取得日時 |

## 事前点検と配置

1. preflightで既存RG・リソース、Provider登録、subscription／RGのARM permissions、Foundryモデル一覧と地域クォータ、Container Apps地域クォータを読む。既存アプリがある場合はrevision／Job実行／image digestとデータの保管先を追加確認し、上書き・削除の対象を提示する。RGがなければ既存データの不存在まで推測しない。
2. Providerは`Microsoft.App`、`OperationalInsights`、`Insights`、`ContainerRegistry`、`Storage`、`DocumentDB`、`KeyVault`、`ManagedIdentity`、`CognitiveServices`、Budget用の`Consumption`。未登録なら承認後の登録・完了待ちが必要。実行者にはARM展開／リソース作成、roleAssignments/write、Cosmos sqlRoleAssignments/write、ACR push、Budget作成の権限が必要。ARM permissionsはDeny Assignment／Policy／Entra権限やMIのデータ権限を証明しない。
3. ACR Basic、Cosmos Serverless、Storage LRS、Consumption、Key Vault Standard、AIServices S0の地域・契約対応を確認。Foundryは要求モデルの**固定name/version、SKU、必要capacityと残quota**を対応づけ、strict構造化出力／v1 Chat Completionsが対応することを確認する。未選択モデルをcatalog読取だけで合格にしない。
4. `validate` → 基盤`what-if` → `foundation --confirm-cost` → ローカル`build`／承認済み`push` → アプリ`what-if --include-apps` → `apps --confirm-cost`の順。[展開コマンド](deployment.md#展開順序)を使い、各what-ifの変更・置換・削除を人が確認してから次段階へ進む。Jevキーをwhat-ifやパラメーターへ入れない。
5. Entra単一テナント・v2・`access_as_user`・SPA redirectを設定し、許可oidを1名だけにする。APIのBinding／Grantも必要。まずAI停止・LiveOptIn=false・Worker未配置で公開し、基盤の接続を確認する。取込Workerは #6 実装済みのQueue消費に限って明示配置する。

## 最小スモークの合格条件

ローカルfixture結果で下表を合格にしない。期待する401／403等も、実トークン／実MIで観測したときだけ合格になる。403が伝播待ちかスコープ不足かを、付与時刻・試行時刻・対象role/scopeで分けて記録する。再試行は明示した件数までに制限し、権限をsubscription全体へ広げて解決しない。

| ID | 操作と観測 |
|---|---|
| `entra` | 実ブラウザログイン→scope付き実API要求。署名・issuer・audience・tid・oid・scope検査、許可外ユーザー／別tenant／scopeなし／誤audienceを拒否。署名を壊したトークンで別tenant試験を代替しない。redirect、logout後の接続切断／API拒否も確認。HAR・JWT本文・実社員名を保存しない。共有demo scopeはデモの設定として記録 |
| `managed-identity` | APIとWorkerのMIを分ける。catalog／conversationsの読書き、API originals upload/readとingestion send、Worker originals read・extracted write/read・ingestion receive/不可視延長/delete・poison send、固定版Key Vault取得、Foundryを確認。APIのextracted／Queue受信、Workerのoriginals書込等の許可外操作も拒否。実行者のaz loginによるデータ操作をMI試験としない |
| `live-ai` | 下記connections suiteでダミーJev Choice＋Noulを1リクエスト、Foundry構造化1、固定ダミー候補の再順位付け1。AiRun・予約・プロバイダーusageをrun IDで照合。model実ID／版と固定設定が一致し、合計最大3試行、各操作成功。モデル不一致／usage不明／予算停止は成功にしない |
| `zero-scale` | APIの0→単発要求→1→全接続終了→0。cold開始・応答完了・warm要求を別測定し、制御プレーンで復帰確認。ブラウザ未接続／相談画面閉鎖の双方を観測。Workerは空Queueで0→承認したダミーOffice1件で1→Job完了後0。Manual Jobは明示開始・終了と、空Queueでの`--once`終了を確認 |
| `recovery` | 既存データを保護した試験環境でQueue送信失敗後もDBのpending Jobが残り、`--reconcile`後に通知・処理されることを確認。プローブ失敗を承認済みの一時revisionで起こし、復帰後に保存した正本・会話・Jobを再読取。失敗注入の方法・解除と対象を先に定め、本アプリ全体の常時fault injectionを追加しない |
| `redeploy-rollback-stop` | 同一設定の再配置、新tag配置、旧tag復帰、Job停止、全active revision停止を下記順序で観測。データ／モデルを削除せず、保存済みダミーが残る |
| `cost-reconciliation` | 推定／AiRun予約と実usage、AzureとJev請求、RU、稼働時間、ログ量を期間・通貨を合わせて照合。未反映ならpending。0台でもACR・Cosmos/Blob/旧版・ログ保持・Key Vault等の保管費、Queue監視操作が残ることを整理 |

### AI接続suite

`--ai-smoke`の既存basicは2呼出しのまま。#9用は`AiSmoke__Suite=connections`、`AiSmoke__Count=3`を**新たな件数承認**とともに指定する。[既存の全承認・モデル・上限設定](ai-adapters.md#承認後のライブスモーク)も必須。Choice/NoulをまとめたJev 1回、構造化1回、再順位付け1回となり、MaxAttempts=1で最大3推論試行。日次の回数／トークン／最大予約額も3試行分を用意する。

再順位付けは事前に承認してseedした`demo-person-01-projection-1`だけを使う。現在の認可と正本を照合し、根拠がリポジトリの固定ダミー資料と一致しなければ送信前に拒否する。このコマンドはseed／訂正／公開撤回の巻き戻しをしない。既存fixtureが変更済みなら、新しい隔離したダミー環境の使用を承認してから再試験する。

```sh
# 承認済み環境のMIを持つプロセス内で実行。ローカルAzure資格情報での代替は不可
python3 scripts/run-env.py -- dotnet run --project src/Dse.Worker -- --ai-smoke
```

Container Apps上で実行する場合は専用の一時Manual Jobを使い、Worker imageとMI、対象接続先、上記設定を固定し、`args: ['--ai-smoke']`、`replicaRetryLimit: 0`、`parallelism: 1`、`replicaCompletionCount: 1`、timeoutを明示して配置内容をレビューする。通常のseed Jobのretry=1のままargsだけ差し替えると、最大件数を超えるため使わない。Job作成も承認対象。キー本文をenvや標準出力へ渡さない。Key Vault固定版はMIで取得する。終了後はAI停止／opt-in無効へ戻し、全execution終了を確認する。

### MI接続だけのスモーク

`--connection-smoke`は推論をせず、API用／Worker用MIのデータ権限を専用Manual Jobから点検する。`Data__Mode=Azure`、`ConnectionSmoke__Approved=true`、`ConnectionSmoke__DummyOnly=true`、承認参照、`ConnectionSmoke__RunId=issue9-<unique-id>`、`ConnectionSmoke__Role=api|worker`、`AZURE_CLIENT_ID`、tenant、Cosmos／Blob／Queue／Key Vault接続先、固定Jev秘密版が必須。4分で打切り、Blob／Queue SDK retryは0、JobもreplicaRetryLimit 0／並列1にする。

隔離されたダミー環境でのみ使う。取込Workerを停止し、ingestion Queueが空であることを確認してAPI試験→同じrun IDのWorker試験を順に行う。API試験のダミー通知をWorker試験が受信・不可視延長・削除する。既存通知が混在すると不一致で失敗し、その通知の不可視時間が変わるため、共有運用Queueで実行しない。Cosmosにはrun IDごとのダミーをcreate/readし、既存IDを上書きしない。originals／extractedへダミーを保存し、poisonにもダミー通知を残す。生成物の削除は別操作であり、このコマンドは削除しない。

出力は各操作の合否、期待した403、実RU、経過msのみ。キー値、MIトークン、秘密を含む例外本文は出力しない。Foundryは別の承認済み3件AI suiteで確認し、この試験の成功だけで推論成功としない。

## 再配置・ロールバック・停止

実行前にrevision、参照imageのtag/digest、Job image、model name/version、runtime設定を保管する。公開・処理中の資料や旧imageは保護する。次の変更操作は**対象・停止影響を承認してから**実行する。`$DSE_SUBSCRIPTION`等は実行者が確認した値を設定する。秘密値のシェル展開は使わない。

1. 同じ非秘密設定／image tagで`what-if --include-apps`→`apps --confirm-cost`。既存ダミーのID／版を照合し、再seedで上書きしないことを確認する。
2. ロールバックは互換性を確認した保管済み旧tagを`IMAGE_TAG`に設定し、同じモデル・認証・接続先・停止設定を維持して`what-if --include-apps`→`apps --confirm-cost`。Single revisionのactive imageを確認し、正本と会話を再読取。データ互換性はimage復帰だけで保証しない。
3. 通常停止では新規要求／uploadを止め、`AI_STOPPED=true`／`AiUsage__LiveOptIn=false`を設定したrevisionを承認済み配置で反映し、実行中処理の終了を確認する。緊急時は先に全active revision／Jobを停止する。既に外部送信した要求の取消や返金とは扱わず、台帳のuncertain予約を維持する。
4. active revisionを一覧して**APIとWorkerそれぞれ全て**を停止し、Jobの実行も停止する。CLIの既定subscriptionに依存しない。

```sh
az containerapp revision list --name dse-api --resource-group dse-growth-loop-rg \
  --subscription "$DSE_SUBSCRIPTION" --query '[?properties.active].{name:name}' --output json
az containerapp revision deactivate --name dse-api --revision "$DSE_REVISION" \
  --resource-group dse-growth-loop-rg --subscription "$DSE_SUBSCRIPTION"
# dse-workerが存在する場合も同じ一覧→各revision停止を実行
az containerapp job execution list --name dse-seed-demo --resource-group dse-growth-loop-rg \
  --subscription "$DSE_SUBSCRIPTION" --query '[].{name:name,status:properties.status}' --output json
az containerapp job stop --name dse-seed-demo --job-execution-name "$DSE_EXECUTION" \
  --resource-group dse-growth-loop-rg --subscription "$DSE_SUBSCRIPTION"
# 一時validation Job等、対象RGの他の承認済み実行も個別に停止
```

5. 全revisionでactiveなし・Stopped・metadata/実replica0、Job一覧でRunningなしを確認する。`replicas`のexit0やactiveなしだけでは停止成功にせず、最終読戻し付きの `stopSnapshotConfirmed=true` を読む。このsnapshotを0→1→0試験の全体成功へ流用しない。Queueはpurgeせず残件数／不可視期限を記録し、消費MIの全プロセスが停止していることを確認する。モデルを削除せず全呼出し元を停止し、usage増加が止まることを確認する。外部呼出し元があれば別途停止が必要。
6. `DEPLOY_INGESTION_WORKER=false`、モデル名空欄、基盤だけの再配置は停止／削除操作ではない。コンパイル済みARMの全nested deploymentはIncremental。省略したWorker／モデル／Budgetは既存のまま残る一方、テンプレートに含めた既存リソースの設定は再適用される。特に空modelDeploymentNameはアプリの推論設定を空にするため、通常の再配置／image復帰でモデル設定を消さない。停止後の`apps`もAPIを再有効化するため、復帰は別途承認して行う。

## 費用の後日照合とTodoの更新

見積もりは単価版／為替版、3推論試行、RU・Blob・Queue操作、Container Apps時間、ログ収集／保持、既存ストレージ量を分離する。単価・仮為替が確定した今回の推定は[実環境記録](validation-results/20261001-issue-9-live/results.md)を参照。Cost ManagementとJevの反映時刻・対象期間を記録し、請求未反映を0円としない。停止直後と後日の請求照合を別runにし、固定費／保存費／残存ログ量も残す。

Issueの各Todoは、その行の全要件を満たす証拠があるときだけチェックする。ローカル検証・読み取り点検だけでEntra／MI／推論／ゼロスケール／復旧／請求照合をチェックしない。未実施・失敗・請求照合待ちが残る間はIssueをopenに保ち、PRは`Refs #9`とし、自動closeを指定しない。[承認前の準備結果](validation-results/20261001-issue-9-preparation/results.md)と[承認後の実環境結果](validation-results/20261001-issue-9-live/results.md)を分けて記録する。

## 確認した公式仕様

2026-10-01確認：[地域のContainer Apps usage API](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/usages/list?view=rest-resource-manager-containerapps-2025-07-01)、[Foundry地域モデル一覧](https://learn.microsoft.com/en-us/cli/azure/cognitiveservices/model?view=azure-cli-latest)、[Foundry quota](https://learn.microsoft.com/en-us/cli/azure/cognitiveservices/usage?view=azure-cli-latest)、[ARM permissions](https://learn.microsoft.com/en-us/rest/api/authorization/permissions/list-for-resource-group?view=rest-authorization-2022-04-01)、[revision停止](https://learn.microsoft.com/en-us/cli/azure/containerapp/revision?view=azure-cli-latest)、[Job実行停止](https://learn.microsoft.com/en-us/cli/azure/containerapp/job?view=azure-cli-latest)、[Incrementalの省略／設定再適用](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deployment-modes)。これらの仕様確認は本環境での実行成功とは区別する。
