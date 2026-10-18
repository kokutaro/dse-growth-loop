# 正本永続化・認可・有効版照合

更新日：2026-09-26。Issue #5の実装範囲。全件ダミー限定。以下のローカル試験と、未実施の実Cosmos／RBAC試験を区別する。

## 公開したAPIと内部境界

既存の `POST /api/search` の手動検索条件と応答形状を維持し、主体のscope・member権限をサーバー設定から解決する。検索投影だけでなく最新正本と根拠の文書版を照合する。

| API | 実装内容 |
|---|---|
| `GET /api/v1/people/{id}` | scope付き人物正本、ETag |
| `PATCH /api/v1/people/{id}` | Bindingで対応した本人のみ。Person＋経験訂正＋監査＋projection Job＋冪等記録を同一batchで保存 |
| `GET /api/v1/people/{id}/experiences` | 本人のみ。id順、ページング |
| `GET /api/v1/documents/{id}` | 文書の処理状態と有効版、ETag |
| `GET /api/v1/documents/{id}/evidence` | 現在有効な文書版の検証済み根拠のみ。旧版は除外 |
| `GET /api/v1/jobs/{id}` | ownerまたはoperator。会話Jobは `sessionId` query必須。本文を含まないJobメタデータ |

書込は `If-Match` と `Idempotency-Key` 必須。未知フィールド、tenant/scope/owner指定、弱いETag、必須値の欠落を400で拒否する。競合は412、同じキーの違う入力は409。エラーはProblemDetailsの `code/traceId/retryable` を含む。GETは120回/分、書込・検索は各30回/分、429にはRetry-Afterを付ける。

ページングは `{items, continuationToken}`、pageSizeは1〜100（既定20）。Data Protectionで保護した15分有効のtokenをtenant/principal/filter/order/pageSizeに束縛し、各ページで再認可する。初期単一レプリカではローカルkey ringを使用し、再配置で失われたtokenは400になる。永続key ringと複数レプリカ運用は未実装。文書版が変われば旧ページtokenも拒否する。

Office受付／Blob取得／Queue送受信は #6。`DocumentAcceptance` はその検証後に呼ぶ内部境界で、Document＋DocumentVersion＋Jobの原子的受付と再送を実装する。multipart uploadや任意JSON保存APIは公開しない。AI呼出し、プロフィール再投影Worker、会話API、知識公開・撤回APIは後続Issue。プロフィール訂正後はactiveVersionをnullにし、Workerによる投影が完成するまでその人物を検索から除外する。

## 保存と投影

`IPrimaryRepository` と `IProjectionRepository` を分離し、正本repositoryではSearchUnitを拒否する。版付きJSONのkind固有フィールドを保持し、Cosmos `_etag` をAPIの `etag` に変換する。旧DemoDataの検索fixtureはそのまま評価器で使用可能だが、実検索の正本照合では版・根拠参照のない古い投影を採用しない。旧環境の投影だけを流用せず、新しい正本を含むseedまたは再投影が必要。

同一コンテナ／論理パーティション内だけTransactional Batchを使用する。全レコードを事前検証し、100操作・2MiB未満を要求する。メモリー実装も競合検査後に一括反映する。ETag条件なしの更新は提供せず、作成の重複は409。検索は同一分類配列要素の照合、安定順、救済枠を維持する。5,000投影超／1,000RU超は503 `SEARCH_LIMIT_EXCEEDED`。RUには正本照合のpoint readも含める（排他操作のRUは別途記録）。打切りを空結果や成功として返さない。

投影IDは版ごとに不変で、古いイベントによる上書きを防ぐ。`ProjectionActivation` は呼出元の完成manifest、ready状態、sourceVersion/accessVersion、正本ETagを検査してactiveVersionを切り替える。manifestを作る抽出・分類Workerは #10。切替前の新しい投影、building、旧版、撤回済み知識、参加不可の人物は照合で除外する。

Document／プロフィールとJobは同じbatchに置き、`notificationState=pending` を残す。Queueや別コンテナへの反映成功は受付成功に含めない。後続Workerはpending Jobを起点に通知を再送し、版・ETag・重複キーで再処理する。コンテナ間の全体rollbackは仮定しない。

## サーバー主体と共有

署名・issuer・audience・委任scope・許可oidを検査したJWTのtid/oidから `Auth:Bindings` を引き、principalId、架空personId、roleを解決する。scopeは別の `Auth:Grants`。Binding未設定／disabledは403。クライアントのrole、scope、人物名を権限として使わない。同tenantの有効Bindingはoid/personId/principalIdが一意。Session／KnowledgeのparticipantIdsはpersonIdであり、ownerIdはprincipalId。

`.env.example` のコメント付き設定例を参照。Azure配置スクリプトはBindings/Grantsの許可した設定名だけをruntimeSettingsへ渡す。実oidはgitへ保存しない。BindingやGrantの変更はAPI／Workerを停止して同時に反映し、再起動する。起動中の設定ホットリロードは提供しない。

`SourceGuard` は投影の本文参照を信用せず、正本のactiveVersion/accessVersion、scope、公開・参加可否、EvidenceSpan→DocumentVersion→Documentの版を確認する。引用本文は検証済み根拠正本から再構成する。

後続処理用の `SourceUseService` は以下の境界を持つ。単に照合済みオブジェクトをキャッシュして後で無条件に使用してはならない。

- `UseAsync`：AI入力等の利用callbackをフェンス内で実行し、正本snapshotを返す。実AI接続は未実装。
- `DisplayAsync`：保存結果のsnapshotを最新正本と比較してから配信callbackを実行する。
- `CommitSharedAsync`：最新SessionのETag/open/AI停止状態と、全参加者の個別Binding・scope・正本版を検査。公開Knowledgeを含むAI投稿のみ。Message＋Session連番更新＋知識版/sessionの重複記録をconversationsで一括保存する。失効・脱退・scope不足・二重投稿時は部分保存しない。

## 失効フェンスと復旧

初期ダミー規模ではtenantごとにcatalogの `system-access-fence` を使う。ETag付き書込で所有権を取り、正本失効・検索応答のシリアライズ・AI入力・共有確定を協調させる。APIとWorkerは同じ経路を使い、正本を直接更新する別経路を作らない。CosmosのSession整合性では、取得時の条件付き書込がcatalogパーティションの読取位置を進める。別コンテナのSessionは確定batchのETagで競合検査する。[Cosmos整合性の公式資料](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency)を参照。

フェンスは期限で自動解放しない。停止した古いプロセスが後から再開して失効後に投稿することを防ぐためで、競合中は503 `ACCESS_BUSY`。不明な保存結果、キャンセル、プロセスクラッシュではfail closedにする。tenant内の処理を直列化するため高スループット用途ではない。すでに配信した文字列や外部AIに渡した過去入力を回収できるという保証ではない。

2026-10-18の回帰修正：所有中の処理から別取得を試して、コールバック開始前に `ACCESS_BUSY` で拒否された場合は、その既知の拒否だけで外側の所有フェンスを残留させない。明示的な入れ子境界にも同じ判定を適用する。他の処理の所有フェンスを解除したり、同時取得を許したりはしない。依存先障害・不明な保存結果・キャンセル（外側で捕捉した入れ子障害を含む）は引き続き保持し、以下のオフライン復旧が必要。実環境で先行した残留の最初の原因が同じだったとは断定しない。

冪等キーはtenant/principal/method/pathごと。本文＋If-Matchのfingerprintを24時間保持し、現在の認可を確認して元status/body/ETagを再生する。業務batchには `committed` receiptを含め、Cosmosが割り当てたETagを読んで `complete` へ進めてから応答する。この間の障害ではフェンスを維持する。

手動回復手順（Azure上の実行は別途費用・操作承認後）：

1. 同じtenantのAPI／Worker／手動Jobをすべて停止し、進行中の書込がないことを確認する。時刻やlease期限だけで判断しない。
2. 運用者がフェンスのETag、保留Job、影響するconversationパーティションを記録する。本文・秘密は運用ログへ出さない。
3. 専用のgit対象外envに `Recovery__ProcessesStopped=true`、取得した `Recovery__FenceETag`、対象 `Recovery__ConversationIds__0` 以降を指定する。catalogだけならConversationIdsは不要。
4. `python3 scripts/run-env.py --env-file <回復用env> -- dotnet run --project src/Dse.Worker -- --recover-fence` を実行する。`FenceRecovery` が各パーティションのcommitted receiptを保存済み正本のbody/ETagで確定後、期待ETagでフェンスを解除する。途中失敗は解除せず、同じ手順で再試行できる。
5. 同じIdempotency-Keyによる再送が元応答を返すことを確認し、必要なpending Jobを後続の通知回復経路へ渡す。API／Workerを再起動する。

このCLIは停止の事実をAzureへ問い合わせない。`ProcessesStopped` は運用者による確認を表す。停止せずにフラグだけを設定してはならない。現在receiptを作る公開書込はcatalogのプロフィール訂正のみ。後続の会話書込が加わる場合は影響conversationを漏れなく列挙する。

## API／WorkerのRBAC

定義のコンパイルのみ検証済み。権限は対応機能の実装完了を意味しない。[Azure Storage組込みロール](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage)に従い、アカウント全体へのStorage付与をコンテナ／Queue単位へ縮小した。

| 対象 | API | Worker | 定義スコープ |
|---|---|---|---|
| Cosmos catalog | 正本／投影照合、本人訂正、Job／receipt／フェンスの条件付き書込 | seed、投影、Job、フェンス | 各identityにcatalogのData Contributor |
| Cosmos conversations | Job状態、後続の会話書込 | 会話Job／共有確定 | 各identityにconversationsのData Contributor |
| Blob originals | 受付時の保存、認可後の原本取得（#6） | 原本読取（#6） | API: Blob Data Contributor、Worker: Blob Data Reader、originalsのみ |
| Blob extracted | なし | 抽出結果の読書（#6） | Worker: Blob Data Contributor、extractedのみ |
| Queue ingestion | 通知送信（#6） | 受信・削除・visibility更新・再通知・スケール監視（#6） | API: Message Sender、Worker: Queue Data Contributor、ingestionのみ |
| Queue ingestion-poison | なし | poison通知送信（#6） | Worker: Message Sender、poisonのみ |

既存環境をIncremental展開するだけでは旧storage-accountスコープのrole assignmentは削除されない。#9で既存付与を一覧し、必要な権限の実動作確認後、別途承認した変更で旧付与を除去する。今回Azureの作成・削除・権限変更は実行していない。

## 監視と検証境界

Cosmosのread/page/batch/searchはRequestCharge、correlationId（Activity TraceId）、DurationMsを記録する。本文・SQL・JWT・例外本文は出さず、APIの未処理例外詳細ログも抑制する。専用Cosmos loggerをInformationに設定する。readinessは成功／失敗とも60秒キャッシュし同時問い合わせをまとめる。liveはDBを照会しない。

ローカル回帰はメモリーadapterを用いる。全kindの往復、旧fixture互換、認可、競合、原子的保存、冪等再送／中断復旧、旧版投影、AI入力／表示／共有の撤回、ページtoken、RU／件数上限を確認する。実行結果は[実装状況](implementation-status.md)に記録する。

未実施の実Cosmos／RBAC試験は #9で、承認済みのダミー環境に対し次を別記録する：異なるCosmosClient間のフェンス競合とSession token、412／409のbatch全体rollback、実continuationToken、RequestCharge、API/Worker双方の表中操作、付与していないスコープの拒否、停止／回復。メモリー試験をこれらの実測成功として扱わない。
