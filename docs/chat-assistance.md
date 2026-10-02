# チャット事例自動支援

Issue #13 のローカル実装。全件ダミー専用。公開知識の紹介と会話全体の公開承認は別であり、ONにしても公開範囲や分析同意は変わらない。

## 処理と保存契約

人間のメッセージと `messageCommitted` Job を会話パーティションで同時保存する。APIはその後、既存の取込Queueへ `tenantId/jobId/version/sessionId` のみ通知する。通知失敗は送信成功を取り消さず、Workerの `--reconcile` がcatalogのChallengeからSessionを辿って未完了Jobを再通知する。Queueは配送ヒント、Message/Jobが正本。

デバウンス後、最後の人間メッセージだけを処理する。旧イベントは完了扱いにして急速連投をまとめる。直近の人間の発言だけを読み、挨拶等をローカルの保守的な語判定で除外する。有用性Noul、既存の質問Choice/Noul変換、Probability/Noul/語索引、根拠引用による再順位付けを順に実行する。未知分類・不要・候補なし・全件不適合・予算超過・停止・失敗では投稿しない。人の保存はこの処理を待たない。ベクトル検索は追加しない。

人物はAI入力候補から除外する。全参加者の閲覧権限、公開状態、資料の有効版、membershipVersionと文脈の最後の連番を毎回正本で検証する。外部AIアダプター内の送信・キャッシュ取得にも同じ検証を適用する。再順位付け終了後の投稿確定もtenantのAccessFence内で検証し、SessionのETagを条件にする。参加者変更後は旧結果・旧キャッシュを使えない。

通常のMessageコレクション・連番・履歴APIを使う。既存の `kind=message` は正本のエンティティ識別子なので保持し、メッセージの種別を **`messageKind=ai_case`** とする。本文はモデルの自由作文ではなく、再順位付けで検証した出典の引用から構成する。`sourceSnapshots`（有効版・資料版・アクセス版）、`citations`（EvidenceReference）、`retrievalReason`、`retrievalRunId`、`aiRunId`、`decisionRunId`、文脈ID、ルール版を保存する。AI投稿からMessageCommitted Jobは作らない。

`sessionId + 文脈Message ID範囲 + ルール版 + membershipVersion` のハッシュを `dedupKey` にする。投稿、連番更新、Job完了、同一事例・論点・イベントの一意receiptは同一トランザクション。論点は最新の人間発言の正規化語集合で一致を判定する。意味的な同義文判定の達成を意味しない。同じ事例は版変更後も同一Sessionでは再投稿しない。

日次上限はtenant全体・UTC日単位。AccessFence中にcatalogへ一意な日次枠を先に予約し、その後会話へ保存する。日次予約後の中断で残った枠は保守的に消費済みと数え、自動返却しない。これによりコンテナー間の非原子性による上限超過を防ぐ。翌日の予約は別枠。

## 既定値と停止

設定は `ChatAssistance__<項目名>`。以下は品質評価前の仮値。

| 項目 | 既定値 | 範囲・意味 |
|---|---:|---|
| Version | chat-assistance-v1 | dedup・監査用ルール版 |
| DebounceSeconds | 5 | 0〜60秒、最後の人間発言から待つ |
| CooldownSeconds | 120 | 0〜86400秒、Sessionでの前回投稿から待つ |
| ContextMessages | 6 | 1〜10件、人間のみ。連結末尾1800文字まで |
| EventLimit | 1 | 0〜1。0で投稿停止、最大1事例の安全上限 |
| SessionLimit | 10 | 1〜100件、Sessionの累積投稿上限 |
| DailyLimit | 20 | 1〜1000件、tenant全体のUTC日次予約上限 |
| Attempts | 3 | 1〜3回、インフラ障害後の再処理上限 |
| UsefulThreshold | 0.7 | 0〜1、有用性Noulの閾値 |

Jobの処理リースは4分、Queue不可視期限は既存の60秒。デバウンス/リース競合では5秒後に再配送する。AIアダプターのタイムアウト・呼出回数・token/費用上限をそのまま適用する。処理中断時はリース満了後に有限回再実行する。AI成功後・DB保存前に止まった場合は再判定されることがあるが、投稿は一意保存される。外部AI呼出しのexactly-onceを保証しない。

既定のSessionは `aiAssistance=stopped`。`AiUsage__Stopped=false` とMock/承認済みLiveモード、SessionのONが揃った場合だけ支援する。AI失敗は自動再試行投稿せず、そのイベントを見送る。インフラ障害でAccessFenceが閉じた場合はAPI/Worker停止確認後、[正本復旧手順](persistence.md)でfenceを回復し、`--reconcile` / `--once` を実行する。

## 利用者の操作

会話UIに自動支援ON/OFF、事例本文と出典、役立った/不要を表示する。履歴は既存の再接続・ページング取得から復旧する。通知を失っても10秒周期のDB履歴取得で表示できる。

- `GET /api/v1/sessions/{sessionId}/messages/{messageId}/sources`：現在の参加者と出典権限を照合し、事例本文・有効版・引用位置を返す。
- `POST /api/v1/sessions/{sessionId}/messages/{messageId}/feedback`：`decision=helpful|unnecessary`。Idempotency-KeyとSessionのIf-Matchが必要。本人の同一メッセージ評価を更新する。自動学習や公開同意には使わない。

撤回・資料版変更後は保存済みの引用本文・取得理由・出典IDを履歴応答から除去し、表示不可とする。出典APIも拒否する。原本や全会話を外部公開する機能は追加しない。

## ローカル検証とライブ評価の境界

`ChatAssistanceTests` はfake AIで有用/不要/未知/停止/失敗、急速連投、重複イベント、Queue11配送、DB保存前中断/保存後応答消失、fence復旧、回数上限、参加者変更、撤回、出典/評価を検証する。`ChatSharedAccessTests` は実AiService＋Mock transportで全参加者の権限とmembership変更によるキャッシュ無効化を検証する。Playwright `chat-assistance.spec.ts` は別APIプロセスでON/OFF・自動事例・出典・評価・履歴再取得を検証する。CIでは独立suiteとして実行する。

不要会話の固定20例はローカルgateで誤投稿0件になることを検証する。これはfixtureの回帰結果であり、ライブAIの誤投稿率「20例中2例以下」の達成ではない。

ライブAI品質とAzureコールド起動は**未実施**。Issue #1の別承認境界に従う。過去のIssue #9の推論/展開承認を本Issueへ流用しない。ライブ試験前にダミー会話20例（語gateを通るが不要な会話を含む）と有用例、期待事例、モデル・設定・ルール版を固定する。失敗の原因と設定変更を記録し、確認集合を使って閾値を後付け調整しない。

計測は「人間MessageのDB確定→AI MessageのDB確定」を記録し、稼働中の目標15秒とAPI/Workerの0→1を含むコールド時間を分ける。UIの10秒ポーリング表示時間も別記する。予算予約・外部呼出数・token・モデル版・費用、重複投稿0件、権限外/撤回事例の入力・投稿0件を記録する。未実施のライブ項目はIssueのTodoにチェックしない。
