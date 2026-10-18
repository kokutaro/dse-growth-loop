# PR環境と本番への継続的デリバリー

2026-10-18。実装は既定で無効。OIDC・限定RBAC・専用の共有PR基盤の設定は未実施で、実際のPR作成→更新→close→削除と本番自動反映の受入は未完了。

## 構成と流れ

| 契機 | 動作 |
|---|---|
| PR作成・更新・reopen | `preview-build.yml` がAzure資格情報を持たないジョブでAPIイメージを作る。`cd-preview.yml` はmain上の制御コードでGitHub正本・現在の書込権限・最新headのCIを確認し、PRアプリを作成/更新する |
| マージまたは未マージclose | closeのビルドワークフローはイベント記録だけで完了する。配備側は現在のPRがclosedであることを再照合し、そのPRが所有するアプリだけを削除する |
| mainのverify成功 | `cd-production.yml` が最新のマージコミットであることを照合し、API/Workerをビルドして既存の本番アプリのイメージだけを更新する |
| 毎時・手動reconcile | `cd-preview-reconcile.yml` が取り残しを点検する。closedが確認できた所有アプリを、通常の更新と同じPR単位の排他枠で削除する。openの古いhead/期限切れ、所有範囲不一致、GitHub照会不能は報告する |

制御コード・IaCのcheckoutは `github.workflow_sha`。配備資格情報を持つジョブでPRのコードやコンテナを実行しない。ビルドと配備は別のGitHubホストランナーで実行し、イメージは同一の検証済みrun IDとSHAで受け渡す。プラットフォーム・ソースラベル・repository ID・PR番号・ローカルimage IDを検査してからAzureへログインする。fork、別repository、現在の書込権限を失った作成者、古いhead、失敗したCIには配備しない。[GitHubの安全な利用](https://docs.github.com/en/actions/reference/security/secure-use)に従い、`pull_request_target` は使わない。

## PR環境の分離と削除境界

- 共有PR基盤は本番と別の `dse-growth-loop-preview-rg / dse-preview-environment`。`infra/preview-foundation.bicep` は手動準備用で、CDから実行しない。
- PR専用の資源は **Container App 1個**：`dse-pr-1379060968-<PR番号>`。repository ID・PR番号・controller版・head SHA・48時間後の点検期限をタグに記録する。`infra/preview.bicep` はIncrementalでこの1資源だけを宣言する。
- `Data__Mode=Demo` と `/tmp/<PRアプリ名>` のローカル正本・ファイル・キューを使う。API内のDemo用Workerが同じローカルキューを扱う。独立したAzure Worker、Cosmos、Storage、Key Vault、Foundry、本番の接続先や主体は配備しない。
- イメージpull専用の `dse-preview-pull` にACR読取だけを付与する。アプリには `identitySettings.lifecycle=None` として渡し、実行中のコンテナへIdentityを公開しない。[Azure公式のpull専用設定](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity#control-managed-identity-availability)を参照。
- AIは `Mock / Stopped=true / LiveOptIn=false`。PR画面は公開の架空データ専用で、実データを投入しない。自動分類は停止既定、Mockの分類回帰は通常CIで確認する。scaleは0〜1。再起動・更新・scale-inでPR内のデータはリセットされ得る。

削除前にGitHubのclosed、固定subscription/RG・型・正確な名前、所有タグ、専用環境、Demo/Mock設定、pull専用Identity、preview専用イメージを再読取する。アダプター自体も本番DELETE・対象外の型・RGを拒否する。RG、共有基盤、既存のAPI/Worker、保存データ、ACRのイメージは削除しない。ARMの配備履歴とACRのビルド成果物は意図して保持し、削除範囲に含めない。

更新と削除の排他キーは両ワークフロー共通の `dse-preview-1379060968-<PR番号>`、`cancel-in-progress=false`。待機順序だけを信用せず、変異直前と作成完了後にもPRを読み直す。途中closeは同じ所有範囲を回収し、reopenは削除を拒否する。手動キャンセルやARM要求のタイムアウトは処理が残る可能性があり、更新要求を盲目的に再送しない。毎時のreconcileがclosedの取り残しを回収する。GitHubの404/通信失敗をclosedと解釈しない。openの期限切れは報告だけで、期限を理由に利用中の環境を消さない。

closed runの `pull_requests` が空の場合は、GitHubのcommit→PR正本を照合して候補を解決する。別repository・fork・別baseや曖昧な複数候補は採用しない。同じheadを再ビルドしてdigestが変わる場合にも、作成要求ごとに一意なrevision suffixを渡す。正常性はSHAと期待digestの両方で照合し、更新前の正常なリビジョンを成功証跡として使わない。

## 本番の保全と復帰

本番は `dse-growth-loop-rg / dse-environment` の `dse-api` と `dse-worker` に固定し、`infra/main.bicep` 全体を再適用しない。環境変数、AI設定、認証、Identity、ネットワーク、scale、コマンド、タグを保存前後で照合する。PATCHはtemplateのimageとrevisionSuffixだけを変える。送信直前にもアダプターが現在設定を再読取し、操作者の変更を上書きしない。

安全な現在の運用に合わせ、APIがRunning、WorkerがStopped・全リビジョン無効・実レプリカ0・`/bin/sleep infinity`、旧イメージもdigest固定であることを前提にする。条件が変わった場合は自動反映を止める。APIを勝手に起動したり、Workerをキュー処理へ戻したりはしない。新しいWorkerリビジョンも停止・無効化し、APIの新リビジョンとlive/readyを確認する。

失敗時は設定が一致し、対象イメージのままであるコンポーネントだけ旧イメージへ戻す。現在のenv/auth/scaleを古いスナップショットで復元しない。片側の読取・復帰が失敗しても、もう片側の安全な復帰を試みる。設定ドリフトや進行中のARM操作は `ROLLBACK_RECONCILIATION_REQUIRED_*` として停止し、操作者の判断を求める。既存の失効フェンスの自動解除やデータの巻き戻しは行わない。

再実行は最新のmain/PR headとCIを再確認する。同じイメージでも正常性を確認する。PRの再試行は最新headに対応する `preview-build` を再実行する。本番は `Production delivery` のworkflow_dispatchで最新のCI成功済みマージコミットを再適用できる。reconcileのforeign/unknownは所有・GitHub状態を確認するまで削除しない。ワークフローの完了表示を、実AI品質・全体E2EやIssue #16の受入完了へ置き換えない。

本番更新とimage復帰も毎回一意なsuffixを使い、失敗後に同じcommitを再実行しても既存のimmutable revision名と衝突しない。API待機中にWorkerのimageが変更された場合を含め、両方の最終digestも確認する。操作者が指定した別imageを復帰処理で上書きしない。

## 有効化前に必要な変更（未承認・未実行）

現状はGitHub Secret/Variableが0件、CD用Environmentはなく、OIDC subjectはdefaultのimmutable方式である。`DSE_CD_ENABLED` が未設定/falseなら全コマンドは無効状態を返し、Azureクライアントを生成しない。以下は**別途確認後の設定内容**で、このPRの実装作業では実行しない。

1. 専用RG `dse-growth-loop-preview-rg` と共有PR環境、pull専用Identityを作る。既存の `dse-environment` や本番データには接続しない。
2. CD用のAzure主体をpreview/productionで分ける。計画名は専用preview RGの `dse-cd-preview` と本番RGの `dse-cd-production`（いずれも新規User-assigned Managed Identity）。既存の実行用 `dse-api-identity`／`dse-worker-identity` は変更・流用しない。新主体のclient/principal IDは作成前のため未確定。長期client secretを作らずGitHub OIDCを使う。GitHub Environments `pr-preview` と `production` を作り、deployment branch policyはcustomの **main 1本だけ** に限定する。
3. GitHubのOIDC subjectに `repo`・`repository_id`・`context`・`workflow_ref` をこの順で含めるcustom templateを設定し、Azure側は下記のmain上のワークフローに完全一致する信頼だけを追加する。現在の `use_immutable_subject=true` を維持し、`repo` 部分にはowner IDとrepository IDを含める。既存のOIDC利用先への影響も確認する。subject形式はOIDC claim確認後に確定し、ワイルドカードやPR refを許可しない。[GitHub OIDC reference](https://docs.github.com/en/actions/reference/security/oidc)、[Azure OIDC設定](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-azure)。

Repository subject templateの変更案は `{"use_default":false,"include_claim_keys":["repo","repository_id","context","workflow_ref"]}`。既存のimmutable設定を無効化する変更は含めない。issuer・audience・完全なsubjectをAzureのFederated Identity Credentialへそれぞれ登録する。

| Azure主体 | 許可するsubject（custom templateの想定値） |
|---|---|
| preview | `repo:kokutaro@4447283/dse-growth-loop@1379060968:repository_id:1379060968:environment:pr-preview:workflow_ref:kokutaro/dse-growth-loop/.github/workflows/cd-preview.yml@refs/heads/main` |
| preview | `repo:kokutaro@4447283/dse-growth-loop@1379060968:repository_id:1379060968:environment:pr-preview:workflow_ref:kokutaro/dse-growth-loop/.github/workflows/cd-preview-reconcile.yml@refs/heads/main` |
| production | `repo:kokutaro@4447283/dse-growth-loop@1379060968:repository_id:1379060968:environment:production:workflow_ref:kokutaro/dse-growth-loop/.github/workflows/cd-production.yml@refs/heads/main` |

issuerは `https://token.actions.githubusercontent.com`、audienceは `api://AzureADTokenExchange`。workflow refを固定することで、PR側がワークフローを改変してEnvironment名を名乗ってもAzureへ認証できない。subject検証が終わるまでCDを有効化しない。

| 対象/スコープ | 必要な権限 |
|---|---|
| preview主体／専用preview RGのみ | Container Apps read/write/delete、managedEnvironments read/join、RG read、ARM deployments read/write/validate・operation読取。RG削除・RBAC変更権限は不要 |
| preview主体／`dse-preview-pull`のみ | Managed Identityのread/assign |
| pull Identity／既存ACRのみ | AcrPull。Storage/Cosmos/Key Vault/AIへの権限なし |
| production主体／既存 `dse-api` と `dse-worker` の各resourceのみ | Container Apps read/write、revisions read/deactivate、replicas read。delete・RBAC変更権限は不要 |
| 各配備主体／既存ACR resourceのみ | AcrPushと `Microsoft.ContainerRegistry/registries/read`。AcrPush自体にはARMのregistry読取が含まれない。現在のACRはLegacyRegistryPermissionsなのでregistry単位の権限になる。制御コードはpreview用repositoryと本番repositoryを区別する |

本番に既存のAPI/Worker IdentityをCI主体として流用しない。ACR権限をrepository単位へ細分化したい場合、既存レジストリのABAC方式変更や専用レジストリが別の検討対象になる。今回はいずれも変更しない。

ACR認証では本番RGを明示し、subscription全体のresource探索を必要としない。配備digestはDocker push成功後の対象image IDとrepository digestから確定し、別publisherが変更できるremote tagの後読みへ依存しない。上記は必要権限の設定案であり、新主体による実操作での検証は未実施。

Repository Variablesは `AZURE_SUBSCRIPTION_ID`、`AZURE_TENANT_ID`、`DSE_PREVIEW_PULL_IDENTITY_ID`。各EnvironmentのVariable `AZURE_CLIENT_ID` は対応するCD主体。値はIDのみで、Secretは不要。権限・信頼・基盤・branch policyの確認後に最後にRepository Variable `DSE_CD_ENABLED=true` を設定する。無効化はfalseへ戻すことで新規処理を停止し、既存の取り残しは所有範囲を確認して回収する。

## 費用要因と検証状況

費用が増える要因はPRアプリのCPU/メモリ実行量、起動・HTTP通信、ACRイメージ保持、ActionsでのDockerビルド・artifact保存/転送。共有PR環境は専用のConsumption基盤で、アプリは0〜1、artifact保持は1日。新しいCosmos/Storage/Log Analytics/AI配置は定義しない。ACR成果物は保持されるため、増加分は別途確認する。追加の費用/試行作業台帳や都度の予算承認ゲートは作らない。

ローカルでは所有範囲・データ/キュー分離・fork拒否・古いhead/CIの拒否・close/reopen競合・取り残し検出・本番設定保全・失敗時復帰とアダプターの禁止操作をfakeで検証する。実際のGitHub→Azure OIDC、PR環境の作成→更新→close→削除、マージ後の本番自動反映、キャンセル中のARM要求とreconcileは、上記設定後の受入試験として残る。
