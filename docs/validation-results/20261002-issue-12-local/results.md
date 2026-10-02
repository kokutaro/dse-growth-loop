# Issue #12 ローカル実装・Todo照合

実施日：2026-10-02。基準main：`6762ac5`、対象：`feat/issue-12-knowledge`の実装作業ツリー。全件ダミー。Foundry/JevのHTTP応答試験およびMock経路であり、クラウド展開・有料推論・実社員記録・実AI品質評価は実行していない。

## 実行結果

| 確認 | 結果 |
|---|---|
| `dotnet test tests/Dse.Tests --filter 'Category!=LargeEvaluation' --collect:'XPlat Code Coverage'` | 256件成功、スキップ0。大集合のオフライン評価はCIと同じく今回対象外 |
| 行カバレッジ | 全体89.76%、KnowledgeService 98.19%、KnowledgeEndpoints 95.83%、KnowledgeSummary 100%。`TestResults/075bff55-2451-46b4-a0b9-f007bb3d7e92/coverage.cobertura.xml`から集計 |
| ソリューションビルド | 成功。既存ASPIRE010警告1件 |
| React型検査・Vite本番ビルド | 成功。500 kB超のbundleサイズ警告あり |
| Playwright Chromium | 19件成功。知識化1・会話4・検索6・プロフィール3・Office取込4・ログアウト1。スイートごとにAPIを起動してレート枠とデータを分離 |
| Python契約 | 16件成功。保存した知識・評価・分類・投影・Jobの実出力をSchema検証 |
| 設定・Bicepテスト | 41件成功。Bicepローカルコンパイルを含む。外部展開なし |
| 評価fixture / 認証単体 | 各4件成功 |
| JSON構文・相対リンク・`git diff --check` | 成功 |

初回にはsandbox IPC/CLI制限、ETag連動不足、フォームのセレクタ、固定fixtureと新契約の混在、試験スイート共通レート枠による失敗があった。原因を修正／独立した実行条件にして再実行した上記の結果を採用する。過去Issueの検証証拠は今回の成功件数に含めない。

## Issue Todoとの対応

| Todo（本文順） | 実装・証拠 |
|---|---|
| 1 解決状態・評価 | `KnowledgeService.ChangeAsync`、Outcome/Feedbackの保存・null・尺度版・評価者・任意コメント、API/UI試験 |
| 2 Foundry六項目下書き | `IKnowledgeDrafter`、Foundry strict JSON、報告/推測/不明、実在する人のMessage/Outcomeだけを根拠として許可。AiTestsの架空ID・根拠欠落・重複欄拒否 |
| 3 編集・レビュー・版指定承認 | `edit/request-review/approve`、版・ETag照合、編集時の全同意消去、新規質問へのE2E |
| 4 公開先・同意分離 | 元参加者全員の版/scope確認。会話認可、公開同意、分析同意を別保存。旧会話の退会者もParticipation履歴から復元。publisherは暫定条件に従う |
| 5 分類・投影・事例検索 | 既存Jev/正規化/スキル/分類体系/SourceGuardを利用。catalogへ承認要約・分類・SearchUnitを原子的保存。生チャット・私的根拠ID・参加者IDは検索対象へコピーしない |
| 6 撤回の即時遮断 | `PublishedAsync`とSourceGuardがconversationsを再照合。検索、AI入力、キャッシュと引用表示を遮断。保存済みAI引用は次回履歴取得で置換し、物理削除を待たない |
| 7 冪等なDB間Job | 承認と公開Jobを同じ会話パーティションで確定。決定論的知識/版ID、catalog確定後のJob完了再開。DB停止と確定後応答消失の両方を障害注入で確認 |
| 8 未解決要約の分析許可 | 全員分析同意と公開承認版の再照合。未解決/低評価/未評価を対象から除外しない。`AnalyzeAndConsumeAsync`が後続分析の利用境界 |
| 9 解決から事例再検索E2E | `web/tests/knowledge.spec.ts`とAPI結合試験成功 |
| 10 不正公開版・scopeの排除 | 未承認、変更前承認、旧版Job、撤回、別scope/tenant、非参加者、退会の拒否を自動試験 |
| 11 重複防止 | 再公開・二重承認・同じ冪等要求・旧版Job・pending Job・catalog確定後クラッシュからの回復で有効知識1件を確認 |
| 12 架空根拠0・評価の非転用表示 | 架空IDを成功採用した件数0（固定応答の拒否試験）。UIに因果効果・AI学習更新完了・人事評価を表さないことを明記。ライブ出力の測定値ではない |

## 残る境界

OWNER-01/03/05の実運用承認、ライブFoundry/Jev要約品質、Azure統合、AI事例自動投稿、事業仮説生成、全体受入は今回の完了に含めない。既にブラウザーへ配信された文字列を遠隔で取り消せるとは主張しない。撤回の次回サーバー取得・AI送信・キャッシュ表示では正本照合を必須にしている。
