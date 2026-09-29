# オンデマンド実行と開発費用ガード

更新日：2026-09-23。Issue #2 / ADR-11。対象は全件ダミーデータ・開発者1名の断続利用。2027年1月末までは他ユーザーの非定常アクセスを想定しない。クラウド稼働・費用削減の実測結果ではない。

## ADR-11：APIはHTTP起動、取込WorkerはQueue起動

APIはConsumption、Single revision、最小0／最大1、0.5 vCPU／1 GiB。HTTP concurrency閾値1、cooldownは300秒。最大1はアプリのスケール設定であり、更新・基盤保守時の一時的重複まで費用上限として保証しない。起動時のプローブは5秒×60回を許容し、応答時間のSLAとは区別する。初回アクセスにはイメージ取得、.NET起動、認証／データ接続の待ちが加わる。画面の要求が失敗した場合は起動を待ち、利用者が再試行する。継続的な自動リトライやウォームアップは追加しない。

接続中のチャット／ストリーミング、再接続、定期HTTP通信は稼働を長引かせ得る。ブラウザを閉じる・接続を明示終了する運用とし、接続断でも処理完了を保証する設計にはしない。後続のチャットは永続化・再接続を実装する。ゼロ復帰時刻はcooldown値だけから断定せず、スケール安定化・通信の残り・基盤挙動を実測する。[公式スケール仕様](https://learn.microsoft.com/en-us/azure/container-apps/scale-app)

未実装の待機WorkerはAzureにもAppHostにも配置しない。現在のWorkerは `--seed-demo` のみ受け付け、他の起動は失敗して終了する。seedはManual Jobで、配置だけでは実行されない。事業分析、通知漏れ回復も手動を既定とし、現時点で実行ハンドラー・スケジュールは存在しない。DB巡回、AI呼出し、定期ウォームアップを非利用時に走らせない。

後続Issue #6では **Queue＋ユーザー割当Managed IdentityのContainer Appスケーラー** を採用する。既存の長時間取込・不可視期間延長・再試行を一つの消費ループで扱いやすいため。イベント駆動Jobは、1件ごとの終了とタイムアウトで処理を分離する必要が出た時の代替案とする。CPUだけでは0台から仕事の到着を検知できない。[Jobsの実行方式](https://learn.microsoft.com/en-us/azure/container-apps/jobs)

`infra/modules/apps.bicep` の `deployIngestionWorker=false` が既定。将来用の構成は最小0／最大1、0.25 vCPU／0.5 GiB、`azure-queue`、`ingestion`、queueLength=1、activationQueueLength=0、pollingInterval=60秒、cooldown=300秒。スケーラーはWorkerのManaged IdentityリソースIDを使用する。Issue #6で消費処理を実装し、`deploy.py` は `DEPLOY_INGESTION_WORKER=true` の明示設定時のみ有効化する。既定false。実Azureの0→1→0検証は #9 に残す。

必要RBACはWorkerのStorage Queue Data Contributor（スケーラーのキュー属性取得と消費を含む）、Storage Blob Data Contributor、Cosmos catalog Data Contributor、ACRのAcrPull。スケーラー認証に接続文字列・共有キーは使わない。現在はStorageアカウントスコープで割り当てている。後続の正本コンテナ等の権限は #5/#6 で追加・限定する。APIにキュー書込権限はまだなく、HTTP経由の取込は未実装。

## 予算と課金対象

**月10,000円（税別）は申請案であり承認済み予算ではない。** `APPROVED_MONTHLY_BUDGET_JPY=0` は未承認。実行者が対象、見積もり、承認額、契約の無料枠残量を確認してから正の値を設定し、各有料コマンドに `--confirm-cost` を付ける。承認フラグは実行者の確認記録であり、契約上の強制上限にはならない。

- 保存・固定費：ACR Basic、Cosmosのデータ／索引、Blob原本・版・ソフト削除、ログ保持、Key Vault等。レプリカ0でも残る。
- 利用量：Container AppsのCPU／メモリ／リクエスト、Cosmos RU、Storage操作／転送、Queueスケーラー監視、ログ収集。
- AI従量：Foundryの入力／出力、Jevの契約・呼出し。単価・為替・再試行で変わる。Jevの外部請求はAzure RG Budgetに含まれないため別途合算する。
- Container Appsの無料枠はサブスクリプション内の他アプリと共用される。専用に全量使える前提で予算申請しない。[Container Apps課金](https://learn.microsoft.com/en-us/azure/container-apps/billing)

新規の価格見積もり・確定単価は本Issueでは作成していない。上記の保存量・稼働時間・ログ月1 GB・AI回数を仮定として見積書に分離し、実行前の単価と利用可能枠で再計算する。

承認額が正ならRGスコープの月次Budgetを作り、実績80%／100%で `AZURE_BUDGET_EMAILS`（カンマ区切り、1〜5件）へ通知する。`AZURE_BUDGET_START_DATE` は作成時の月初。JPY請求契約でのみ使い、`AZURE_BUDGET_CURRENCY=JPY` を設定する。ARMは通貨変換しないので、実際の請求通貨をCost Managementで確認する。通知には集計遅延があり、リソース停止・AI停止・強制的な予算遵守は行わない。[Budgetの制約](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets)

## ログ・履歴の保持

| 設定 | 既定 | 効果／制限 |
|---|---|---|
| `LOGS_DAILY_QUOTA_GB` | 0.05 | 日次収集cap。月1 GBは別の想定値。30日ならcapの単純積は1.5 GBであり月1 GBを保証しない |
| `LOGS_RETENTION_DAYS` | 30 | Log Analyticsの保持日数。短縮は保持データに影響するので変更内容を確認する |
| `APP_LOG_LEVEL` | Warning | API/Workerの.NETログ。ASP.NET CoreはWarningを指定。本文・秘密を記録しない |
| `TELEMETRY_SAMPLING_PERCENTAGE` | 10 | Application Insights設定とAPI/Workerの `Telemetry__SamplingPercentage` → OpenTelemetryのSamplingRatioに適用。stdout/stderrを間引く設定ではない |
| `BLOB_VERSIONING_ENABLED` | true | Blob版管理。無効化しても旧版は削除しない |
| `BLOB_SOFT_DELETE_DAYS` | 7 | 削除したBlobの復旧保持期間。既存の期限変更は別途影響を確認する |
| `BLOB_HISTORY_REVIEW_DAYS` | 30 | これより古い履歴を手動レビューする方針タグ。自動削除ではない |
| `ACR_KEEP_LATEST_TAGS` | 3 | イメージリポジトリごとに最低限残す最新タグ数の方針タグ |
| `ACR_MINIMUM_IMAGE_AGE_DAYS` | 30 | これより新しいイメージを削除候補にしない方針タグ |

既存のAzure Monitor OpenTelemetry登録は接続文字列がある場合に有効になる。今回、固定割合のSamplingRatioを設定し、TracesPerSecondによる上書きを解除した。ローカル設定では `Telemetry__SamplingPercentage` を直接指定でき、未指定は10%。請求・AI使用量の台帳はサンプリングしない。トレース外ログやメトリクスまで同率で削減されるとは扱わず、Container Appsログはログレベルと出力量で抑制する。実Azureへの送信量は未検証。

日次capには超過や対象外データの制約がある。到達すると調査に必要なログが欠ける可能性があり、厳密な請求上限ではない。[Log Analytics日次cap](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap)

Blob履歴とACRイメージは**手動レビュー・承認後の削除**とする。ライフサイクル削除／ACR purge Tasksを配置しない。現在・ロールバック用revision／Jobが参照するdigestとタグ、公開根拠の原本・版、処理中データは日数に関係なく保護する。候補一覧、復旧可能性、削減量を確認して別途削除承認を得る。本変更では既存データを削除しない。

## AIアダプターの設定契約 v1

`.env` から配置時に次の環境変数へ変換する。Issue #8で使用量台帳・予約・実呼出し・強制拒否を実装した。[AIアダプター運用手順](ai-adapters.md)を参照。現行の業務フローはAIを自動呼出しせず、実送信には明示opt-inも必要。

| `.env` | アプリ設定 | 既定 |
|---|---|---|
| `AI_STOPPED` | `AiUsage__Stopped` | true |
| `AI_DAILY_TOKEN_LIMIT` | `AiUsage__DailyTokenLimit` | 0 |
| `AI_DAILY_CALL_LIMIT` | `AiUsage__DailyCallLimit` | 0 |
| `AI_DAILY_ESTIMATED_JPY_LIMIT` | `AiUsage__DailyEstimatedJpyLimit` | 0 |
| `AI_USD_TO_JPY` | `AiUsage__UsdToJpy` | 0 |
| `AI_PRICE_VERSION` | `AiUsage__PriceVersion` | 空欄 |
| `AI_FX_VERSION` | `AiUsage__FxVersion` | 空欄 |

日界は `AiUsage__DayTimeZone=UTC`。スコープはこの検証環境全体でAPI／Worker・Foundry／Jevを合算する。0は無制限を意味せず拒否、未設定・不正設定・台帳不達も拒否する。停止解除には全上限と為替が正、承認予算・単価版・為替版が必要。単価版はプロバイダー／モデル／入力・出力・キャッシュ等の料金表を参照できる不変ID、為替版は日付・出典・適用レートを参照できる不変IDにする。

アダプターは送信前に永続台帳へ最大入力＋最大出力トークン、1回、最大推定円額を原子的に予約する。いずれかの上限を超える場合は送信しない。再試行も1回ずつ予約し、タイムアウトで請求不明なら予約を保守的に保持する。Jev等の非トークン課金も回数／費用枠で管理し、トークンが返らない場合を無料扱いしない。停止フラグは予約時と送信直前に確認するが、既に送信済みの呼出しを取消したとは扱わない。

呼出しID、runId、モデル／単価／為替版、予約量、実使用量、推定額、取得できたプロバイダー要求IDを保存する。`estimatedCostJpy` と請求照合後の `billedCost`／通貨／請求期間は別フィールドにする設計で、実請求照合は未実装。推定額を請求実績として報告しない。円換算は十進数で行い、境界・競合・日跨ぎ・再試行・停止のオフライン試験を #8 で追加した。設定変更は現状revision更新で反映する方式で、即時の遠隔停止スイッチは未実装。

## Azureの0→1→0確認手順（Issue #9）

1. 対象subscription/RGと承認額、通知先、費用枠を確認してwhat-ifを保存する。既存の `dse-worker` がある場合、Incremental展開は削除しない。旧アプリ／revisionの稼働状況を確認し、停止・削除は対象を提示して別途承認する。テンプレート変更だけで既存Workerが停止したとは扱わない。
2. API配置後、ブラウザ、監視、負荷生成を止め、CLIの `az containerapp replica list --name dse-api --resource-group dse-growth-loop-rg --revision <active-revision>` で0台を確認する。revisionは `az containerapp revision list` で取得する。確認のためにアプリURLを定期pingしない。
3. 単発のHTTPS要求を送り、開始時刻・HTTP結果・応答時間・0→1を記録する。通常の認証付き検索も確認し、コールドとウォームを区別する。Startup失敗、ACR pull、RBAC伝播、Cosmos不達をログで分離する。
4. 全要求・ブラウザ接続を終了し、cooldownと安定化を含めて十分待ち、制御プレーンで1→0を確認する。10分経っても戻らない場合は接続、外形監視、旧revisionを調査し、失敗も記録する。
5. #6の消費処理実装後、Workerスケーラーを有効化し、空キューで0、承認済みダミーメッセージ1件で1、処理完了後0を確認する。MIによる属性取得／読取／削除、不可視期間更新、再送・poison、停止時の回復を確認する。Queue監視は0台でもStorage操作を発生させるため費用に含める。
6. seedの手動実行だけが起動すること、分析・回復・ウォームアップのスケジュールがないこと、Budget設定と通知先を確認する。Log Analytics収集量、Cosmos RU、Container Apps稼働時間を後日Cost Managementの請求データと照合する。

`/health/ready` は呼ぶたびにCosmosのcatalogを確認する。15秒周期なら1台で最大5,760回/日となるため、配置のStartup/Liveness/ReadinessはDBを呼ばない `/health/live` を使う。DBが不達でもプロセスのreadinessは成功するというトレードオフがあり、実検索と手動のreadyで障害を確認する。外部監視のHTTP要求もAPIを起動し得る。これらの確認は本Issueでは未実施であり、ローカルBicep検証の成功とは別に記録する。
