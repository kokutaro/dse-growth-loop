# データ・状態・整合性契約

## 共通メタデータ

| 項目 | 契約 |
|---|---|
| `id` | サーバーが生成する不透明ID。1〜128文字、英数字と`._:-`、先頭英数字。グローバル一意を生成規則とするが、DBキーはパーティションと組で扱う。連番やIDの知識は認可にならない |
| `tenantId` | 検証済みJWTの`tid`とサーバー設定から解決。全レコード必須。`demo-tenant`はローカルfixture専用 |
| `ownerId` | サーバーの主体ID（`principalId`）。人物IDやメールアドレスではない。ジョブも依頼元主体を保持し、処理サービスを所有者にしない |
| `scopeIds` | サーバーの所属・リソース方針から付与。空は禁止。共通scopeの一致は必要条件であり、本人・参加者・公開状態の検査を省略できない |
| `schemaVersion` | レコード形状`1.0`。分類体系・モデル・業務版とは別 |
| `sourceVersion` | 集約の業務内容の単調増加整数。文書版はDocumentの版、抽出物は入力文書版、人物は人物集約版、投影は元集約版。異なる集約間の数字を比較しない |
| `activeVersion` | 現在利用可能な元集約版。未公開・無効時はnull。過去の有効版を維持したまま新しい版を取込可能。正本のポインターと一致しない投影は利用しない |
| `etag` | JSON表現の強いETag。ストアの`_etag`をAPI境界で写す。業務版と異なりリース・確認等の更新でも変わる。クライアント書込bodyに含めない |
| `createdBy` / `correctedBy` | `{type: principal|service, id}`。作成者は不変。本人訂正・確認時に後者と監査記録を更新し、再取込は消さない。ログインoidそのものは業務レコードへ複製しない |
| `createdAt` / `updatedAt` | サーバーUTC時刻、RFC 3339。クライアント時刻を信頼しない |
| `accessVersion` | 公開状態・閲覧範囲・分析許可の変更で増加。本文版が同じでも再照合対象。Sessionのメンバー版は`membershipVersion` |

## 正本と配置

| 型 | 正本／投影と配置 | 必須の参照検証 |
|---|---|---|
| Document / DocumentVersion / EvidenceSpan | `catalog /tenantId`。版・根拠本文は不変、Documentは有効版ポインター。原本バイト列のみBlob | DocumentVersion→Document、EvidenceSpan→その文書版。根拠の位置・本文ハッシュを実ファイルと照合 |
| Person / Experience / DepartmentActivity | `catalog`。PersonとExperienceの本人訂正を正本として保持 | Experience.personIdの所有主体、参照文書の閲覧権限。部署の活動を所属者全員の経験へ転記しない |
| ClassificationResult / ExperienceSignal | `catalog`。推論の保存結果、入力根拠とAiRunを固定 | 同tenant・同単位・同文書版の検証済み根拠のみ |
| SearchUnit | `catalog`。正本から再生成する投影 | entityId/unitId、全分類、正本のactiveVersion/accessVersion、projectionState=ready |
| Challenge | `catalog`。課題と初期化Job | 課題所有者。Session IDは決定論的で初期化を再実行可能 |
| Participation | `conversations /sessionId`。依頼と参加承諾の正本 | 課題所有者、招待対象本人。同じSessionと人物の依頼は1件 |
| Session / Message / Outcome / Feedback | `conversations /sessionId`。Session.id=sessionId。tenantも必須 | catalogの課題から所有者だけのSessionを作成。承諾時にParticipationとSession参加者を同時更新。Messageのauthor、sequenceはサーバー採番 |
| Knowledge | `catalog`。公開要約・対象版・参加者確認・承認者の正本 | 元Session参加資格とメッセージIDを検証。会話を移し替えず、承認した要約だけを保存 |
| Opportunity / RetrievalRun | `catalog`。分析・検索実行者の非公開結果 | 正本版・根拠ID・許可版のスナップショット。取得時も許可を再検証 |
| Job / AiRun | 対象と同じコンテナ・パーティション。`container`＋nullable `sessionId`を保持 | catalogならsessionId=null、conversationsなら非null。JobはaggregateId/aggregateVersion、AiRunは入力版と使用量を固定 |

`IdentityBinding`は運用設定として別管理する（[権限仕様](authorization.md)）。検索・AI対象へ混入させない。保存済みMessageは`deliveryState=saved`かつID/連番必須。UI専用`UnsavedMessage`はID/連番を持たず、`unsaved/sending/save_failed`を区別する。

## 原子的更新と回復

| 操作 | 同じパーティション内で確定する内容 | 別処理・障害時の扱い |
|---|---|---|
| 文書受付 | Document＋DocumentVersionメタデータ＋Job＋冪等性記録 | 一時Blob保存が先、DB確定後Queue通知。一時Blobだけ残れば未受付、削除は保持方針と別承認に従う |
| 本人訂正 | Person＋対象Experience＋監査差分＋投影Job＋冪等性記録 | 1要求20経験まで。どれかの版が競合なら全体を拒否。再取込候補は別版、本人の値を維持 |
| 参加承諾・退会 | Participation＋Session参加者集合・membershipVersion＋冪等性記録 | 同一会話パーティションで確定。退会前のSignalRグループは無効になる |
| Session作成 | create-only Session（決定論的IDが初期化キー） | 決定論的sessionIdで再実行しても重複作成しない。catalogの初期化Jobをreadyへ。途中停止は所有者GET/一覧または作成再送で回復 |
| メッセージ保存 | Session.lastSequence増分＋Message＋messageCommitted Job＋冪等性記録 | Queue/SignalR配送は後。保存成功＋通知失敗でもMessageはsaved。再接続は永続連番から再取得 |
| 公開・撤回 | Knowledgeの状態/版/確認・accessVersion＋投影Job＋監査＋冪等性記録 | 公開反映遅延は許容。撤回正本は直ちに利用を拒否し、投影・キャッシュ・既存引用は遅れて無効化 |
| AI結果確定 | 同じ対象パーティションのAiRun＋Job結果＋業務結果 | AI呼出しはDB外。リース喪失・対象版変更なら結果を採用しない。呼出し済み使用量は成功/失敗にかかわらず記録 |

同一パーティションでも全データを一括更新しない。1バッチを最大100操作・2MiB未満に収める設計とし、監査・Job・冪等性レコード分を予約する。超過する入力は分割受付前に拒否するか、新しい版へ段階構築し最後に有効版だけ切り替える。Blob、Queue、異なるコンテナ・sessionId、外部AIを跨ぐトランザクションはない。[Cosmos DBのTransactional Batch制約](https://learn.microsoft.com/ja-jp/azure/cosmos-db/transactional-batch)を参照（2026-09-23確認）。

投影は新しい`projectionVersion`で構築し、全単位完成後に親の有効ポインターをETag付きで更新する。途中版を検索へ混ぜない。旧イベントが後着した場合はaggregateVersion比較で無効化する。撤回時は完全削除を待たず、毎回正本照合で除外する。

Jobは永続状態が正本。Queueは`jobId/container/tenantId/sessionId`だけの通知で、通知内容もDBと照合する。`notificationState=pending`のままプロセス停止してもJobは残る。運用者の手動照合／retryが再通知し、DBの常時巡回は行わない。処理済み通知は無視し、ETag付きリースを獲得したWorkerだけが処理する。期限切れは再獲得、1Job最大3回。失敗したJobの手動再実行は新IDと`supersedesJobId`で履歴を保つ。

## 許可状態遷移

機械可読の正本は[transitions.json](transitions.json)。guardはサーバーが評価し、クライアントの自己申告で満たさない。

| 対象 | from → to | 主体・条件 |
|---|---|---|
| 取込 | queued → processing | Worker、リース取得（機械抽出にAI予算は不要） |
| 取込 | processing → ready / needs_review / failed | リース所有・対象版一致。readyには根拠検証成功も必要 |
| 取込 | failed / needs_review → queued | 所有者または運用者、再実行許可、新Job（AI接続時は別途予算許可） |
| 本人 | ai_unconfirmed → self_corrected / self_confirmed | 紐付け済み本人、If-Match一致 |
| 本人 | self_corrected → self_confirmed / self_corrected、self_confirmed → self_corrected | 本人、If-Match一致。Workerからai_unconfirmedへの巻き戻しは禁止 |
| 参加 | requested → accepted / declined | 招待対象本人のみ、If-Match一致。再依頼は新レコード |
| 知識 | draft → review_requested | 所有者、参加者と要約版・公開scopeを固定 |
| 知識 | review_requested → published | 公開承認者、参加者全員が同じ版・scopeを確認、最新ETag |
| 知識 | review_requested → draft | 所有者の編集・差戻し。確認を全消去 |
| 知識 | published → withdrawn | 所有者・元参加者・公開承認者、accessVersion更新 |
| 知識 | published / withdrawn → draft | 所有者、新版作成、確認消去。publishedの直接編集は旧公開も無効化 |

根拠検証済みAI抽出は`ai_unconfirmed`でも利用できる。本人訂正と再抽出が異なる場合、`correctionState=conflict`と`pendingAiVersion`で差分を保存し、本人値を保持する。HTTPの同時編集は412、業務的な再抽出競合は409 `CORRECTION_CONFLICT`として区別する。

AI停止では未実行のAiRunを`budget_stopped`、modelId=null、使用量0で記録しJobも停止する。Documentはqueuedのまま旧activeVersionを維持できる。呼出し後の障害は`failed`で使用量を保持し、停止前未実行と混同しない。無料の通知再送でAI予算を消費したことにはしない。

## 分類・期間・fixture互換

Choiceの全カテゴリを`probabilities`オブジェクトに保存し、各値0〜1、合計1（許容誤差1e-6）、choiceは最大値のいずれか。`confidence`は別の診断値で、AI実行では必須の数値、既存fixtureでは不明のnull。経験割合・年数・能力評価へ換算しない。

Noulは別型。`supported`は根拠ありでyesProbabilityが0〜1（0も有効）、`unknown`は根拠不足、`not_evaluated`は未実行。後二者はnullでありfalse/0にしない。異なるsignalの確率合計は制約しない。ExperienceSignal正本は根拠・AiRunを持ち、SearchUnit内の小型Signalはその投影。

Periodはstart/end/precision/months。unknownではすべてnull。年/月だけの記載は下限日を保存してprecisionで精度を保持し、端数・不明を0年にしない。monthsは暦上の計算結果でAI値を採用しない。複数案件の重複期間は和集合で数え、単純合算しない。基準日不明なら終了・期間はnull。

`taxonomy-v1`は既存domainのsales/operations/manufacturing/mechanical/softwareにunknownを加え、signalはhandover。未知の自然言語分類はunknownへ、未知の機械IDや欠けた分布は不正入力として扱う。taxonomy追加は版更新と再分類計画を必要とする。

現`DemoData.Create`は疎な分布、Confidence・監査・有効版を持たない`LegacySearchUnit`として検証する。既存の分類値・ID・`confirmationState=fixture`・検索結果は変更しない。新正本へ移行する場合はfixture由来を明示し欠落カテゴリ0を補うが、実AIの測定結果を捏造しない。通常のAI応答の欠落を0で補完して成功にはしない。正規化辞書はNFKC→trim→不変小文字→完全一致別名変換→重複排除→ordinal順。辞書は登録・検索で共通の版を使い、現fixtureの語はそのまま保つ。

Issue #7の保存先・冪等性・再接続の実装詳細は[会話手順](../conversations.md)。旧fixtureのsessionIdなしParticipationは互換検証専用で、新しい会話の認可には使用しない。

### Issue #10 の同一パーティション公開

現在のプロフィール処理は新経験・分類・全SearchUnit・親の有効版・完了Jobを単一catalogトランザクションで公開する。部分版は保存しない。本文・根拠・断片数上限内で完結し、上限超過は保留する。PersonのdocumentHeadsが同名/同種資料の最新受付を指定し、古い原本の通知を排除する。本人訂正を保持し、競合候補はpendingExtractionへ保存する。[操作・復旧](../profile-pipeline.md)を参照。
