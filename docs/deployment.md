# ローカル開発とAzure展開

更新日：2026-09-26。対象は全件ダミーデータの初期実装。[実装状況](implementation-status.md)も参照。

## 設定の注入

`.env.example` を `.env` へコピーし、`python3 scripts/run-env.py -- <command>` で起動する。既存の環境変数がファイルより優先される。`.NET` 単体では `.env` を自動読込しない。

形式は1行ごとの `KEY=VALUE`。単一・二重引用符で囲める。コメントは行頭 `#` のみ。変数展開、コマンド置換、複数行、`export` 構文には対応しない。シェルとして評価しない。秘密値を `VITE_*` に入れない。

| 設定 | 用途 |
|---|---|
| `Data__Mode` | `Demo` は固定ダミー、`Azure` はCosmos。未指定はAzure |
| `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` | ローカルはDevelopment。AzureではBicepがProductionを設定 |
| `Auth__TenantId` / `Auth__ClientId` | Entraの単一テナント・SPA/APIアプリ登録ID |
| `Auth__AllowedObjectIds__0` 以降 | 許可するEntraユーザーのオブジェクトID。連続番号で指定 |
| `Auth__Bindings__N__*` / `Auth__Grants__N__*` | 許可tid/oidから架空人物・role・scopeへのサーバー対応表。未設定なら業務APIは403。`.env.example`と[永続化手順](persistence.md)参照 |
| `Cosmos__Endpoint` / `Cosmos__Database` | Cosmos接続先。キーは使用しない |
| `AZURE_CLIENT_ID` | AzureではBicepがManaged IdentityのIDを注入。ローカルは通常未指定 |
| `AZURE_SUBSCRIPTION_ID` / `AZURE_LOCATION` | 展開先。RGは `dse-growth-loop-rg` 固定 |
| `FOUNDRY_LOCATION` | Foundry配置先。アプリ配置先とは独立 |
| `FOUNDRY_MODEL_NAME` / `FOUNDRY_MODEL_VERSION` | モデル実IDと固定版。モデル配置時のみ必要 |
| `Foundry__Deployment` | 空欄ならモデルを配置しない。指定するとBicepで配置 |
| `FOUNDRY_MODEL_SKU` / `FOUNDRY_MODEL_CAPACITY` | 配置方式と割当容量。利用可能地域・契約・クォータを確認して指定 |
| `IMAGE_TAG` | 更新ごとに一意なイメージタグを指定 |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Azure Monitor接続先。AzureではBicepが注入 |
| `Storage__*` / `KeyVault__Uri` / `Foundry__Endpoint` / `Jev__*` | 次工程のアダプター用設定枠。現在は保存・推論処理で使用しない |

AppHostはDemoを明示する。Azureデータへの接続検証は個別起動し、`Data__Mode=Azure`、接続先、認証設定を指定する。ローカルの `DefaultAzureCredential` には `az login` 等の資格情報とcatalogのCosmosデータプレーン権限が必要。一般のContributorだけではデータ読取権限にならない。

AspireのDashboard/DCP依存はOS別のため、AppHostのみ `packages.<RID>.lock.json` を使う。macOS ARM64とLinux x64用を同梱する。別OSでは最初に `dotnet restore` でそのOS用のロックを生成して確認する。API/Workerの依存ロックはOS共通。

## Bicepの構成

`infra/main.bicep` はサブスクリプションスコープでRGと次のリソースを作成／更新する。

- Container Apps環境、API（最小0／最大1、Single revision＋HTTPS起動）。Queue消費WorkerはDEPLOY_INGESTION_WORKER=trueで明示配置。既定は配置しない。
- 手動Container Apps Job `dse-seed-demo`（ダミー初期データ投入）。
- ACR Basic。管理者パスワードを無効化し、Managed Identityでpull。
- Cosmos DB for NoSQL Serverless、DB `dse-growth-loop`、`catalog`（`/tenantId`）、`conversations`（`/sessionId`）。予約RUなし。
- StorageV2 LRS。非公開Blob `originals` / `extracted`、Queue `ingestion` / `ingestion-poison`。Blob版管理、7日ソフト削除。共有キー無効。
- Key Vault（RBAC、ソフト削除、purge protection）。秘密そのものはテンプレートに含めない。
- Foundry AI Servicesアカウント、プロジェクト、任意のモデル配置。
- Log Analytics（既定30日保持、0.05 GB/日収集cap）、Application Insights（サンプリング設定10%）。設定と制限は[費用ガード](development-cost-controls.md)を参照。
- API/WorkerごとのManaged Identity。catalog/conversationsとBlob/QueueはAPI/Worker別にコンテナ単位RBACを付与する。詳細は正本永続化手順の操作表を参照。

公開サービスエンドポイント＋Entra認証の初期構成で、Private Endpoint/VNet閉域化は未実装。全件ダミーの小規模デモが対象。ACR・保存データ・実行中レプリカ・ログ等に費用が発生し、ログ上限は請求全体の上限ではない。GlobalStandardモデル配置は国内完結を保証しない。

## 展開前の準備

1. Azure CLIとBicep CLIを準備する。検証時のBicep版は `0.47.16`。
2. `az login` を実行し、`.env` に `AZURE_SUBSCRIPTION_ID` を設定する。
3. RG・リソース・ロール割当を作れる権限とResource Provider登録を確認する。対象は `Microsoft.App`、`Microsoft.OperationalInsights`、`Microsoft.Insights`、`Microsoft.ContainerRegistry`、`Microsoft.Storage`、`Microsoft.DocumentDB`、`Microsoft.KeyVault`、`Microsoft.ManagedIdentity`、`Microsoft.CognitiveServices`。
4. アプリ配置前にEntraアプリを登録する。Entra登録はARM/Bicepには含めていない。
5. Foundryの地域・モデル・クォータを確認する。未確定ならモデル関連値は空欄のままにする。

## Entraアプリ登録

単一テナントのSPA/API兼用アプリを使用する。クライアントシークレットは不要。

- Application ID URIを `api://<client-id>` とし、委任スコープ `access_as_user` を公開・許可する。
- マニフェストの `api.requestedAccessTokenVersion` を `2` にする。
- SPAリダイレクトURIに `https://<APIのFQDN>/redirect.html` を登録する。ローカルEntra検証時は `http://localhost:5173/redirect.html` も登録する。
- テナントで管理者同意が必要なら事前に同意する。
- `.env` にテナントID、client ID、参加者のオブジェクトIDを設定する。架空社員IDとログインユーザーIDは分離する。

MSALがアクセストークンを取得してメモリに保持する。APIは署名・issuer・audience・tenant・許可ユーザー・スコープを検証する。許可リスト未設定ではAzureモードを起動しない。公開範囲は現在、許可された参加者共通の `demo` のみ。

## 展開順序

承認額 `APPROVED_MONTHLY_BUDGET_JPY`、通知先 `AZURE_BUDGET_EMAILS`、月初の `AZURE_BUDGET_START_DATE` を設定する。月1万円は申請案で、未承認の既定値は0。詳細は[オンデマンド実行と費用ガード](development-cost-controls.md)。ローカルbuildはDockerとベースイメージ／依存パッケージ取得の通信が必要。pushする実行者には対象ACRへのAcrPush権限が必要。

以下は利用者がAzureへ展開する際の手順。本実装作業ではクラウドへの操作を実行していない。

```sh
# ローカル構文・型検証。Azureログイン不要
python3 scripts/deploy.py validate

# 基盤の変更確認、作成
python3 scripts/deploy.py what-if
python3 scripts/deploy.py foundation --confirm-cost

# 手元のDockerでlinux/amd64イメージをビルド（Azure操作・pushなし）
python3 scripts/deploy.py build
# 明示push。ACRストレージ等の費用が発生
python3 scripts/deploy.py push --confirm-cost

# 代替：ACR Tasks（有料）。この経路を選ぶ場合、上のbuild/pushは不要
# python3 scripts/deploy.py build --build-method acr --confirm-cost

# Entra設定後、アプリを含めて確認、作成
python3 scripts/deploy.py what-if --include-apps
python3 scripts/deploy.py apps --confirm-cost

# 必要なときだけ初期データ投入
python3 scripts/deploy.py seed --confirm-cost
```

`apps` 出力の `apiUrl` をEntraのリダイレクトURIへ反映する。`seed` は開始要求であり完了保証ではない。次のコマンドとログで完了を確認する。

```sh
az containerapp job execution list --name dse-seed-demo --resource-group dse-growth-loop-rg
```

同じダミーIDが存在すればCreateのConflictを無視し、既存状態を上書きしない。API/Workerの毎起動では投入しない。RBAC直後の403やACR pull失敗は権限伝播を確認して再実行する。更新は新しい `IMAGE_TAG` で `build` → `push --confirm-cost` → `what-if --include-apps` → `apps --confirm-cost`。

スクリプトは同じ名前 `dse-growth-loop` のサブスクリプション展開を使う。最初の展開メタデータのリージョンを途中で変更するとAzureが拒否する場合がある。`apps` でも基盤テンプレートを同時に適用する。Incremental展開のため、基盤のみ実行し直しても既存アプリは削除しない。旧定義で配置済みの待機Workerも自動停止・削除されない。[移行確認手順](development-cost-controls.md)に従う。モデル設定は再展開時も維持する。空欄へ戻しても既存モデルの削除にはならない。

## 稼働確認と制限

- `/health/live` はプロセス稼働、`/health/ready` は手動のcatalog読取可否。配置プローブは `/health/live` を使いDB定期照会をしない。
- `/api/config` はブラウザ用の公開認証設定のみ。
- `/api/search` はJWT必須（Development＋Demoのみ省略）。30回/分、JSON最大32KiB。
- コンパイルではSKU利用可否、クォータ、権限伝播、モデル利用権限を保証できない。Azureでのwhat-if・展開・動作確認は未実施。
- Jev秘密値のKey Vault格納、実AI接続、手動の事業分析は次工程。WorkerはQueue消費と手動ダミー投入・フェンス回復・通知回復を提供。定期ジョブは配置しない。

## 確認した公式仕様

- [Container Apps Bicepリファレンス](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-07-01/containerapps)
- [FoundryアカウントBicepリファレンス](https://learn.microsoft.com/en-us/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts)
- [MSALログインとredirect bridge](https://learn.microsoft.com/en-us/entra/msal/javascript/browser/login-user)
- [Aspire AppHost SDK](https://aspire.dev/get-started/aspire-sdk/)

Issue #5でcatalog／conversationsの書込とコンテナ・Queue単位のRBACを定義した。[API／Workerの操作表と旧付与の扱い](persistence.md#apiworkerのrbac)を参照。実Azureの適用・許可／拒否検証は未実施。

## Office取込Worker

`DEPLOY_INGESTION_WORKER=true` は承認済みのapps配置時に指定する。既定false、有料操作の承認境界は維持。[取込設定・ローカル永続化・回復CLI](document-ingestion.md)を参照。実Queueとスケール0→1→0の受入は #9 に記録する。

## 会話接続（Issue #7）

SignalR Long PollingはAPIと同じoriginの`/api/hubs/conversations`を使用する。最大1レプリカを維持し、Redis/Azure SignalR Serviceを追加しない。接続や画面の履歴取得中は稼働が続くため、利用後は相談画面を閉じる。再配置・ゼロスケールで接続が消えてもCosmosの会話履歴は残る。認証失効・ログ抑止・回復手順は[会話手順](conversations.md)。Azureの接続/0→1→0は未検証。
