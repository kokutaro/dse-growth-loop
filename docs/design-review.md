# DSE Growth Loop：社員向けWeb UI 旧初案

> この初案は匿名の課題起点のコンセプトを十分反映していなかったため、デザイン採用対象から外した。[修正版と既存APIの課題](design-revision-anonymous.md)を先に参照。以下は過去の変更・検証記録。

2026-10-05。基点はリモートmainと一致する `78f182a30f106a8d34f7989d39d23f622e5d5c08`。作業ブランチは `design/employee-workspace`。着手時の作業ツリーはclean。初案完成時は未commit。後続のユーザー承認により、このブランチへのcommit・pushが許可された。PR・mainへのmerge・Azure deployは対象外。

## レビューする画面

[画像一覧を開く](design-preview/index.html)。ブラウザーでローカルHTMLとして開ける。外部通信は不要。

- 入口：[desktop / 1440px](design-preview/home-1440-viewport.png)、[mobile / 390px](design-preview/home-390-viewport.png)
- 全体：[1440px](design-preview/home-1440.png)、[390px](design-preview/home-390.png)、[320px](design-preview/home-320.png)
- 検索結果と出典：[1440px](design-preview/results-1440.png)、[390px](design-preview/results-390.png)
- 状態：[読込中](design-preview/loading-1280.png)、[エラー](design-preview/error-1280.png)、[0件](design-preview/empty-1280.png)

画像はこの変更の実装をPlaywright＋導入済みChromiumで撮影した。検索結果は実ローカルAPIのDemo／Mock。読込・エラー・0件の画像はPlaywrightの応答保留／fixtureで再現した。実社員データ、実AIの推薦品質、Azureの稼働を表す画像ではない。

Library skillの正式な複数ファイル保存経路を試したが、環境の接続エラーで保存できなかった。永続Libraryリンクはない。実ファイルは `/workspace/dse-growth-loop/docs/design-preview/` に保全した。クラウド作業環境の保持期間を越える永続性は保証できない。ZIP取得や外部公開は必要ない。

## 診断と設計意図

AGENTS、README、実装状況、自然文検索の文書と実コードを確認した。AGENTSとREADMEの一部には初期実装当時の説明が残っているため、後続の実装記録と現コードを優先して機能を確認した。checkoutの `.agents/skills` は存在せず、共有 `.agents` にもUI技能はない。既存の深緑・ロゴ文字組を引き継いだ。新規フォントや画像素材の外部配信は追加していない。

従来画面は検索条件の内部ID・テスト主体・文書処理などが同じ情報階層に並んでいた。初案は次の3用途をナビゲーションと見出しで整理する。

1. **人・事例を探す**：自然文と検索ボタンを主操作にし、条件生成・編集は折りたたむ。人物と事例は既存の別欄と返却順を維持。適合理由、不足情報、本人確認、fixture／実AI、出典を残す。
2. **課題・相談**：上部の「課題を投稿する」から入力欄へ移動。投稿と参加する課題一覧を分ける。テスト用人物切替を明示的な折りたたみに移す。課題一覧の読込／空状態を追加。
3. **自分の経験**：プロフィールの本人確認・訂正と、資料の登録を一つの区画にまとめる。

落ち着いた深緑、白、薄い灰緑を使用。グラデーションや実績の装飾カードは追加しない。共通の見出し、入力欄、ボタン、出典、状態表示を定義し、主操作と補助操作の強弱をつけた。狭い画面は一列に並べる。スキップリンク、明示的なフォーカスリング、textareaを含むフォーカス、検索中のaria-busyを追加した。入力境界も見分けやすい色にした。

## 実装範囲と残した機能

| 項目 | この初案の扱い |
| --- | --- |
| 自然文検索／手動検索／条件編集 | 既存実装。API要求・既定条件・閾値・候補順は変更なし |
| 人物／事例／根拠／検索失敗時の推薦抑止 | 既存実装を保持 |
| 課題投稿・参加依頼・承諾・辞退・退会・チャット | 既存実装。検索候補の人物ID指定と候補リストを保持。自動で参加させない |
| 解決評価・要約レビュー・公開同意・承認・撤回 | 既存の相談内操作を保持。事例公開と分析同意は別のまま |
| プロフィール・文書取込・原本・再実行・処理診断 | 既存実装を保持 |
| 通知 | 既存の参加依頼・会話更新経路を保持。新しい通知センターや自動マッチング通知は追加していない |
| 事業化提案 | 未実装。稼働済みとして見せる画面や会社成長の数字は追加していない |
| 認証・認可・バックエンド・検索契約 | 変更なし |
| デモ表示 | 現段階のダミー限定という利用条件を保持。見た目の改善は本番運用の承認を意味しない |

独立した画面ルーターへの分割は今回行っていない。アンカーで区画へ移動するため、入力や開いている会話は維持される。既存の「相談画面を閉じる」で会話接続を終了できる。社員名での相談相手選択、詳細な通知一覧などは後続の検討事項であり、この初案の実装済み機能ではない。

## ローカル起動

Node.js 24.13以上、.NET 10.0.400系が必要。今回の環境はNode 24.19、.NET 10.0.401。Azure CLIや資格情報は不要。APIを直接起動すると、今回のLinux AppHost lock問題を回避できる。

```sh
cd /workspace/dse-growth-loop
# このCodex環境で追加したローカルSDKを使う場合
export PATH=/tmp/dse-dotnet:$PATH
export DOTNET_CLI_HOME=/tmp/dse-dotnet-home
export NUGET_PACKAGES=/tmp/dse-nuget

npm --prefix web ci --cache /tmp/dse-npm-cache
dotnet restore src/Dse.Api --locked-mode
ASPNETCORE_ENVIRONMENT=Development Data__Mode=Demo Ai__Mode=Mock \
Ingestion__RunWorker=true Profile__SecondaryWorkFragments=true \
AiUsage__Stopped=false AiUsage__MaxOutputTokens=3000 \
ASPNETCORE_URLS=http://127.0.0.1:5180 \
dotnet run --project src/Dse.Api --no-launch-profile
```

別のターミナル：

```sh
cd /workspace/dse-growth-loop
npm --prefix web run dev -- --host 127.0.0.1
```

`http://127.0.0.1:5173` を開く。Demoは架空データ、Mockはローカル処理。データはAPI再起動でリセットされる。Azure、有料AI、実認証、クラウドJobs、実データは使用しない。公開用hostの指定も不要。

## 検証

今回実行した結果。ログは [validation](design-preview/validation/) に保全。

| 検証 | 結果 |
| --- | --- |
| UI TypeScript型検査＋Vite build | 成功。500kB超のbundle警告は残る |
| 認証単体 | 4件成功 |
| API build（no-restore） | 成功 |
| .NET単体／API | 368件成功 |
| JSON Schema／OpenAPI契約 | 17件成功 |
| 既存E2E | 検索14、会話5、文書4、プロフィール4、知識公開1、自動事例1、ログアウト1：計30件成功（プロフィールは再実行） |
| 追加UI検証 | 4件成功。1440／390／320px、横はみ出し、キーボード、読込／エラー／空状態 |
| JavaScript実行時エラー | 3ビューポートの追加UI試験で0件 |
| 色の確認 | 主要文字色の計算結果は [contrast.json](design-preview/contrast.json)。本文11.10:1、補助文字6.12:1、placeholder4.63:1、主ボタン7.74:1 |
| 差分空白 | `git diff --check` 成功 |
| lint | 専用scriptは既存package.jsonにない。独立したlintは未実行 |

E2Eの期待値・検索API body確認は維持し、移動した折りたたみを開く操作を既存テストに追加した。追加テストは [web/tests/design.spec.ts](../web/tests/design.spec.ts)。ログアウト試験はAzure configのローカルfixtureを使い、認証先は通信をabortする既存試験であり、実Azure認証ではない。

プロフィールの初回は3/4成功、再公開後の「有効版」待機が既存5秒のassertionを超えて失敗した。条件・待機時間・製品コードを緩めず、APIを新しく起動して同じ4件を再実行し成功した。初回ログも保存している。待機揺らぎの原因確定は今回行っていない。

全solutionの `dotnet restore --locked-mode` は、既存AppHostのLinux用 `Aspire.Dashboard.Sdk.linux-x64` がlockにないためNU1004となった。API／テストプロジェクトのlocked restoreとbuild/testは成功した。lockや依存バージョンは変更していない。Playwright標準ブラウザー取得は環境の403で不可だったが、導入済み `/usr/bin/chromium` を使用した。既定ブラウザーを変えず、任意の実行パスを指定できる環境変数をconfigに追加した。

```sh
# 開発サーバーを停止し、5173/5180を空けてから実行
cd /workspace/dse-growth-loop/web
export PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium
for suite in search conversations documents profiles knowledge chat-assistance logout design; do
  npm run test:e2e -- "tests/$suite.spec.ts" --workers=1 || exit 1
done
```

色の計算とChromiumの操作検証は、支援技術全般の適合認証ではない。実スクリーンリーダー、Safari／Firefox、実モバイル端末、Azure／Entra／実AIでの検証は未実施。

## レビューしてほしい点

- 検索を入口にし、すぐ課題投稿へ移れる導線で、社員の利用場面に合うか。
- 「課題」「相談候補」「参考事例」「自分の経験」の表現が理解しやすいか。
- 余白・文字の大きさ・落ち着いた色調を、本番向けの基準として進めてよいか。

次の改善候補は、検索結果から人物IDを手入力せず相談に引き継ぐ操作、相談一覧の状態表示、資料処理の内部IDの折りたたみ。今回は既存業務・状態遷移への影響を広げず、レビュー可能な第一案までとした。

## Sites用の引継ぎ（後続承認）

ユーザー承認により、初案ブランチをGitHub経由で正式Sites環境へ引き継ぐ。Sites登録・公開は別の正式対応環境で行う。このrepoへSitesのproject IDや認証情報は入れない。

画面そのものを作り直さず確認できる[静的ビューアー](design-preview/viewer.html)を用意した。PC／スマホ、検索前／検索結果を切り替えられる。画面内のフォーム操作は画像のため動かず、保存・送信しないことを明示した。1440／390px×4切替の8ケースで画像読込・横はみ出し・JavaScriptエラー・外部通信を確認した（[検証結果](design-preview/viewer-qa.json)）。

公開用checkoutには次の7ファイルだけをコピーする。`viewer.html` を `dist/index.html` に、それ以外を `dist/` 直下に置く。相対参照を保持し、Sitesの設定は別checkoutで正式手順により作成する。

- `docs/design-preview/viewer.html`
- `docs/design-preview/viewer.css`
- `docs/design-preview/viewer.js`
- `docs/design-preview/home-1440.png`
- `docs/design-preview/home-390.png`
- `docs/design-preview/results-1440.png`
- `docs/design-preview/results-390.png`

`validation/`、`viewer-qa.json`、評価データ、repo全体、`.env`、認証設定は公開物に含めない。画像は全て今回のローカルDemo／Mockの架空人物・架空事例。この静的ビューアーは元アプリの動作検証や本番稼働を意味しない。
