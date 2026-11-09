# 共有Workerの限定故障試験

## 概要

以前の架空Word一段落だけを使い、通常のAzure Queue・常用Worker・Cosmosの成功応答再利用と業務結果の冪等性を確認する保守手順。既存資料・人物head・辞書・承認履歴は変更しない。少数の故障注入で実障害の発生頻度を推定しない。

## 試験境界

| 境界 | 注入と判定 |
|---|---|
| ProfilePipeline.BuildAsync成功後、原子commit前 | 成功応答は保存済み。Jobだけに一度限り360秒のDependencyRetryExceptionを注入。queued・availableAt・投影未反映を確認し、その間に常用Worker revisionを再起動。6分超で同じJobが永続応答を再利用して完了することを確認 |
| 原子commit成功返却後、Queue削除前 | 一度限りIOExceptionでQueue ack喪失を模擬。ready Jobを巻き戻さず、再配達で投影・人物・承認を増やさず本文cleanupを行うことを確認 |

これはAI応答そのものの喪失や、receipt保存の成否が不明な窓とは別である。外部providerのexactly-once、二重請求の絶対防止を保証しない。max1の常用Workerの逐次再配達を、複数consumerの同時取得と称さない。

## 制限

WorkerTrialCheckpointsは既定無効。Data=Azure、明示した試験ID・workflow・新規document ID・既知架空WordのSHA256、現在の最初のenrichment Job・集約版1、現在の所有者/operator権限とscope、lease、原文の現行性を全て照合する。supersedesJobIdを持つ明示再試行Jobは対象にせず、同じ資料から故障を再armingしない。rootには適用しない。public APIから任意の故障を要求できる経路を設けない。

documentのworkerTrialにarmedAt/expiryを内部CLIだけで付ける。期限は最大1時間、新規資料だけ。一度限りのclaimをJobへCAS保存し、その保存が不明なら故障を盲目的に再注入しない。claimの存在だけを故障発火の証拠にせず、queuedと360秒待機、またはreadyとQueue再配達を併せて記録する。

workerTrial/workerTrialClaimsは契約上もサーバー生成の読取専用メタデータとする。スキーマのreadOnly注釈だけに依存せず、通常uploadはこれらのform入力を拒否する。型にないcheckpointや不正な日時も契約検証で拒否する。

## 準備

1. main・API/Workerの実revision、全catalog/conversationsの進行中Job・pending実行・fence、Queue概数を読み取る。古い未通知の会話Jobを勝手にreconcile/取消しない。無関係な処理があれば試験を開始しない。
2. 既存の架空workflow全記録のID/ETag/版、原文SHA、辞書版と承認履歴を退避する。値を取得する構成項目は公開endpointと既存架空利用者のID/roles/scopesの明示許可リストだけ。Auth/Cosmos/Storageの全値や接続文字列・鍵をexportしない。
3. 有料推論の新しい対象・最大呼出し数・設定単価見積を確認する。旧試験の未使用枠を流用しない。AccountingMode=Disabledを金額上限の強制と扱わない。CI・API/Worker・Cosmos/Blob/Queue・ACR・ログ・通信等の運用費は別で、実請求は未確定。
4. 既存権限を持つ独立したMacのCLIを読取モードで確認する。APIへ依存せず、推論を必ずMock/Stoppedに上書きする。ローカルの1プロセスだけAZURE_TOKEN_CREDENTIALS=AzureCliCredentialで既存ログインを明示選択し、MacにないManaged Identityの探索待ちを避ける。共有Workerの認証設定やIAMは変更しない。

```bash
bash scripts/worker-trial.sh inspect <公開設定JSON> <証跡ディレクトリ>
bash scripts/worker-trial.sh plan <公開設定JSON> <証跡ディレクトリ>
```

設定JSONにはData:Mode、Cosmos:Endpoint/Database、Storage:BlobEndpoint/QueueEndpoint、Auth:TenantId/Bindings/Grantsと、Diagnostics:WorkerTrialの明示した対象を指定する。資格情報を入れず、Azureの既存ログインを使う。

## 実施と元設定への復帰

レビュー・CI済みのコードを既存配備ワークフローで共有反映する。Workerの次の試験専用envが元から存在しないことを確認して記録し、一時的に設定する。他のenv・identity・network・scaleは変更しない。

```text
Diagnostics__WorkerTrial__Enabled=true
Diagnostics__WorkerTrial__Id=trial-worker-<一意なID>
Diagnostics__WorkerTrial__WorkflowId=<既存架空workflow>
Diagnostics__WorkerTrial__DocumentId=doc-worker-trial-<一意なID>
Diagnostics__WorkerTrial__SourceSha256=a651bb75f9d2a75a4f9a1595a925798d72114c815cc9cf9c1776861f8efa7f4e
```

`plan`は原本・現在の人物/権限/scope・生成予定ID・workflow Job・fenceを読取だけで検証する。Workerの実envと対象を照合してからoperator JSONのEnabledを明示的にtrueにし、`prepare`へ進む。`prepare`は固定されたrepositoryの架空fixtureをOffice検証し、現在の人物・権限・scope・既存workflowを確認する。新規reportのtitleと生成予定activity IDの不存在を確認し、既存DocumentAcceptance、Blob、通知機能へ渡す。既存person.documentHeadsに影響するimportKeyは付けない。通常ブラウザupload成功とは区別する。prepare-once.lockが作成された後は結果不明でも再送せずinspectする。

```bash
bash scripts/worker-trial.sh prepare <公開設定JSON> <証跡ディレクトリ>
bash scripts/worker-trial.sh inspect <公開設定JSON> <証跡ディレクトリ>
bash scripts/worker-trial.sh duplicate <公開設定JSON> <証跡ディレクトリ>
```

duplicateは対象enrichment Jobの通知を2件だけ送る。queued/availableAt・成功receipt・新しい投影がまだないこと、root fence解放・AI実行枠完了を照合してから`az containerapp revision restart`で対象常用Worker revisionを1回再起動する。結果不明なら管理面を先に読み、盲目的に再実行しない。6分超の再開時刻、再送/再利用、Job attempt、投影のID/版/件数、承認・監査・台帳を記録する。

終了後は追加した試験専用envだけを削除して無効化し、同じコード版・元のmin/max・identity・networkと元記録を確認する。APIを温めず管理面でAPI/Workerのreplicaとhealth、preview cleanupを確認する。試験原本・Job・監査は証跡として残し、不要な完全削除をしない。

## 内側の不明結果と停止・復旧

workflowの業務データは分離されているが、system-access-fenceは元tenant全体で共有される。receipt保存前後やprovider待機中に故障を起こすと、同じJobのretryだけで回復できず全体の停止・照合が必要になり得る。この試験とは別に復旧を準備するまで内側へ故障を注入しない。

独立した管理手段で全API/Worker revision・手動実行を停止し、進行中書込み・provider完了/停止を確認する。現在の管理REST APIはcontainerAppsのstop/start操作を提供するが、ローカルAzure CLIに同名コマンドがない場合がある。停止状態と全replicaの停止を再読し、停止要求の受付だけで止まったと扱わない。元scaleを0へ変更する方法は使わない。

[停止API](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps/stop?view=rest-resource-manager-containerapps-2026-07-01)、[コンテナの終了](https://learn.microsoft.com/en-us/azure/container-apps/application-lifecycle-management)を参照する。

原文版・Job・AiRun/provider request ID・実行枠・receipt・成功本文の有無・fenceの期待ETagを読み取る。**root向け--recover-fenceはworkflow物理パーティションのcommitted idempotencyを自動走査しない**。対象workflowのcommitted receiptの有無と整合を別途確認し、未確定のままrootだけを解除しない。pending成功記録を消して再送を解禁しない。[応答再利用](ai-result-replay.md)・[既存の回復手順](persistence.md)の停止条件を維持する。

## 検証の区別

ローカルのWorker再構築/時計進行は共有Azureでの再起動実測とは区別する。provider実送信の回数、永続/メモリ再利用、内部結果/承認監査、AiRun監査の増分を別々に数える。強制再起動や保存障害の少数成功だけで「滅多に起きない」と結論しない。手動停止/ETag照合/復旧が必要な事象を「ただretryすればよい」と説明しない。
