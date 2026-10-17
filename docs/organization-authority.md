# 手動ダミー部署正本・所属履歴と同意の世代

2026-10-17。Issue #15のローカル実装。設計は[部署正本の設計記録](issue15-department-authority-design-20261017.md)、APIは[organization-authority-v1](specs/openapi-organization-v1.json)。管理元は架空の手動正本であり、実HR・Graph・SCIMとの接続や正式な人事情報の保証はない。部署と認可scopeは別に管理する。

## 正本と変更

catalogの同一tenant partitionに `authorityControl`、`department`、`personAuthority` を保存する。Controlの新規 `generation` と正の `revision` を組み合わせたtokenを、公開・分析・表示名の同意へ固定する。変更内容をA→B→Aと戻しても、確定操作ごとにrevisionが進むため旧同意は再利用できない。大量同期や自動予約は設けない。

部署は安定したID、管理者用名称、有効期間、後継IDを持つ。改称は同じID、分割・統合は新IDとし、廃止済みIDの再開を拒否する。後継への権限継承はない。主部署履歴はUTCの半開区間 `[from,to)` で1人1つ、重複・不明部署・部署の有効期間外・未来日の確定を拒否する。所属不明はnullとして保持できる。

部署・履歴変更は既存operatorのみ。期待Control ETag、期待authorityToken、冪等キー、理由が必要で、正本・Control・監査・receiptを1batchで更新する。監査の前後hashはETagを除いた保存内容を正規化して計算し、保存後の原本から再検証できる。dry-runではこれらを保存しない。管理要求の未知フィールドを拒否し、管理者が本人同意を代行するAPIはない。analyst/publisher/memberからoperatorへの自動昇格はない。

## 活動と同意の扱い

| 変更 | 参加資格 | 公開・分析・表示名 |
| --- | --- | --- |
| 同じscopeで通常異動・部署改称・履歴訂正 | 現在の参加を維持。部署によるscopeの拡大はない | tenant全体の旧tokenを失効。全員の新しい確認が必要 |
| enabled・OID/principal・role・scope変更 | 本人のaccessEpochを更新。旧参加・古いActorAccessを拒否 | 旧tokenを失効。元の値へ戻しても復活しない |
| 再有効化 | 現在の依頼者による新しい招待と本人acceptedが必要 | 参加承諾と別に再同意。依頼者自身が失効した既存活動は自動再開せず、新しい活動を作る |
| ダミーDB復元・世代更新 | 全員のepochを更新し、一般利用者は無効のままにする | 全旧tokenを無効化。旧同意の移行・自動承認はない |

参加者のID配列に履歴が残っていても、Sessionの本人epochと現在の正本が一致しなければ会話の読取・送信・AI共有を許可しない。検索候補のopaque tokenも発行時のauthorityTokenと対象epochに固定し、退職→再有効化を挟んだ古い候補から招待できない。

課題と同じcatalog batchで初期化Jobへ作成時の `ownerAccessEpoch` を固定する。Session作成前に中断しても、再開時に新epochを付け直さない。旧epochまたはpinのない未完了Jobは再開を拒否し、新しい活動を作る。依頼者が失効した旧活動は直接アクセスを拒否し、本人の課題・ルーム一覧から除外する。19件の招待上限は新しい参加行に適用し、失効した同一人の行を置き換える再招待では行数を増やさない。

再有効化後の旧所有活動は招待一覧からも除外する。画面の初期読込で使う課題・ルーム・招待・公開知識の各APIは、新しい活動を妨げない。手動ダミー部署は履歴用の終了部署を含め100件までとし、101件目の新規追加は確認・確定とも `ORGANIZATION_LIMIT`（422）で保存前に拒否する。100件時も既存部署の更新とinspectは利用できる。

終了済み部署の開始・終了日時は日時として比較する。同じUTC時刻の `Z` と `+00:00` の表記差は改称・後継ID訂正を妨げない。既存IDの開始日時変更、終了日時変更、終了後の再開は拒否する。

設定が渡される認可境界では、`Data:Mode` 未指定もAzureとして正本とbinding・OID・role・scopeを照合する。設定からoperatorを削除しても正本の旧roleを使い続けず、不一致を拒否する。Demoでの設定照合省略は明示的な `Data:Mode=Demo` に限る。これはコードの拒否条件であり、実際の認可設定を変更するものではない。

公開同意は元の参加者（退会者を含む）全員が対象、表示名は現在の参加者全員が対象で、混同しない。必要な元参加者が再同意できない場合は公開・分析を停止したままにする。

要約のGETは `reviewedVersion` を返し、request-review/consent/approveで表示済みの同じ値を必須とする。要約版・scope・本文・発生部署snapshot・現authorityTokenに束縛したopaque hashであり、サーバーが新しい版へ黙って同意を付け替えない。旧公開原本の再レビューはdraftへ戻し、同意・承認・activeVersionを消してsource/access版を進める。公開の再承認には全員の新しい公開同意が必要で、分析利用は別の同意値である。

同意の冪等再送は現在状態を返し、過去の開示応答や撤回されたgrantを復元しない。旧catalog・投影・保存済み分析結果も現在tokenと原本を再照合し、不一致を非表示にする。管理更新とAI入力消費・最終応答・共有確定には同じ永続AccessFenceを使う。失敗で閉じたフェンスは時刻経過で自動解除しない。ネストした処理は明示的に同じ保持へ参加し、その処理が終了するまで親もフェンスを解放しない。

## 発生部署と匿名境界

課題作成者personIdをサーバーで固定し、課題createdAt時点の主部署を期間履歴から解決する。snapshotにはdepartmentId/termId/asOf/source/generation/revisionを含める。後日の異動先へ過去の課題を付け替えず、本人が入力したdepartmentIdやLLMで補完しない。履歴不足は不明であり、部署条件を満たしたとは扱わない。件数の意味は「発生時の組織単位数」で、現在部署数や市場需要の実測ではない。

部署名・OID・epoch・snapshotをAIへ追加送信しない。Live分析の入力は従来どおり許可された要約本文・根拠ID・版のみ。社員向け共有DTOへ内部部署IDや社員ディレクトリーを追加しない。要約は所属確認の有無とroom局所参照を返し、分析根拠の公開metadataではdepartmentIdをnullにする。`/me/organization` は本人の現在の架空部署名と確認状態だけを返す。自由文の完全匿名化、既に閲覧・外部送信された内容の回収は保証しない。

## ローカルダミー管理の再現

以下はAPI/Workerを停止した専用の新規ローカルディレクトリーで行う。実Azureモード、停止確認なし、明示したローカルpathなしのCLI要求は、storage/credentialsを生成する前に拒否する。

```sh
export DOTNET_ENVIRONMENT=Development Data__Mode=Demo
export Data__LocalPath=/tmp/dse-organization-demo-new
export Organization__LocalOperatorConfirmed=true
export Organization__ApiStopped=true Organization__WorkerStopped=true

dotnet run --project src/Dse.Worker -- --organization-bootstrap
dotnet run --project src/Dse.Worker -- --organization-inspect
```

bootstrapは架空社員3名と専用の `dummy-operator` を明示投入する。既存のauthorityを上書きせず、旧データのあるdirectoryを黙って変換しない。通常のDemo起動も既存の権限・同意・部署履歴を設定から再投入しない。Azureで正本が未登録なら設定/JWTを代替正本にして許可しない。設定と正本のbinding/roles/scopeが不一致の主体も拒否し、設定直編集を正規の変更手順とは扱わない。

部署・所属変更JSONの形はAPI契約に従う。`Organization__ChangeFile` にそのファイル、`Organization__ExpectedETag` にinspectのControl etag、`Organization__IdempotencyKey` に新しいキーを設定し、`--organization-validate` → `--organization-change` を実行する。変更JSONにはinspectの `authorityToken` を `change.expectedAuthorityToken` として含める。validationは承認の代行ではなく、現在の期間・版の検査である。

enabled・binding・role・scope変更はHTTPへ公開せず、停止下の `--organization-access` のみ。入力は `personId`, `principalId`, `oid`, `enabled`, `roles`, `scopeIds`, `reason`, `expectedAuthorityToken`。ダミーOIDは空文字またはGUIDとし、別人と同じprincipal/OIDを拒否する。変更後は新しいETag/tokenを取得する。

復元後はAPI/Workerを止めたまま、inspectで得たETagと `Organization__Reason` を指定し `--organization-reset-generation` を実行する。先にmaintenanceを保存し、新generation・全epoch・監査を更新してからactiveにする。途中失敗はmaintenanceとフェンスを閉じたままにする。専用operatorだけを管理のため残し、一般利用者は全員無効にする。復元された権限を人が確認し、必要な利用者をaccessコマンドで個別に有効化してから開放する。旧活動・同意・投影を救済しない。新しい活動と再同意が必要である。既存の検証原本・証跡ファイルは削除しない。

同じDB内の増分値だけで、未申告のDB全体巻戻りを自動検出することはできない。世代更新を省略した復元を安全化したとは扱わない。maintenanceの復旧や実データを保持するオンライン復元は、停止証拠・権限再確認・復元対象外の世代アンカーを含む別の受入が必要である。

## 承認済みAzureダミー環境の限定初期化

`--dummy-authority-plan` / `--dummy-authority-apply` は、旧ダミー人物はあるが正本が全くない停止済みAzure環境に限る。通常起動・一般seed・復元CLIとは別で、対象DBは `dse-growth-loop`、catalogの設定済みtenant区画、人物は `demo-person-01` に固定する。既存人物の所有者と既存bindingを照合し、既存roleがmember/operator/publisher、grantが同じ人物へ対応する場合だけ実行できる。analystや他の人物の資格を追加しない。API/Worker/他Manual Jobの停止と、各実行設定のbinding/role/scope一致は作業者が事前確認する。フラグだけでAzureの停止状態を自動検出する機能ではない。

MIのみでCosmosに接続し、AI・KeyVault・queue・blob・既存データ削除を呼ばない。次の設定を明示し、既存のCosmos/tenant/MI/binding/grantを維持する。

```sh
export Data__Mode=Azure Ai__Mode=Mock
export AiUsage__Stopped=true AiUsage__LiveOptIn=false
export Bootstrap__Approved=true Bootstrap__DummyOnly=true
export Bootstrap__ApiStopped=true Bootstrap__WorkerStopped=true Bootstrap__JobsStopped=true
export Bootstrap__ConfigurationMatched=true
export Bootstrap__ApprovalReference=approved-dummy-bootstrap
export Bootstrap__IdempotencyKey=unique-dummy-bootstrap-key
dotnet Dse.Worker.dll --dummy-authority-plan
# 秘密値を含まないplanHashを確認して、その値を設定する。
export Bootstrap__ExpectedPlanHash='<planHash>'
dotnet Dse.Worker.dll --dummy-authority-apply
```

planは読取専用。applyは同じ設定・所有者・profile ETagから作ったplanHashを要求し、control1件・架空部署2件・対象personAuthority1件・監査1件・冪等receipt1件を同じcatalog transactionでcreateする。新generation/accessEpochを使う。旧人物・knowledge・投影・同意・活動参加・AI履歴の内容とETagを変更せず、旧同意/token/参加epochを新世代へ補填しない。残りの人物は正本未登録のため利用不可のままである。旧活動を継続する移行機能ではなく、新しい活動と再同意が必要になる。

既存control・部署・personAuthorityが1件でもあれば、上書きや世代リセットを拒否する。同じキーの再実行はreceiptと作成済み行のhashを確認して無変更で返す。行が失効・変更されていれば再有効化せず拒否する。transaction応答が不明でも、同じキーで結果を確認してから次操作を判断する。CLIはplanHash・固定dummy ID・件数・結果コードのみ出力し、OID、principal、本文、secret、SDK例外本文を出力しない。

このCLIの存在は実行承認ではない。実環境への投入と有料AI試験は対象・影響を示した明示承認が必要。Azure RBAC、Entra、ネットワーク設定を変更しない。

## 環境互換と残る受入

Aspireは開発機のOS用Dashboard/DCPを暗黙参照するため、Linux x64では `src/Dse.AppHost/packages.linux-x64.lock.json` を使う。既存 `packages.lock.json` は変更しない。差はOSX arm64の2パッケージをLinux x64の2パッケージへ置き換えた点だけで、共通依存の版・hashは同一。Linuxのlocked restoreを検証し、Mac/Windows用の従来の選択は変えない。この作業でMac/Windowsの実行成功を新たに測定したとは扱わない。

クラウド環境で固定Playwrightブラウザーの配布先が403の場合、任意の `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` をローカル検証に指定できる。未設定のCIは従来の固定Chromiumを使う。今回のローカルブラウザー版と結果は[検証記録](validation-results/20261017-issue15-authority/results.md)で区別する。

実Entra/Cosmosの複数process競合、実HRの管理責任・更新元、少量の実AI/手動Job、停止・復旧の実環境受入は未完了である。これらをFake/ローカル試験の成功へ置き換えず、Issue #15をCloseしない。新しい外部接続・実権限変更・Azure設定変更・有料AI呼出し・初回検索遅延の測定や最適化は実施しない。
