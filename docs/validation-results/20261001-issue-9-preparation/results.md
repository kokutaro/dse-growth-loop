# Issue #9 実行準備・読み取り点検結果

実施日：2026-10-01。基準commit：`ecb6a766c07b2d82416a27ed97bbb17ed0073493`＋本PRの未コミット変更。実行者：Codex（開発者のローカルworkspace）。配置／Entra変更／秘密投入／有料推論の承認：未取得。Issue全体の合否：**未完了**。全体受入は #16 に残す。

## Azure読み取り点検

UTC `2026-10-01T03:51:19`〜`03:51:29`。現時点のAzure CLI選択subscriptionを読み取り先として使用した。これを承認済み配置先としない。subscription／tenantの実識別子は公開記録へ転載せず、ローカルの`.artifacts/azure-validation/20261001-current-account-preflight/report.json`に保管した。

実行コマンド（識別子のみ伏せた）：

```sh
az account show --query '{subscriptionId:id,tenantId:tenantId,state:state}' --output json
python3 scripts/azure_validation.py preflight --run-id 20261001-current-account-preflight \
  --subscription <CLI選択subscription-id> --out .artifacts/azure-validation
```

| 点検 | 観測・判定 |
|---|---|
| account | Enabled。読取成功 |
| `dse-growth-loop-rg` | `exists=false`。このRGのリソース一覧／RG内権限は未実行。subscription全体にデータがないとは解釈しない |
| Provider登録 | `Microsoft.App`、`Microsoft.OperationalInsights`、`Microsoft.ContainerRegistry`、`Microsoft.DocumentDB`、`Microsoft.KeyVault`、`Microsoft.ManagedIdentity`の6種がNotRegistered。前提条件失敗 |
| 登録済みProvider | `Microsoft.Insights`、`Microsoft.Storage`、`Microsoft.CognitiveServices`、`Microsoft.Consumption` |
| subscription ARM permissions | 読取成功。配置時のPolicy／Deny／Entra権限／MIデータ権限は未判定 |
| Foundry | japaneastのモデル一覧・usage読取成功。要求モデル実ID／固定版／SKU／capacityは未選択。対応quotaとの照合は未完了 |
| Container Apps | japaneastの地域usage読取成功。実環境作成・Consumption割当の可否は未検証 |

ツール終了1は未登録Providerのため。Provider登録は実行せず、既存リソース／データを変更しなかった。Jev契約・キー版、モデル選択・単価版／為替版、配置先・実行者・予算・操作範囲の承認を確定してから、別runで再点検する。

## ローカル検証

UTC `2026-10-01T04:00:17`〜`04:00:18`のBicep／差分検証は[機械可読記録](../20261001-issue-9-local/report.json)を参照。その後、同日04:00〜04:10 UTCに以下を実行した。過去の検証件数の転載ではない。

| コマンド | 今回の結果 |
|---|---|
| `dotnet restore --locked-mode` | 成功 |
| `dotnet build --no-restore -c Release` | 成功。既存のASPIRE010警告1件 |
| `dotnet test tests/Dse.Tests --no-restore -c Release --collect:"XPlat Code Coverage"` | 196件成功、失敗／skipなし。AiSmokeの行カバレッジ100%。fakeポート／fake HTTP／ローカル正本で、実サービス接続の結果ではない |
| `python3 -m unittest discover -s tests/scripts` | 38件成功。読み取り専用性、明示subscription、秘密を含む失敗の抑止、記録上書き拒否、欠測値null、複数active revision、1名配置制限、Incremental構成を検証 |
| `python3 -m coverage run --source=azure_validation -m unittest discover -s tests/scripts -p test_azure_validation.py`、`python3 -m coverage report --include='*/azure_validation.py' --fail-under=80` | 追加10件成功、記録ツール行カバレッジ90% |
| `npm --prefix web run build` | TypeScript／Vite成功 |
| `python3 -m unittest discover -s tests/evaluation` | fixture4件成功。AI品質の実測ではない |
| `.artifacts/issue-9-contract-venv/bin/python -m coverage run --source=tests/contracts -m unittest discover -s tests/contracts` | 独立venvで契約13件成功。`coverage report --include='*/contract_rules.py' --fail-under=80`で契約不変条件の行カバレッジ100% |
| 変更Markdownの相対リンク確認・`git diff --check` | 成功 |

最初の追加テストは記録ツール未実装／3件スモーク未実装／複数ユーザー拒否未実装で失敗し、実装後に成功した。ローカル契約テストの初回実行は`openapi_spec_validator`未インストールで起動失敗したため、独立venvに既定requirementsを導入して再実行し、上表の成功を確認した。UIは変更しておらず、今回のローカルPlaywrightは未実行。

## ライブ測定とTodo照合

配置0回、秘密取得0回、外部推論0回。Cosmos RU、AI実トークン、cold／warm時間、0復帰時間、請求額、保管費、ログ量は**未実測**。0として計上しない。見積もりはモデル／単価／為替未確定で未作成。実請求の遅延・停止中の保管費は[後日照合手順](../../azure-validation.md#費用の後日照合とtodoの更新)に従う。

| IssueのTodo | 確認結果 |
|---|---|
| 現状・SKU・モデル・quota・権限 | 読取の一部のみ完了。対象承認・要求モデル／SKU・権限の残項目あり、未チェック |
| validate→配置・認証必須公開 | ローカルvalidate／1名ガードのみ。what-if以降未承認、未チェック |
| Entra実トークン | 未実行、未チェック |
| MIの役割別接続／拒否 | 未実行、未チェック |
| Jev Choice/Noul・Foundry構造化/再順位付け | 3件承認ゲートと固定ダミーのスモークを実装。ライブ未実行、未チェック |
| API/Worker/Jobの0→1→0 | レプリカ読取記録を実装。ライブ未実行、未チェック |
| reconcile・プローブ失敗・再起動後永続化 | 手順のみ。ライブ未実行、未チェック |
| 再実行・旧image復帰・停止手順と空パラメーター | [操作手順](../../azure-validation.md#再配置ロールバック停止)を作成。コンパイル済みARMの全nested deploymentがIncremental／Workerとmodelは条件付きであることを今回テスト。手順作成のTodoは完了。実ロールバック／停止の合否は未測定 |
| 実測値を含むrun成果物 | 本記録はローカル・読取限定。ライブの構成／件数／RU／tokens／時間が未測定で未チェック |
| 失敗／未承認を未完了に残す | 本記録・Issue本文で明示。ローカル成功でライブを代替しない、完了 |
| 見積もり・実測・停止中費用照合 | 未実行、未チェック |
| 初期接続／配置リスクを解消・全体受入は後続へ | #16へ全体受入を残す。初期接続リスクは未解消で未チェック |

Issue #9はopenを維持する。PRには`Closes #9`を付けない。承認後に実際のリソース種別／構成／コマンド／件数／実測値を持つ別runを作り、該当Todoの全要件を照合してから残りを更新する。
