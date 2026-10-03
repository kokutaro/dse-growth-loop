# DSE Growth Loop

社内の課題を、経験のある人と過去の知見につなぎ、解決の過程を次の課題解決と事業仮説へ還元する仕組み。

## 設計資料

- [アーキテクチャ文書](docs/architecture.md)：コンセプト、仕組み、構成、データ、処理、検証計画。
- [正本永続化・認可・復旧手順](docs/persistence.md)：Issue #5のAPI、RBAC、失効フェンス、検証境界。
- [AIアダプター・使用量台帳](docs/ai-adapters.md)：Issue #8の型付きポート、固定モデル、予算予約、承認後のライブ確認手順。
- [実装契約 v1](docs/specs/README.md)：正本・状態遷移・API・権限・分類体系、JSON Schema/OpenAPIと検証例。後続Issue共通の入口。
- [技術選定の調査記録](docs/technology-research-2026-09-21.md)：2026年9月21日に確認した一次情報と選定理由。
- [検索検証計画](docs/retrieval-validation-plan.md)：Probability・補助Noulによる一次検索とLLM再順位付けを段階別に検証。
- [チャット事例自動支援](docs/chat-assistance.md)：ON/OFF、出典・評価、既定値、Queue復旧、fake/live検証境界。
- [構成図](docs/diagrams/architecture.html)：ブラウザで開ける単独HTML。図中の文章は日本語。ビューアの固定操作UIとHTMLの言語指定は英語。
- [構成図の編集元](docs/diagrams/architecture.json)。

設計版0.2に沿って初期実装を開始。React UI、.NET 10 API/Worker、Aspire AppHost、Bicepを追加した。現在動くのは手動条件による一次検索と根拠表示、および認可付きの本人訂正・正本照合API。docx/xlsxのアップロード・機械抽出・出典プレビューと永続ジョブ回復にも対応。[取込手順](docs/document-ingestion.md)を参照。原本からの構造化・全分布分類・検索投影、本人訂正・再取込差分・公開撤回にも対応。[プロフィール手順](docs/profile-pipeline.md)を参照。課題・参加承諾・永続チャット・再接続にも対応。[会話手順](docs/conversations.md)を参照。チャットの事例自動投稿にも対応し、別承認でダミーライブ検証を実施。[結果](docs/validation-results/20261003-issue-13-live/results.md)を参照。[実装状況](docs/implementation-status.md)に実装済み・未実装・未検証を区別する。

## ローカル起動

.NET SDK 10.0.400、Node.js 24.13.0以降、Python 3を使用する。ダミーデータでの起動にAzure接続やAPIキーは不要。

```sh
dotnet restore --locked-mode
npm --prefix web ci
dotnet run --project src/Dse.AppHost
```

UIは `http://localhost:5173`。AppHostはAPIとViteを開発用Demoモードで起動する。DockerやCosmosエミュレーターは不要。

個別起動や接続先設定には `.env` を使える。既存の環境変数が優先される。

```sh
cp .env.example .env
python3 scripts/run-env.py -- dotnet run --project src/Dse.Api --no-launch-profile
# 別ターミナル
npm --prefix web run dev
```

## Azure配置

RGは **`dse-growth-loop-rg`** 固定。[Azure展開手順](docs/deployment.md)を参照。APIはHTTP起動の最小0／最大1、WorkerはQueue消費を明示opt-inし、手動seed／フェンス回復／通知回復を提供する。ビルドはローカル既定で、有料操作には承認予算と明示フラグが必要。[費用ガードとAI上限契約](docs/development-cost-controls.md)を参照。実際のAzure展開・有料API呼出しはまだ実施していない。

[Azure接続検証](docs/azure-validation.md)では、読み取り専用の事前点検とレプリカ記録、許可ユーザー1名の配置ガード、件数承認付きのJev Choice/Noul・Foundry構造化/再順位付けスモーク、復旧・image復帰・停止の手順を提供する。承認後にAzureの主要接続、3推論試行、API/Workerの0→1→0、通知再送、検証後停止を実測した。[実環境結果](docs/validation-results/20261001-issue-9-live/results.md)を参照。認証拒否ケース・MIの残りの範囲・プローブ原因確定・請求照合が残るためIssue全体は未完了。

## 検証

```sh
dotnet build --no-restore
dotnet test tests/Dse.Tests --collect:"XPlat Code Coverage"
npm --prefix web run build
cd web
npx playwright install chromium
for suite in conversations documents search; do
  npm run test:e2e -- "tests/$suite.spec.ts" --workers=1 || exit 1
done
cd ..
python3 scripts/deploy.py validate
python3 -m unittest discover -s tests/scripts
python3 -m venv /tmp/dse-contracts-venv
/tmp/dse-contracts-venv/bin/pip install -r tests/contracts/requirements.txt
/tmp/dse-contracts-venv/bin/python -m unittest discover -s tests/contracts
git diff --check
```

E2EはCIと同じくテスト群ごとにAPIを再起動し、ダミー主体のレート枠とデータを分離する。一括実行では共有の読み取り上限に達して429になる場合がある。本番のレート制限は緩和しない。

Bicep検証にはAzure CLIとBicep CLIが必要。E2E時は5173/5180ポートを空ける。CIも同じビルド・テストを実行し、Azure展開は行わない。

## 今回の前提

数名へのコンセプト説明・デモを目的とする。入力はすべてダミーデータ。Azure上に専用UIを構築し、Microsoft FoundryとJevを利用する案。実データを使う限定パイロットとは区別する。

## 採用する検証構成

原本をBlobへ保管し、機械抽出した文章をJevで分類する。Choiceの最上位だけでなく`probabilities`を保持し、Noulによる経験有無判定も補助的に加える。Cosmos DB for NoSQL Serverlessの属性索引で広く候補を検索し、Foundry上のLLMが職歴・事例の根拠を読んで再順位付けする。

AI Search、PostgreSQL、埋め込み・ベクトル検索は初期構成から外す。`confidence`は分類分布全体の補助情報であり、各分類の`probability`とは区別する。非同期通知はAzure Queue Storageを用いる提案とし、DBの常時巡回を避ける。

本資料に記載したモデル・価格条件・APIの提供状況は調査時点の情報。実装開始時に利用リージョン、契約、クォータ、バージョンを再確認する。

## オフライン検索評価

[ダミー正解集合と評価器](docs/offline-evaluation.md)で、有料呼出しなしの検索回帰と指標計算を実行できます。正解の人手確認・既存試験の受領・実AI品質の測定は未実施です。

## Issue #11の実装経路

自然文の条件生成・編集、人物/事例別の一次取得、根拠引用による再順位付けと失敗時の表示を`/api/v2`で追加した。既存の手動`/api/search`を維持し、v1の自然文検索は計画契約として区別する。同期APIなので202/pollは使わない。版付き設定・候補上限・根拠予算・検索履歴と再現の範囲は[自然文検索手順](docs/natural-search.md)を参照。fixture試験はライブRecall/順位品質の合否ではなく、ライブ評価はIssue #14で行う。

解決評価・根拠付き要約レビュー・公開知識の検索と撤回は[知識公開手順](docs/knowledge-publication.md)を参照してください。ローカルダミーでは架空人物01が参加publisherです。実運用の公開責任者・尺度・分析方針は暫定のままです。

検索比較の実行・承認条件は[Issue #14の比較手順](docs/retrieval-experiments.md)を参照。[実JEV分類と語一致の比較結果](docs/validation-results/20261003-issue-14-jev/results.md)と、ラベル承認後の[追加20問による再評価](docs/validation-results/20261003-issue-14-jev-v2/results.md)を追加した。[Luna6の実再順位付け](docs/validation-results/20261003-issue-14-luna6/results.md)では最終HitRate@5が19/20、正解なしの誤推薦0/4。[評価グラフ](docs/validation-results/20261003-issue-14-luna6/figures.md)で品質・順位安定性・費用を確認できる。合成fixture・実AI応答の測定・未測定の検索全体SLOを区別する。

実Cosmosの投影RU・少量利用の費用推計は[検証結果](docs/validation-results/20261003-issue-14-cosmos/results.md)を参照。追加AI呼出しなしで既存候補との一致を確認した。
