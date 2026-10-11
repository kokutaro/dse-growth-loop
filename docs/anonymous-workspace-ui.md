# 課題と匿名協力を中心にした社員ワークスペース

> この文書の以下の実装表・未実装 API・Azure 停止方針は **2026-10-08 の履歴**です。2026-10-11 に匿名サーバー API と UI 接続を追加しました。現在の実装境界・制限・検証区分は [anonymous-workspace-contract.md](anonymous-workspace-contract.md) と [identity-disclosure.md](identity-disclosure.md) を参照してください。社員 UI は `/api/v1/workspace/*` の allowlist 応答を使い、旧 API を本番 operator/development に分離する能力宣言を確認します。Azure 実環境検証や完全な自由文匿名化の合格を意味しません。

2026-10-08 のローカル実装。基点は main `78f182a30f106a8d34f7989d39d23f622e5d5c08`。専用 checkout と `codex/anonymous-workspace-ui` ブランチで作業し、元リポジトリと過去の検証成果を変更していない。GitHub push、PR、マージ、Azure デプロイは実施していない。

## UI と実装境界

デザイン基準は既存の [社員向けプレビュー](https://dse-growthloop-ui-preview.nyanpass.chatgpt.site)。Mac に渡された修正版ソースと ZIP（SHA-256 `8d0ec2934c97f6ec786c7e7a6157d590359fab1aa4e07057d02958ecce40ed25`）を確認し、淡い背景、白いカード、青い操作、サイドメニューとスマホの文字付き5メニューを React/Vite へ移植した。静的プレビューのダミー状態機械を本番実装としてコピーしていない。

| 画面・機能 | 今回の状態 | 保存・接続先 |
| --- | --- | --- |
| ホーム、自分の課題・相談、参加依頼、活動ルーム、事例・ナレッジ、マイページ | React のルートとブラウザー履歴を実装 | ローカル Demo のみ匿名ワークスペースを開く |
| 課題投稿 | 実装・API 接続 | 既存 `POST /api/v1/challenges`、冪等性キーを保持 |
| 下書きと未保存入力 | 実装 | ページ内状態。メニュー／戻る／進むで保持、再読込で消える。投稿済み編集履歴は活動へ誘導 |
| 参加依頼への参加・辞退 | 実装・API 接続 | 本人の Participation を `PATCH`。参加で即時に会話権限を得る。依頼者の追加承認なし |
| 会話保存・再送・履歴照合 | 実装・API 接続 | ETag、clientMessageId、Idempotency-Key を使う。タブの sessionStorage に主体・ルーム別の未確認送信を保持。履歴照合と10秒の再取得 |
| 活動完了 | 実装・API 接続 | 所有者が確認後に close。完了後の送信は無効 |
| 事例公開・編集後の同意解除 | 既存 Knowledge コンポーネントへ接続 | 版と公開先ごとの双方同意、別の分析同意。編集で同意・承認・検索公開が解除される |
| 参考事例 | 既存検索 API へ接続 | 人物一覧は表示しない。詳細を開く際に検索 API の SourceGuard で再照合。原会話や個人プロフィールのリンクなし |
| 本人プロフィール・文書取込 | 既存機能をマイページに配置 | Demo の主体を API ヘッダーに反映。取込待ち情報を主体別に分離 |
| 実名開示 | 未実装 | 双方同意を保存・照合する API がないためボタン無効 |
| 課題からの匿名候補提案・実通知 | 未接続 | 架空の協力者への依頼はローカル検証操作だけ。AI 検索・通知送信を新 UI から始めない |
| 未読数・活動進捗・協力範囲／通知設定 | 新 API への接続未実装 | プレビューの固定数値・架空設定を実データとして表示しない |

トップページは課題と協力のワークスペース。以前の検索・プロフィール・取込・会話検証画面は `/validation.html` に保持し、既存 E2E もその画面を検証する。既存の人物検索は検証画面に残るが、社員向けホームの中心には置かない。

## 本番の匿名性に足りない契約

現在の `ConversationEndpoints` は内部レコードを返す。Session/Participation/Message に `ownerId`、`personId`、`participantIds`、`createdBy` 等が含まれ、自然文検索は人物名と根拠を返す。自由入力の課題・メッセージ・原文根拠にも識別情報が入り得る。フロントで「投稿者A」「協力者B」「あなた」「相手」に置き換えても、ネットワーク応答から識別できるため本番の匿名性を保証しない。

そのため新ワークスペースは `/api/config` が Azure のとき、相談・依頼・会話・事例の API を呼ばず準備中画面を表示する。マイページは既存の本人向け認証機能の入口として残す。この UI の停止はサーバー API 自体の停止・匿名化ではない。`validation.html` と既存 API は従来の開発検証契約のままであり、実社員向けの匿名機能として展開しない。

以下は必要なサーバー実装の候補であり、このタスクで実装・承認した API ではない。

| 必要な契約 | サーバー側で必要な処理 |
| --- | --- |
| ワークスペースの安全な DTO と能力情報 | Challenge・Invitation・Room・Message の項目を明示的に許可。principal/person/tenant/所属/連絡先/プロフィールリンク/原文参照を返さない。匿名契約の版と対応能力をサーバーが宣言 |
| ルーム単位の匿名名 | owner/participant を内部で認可し、ルームごとに分離した匿名名と不透明な操作 ID を返す。他の課題との追跡可能な共通 ID を渡さない。参加前の本文取得・退会後の取得はサーバーで拒否 |
| 課題起点の候補提案と依頼 | 課題 ID から適合根拠と匿名候補を返し、匿名候補 token で依頼。実 personId 入力や人物ディレクトリーを社員 UI に要求しない。可用性・公開撤回・範囲・同時変更を再検証 |
| 参加・辞退の本人決定 | 既存 `DecideAsync` の即時 accepted を維持。ETag と冪等性、同時受諾・辞退・退会の一貫性。受諾を実名開示同意へ転用しない |
| 実名開示の相互同意 | `GET /rooms/{id}/identity-consent`、版付き `POST` 等の別契約。双方の現在有効な同意が揃うまで識別情報を一切返さない。共有する項目・取消・参加変更・監査・競合を定義 |
| 公開用要約と同意 | 既存 Knowledge の版＋scope＋全参加者同意・公開承認を維持し、公開 DTO から識別情報・原会話・原文参照・内部 ID を除く。要約編集で同意と検索公開を失効。実名開示・分析同意とは別管理 |
| 通知・ログ・配送 | 通知は「新しい参加依頼が届いています」のみ。実名、所属、課題本文を通知や URL、Hub ヒントへ載せない。ログ・エラー・テレメトリー・AI 入出力にも別の識別情報保護が必要 |
| 匿名性の受入試験 | 実 JWT の複数主体で JSON/DOM/ネットワーク/通知/URL/ログを検査。非参加者・退会後・別 tenant/scope・同意変更の並行処理を検証。サーバーの DTO テストを UI 表示テストで代用しない |

公開用要約の匿名化・同意失効は既存の `KnowledgeService` と `SourceGuard` を土台にできるが、API レコードの全面公開をそのまま社員向け DTO にしない。必要な同意 API とサーバー試験が揃ってから、新 UI の Azure 停止を解除する。

## ローカル起動と検証

.NET SDK 10.0.400、Node.js 24.13.0 以降を使う。リポジトリルートから以下を実行する。

```sh
dotnet restore --locked-mode
npm --prefix web ci
dotnet run --project src/Dse.AppHost
```

`http://127.0.0.1:5173/` が新 UI、`http://127.0.0.1:5173/validation.html` が既存検証画面。AppHost は Demo API と Mock AI を設定する。Azure 起動・API キー・有料 AI は不要。Ctrl+C でローカル AppHost と子リソースを停止する。下書きはブラウザー再読込で消え、会話の未確認送信はそのタブ内に残る。Demo API の正本はプロセス内であり、API 再起動後の本番永続化をこの画面で保証しない。

```sh
npm --prefix web run build
npm --prefix web run test:auth
npm --prefix web run test:e2e
dotnet test tests/Dse.Tests
python3 -m unittest discover -s tests/scripts
git diff --check
```

E2E の標準コマンドはテストを列挙して **各テストごとに API/Vite を再起動する**。同一の架空人物と共有アップロード／書込上限を使う既存テストの影響を分離し、API のレート制限は変更していない。複数ブラウザー・同時操作は個々のテスト内で引き続き行う。通常の Playwright 一括実行は `test:e2e:raw`。結果は `web/test-results/isolated-summary.json` とテスト別の出力フォルダーに残す。ポート5173/5180を使用するため、手動 AppHost を停止してから実行する。

専用 lint 設定・スクリプトはこのリポジトリに存在しない。型チェック、JS 構文、ビルド、差分空白チェックを実施し、lint 成功とは表現しない。詳細な最終結果と画面証跡は [2026-10-08 検証記録](validation-results/20261008-workspace-ui/results.md) を参照。
