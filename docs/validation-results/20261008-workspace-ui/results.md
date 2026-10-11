# 2026-10-08 社員ワークスペースUI：ローカル最終検証

## 基点と保存先

- 元リポジトリ：`/Users/nyanpass/source/repos/dse-growth-loop`。clean main `78f182a30f106a8d34f7989d39d23f622e5d5c08` を確認し、終了時も未変更。
- 専用作業コピー：`/Users/nyanpass/Documents/Codex/2026-10-08/task-3/repo`、ブランチ `codex/anonymous-workspace-ui`。変更は未コミットで保持。過去のworktreeと検証成果は変更していない。
- 参照プレビューの修正版ZIPは存在とSHA-256一致を確認した。古いdesignブランチ全体は取り込んでいない。
- 実装境界・起動コマンド・不足するAPIは [anonymous-workspace-ui.md](../../anonymous-workspace-ui.md)、有料検証の未完了と再開条件は [resume-issue30.md](resume-issue30.md)。

## 変更一覧

1. 課題と匿名協力を中心にしたホーム、課題・相談、参加依頼、活動ルーム、事例・ナレッジ、マイページをReact/Viteに実装。参照の色・カード・メニューを移植し、人物検索をホームの中心に置いていない。
2. 既存Demo APIへの課題投稿、本人の即時参加・辞退、会話保存・再送・履歴照合・権限失効、活動完了、公開済み事例の再照合を接続。下書きはページ内、未確認送信はタブ内で主体別に保持。
3. 既存Knowledgeの版・scope別双方公開同意と編集時の失効を利用。実名開示は別API未実装のため無効。新UIのAzureモードは匿名契約不足を検出して業務APIを呼ばない。
4. 旧機能とテストを `/validation.html` へ保持。本人プロフィールと文書取込をマイページへ配置し、Demo主体の切替と取込待ち保存を分離。
5. 新UI12件のE2Eを追加。既存テストを含め、各テストでAPI/Viteを再起動する実行コマンドを追加。サーバーのレート制限と既存テストのアサーションを緩めていない。
6. Issue30採点器で、対象外の誤HTTP200を失敗とし、22検索と全24応答の線形p95を別集計、UI alertを失敗扱い。回帰試験を追加。

## 最終自動検証

コードの最終変更後に実行。以下の自動検証の後は、文書・証跡の追加のみ。Mock/Demoとfixtureの検証であり、実JWT・Cosmos・有料モデルの成功ではない。

| 検証 | 結果 | 証跡 |
| --- | --- | --- |
| .NET locked restore・solution build | 成功、エラー0。既存ASPIRE010警告1 | [restore](logs/restore-final.log)、[build](logs/dotnet-build-final.log) |
| TypeScript型チェック＋Vite本番ビルド | 成功 | [UI build](logs/ui-build.log) |
| E2E（既存30＋新UI12） | 42成功・0失敗 | [集計JSON](isolated-summary.json)、[実行ログ](logs/e2e-final.log) |
| .NET単体・API／統合（Demo） | 368成功・0失敗・0skip | [ログ](logs/dotnet-final.log)、[TRX](final-dotnet.trx) |
| 認証ユニット | 4成功・0失敗 | [ログ](logs/auth-final.log) |
| Python scripts（オフラインBicep・scorerを含む） | 107成功 | [ログ](logs/python-final.log) |
| JSON Schema／API契約 | 17成功 | [ログ](logs/contracts-final.log) |
| 評価fixture | 5成功 | [ログ](logs/fixtures-final.log) |
| JS構文・差分空白 | 成功 | `node --check web/scripts/test-e2e.mjs`、`git diff --check` |

初期の共有API一括E2Eは24成功・6失敗だった。同じダミー主体のレート上限への累積を切り分け、実行プロセスを分離して再検証した。プロフィール再公開の単独回帰も成功している。新しい再送テストの初回は、合成503の完了を待たず再読込するテスト同期で1件失敗し、エラーと正本保存の完了を待つよう修正して最終42件を通した。過去の失敗記録は作業ルートに保持し、成功に書き換えていない。

専用lint設定／スクリプトはなく、lintを実行したとは扱わない。Pythonログの有料実行拒否メッセージは、承認なし呼出しを拒否するテストの期待結果。

## Macの実画面確認

ChromeでPCと320×844・390×844相当の幅を確認。主要6画面を両スマホ幅で撮影し、手動の下書き→投稿→架空協力者への依頼→本人参加→匿名会話保存を実行した。参加後に相談者の追加承認を要求せず、会話に投稿者A／協力者Bを表示。事例詳細は公開検索の再照合後に表示し、本人プロフィールの読込と文書取込画面も確認した。

| 画面 | PC／320px／390pxの証跡 |
| --- | --- |
| ホーム | [PC](screenshots/final-home-desktop.png)、[320](screenshots/final-home-320.png)、[390](screenshots/final-home-390.png) |
| 自分の課題・相談 | [320](screenshots/final-tasks-320.png)、[390](screenshots/final-tasks-390.png) |
| 参加依頼 | [320](screenshots/final-invitations-320.png)、[390](screenshots/final-invitations-390.png) |
| 活動ルーム一覧 | [320](screenshots/final-rooms-320.png)、[390](screenshots/final-rooms-390.png) |
| 事例・ナレッジ | [320](screenshots/final-knowledge-320.png)、[390](screenshots/final-knowledge-390.png) |
| マイページ | [320](screenshots/final-profile-320.png)、[390](screenshots/final-profile-390.png) |

[寸法記録](manual-layout-metrics.json)では全取得でdocument幅がviewport幅を超えず、5メニューは文字表示と44pxのリンク高を保持。[ブラウザーエラー記録](manual-browser-errors.json)は0件。架空協力者の文書取込権限がない場合のFORBIDDEN表示も確認し、相談者の本人権限で正常読込を確認した。これは物理スマホ・スクリーンリーダー・文字拡大・全ブラウザーの検証ではない。

LibraryへPC／320／390のホーム画像保存を試みたが、この実行環境から正式保存を利用できず、Library IDは取得していない。保存済みとは扱わない。画像は上記のローカルファイルと成果物ZIPに保全した。

## 安全停止と残る検証

2026-10-08 13:38 UTCにブラウザーの表示幅を復元し、確認用タブを閉じて操作を終了。ローカルAppHostをCtrl+Cで停止し、5173／5180の待受がないことを確認した。以後ブラウザーを操作していない。push・PR・マージ・公開・Azureデプロイ・有料リソース起動・有料AI・権限／認証変更は実施していない。

未完了：本番匿名DTO、匿名候補依頼、実名開示の双方同意、通知APIと実JWT匿名性試験。warm24／cold3の有料実環境観測、旧ZIPの受領・差分確認、watchdog実装の検証は [再開条件](resume-issue30.md) の承認・取得待ち。Azure31リビジョンと9Jobの停止状態は今回の読み取りJSONに保持し、短時間起動方式を繰り返していない。

成果物は作業ルート `artifacts/` の差分patch、変更ファイルZIP、SHA-256 manifest。基点コミットに対する `git apply --check` とZIP/patchの内容照合を最終作成時に行う。依存物、環境秘密、認証キャッシュ、private AppHostログ、`.git`は含めない。
