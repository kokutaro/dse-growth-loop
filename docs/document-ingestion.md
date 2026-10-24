# Office文書取込（Issue #6）

社内の「02. スキル記入シート(例つき)」は、Google SheetsからXLSXへ書き出して同じ本人アップロードへ登録できる。[社内形式の入口・項目・制限](internal-resume-ingestion.md)を参照。URLの直接取込・元シート更新は行わない。

全件ダミー用途。docx/xlsxを機械抽出し、原本と抽出JSONを非公開Blobへ、Document・DocumentVersion・Job・EvidenceSpanをcatalogへ保存する。機械抽出後は #10 の構造化・分類・検索投影ジョブへ接続する。[プロフィール手順](profile-pipeline.md)を参照。取込Jobの `ready` は機械抽出の完了、enrichment Jobの `ready` は検索投影の完了であり、画面でも区別する。

## 受付と認可

既存v1契約に合わせ `POST /api/v1/documents`。multipartの `title`、`documentType`（profile/report/case）、`file` 1件と `Idempotency-Key` が必要。202は `documentId/version/jobId/status/statusUrl` とLocationを返す。検索JSONの32 KiB制限は維持する。

本人のPerson正本から対象者・部署・閲覧scopeを決定する。クライアントによるpersonId/departmentId/ownerId/tenantId/scopeIds指定、他人への代理登録、任意の公開範囲拡張は受け付けない。`GET /api/v1/upload-context` で登録先を表示する。Issue本文の指定UIは既存認可契約に合わせ、この版ではサーバー指定値の確認表示とした。

`GET /documents/{id}` と `/jobs/{id}` で進捗・失敗コード、`/documents/{id}/evidence` で有効版の出典、`/documents/{id}/original` で現在版の原本を取得する（全て `/api/v1` 配下）。原本は現在の正本と認可をフェンス内で確認し、attachment/no-storeで返す。SASやBlob URLは返さない。非表示対象は404。本文・ファイル名・原本・認証情報をログへ出さない。

同一キーは24時間、同じファイルhash＋メタデータに対して受付結果を再生する。別キーの重複はtenant・所有者・文書種別・タイトル・SHA-256・処理版を含む決定的IDで検出する。異なる所有者や閲覧範囲へのデータ流用はしない。変更されたファイルは新Document/version=1として登録する。既存Documentへの改版APIは今回追加しないが、Workerは正本版と現在Jobを照合して旧通知を破棄する。

## 入力制限と抽出

Issue #6の初期案に統一し、以前の契約にあったpptx/20 MiB/100 MiB/2,000エントリを置き換えた。測定済み性能上限ではない。設定は初期値以下に制限できる。

| 設定 | 初期値・安全上限 |
|---|---|
| `Ingestion__OriginalBytes` | 10 MiB、multipart overhead 64 KiB |
| `Ingestion__ExpandedBytes` | 50 MiB（全体と単一エントリ） |
| `Ingestion__Entries` | 10,000 |
| 圧縮比 | エントリ単位100倍 |
| `Ingestion__ProcessingSeconds` | 120秒、キャンセル・協調停止 |
| `Ingestion__Attempts` | 3回、手動再実行は新Job |
| アップロードレート | 主体ごと5回/分、429＋Retry-After |

ZIPのcentral/localヘッダー・暗号化フラグ・実読取量・CRC、パッケージ型、ルート関連、MIME/拡張子、XML DTD禁止を検査する。ZIP64・分割ZIP・Deflate/無圧縮以外の圧縮方式は対象外。旧Office・暗号化・マクロ・埋込コード・パストラバーサル・壊れたZIPを拒否。外部リンクは追跡せず `EXTERNAL_LINKS_IGNORED` を付ける。Open XMLのパーツを.NETのZIP/XML APIで読むためOffice実行環境は不要。

- Word本文の段落、表の行・セル・段落を位置に保持。ヘッダー/フッター/脚注は警告付き対象外。
- Excelのシート名・セル番地・結合セルのアンカー範囲を保持。空セルは出典を作らず、結合範囲を展開複製しない。shared string、inline string、boolean、保存数値・日付を扱う。1900/1904日付系を尊重し、Excel固有1900-02-29は原serial付き表示。数式は `<v>` の保存値のみ、欠落は警告。再計算しない。
- 8,000 Unicodeコードポイントで出典を分割し、元位置とoffsetを維持。画像OCRなし。空文書は `needs_review/EMPTY_DOCUMENT`。
- `UNSUPPORTED_DOCUMENT/INVALID_PACKAGE/INVALID_INPUT` は400、過大は413 `PAYLOAD_TOO_LARGE`。Worker検出時はJob失敗。`HASH_MISMATCH/PROCESSOR_VERSION_UNSUPPORTED/PROCESSING_TIMEOUT/RETRY_EXHAUSTED` を状態に記録する。依存障害は `DEPENDENCY_UNAVAILABLE`。

## 永続化・リース・回復

原本を `originals/{tenant}/staging/{random}.{extension}` に保存してから、Document＋Version＋Job＋冪等記録を同一パーティションのBatchで確定する。DB失敗時の孤立Blobは自動削除しない。DB成功後のみQueueへtenantId/jobId/versionを通知する。通知失敗でも202は永続受付済みを意味し、`notificationState=pending` から明示reconcileできる。送信直後の中断は重複通知を許容する。

Workerは60秒のJobリースをETagで獲得し、20秒ごとにJobリースとQueue不可視期限を更新する。取得・更新後のpop receiptを使って削除する。Jobの試行回数はDB正本、Queue dequeue countに依存しない。途中終了では通知が再可視化され、期限切れリースを次の起動で獲得する。停止前に握ったアクセスフェンスが残った場合だけ、既存の停止確認付き `--recover-fence` が必要。

出典は決定的IDの不変レコードとして20件以下のBatchで保存し、各Batchに所有JobのETag条件を含める。全出典と抽出Blob保存後、Document/Version/Jobをまとめて確定しactiveVersionを切り替える。中断時の未公開出典は再実行時にhash照合して再利用する。旧リース所有者・旧版・旧Jobは公開できない。

一時障害は10秒・20秒の待機で最大3回。依存サービスのRetry-Afterがあれば尊重する。不正入力は即失敗、上限到達はfailed＋`ingestion-poison`へID/版のみ通知する。poison通知直後の中断によるpoison重複は許容する。Job正本と再実行キーで業務結果は重複させない。完了済み通知は副作用なく削除する。

所有者またはscope内operatorが `POST /api/v1/jobs/{id}/retry`（If-Match、Idempotency-Key、reason）を利用する。queuedは同Job再通知、failed/needs_reviewはsupersedesJobId付き新Job、有効リース・完了済み・旧版は409。期限切れprocessingの明示回復はoperatorのみ。UIに再実行ボタンがあり、operator向け状態一覧はCLIで提供する。

```sh
# Azureでは既存の環境注入・Managed Identity/開発者資格情報を利用する
# 通知漏れ/期限切れの明示回復（通常起動でDB巡回しない）
dotnet run --project src/Dse.Worker -- --reconcile
# 本文を出さない状態一覧
dotnet run --project src/Dse.Worker -- --jobs
# 継続消費（引数なしも同じ）/ 1通知だけ消費
dotnet run --project src/Dse.Worker -- --consume
dotnet run --project src/Dse.Worker -- --once
```

中断一時Blobの技術的保持窓は24時間。業務データの保持期限ではない。API/Workerを停止後、`Cleanup__ProcessesStopped=true` と `--cleanup-staging` で、同tenantのVersionから参照されない古いstaging Blobだけを一覧する。削除する場合は対象を確認・承認後 `Cleanup__Apply=true` と `Cleanup__Keys__0` 以降で列挙し、同コマンドを実行する。ETag一致で削除し、確定原本・抽出物・他tenant・新しい一時Blobは対象外。Worker MIには原本削除権限を増やさず、回収は適切な原本コンテナ権限を持つ運用主体で実施する。

## ローカルとAzure検証の区別

AspireはDevelopment/DemoでAPI内Workerを動かし、`.artifacts/local-ingestion` のローカルファイルへ正本・Blob・Queueを保存する。再起動後も保持する。`Data__LocalPath` で変更できる。APIと独立Workerを同じパスで動かす場合、API内Workerは `Ingestion__RunWorker` を設定しない。ファイルの排他ロックとatomic renameはローカル試験用であり、Cosmosの性能・可用性を再現しない。Data__LocalPathなしのDemo正本は従来どおりメモリー（APIテスト用）。本番ではDemoを禁止する。

Azureは `DEPLOY_INGESTION_WORKER=true` で配置を明示opt-inし、Queue＋Managed Identityスケーラーの0→1→0を使う。既定未配置、有料操作の予算/明示フラグは維持する。APIの原本書込・Queue送信、Workerの原本読取・抽出物書込・Queue受信/更新/削除・poison送信は既存コンテナ単位RBACを使う。空QueueのStorage監視操作にも費用があり、CPUだけの起動は使わない。

実Queue/Cosmos/RBAC、0→1→0、Queue更新中の強制停止は #9 の承認済みAzure試験で別途記録する。ローカル成功を実サービス成功とは扱わない。AI呼出しはないためAI停止フラグを機械抽出の停止条件にはしない。

参考：[Queueメッセージ更新](https://learn.microsoft.com/en-us/dotnet/api/azure.storage.queues.queueclient.updatemessage)、[数式の保存値](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/working-with-formulas)。
