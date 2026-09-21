# 初期実装の状況

更新日：2026-09-21。設計版0.2の実装着手分。Azure展開と有料AI呼出しは未実施。

## 実装した範囲

| 領域 | 現在の状態 |
|---|---|
| 実行単位 | .NET 10 API/Worker、Domain/Infrastructure、Aspire AppHost |
| UI | React/TypeScript/Vite。手動条件入力、人物・事例、経路と根拠表示、障害と空結果の区別 |
| 一次検索 | Probability/Noul/正規化語のOR検索、同じ分類要素の照合、テナント・公開範囲・状態・参加可否・分類体系版の除外 |
| 候補統合 | 人物・事例単位の重複排除、根拠保持、安定ID順、救済枠、上限前後の件数 |
| データ接続 | Cosmosパラメーター化クエリ、Managed Identity、ダミーデータの非上書き投入 |
| 認証 | Entra JWT、許可ユーザー、委任スコープ。Development＋Demoのみ匿名 |
| 設定 | 環境変数／`.env`、Bicepパラメーター変換。秘密ファイルをgit/Dockerから除外 |
| 配置・監視 | Bicep、Dockerfile、段階的展開、health endpoint、Azure Monitor接続 |
| CI | .NET・UIビルド、単体/API/E2Eテスト、Bicepコンパイル。クラウド展開なし |

固定ダミーは架空社員3名＋架空事例1件。分類値・Noul値はfixtureであり、Jev実測値ではない。画面でも固定データと再順位付け未実施を明示する。検索投影には全カテゴリ値を保持するが、AI実行の正本・生応答・Confidence保存処理はまだない。

条件は手動指定。`retrieval-v1` の分類閾値0.2、Noul閾値0.5、候補上限20は仮値。候補枠の約20%（最低1件）をProbability以外で一致する候補へ予約する。最適値・品質達成を意味しない。Cosmos取得が5,000単位または1,000RUを超えたときは途中結果を正常な候補集合として返さずエラーにする。

## 次工程

1. Open XML取込、Blob原本、文書版・根拠、同一パーティション内のジョブ登録とQueue通知。容量・展開上限、再送、通知漏れ回復、poison、冪等性。
2. Foundry構造化とJev公式API。全分布・Confidence・Noul未評価・モデル版・入力ハッシュの正本保存、本人訂正と再取込競合。
3. 質問のJev分類、Foundry再順位付け、候補／根拠ID検証、正本の有効版と公開撤回の再照合。
4. 依頼・参加承諾、SignalRチャット、事例自動投稿、公開承認、解決評価。
5. 事業仮説生成と日次Container Apps Job。

今回のAPIは読み取り専用で、訂正・撤回・追加を公開していない。Entra実テナント・部署別ACL・実AI・実Cosmosの統合試験は今後実施する。構成図は目標構成であり稼働済み構成図ではない。

## 検証記録

今回ローカルで実施した結果。検索品質のRecall評価やAzure実環境試験と区別する。

- .NET単体/API統合テスト：16件成功。副分類、配列相関、Noul未評価、認可、安定順、救済枠、入力、レート制限、JWT。
- .NET行カバレッジ：全体85.11%。Domain 100%、API 96.96%、Infrastructure 60%。実Azure通信分岐は未検証。
- React型検査・Vite本番ビルド：成功。
- Playwright Chromium：検索・副分類・空結果/障害の3シナリオ成功。
- 設定・展開スクリプト：5件成功。環境変数優先、コマンド非実行、不正形式検出、what-ifとcreateの分離、認証未設定の拒否。
- Bicep 0.47.16：サブスクリプションテンプレートのコンパイル成功、エラー・警告なし。
- API/WorkerのDockerイメージビルド：成功。APIコンテナから配信した本番ビルド済みUIで検索操作も成功。
- Aspire起動、UIのAPIプロキシ、readinessの疎通確認：成功。
- 画面を1360px・390px幅で確認。JavaScriptエラーとモバイル横はみ出しなし。

再現コマンドは[README](../README.md)と[展開手順](deployment.md)。カバレッジは `tests/Dse.Tests/TestResults/` に出力しgit対象外。
