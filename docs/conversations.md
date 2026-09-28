# 課題・参加承諾・永続チャット

更新日：2026-09-27。Issue #7の実装。全件ダミー用途。実Cosmos/Entra/Container Appsでの結合・再配置試験は未実施。AI投稿は#13で実装する。

## ローカルで試す

`dotnet run --project src/Dse.AppHost`を実行し、Web UIの「課題と相談チャット」を開く。AppHostは`.artifacts/local-ingestion/records.json`へ保存し、APIを再起動しても履歴を再取得できる。API単体起動で`Data__LocalPath`を省略したDemoはインメモリの試験用で、再起動すると消える。

1. 架空人物01で課題名と困っていることを登録する。所有者だけが参加するSessionを準備する。
2. 一次検索で人物候補を確認し、カードの人物IDを「相談する人物ID」に入力して依頼する。候補表示は参加同意ではない。
3. 別タブまたは別ブラウザーの「ローカルテスト主体」を架空人物02へ切り替え、「課題を読み込む」。本人への依頼を開き、承諾または辞退する。承諾前は会話本文を取得できない。
4. 承諾後は双方から送信できる。自動支援は既定OFF。ON/OFFを保存できるが、AI呼出し・AI投稿は行わない。
5. 参加者は退会でき、以後のAPI/Hub参加・送信・履歴取得を拒否する。所有者は相談を終了できる（終了後は読取のみ、再開不可）。所有者の退会は提供しない。

`X-Demo-Person`の固定3名への切替はDevelopmentかつDemo専用。Azureではこのヘッダーを認可に使わず、JWTのtenant/oid→IdentityBinding→principal/person/role/scopeで解決する。

## APIと正本

| API（`/api/v1`省略） | 意味 |
|---|---|
| `POST /challenges` | 課題と初期化Jobをcatalogへ原子的に保存し、決定論的Sessionを準備して201。Idempotency-Key必須 |
| `GET /challenges`、`GET /challenges/{id}` | 所有課題または本人へのrequested/accepted依頼のみ。ページング対応 |
| `GET /conversation-context` | サーバーが解決した本人のprincipalId/personId |
| `GET /sessions/{id}` | 現参加者だけ。JSONのetagとETagヘッダー |
| `GET /sessions/{id}/participations` | 所有者は依頼一覧、それ以外は本人の状態だけ。最大19依頼＋所有者1名 |
| `POST /sessions/{id}/participations` | 所有者が依頼。SessionのIf-Match＋Idempotency-Key |
| `PATCH /sessions/{id}/participation` | 本人がaccepted/declined/left。本人ParticipationのIf-Match＋Idempotency-Key |
| `PATCH /sessions/{id}` | 参加者のAI設定、所有者のclose。SessionのIf-Match＋Idempotency-Key |
| `GET /sessions/{id}/messages` | afterSequence（既定0）、pageSize（1〜100）、署名付きcontinuationToken。連番昇順 |
| `POST /sessions/{id}/messages` | SessionのIf-Match＋Idempotency-Key、clientMessageIdとbody。保存完了後だけ201 |

参加・設定変更の応答は`CommittedChange={id,sessionId,status,sourceVersion}`。次の編集用ETagは必ずGETする。保存済みMessageはkind=message、authorType=human、ownerId/createdBy（送信主体）、createdAt（サーバーUTC）、sequence、clientMessageIdを保持する。tenant/scope/author/sequence/AI種別はクライアントから指定できない。

Session・Participation・Message・messageCommitted Job・会話冪等性レコードは`conversations /sessionId`。Challengeは`catalog /tenantId`。参加状態とSession参加者集合を同じバッチで更新する。受諾・退会でmembershipVersionを増加し、非参加者・別tenant/scopeを拒否する。

課題作成はコンテナを跨ぐため、catalogのChallenge＋sessionInitialization Jobを先に保存する。Jobには所有者personIdと入力hashを固定する。Session IDは決定論的で、初期化はcreate-only。途中で停止した場合は同じ作成キーでの再送、または所有者の課題GET/一覧で再開し、既存Sessionや会話を上書きしない。初期化途中の第三者に会話権限はない。初期化Jobのready/resultIdで完了を記録する。

Issue #3の未実装だったSession作成202 APIとcatalog Participation案を、この同一パーティションの承諾方式へ具体化した。旧fixtureのsessionIdなしParticipationは読取契約として残すが、本機能から生成・認可に利用しない。既存の運用データを移行したという主張ではない。

## 保存・再送・再接続

1. SessionのETagを検査し、lastSequence増分・Message・MessageCommitted Job・二つの冪等性レコードを同じTransactional Batchで確定する。
2. 主体＋clientMessageId、主体＋Idempotency-Keyの両方を検査する。同じ本文は元のMessageを返し、違う本文は409。会話の保存キーは24時間で失効させず、履歴保持中は重複を防ぐ。Message再送は現在の認可確認後、ETag競合検査より先に確定結果を照合する。ETagを再取得しても本文/clientMessageId/キーは変えない。
3. DB障害は503 SAVE_FAILED。同時編集の未確定側は412。UIは本文とキーを残して「未保存（結果未確認）」と表示する。再送時に現在のETagを取得する。保存済みへの変更はDB応答または履歴取得で確認した場合だけ。
4. SignalRは連番だけの配送ヒント。本文・送信完了の正本ではない。送信後の通知が失われても保存成功を取り消さない。
5. 初回/再接続時はグループ参加後、最後に連続取得できた連番からページングする。sequenceで重複排除し、通知漏れは表示中10秒ごとの取得でも補う。途中ページで失敗した場合は最後の成功ページから再開。連番があるのにMessageがない場合は503 HISTORY_INCOMPLETEとし、空の成功結果にしない。

UIの再送待ちはtenant/principal/session単位で当該タブのsessionStorageへ保存する。トークンは入れない。タブ自体を破棄すると再送待ちは失われる。既にDBへ確定した履歴はタブやAPIプロセスとは独立して保持される。

## 接続と認証

SignalRはAPI内・最大1レプリカ。Redis/Azure SignalR Serviceは追加しない。`/api/hubs/conversations`のLong Pollingだけを有効にし、AuthorizationヘッダーでJWTを送る。WebSocket/SSEへのフォールバックは無効。`access_token`クエリは400で拒否し、HTTP HostingのリクエストURLログはWarning以上、HubのURLトレースは収集対象外とする。CSPのconnect-src 'self'で同一originのHTTP接続を許可する。

Hub接続にはAPIと同じ認証・許可ユーザーポリシーを適用し、Joinで現在のtenant/scope/参加者をDB検証する。グループ名にmembershipVersionを含め、退会前のグループには以後通知しない。通知に本文は含めず、履歴APIで毎回再認可する。既存参加者は履歴同期時に新しいグループへ参加し直す。Hubも主体別120要求/分のレート制限を適用する。Long Polling/Joinは通常APIの書込30回/分とは別枠で、接続維持だけで業務書込を使い切らない。

`CloseOnAuthenticationExpiration`で認証期限に接続を閉じ、再接続でMSALがトークンを取得する。期限切れ、許可外ユーザー、無効IdentityBindingは新規要求を拒否する。EntraのJWTは既発行トークンの外部失効を即時通知で追従する実装ではない。有効期限までの遅延があり、緊急停止はAllowedObjectIds/Bindingsを更新してAPIを再配置する。設定はプロセス起動時に読むため再配置が必要で、全メモリ接続も破棄される。退会はDBのメンバー変更により直ちに以後の本文取得を拒否する。

Long Pollingの保持・再取得と画面の履歴確認はAPI稼働時間に含まれる。相談画面を閉じる、人物を切り替える、ページを離れる際に接続と定期取得を停止する。接続が残っている間のゼロスケールは期待しない。API再配置後は接続を作り直しDBから回復する。実Azureの0→1→0や実JWT更新は#9で検証する。

## 将来のAI支援イベント

`MessageCommitted`契約はeventId、tenantId、sessionId、messageId、sequence、membershipVersion、payloadVersion=1.0。本文を通知に複製しない。Job type=messageCommittedはMessageと同時にDBへ保存し、既存Office Workerには配送しない。現時点では消費せずqueued/pendingを保持する。#13の消費処理はDB正本・現在の参加者・AI ON/OFF・open状態・利用量制限を再検証して実行する必要がある。イベントがあることはAI実行の許可ではない。

## 今回の検証範囲

ローカルのメモリ/ファイルアダプターで、二主体の承諾前/非参加者/退会後拒否、Hub Join認可、別tenant/scope、同時・重複送信、入力改変、DB停止、保存直後の応答消失、初期化途中停止、再起動、AI停止、closeを検証する。ブラウザー試験にはHTTP障害と応答消失の注入を含む。これらは実Cosmosの障害試験やEntraライブ試験を代替しない。実行結果は[実装状況](implementation-status.md)に記録する。
