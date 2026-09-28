# API契約 v1

[OpenAPI 3.1](openapi.json)がパス・要求・応答形状の正本。この文書は認可、複数リソースの前提、HTTPだけでは表せない動作を規定する。現行`/api/search`の手動検索は変更しない。新契約は`/api/v1`配下。実装範囲は[実装状況](../implementation-status.md)と[会話手順](../conversations.md)を参照。OpenAPI 3.1は[JSON Schema 2020-12に対応する仕様](https://spec.openapis.org/oas/v3.1.0#schema-object)を使用する。

## エンドポイントと前提版

すべて認証必須。役割指定は[アクセス行列](authorization.md)の関係条件も満たす必要がある。GETは200、通常作成は201、非同期受付は202。JSONの返却型と要求例は[examples.json](examples.json)を参照する。

| 操作 | パス（`/api/v1`省略） | If-Matchの対象／成功の意味 |
|---|---|---|
| POST upload | `/documents` | 不要。multipartの原本とJobを永続受付して202＋jobId |
| GET status / evidence | `/documents/{documentId}`、`/documents/{documentId}/evidence` | Documentのstatus/activeVersionとETag。根拠は有効文書版のみ、古い版を暗黙表示しない |
| GET / PATCH profile | `/people/{personId}` | PATCHはPersonのETag。experienceEditsを含む全体を本人の集約として競合検査 |
| GET experiences | `/people/{personId}/experiences` | 本人のプロフィール詳細（閲覧scopeも検査）、ページング |
| POST search | `/search` | 不要。queryとlimit。200で候補とrunId、再順位付け状態 |
| POST / GET challenge | `/challenges`、`/challenges/{challengeId}` | 作成は不要。既定scopeはサーバー設定 |
| POST participation | `/sessions/{sessionId}/participations` | SessionのETag。ownerから対象personへ依頼、requested |
| GET / PATCH participation | `/sessions/{sessionId}/participations`、`/sessions/{sessionId}/participation` | PATCHは本人ParticipationのETag。accepted/declined/leftを決定 |
| Session準備 | `POST /challenges`に統合 | catalogの初期化Jobから所有者だけのSessionを準備して201。途中停止は同一キー再送/所有者GETで回復 |
| GET / PATCH session | `/sessions/{sessionId}` | PATCHはSessionのETag。AI停止/再開は参加者、closeはowner。closed→openは禁止 |
| GET / POST message | `/sessions/{sessionId}/messages` | POSTはSessionのETag。201で保存済みMessageと連番。AI生成はこのユーザーAPIで指定不可 |
| GET / PUT outcome | `/sessions/{sessionId}/outcome` | PUTはSessionのETag。ownerが解決状態を記録しSessionの版も更新 |
| POST feedback | `/sessions/{sessionId}/feedback` | SessionのETag。自分の評価のみ。同session/target/owner/typeで1件、以後は別の更新APIを設計するまで409 |
| POST knowledge | `/knowledge` | bodyのSessionのETag。参加者の下書き作成Jobを202受付 |
| GET / PATCH knowledge | `/knowledge/{knowledgeId}` | PATCHはKnowledgeのETag。編集はsourceVersion/publicationVersionを増加し、確認/分析許可を無効化 |
| POST review / confirm / publish / withdraw | `/knowledge/{knowledgeId}/request-review`、`confirm`、`publish`、`withdraw` | KnowledgeのETagとbodyのpublicationVersion一致が必須。confirmは自分の確認だけを追加し、全員確認だけでは自動公開しない |
| PUT analysis consent | `/knowledge/{summaryId}/analysis-consent`、`/challenges/{summaryId}/analysis-consent` | 対象Knowledge/ChallengeのETagとsourceVersion。Knowledgeは各参加者の意思を記録、全員揃った時だけgranted |
| POST opportunity run | `/opportunities/runs` | 不要。analystがsummaryIdsを指定し202。未許可/旧版を含む要求は受付時拒否し、実行時も再照合 |
| GET opportunity | `/opportunities`、`/opportunities/{opportunityId}` | analyst自身の結果のみ。元要約の公開・分析許可が変わった結果は隠す |
| GET / POST retry | `/jobs/{jobId}`、`/jobs/{jobId}/retry` | retryはJobのETag。会話JobはqueryにsessionIdが必要。該当パーティションのtenant/ownerを再検査 |

POST/PATCH/PUTはすべて`Idempotency-Key`必須（自然文searchも有料再実行を抑制）。bodyのtenantId/scopeIds/ownerId/etag/createdByは受付けない。scope選択はv1の一般APIで提供せず、サーバーの事前設定に従う。署名URLや資格情報は応答へ含めない。

親ETagを使う操作は、子の作成と親版の更新を同じパーティションで確定する。別コンテナに作成するSession/Knowledgeは、まず元の版付き受付Jobを元パーティションに保存してから確定処理する。Issue #7のSessionはChallenge登録に統合し、初期化完了後201を返す。参加/設定変更はCommittedChangeを返し、編集用ETagはGETで取得する。Knowledge作成はこの理由から202＋jobIdを返す。Job完了後、作成IDを取得できる。作成レスポンスのETagは作成リソースの版であり、次回親ETagを必要とする操作の前には親を再取得する。

## 入力・処理上限

初期ダミー用途の契約値。性能測定済み上限ではない。文字数はUnicodeコードポイント、転送サイズはUTF-8バイトで両方を満たす。

| 項目 | 上限・拒否 |
|---|---|
| JSON body | 32KiB。413、どのフィールドも切り詰めて成功にしない |
| upload | ファイル10MiB、1個、multipart overhead 64KiB。空ファイル400。docx/xlsxのみ。拡張子/MIME/コンテナ署名の不一致400 |
| Open XML展開 | 展開合計50MiB、10,000エントリ、1エントリ50MiB、圧縮比100倍まで。マクロ、暗号化、埋込コード、パストラバーサルは拒否。外部リンクは追跡せず警告。受付後に判明した不正はJob.failed/INVALID_PACKAGE等（過大ならPAYLOAD_TOO_LARGE）。機械抽出120秒、最大3試行 |
| query | 1〜2,000文字、空白のみは400。limit=1〜100、初期UIは20 |
| title / displayName | 300 / 100文字 |
| message / summary / evidence text | 8,000文字。複数フィールド合計でもbody上限を満たす |
| feedback comment | 2,000文字、任意未入力はnull |
| 配列 | scope20、Session参加者20、profile経験訂正20、通常ID/根拠100、分析入力100。Schemaに個別指定 |
| レート | 主体単位search 30回/分、その他書込30回/分、GET120回/分、upload5回/分。429＋Retry-After。AI予算とは別のゲート |
| AI | 月額承認、UTC日次費用/回数/トークン、停止フラグを毎実行確認。[費用契約](../development-cost-controls.md)を使用 |

必須文字列の空白のみ、同じIDの重複、未知フィールド、NaN/Infinity、数値の文字列表現を拒否する。JSON Schemaのformat検査を有効にする。入出力の業務参照・確率合計・期間・公開確認条件は追加検証も必要。

## ページング

GETの配列は`{items, continuationToken}`。`pageSize`は1〜100（省略20）。メッセージはsequence昇順、根拠はdocumentVersionId/id昇順、経験はid昇順、OpportunityはcreatedAt/id降順。tokenはサーバー署名付きの不透明値、最大4,096文字、有効15分、tenant/principal/filter/orderに束縛する。改変・別主体・期限切れは400 `INVALID_INPUT`。後続ページも認可を再検査し、権限が失われた項目は除外する。ページ間の全件スナップショットは保証しない。UIはidで重複排除し、token=nullで終了。空ページ＋非null tokenは次ページがあり得る。

## 冪等性・競合・非同期受付

キーは16〜128文字の英数字`._:-`。スコープはtenant＋principal＋HTTP method＋正規化path（sessionId等、意味のあるqueryを含む）＋キー。正規化した入力本文とIf-Matchのhashを永続記録する。uploadはファイルhashとメタデータを使う。

- 24時間以内の同一キー/同一hashは最初の確定status・body・Location・ETagを再生する。**認証・現在の認可を再確認してから**再生し、撤回済み本文をキャッシュから復活させない。
- 同一キーの異なる内容は409 `IDEMPOTENCY_CONFLICT`。処理中なら同じjobIdの202、同期要求がまだ確定前なら409＋Retry-Afterで再照会を促す。
- 冪等性記録と業務更新を同一パーティションで確定する。受付後に応答が失われても同じキーで再送する。入力/認証失敗は受付記録を作らない。再送時の旧If-Matchは、既存の確定結果を識別した後に判定するため、保存済みの同じ操作を412にしない。
- If-MatchはGETで得た強いETag、`*`/欠落は400、古い値は412 `STALE_VERSION`。再取得・比較してから**新しいキー**で編集を送る。黙ってlast-write-winsにしない。
- 202はJobの永続保存までを保証し、AIやQueue到達の成功ではない。`Accepted={jobId,status:queued,statusUrl}`、文書uploadは追加でdocumentId/versionを返す。Location=statusUrl。Job取得でstatus/resultId/lastErrorCodeを確認する。conversationsのstatusUrlにはsessionIdを含める。
- Job.retryはqueued＋notificationState=pendingなら同じJobを再通知する（追加AI不要）。failed/needs_review/budget_stoppedの再実行は新Job＋supersedesJobId。processingの有効リースがあれば409。期限切れはoperatorの手動回復としてリースを再獲得。readyの再実行は409。再実行で有料ゲートを解除しない。

保存に失敗したMessageは503 `SAVE_FAILED`。端末はclientMessageId/bodyを`save_failed`として保持し、自動的に保存済みへ変えない。ネットワーク切断で結果不明なら同じIdempotency-Key/clientMessageIdを再送して結果を確認する。保存済みMessageの通知漏れはGETによる連番回復で補う。

## エラー・縮退と画面表示

エラーbodyは`application/problem+json`のProblem（type/title/status/code/detail/traceId/retryable）。内部例外、SQL、原文、トークンをdetailへ出さない。

| HTTP／実行状態 | code | UI表示と操作 |
|---|---|---|
| 400 | INVALID_INPUT | 「入力内容を確認してください」。対象フィールドを表示、値を保持 |
| 401 | UNAUTHENTICATED | 「ログインしてください」。再認証 |
| 403 | FORBIDDEN | 「この操作は許可されていません」。権限を推測して再送しない |
| 404 | NOT_FOUND | 「対象を表示できません」。存在/権限を区別して漏らさない |
| 409 | INVALID_TRANSITION / CORRECTION_CONFLICT / IDEMPOTENCY_CONFLICT | 「状態が変わった／本人訂正との比較が必要／同じキーの内容が異なる」。再取得・差分確認 |
| 412 | STALE_VERSION | 「別の更新があります」。未保存内容を保持して比較 |
| 413 | PAYLOAD_TOO_LARGE | 「ファイルまたは入力が上限を超えています」。縮小して新規送信 |
| 429 | RATE_LIMITED | 「しばらく待って再試行」。Retry-After秒を表示 |
| 429（受付前）／Job.budget_stopped | AI_BUDGET_STOPPED | 「AI処理は予算または停止設定により停止中」。勝手に再試行しない。429のRetry-Afterは次の状態確認まで60秒で、予算リセット保証ではない |
| 503 | DEPENDENCY_UNAVAILABLE / SAVE_FAILED | 「接続できません／未保存です」。未保存入力とclientMessageIdを保持 |
| AiRun.failed／検索200 degraded | AI_FAILED | 「AI処理に失敗／再順位付け未実施」。一次候補と検証済み根拠のみ表示、自動投稿なし |
| 検索200 no_match | NO_MATCH | 「該当する候補がありません」。candidates=[]、エラー扱いしない |
| 検索200 budget_stopped | AI_BUDGET_STOPPED | 保存済み分布・語検索から返せる候補のみ表示。「AI処理停止中」を明記 |
| 409／非同期失効 | SOURCE_REVOKED | 「参照元の公開状態が変わりました」。生成破棄、本文/引用を表示しない |

Jev停止時に別AIへ自動切替しない。質問分類が利用できなければ手動条件検索・正規化語へ縮退する。Foundry停止時は一次候補を返せても再順位付け成功と記録しない。新規有料処理自体を受付けられなければ429、永続受付後の停止はJob状態で返す。失敗・該当なし・予算停止を同じ空配列表示にまとめない。

Issue #6の実装・設定・失敗コード・回復手順は[Office文書取込](../document-ingestion.md)。原本取得 `/documents/{documentId}/original` と本人の登録先取得 `/upload-context` も認可必須。機械抽出はAI予算を消費しない。

Issue #7のメッセージは`afterSequence`＋署名付きtokenでページングする。Messageの冪等キーは履歴保持中失効させず、再送はETagが変わっても同じclientMessageId/本文の保存結果を再生する（他の変更操作はIf-Matchをhashに含める）。詳細は[会話手順](../conversations.md)。
