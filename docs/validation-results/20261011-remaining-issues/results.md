# ローカル実装・検証結果（2026-10-11）

実リポジトリ `kokutaro/dse-growth-loop` の基点 `78f182a30f106a8d34f7989d39d23f622e5d5c08` から、専用checkout `repo-issues-20261011`、ブランチ `codex/remaining-issues-20261011` で作業した。旧 `repo` のUI変更59ファイルを取り込み、そのcheckoutと旧成果物は変更せずハッシュ一致で保持した。[保持確認](preservation.json)。元リポジトリ `/Users/nyanpass/source/repos/dse-growth-loop` はcleanのまま。

GitHubは読取のみ。最新open Issueは #1、#14、#15、#16、#30〜#35、open PRは0件。Issueの受入条件、今回の対応順、実環境再開条件は [受入条件一覧](../../remaining-issues-acceptance-20261011.md) を参照。どのIssueもcloseしていない。GitHub push/PR/merge、Azure起動・デプロイ・設定変更、外部モデル呼出しは実行していない。

## 変更

- 指定プレビューの5メニューとマイページ、匿名の課題・協力を中心とするReact UIを実アプリに移植。「参加する」から追加承認なく活動開始するDemo経路、会話、事例公開、編集による双方公開同意の失効を維持した。スマホの画面移動後のスクロール残留を修正。実社員向けのサーバー匿名化DTOと実名開示の双方同意APIは不足しており、Azure経路の新UIは閉じる。[UI契約](../../anonymous-workspace-ui.md)
- #33：scope、撤回、旧版、参加不可、taxonomyの不一致を拒否し、AI処理中に原本・根拠が変わった場合も最新原本を再確認する。候補と説明の再流出を防ぐ19件の追加回帰を含む。[対象ログ](logs/issue33-targeted.log)
- #15：分析同意された承認済み公開要約だけを使う手動分析、課題単位の重複排除、決定論的集計、候補ごとの人の判断、公開根拠の本文閲覧を実装。所有者・scope・最新同意と版を入口/読取/判断/送信で確認し、撤回で候補と全派生集計を無効にする。入力上限、冪等、タイムアウト、永続同時実行枠を設ける。要約の分析メタデータ訂正は再同意・再承認を必要とする。[機能・API・運用契約](../../business-analysis.md)
- #31/#32：保存応答の条件/hash照合、逆順・負例・不正根拠の再採点、構造化の項目別分母・unknown・欠落を扱う採点器と人のレビュー票を追加。[オフライン結果](../../offline-quality.md)
- #30：承認済みv3の保存実サービス48検索を再採点しサービス品質合格を確認。新しいモデル送信ではなく、総合ゲートは認証付きHTTP/時刻待ち。[再採点](../20261011-acceptance-inventory/issue30-v3-replay-summary.json)
- #34/#35/#16/#14/#1：同run計測schema・未承認計画、統合試験設計、月額/採用ゲートと不足を記録。#34の完全収集器、#16の先行Issue完了後の全体結合は完成していない。各Issueの受入を資料準備だけで満たした扱いにしない。

## 実行した検証

| 検証 | 結果 | 証跡 |
| --- | --- | --- |
| .NET restore/build | 成功、0 error。既存ASPIRE010 warning 1件 | [build](logs/build-evidence.log) |
| .NETユニット・統合 | 423/423成功、skip 0 | [TRX](dotnet-evidence.trx)、[log](logs/dotnet-tests-evidence.log) |
| Python scripts | 121/121成功（offline14件を含む） | [log](logs/python-tests-final.log) |
| JSON schema/API契約export（要約メタデータ・新分析API含む） | 18/18成功 | [log](logs/contracts-tests-evidence.log) |
| 評価fixture | 5/5成功 | [log](logs/evaluation-tests-final.log) |
| フロント認証unit | 4/4成功 | [log](logs/auth-tests.log) |
| TypeScript型チェック・Vite build | 成功 | [log](logs/web-build-evidence.log) |
| Playwright E2E | 重複除外44/44成功。最新UI/分析14件も再成功 | [集計](e2e-summary.json)、[最終再検証](logs/e2e-evidence.log) |
| Worker手動分析・同run再読取 | Mock/persistent storeでcompleted、AI送信0 | [初回](logs/worker-analysis-first.log)、[再読取](logs/worker-analysis-replay.log) |
| Bicepコンパイル | 成功。ローカル既設compilerのみ、deploy 0 | [記録](bicep-build.json) |
| #34 schemaと不正入力4種、既存gold/hash照合 | 成功。測定は未実行 | [検証JSON](../20261011-acceptance-inventory/validation.json) |

`web/package.json` にlint scriptはなく、lint成功とは報告しない。E2Eは各ケースを新しいDemo/Mockプロセスで隔離し、累積のrate limitを回避した。会話テストの初回は2回の10秒replayを含む処理全体30秒でtimeoutした。API assertion各20秒は維持し、当該全体枠60秒に直して14.5秒で再成功した。初回失敗ログも [保持](logs/e2e-remaining.log)。本番rate limitや成功条件は緩めていない。

過去mainで問題になったプロフィール公開撤回・明示再公開の回帰も今回成功。解析fixtureの日付読取とテストキーの形式を直した後に全423件を実行し、最終結果を記録した。未解決のテスト失敗は0件だが、Mock成功を本番認証/AI/Cosmosの合格証拠へ転用しない。

## MacのChrome実画面

専用の実Google Chromeをheadedで起動し、1440×1000と390×844の6画面と分析/公開根拠を撮影した。2026-10-11 04:18:38 UTC、Chrome154.0.8037.99。各画面のscrollWidthはviewport幅と一致し、横はみ出し0。分析は許可済みダミー要約3件、2部署、申告12時間を表示し、候補判断「調査中」を保存、公開根拠の本文を確認した。PCホーム・分析根拠、mobileの6画面と分析根拠を目視確認済み。[記録](browser-review.json)、[再実行script](review-local.mjs)

| 画面 | PC | スマホ幅 |
| --- | --- | --- |
| ホーム | [画像](screenshots/pc-home.png) | [画像](screenshots/mobile-home.png) |
| 自分の課題・相談 | [画像](screenshots/pc-tasks.png) | [画像](screenshots/mobile-tasks.png) |
| 参加依頼 | [画像](screenshots/pc-invitations.png) | [画像](screenshots/mobile-invitations.png) |
| 活動ルーム | [画像](screenshots/pc-rooms.png) | [画像](screenshots/mobile-rooms.png) |
| 事例・ナレッジ | [画像](screenshots/pc-knowledge.png) | [画像](screenshots/mobile-knowledge.png) |
| マイページ | [画像](screenshots/pc-profile.png) | [画像](screenshots/mobile-profile.png) |
| 手動分析 | [画像](screenshots/pc-analysis.png) | [画像](screenshots/mobile-analysis.png) |
| 公開根拠本文 | [画像](screenshots/pc-analysis-evidence.png) | [画像](screenshots/mobile-analysis-evidence.png) |

## 未完了と再開条件

#15のDemo/Mockローカル機能は実装・検証済み。Live drafterと共有AI台帳接続は未実装でLive入口501、タイムアウト後の持続スロットのオフライン復旧CLIも未実装。新分析API/DTOは [OpenAPI](../../specs/openapi-business-analysis-v1.json)と契約テストを整備した。Manual Azure Jobはデフォルトdeploy=falseの設計・テンプレートのみ。Issue全体の完了ではない。

#31は新しいモデル/プロンプト/根拠量/確率提示の比較が未実測、#32は新goldの人による承認と意味的90%の合格証拠がない。#30はwarm24/cold3、#33は実AI/Cosmos、#34は全操作RU・保存/索引・AI/runtime・冷温HTTP/UI・適用単価/実請求照合、#35は全Azure月額8,000円ゲートと採用判断が残る。#16は先行Issueの完了待ち。

実環境を再開するには、最新共有台帳と実請求未照合分、累計API/Job稼働枠、全費目の今回上限、独立停止・元設定復元、本人ブラウザー認証/観測担当を先に固める必要がある。既存資料の停止状態や費用残量は過去時点。今回Azure状態を読んだ保証にはしない。API短時間起動→通知→ログイン待ちは繰り返していない。必要な旧追加修正ZIPはMac未取得。[具体的な不足・停止方法・承認範囲](../../remaining-issues-acceptance-20261011.md#実環境再開が止まる理由と必要な準備)

今回のローカルAspireと専用Chromeはレビュー後に停止。停止証跡は [local-stop.json](local-stop.json)。private Aspire dashboard tokenや生認証情報は成果物に含めない。旧未コミット変更・検証証拠は保持し、新変更は未コミットで引き継ぐ。
