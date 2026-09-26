# DSE Growth Loop

社内の課題を、経験のある人と過去の知見につなぎ、解決の過程を次の課題解決と事業仮説へ還元する仕組み。

## 設計資料

- [アーキテクチャ文書](docs/architecture.md)：コンセプト、仕組み、構成、データ、処理、検証計画。
- [正本永続化・認可・復旧手順](docs/persistence.md)：Issue #5のAPI、RBAC、失効フェンス、検証境界。
- [実装契約 v1](docs/specs/README.md)：正本・状態遷移・API・権限・分類体系、JSON Schema/OpenAPIと検証例。後続Issue共通の入口。
- [技術選定の調査記録](docs/technology-research-2026-09-21.md)：2026年9月21日に確認した一次情報と選定理由。
- [検索検証計画](docs/retrieval-validation-plan.md)：Probability・補助Noulによる一次検索とLLM再順位付けを段階別に検証。
- [構成図](docs/diagrams/architecture.html)：ブラウザで開ける単独HTML。図中の文章は日本語。ビューアの固定操作UIとHTMLの言語指定は英語。
- [構成図の編集元](docs/diagrams/architecture.json)。

設計版0.2に沿って初期実装を開始。React UI、.NET 10 API/Worker、Aspire AppHost、Bicepを追加した。現在動くのは手動条件による一次検索と根拠表示、および認可付きの本人訂正・正本照合API。実AI、文書取込、チャット等は次工程。[実装状況](docs/implementation-status.md)に実装済み・未実装・未検証を区別する。

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

RGは **`dse-growth-loop-rg`** 固定。[Azure展開手順](docs/deployment.md)を参照。APIはHTTP起動の最小0／最大1、Workerは手動seed／フェンス回復のみ。ビルドはローカル既定で、有料操作には承認予算と明示フラグが必要。[費用ガードとAI上限契約](docs/development-cost-controls.md)を参照。実際のAzure展開・有料API呼出しはまだ実施していない。

## 検証

```sh
dotnet build --no-restore
dotnet test tests/Dse.Tests --collect:"XPlat Code Coverage"
npm --prefix web run build
cd web
npx playwright install chromium
npm run test:e2e
cd ..
python3 scripts/deploy.py validate
python3 -m unittest discover -s tests/scripts
python3 -m venv /tmp/dse-contracts-venv
/tmp/dse-contracts-venv/bin/pip install -r tests/contracts/requirements.txt
/tmp/dse-contracts-venv/bin/python -m unittest discover -s tests/contracts
git diff --check
```

Bicep検証にはAzure CLIとBicep CLIが必要。E2E時は5173/5180ポートを空ける。CIも同じビルド・テストを実行し、Azure展開は行わない。

## 今回の前提

数名へのコンセプト説明・デモを目的とする。入力はすべてダミーデータ。Azure上に専用UIを構築し、Microsoft FoundryとJevを利用する案。実データを使う限定パイロットとは区別する。

## 採用する検証構成

原本をBlobへ保管し、機械抽出した文章をJevで分類する。Choiceの最上位だけでなく`probabilities`を保持し、Noulによる経験有無判定も補助的に加える。Cosmos DB for NoSQL Serverlessの属性索引で広く候補を検索し、Foundry上のLLMが職歴・事例の根拠を読んで再順位付けする。

AI Search、PostgreSQL、埋め込み・ベクトル検索は初期構成から外す。`confidence`は分類分布全体の補助情報であり、各分類の`probability`とは区別する。非同期通知はAzure Queue Storageを用いる提案とし、DBの常時巡回を避ける。

本資料に記載したモデル・価格条件・APIの提供状況は調査時点の情報。実装開始時に利用リージョン、契約、クォータ、バージョンを再確認する。

## オフライン検索評価

[ダミー正解集合と評価器](docs/offline-evaluation.md)で、有料呼出しなしの検索回帰と指標計算を実行できます。正解の人手確認・既存試験の受領・実AI品質の測定は未実施です。
