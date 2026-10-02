# 解決評価・知識の公開と撤回

Issue #12のダミー実装。実AI品質や実データ運用の受入結果ではない。公開承認者・評価尺度・分析実施者の暫定条件は[決定ログ](specs/decisions.md)を参照。

## 画面の流れ

1. 課題の相談画面でメッセージを保存し、所有者が解決／部分解決／未解決と結果を保存する。会話の終了と解決状態は別。
2. 各参加者が相談の有用性を1〜5または未回答で保存する。評価者、`usefulness-v1`、任意コメントを保存し、未回答を0やBadへ変換しない。因果効果・AI学習完了・人事評価として扱わない。
3. 所有者がAI下書きを要求する。Foundryアダプターは人の発言と結果の実在IDだけを読み（AI投稿を参加者の報告へ変換しない）、課題／前提／試したこと／結果／再利用条件／未解決点の六項目を返す。参加者の報告・推測・不明を区別し、架空IDや根拠なしの報告／推測は拒否する。初期値不明の欄を人が編集する。
4. 要約を保存してレビューを依頼する。版と公開scopeを固定し、元参加者全員が公開同意を明示する。分析同意は別のチェック。公開権限だけを理由に分析へ渡さない。
5. 会話参加者かつpublisherが版を指定して承認する。Development＋Demoの架空人物01がテスト用publisher。Azureでの追加ロールは運用設定に従い、本人ヘッダーで認証を迂回しない。
6. 承認後、永続Jobが分類・SearchUnit投影を行う。新しい自然文質問で事例として再利用できる。原会話の参加者ID・メッセージ・私的根拠IDをcatalogへコピーしない。出典は承認済み要約自体。
7. 編集・結果の変更は旧公開を即時無効化し、新版の全員確認と承認が必要になる。参加者は公開同意を取り下げるか公開を撤回できる。所有者の編集で再公開可能だが、旧版の承認やJobを流用しない。

会話閲覧は現在のSession参加者認可、知識公開は版・scopeの全員同意＋publisher承認、分析は同じ版の全員分析同意を別々に検査する。退会者は会話を閲覧できず、その参加者の確認を偽装／省略できない。公開先は会話の許可scope内に限定する。

## 保存と正本照合

`conversations / sessionId`にOutcome、評価者ごとのFeedback、`knowledgeDraft`、承認Job、冪等性記録、操作監査を保存する。契約は`contractVersion=knowledge-v1`。旧仕様fixtureのOutcome/Feedback/KnowledgeとはJSON Schemaの条件分岐で区別する。`knowledgeDraft`は原会話の根拠IDと版ごとの同意を持つ私的正本で、catalogの`knowledge`は承認済み要約だけの投影。

承認は正本と`knowledgePublication` Jobを同一パーティションで確定する。その後、catalogの知識・全Probability/別Confidence・Noul・SearchUnitを同一バッチで保存し、会話側Jobをreadyへ更新する。DBを跨ぐexactly-onceを保証しない。知識IDと版付き投影IDが決定論的なので二重承認・再試行で重複しない。catalog確定後に会話Jobの更新を失っても、既存投影を照合して完了でき、再分類呼出しは不要。分類試行はJobに記録して3回までとし、上限後は新版のレビュー・承認からやり直す。

検索・AI入力・キャッシュ再利用・引用の出力直前に、conversationsの状態、承認版、同意、scope、membershipVersionを照合する。撤回後も物理削除や索引更新を待たない。保存済みAI引用は履歴の次回取得で非表示へ置換し、元のメッセージは監査用に保持する。AI引用が表示中の会話は新着メッセージがなくても履歴を再照合する。公開変更を完了した画面は検索結果を失効させ、変更前に取得して遅れて到着した検索応答も表示へ戻さない。既にブラウザーへ配信済みのテキストは取り消せないため、再取得時の正本チェックを必須とする。旧固定事例fixtureは従来の正本・文書根拠照合を維持する。

分類は既存Jevアダプター、正規化・スキル抽出、分類体系とSearchUnitを再利用する。分類AIへ送る本文は承認済み要約だけ。AI下書きにはモデル／プロンプト／分類体系／データ版・根拠ID・使用量・再試行をAiRunに保持し、本文や認証情報は監査ログへ保存しない。AI停止・入力上限・不正応答では下書きを保存しない。

## APIと復旧

- `GET /api/v1/sessions/{sid}/knowledge`：参加者の評価・私的要約・同意・公開Job状態。
- `POST /api/v1/sessions/{sid}/knowledge/draft`：所有者の初回AI下書き。SessionのIf-Matchを要求。
- `POST /api/v1/sessions/{sid}/knowledge/{operation}`：`outcome/feedback/edit/request-review/consent/approve/withdraw`。If-MatchとIdempotency-Key必須。要約未作成時はSession、それ以降は要約のETag。版違い409、ETag違い412。未知フィールド・不正型は400。
- `POST /api/v1/sessions/{sid}/knowledge-recovery`：会話所有者または参加publisherが永続Jobを再実行する。処理は冪等、Jobから権限を補わない。

公開後の即時投影はAPIで試行する。失敗しても承認を取り消さずJobをpendingで保持する。画面の「検索への反映を再試行」、またはオンデマンドWorkerを使う。DB常時巡回や有料通知サービスは追加しない。

```sh
# Data/Ai/認証設定は既存の環境注入手順に従う。以下はローカルMockの例。
DOTNET_ENVIRONMENT=Development Data__Mode=Demo Data__LocalPath=/tmp/dse-knowledge Ai__Mode=Mock AiUsage__Stopped=false \
Recovery__ConversationIds__0=SESSION_ID \
dotnet run --project src/Dse.Worker -- --publish-knowledge
```

API/Worker間で共有する永続ストア設定を指定すること。未知のDB確定結果やプロセス停止でAccessFenceが閉じた場合は、両プロセスを停止して[フェンス復旧](persistence.md)を先に実施する。ライブAIの再実行には既存の費用承認・利用量制御を適用する。

後続の事業分析は`KnowledgeService.AnalyzeAndConsumeAsync`のAccessFence内で要約を利用し、遅延した結果の保存時にも版を再照合する。analyst権限かつscopeを持つ主体へ、公開承認・全員分析同意のある要約だけを渡す。未解決、低評価、未評価も対象にでき、高評価だけを需要の正解にしない。仮説抽出そのものはIssue #15。

## 検証

`KnowledgeTests`は複数主体、未承認、旧版、scope、参加変更、同意撤回、再公開、二重承認、引用遮断、Job再実行、catalog確定前／後のDB障害、実アダプターのMock経路とAPIを検証する。`web/tests/knowledge.spec.ts`は結果→評価→AI下書き→編集→全員確認→承認→新規質問で事例検索→撤回のローカルE2E。ライブFoundry/Jevの要約品質とクラウド統合は今回未実行。
