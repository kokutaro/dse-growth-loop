# 初期実装の状況

更新日：2026-09-23。設計版0.2の実装着手分＋Issue #2の費用ガード。Azure展開と有料AI呼出しは未実施。

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
| 配置・監視 | Bicep、Dockerfile、HTTPゼロスケール、手動seed、ローカルbuild／明示push、有料操作の承認フラグ、Budget通知・ログ保持設定 |
| CI | .NET・UIビルド、単体/API/E2Eテスト、Bicepコンパイル。クラウド展開なし |

固定ダミーは架空社員3名＋架空事例1件。分類値・Noul値はfixtureであり、Jev実測値ではない。画面でも固定データと再順位付け未実施を明示する。検索投影には全カテゴリ値を保持するが、AI実行の正本・生応答・Confidence保存処理はまだない。

条件は手動指定。`retrieval-v1` の分類閾値0.2、Noul閾値0.5、候補上限20は仮値。候補枠の約20%（最低1件）をProbability以外で一致する候補へ予約する。最適値・品質達成を意味しない。Cosmos取得が5,000単位または1,000RUを超えたときは途中結果を正常な候補集合として返さずエラーにする。

## 次工程

1. Open XML取込、Blob原本、文書版・根拠、同一パーティション内のジョブ登録とQueue通知。容量・展開上限、再送、通知漏れ回復、poison、冪等性。
2. Foundry構造化とJev公式API。全分布・Confidence・Noul未評価・モデル版・入力ハッシュの正本保存、本人訂正と再取込競合。
3. 質問のJev分類、Foundry再順位付け、候補／根拠ID検証、正本の有効版と公開撤回の再照合。
4. 依頼・参加承諾、SignalRチャット、事例自動投稿、公開承認、解決評価。
5. 事業仮説生成と通知漏れ回復の手動実行。日次スケジュールは配置しない。

今回のAPIは読み取り専用で、訂正・撤回・追加を公開していない。Entra実テナント・部署別ACL・実AI・実Cosmosの統合試験は今後実施する。構成図は目標構成であり稼働済み構成図ではない。

## 検証記録

以下は初期実装時（2026-09-21）の記録。Issue #2で今回再実行した結果とは区別する。検索品質のRecall評価やAzure実環境試験ではない。

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

## Issue #2：2026-09-23の変更と検証

- APIはSingle revision、HTTPトリガー、最小0／最大1、cooldown 300秒。定期プローブではCosmosを照会しない。
- 待機WorkerはAzure／AppHostとも初期配置しない。現在のWorkerは手動seed専用で、引数なし起動は失敗して終了する。Queue＋MIの0台起動構成は条件付き定義として用意し、取込ハンドラー実装まで無効。
- buildはローカル既定。push／ACR Tasks／展開／seedには正の承認予算と `--confirm-cost` が必要。
- RG Budget通知、ログレベル・収集cap・保持・サンプリング設定、Blob／ACRの手動保持方針を追加。月1万円税別は申請案で未承認。
- 後続AIのUTC日次トークン／回数／推定費用上限、停止、単価・為替版の設定契約を追加。実行時の台帳・上限制御は #8。
- 設定・展開・コンパイル済みARMのテスト：21件成功。Python行カバレッジ全体91%、追加の `cost_config.py` は100%、`deploy.py` は98%。外部コマンドの試験はモック、Bicepコンパイルは実行。
- .NET単体/API/サンプリング設定テスト：22件成功、行カバレッジ86.09%。ソリューションビルド成功（警告なし）。
- UIの型検査・Vite本番ビルド、Playwright Chromiumの3シナリオ成功。ローカル通信制限による初回起動タイムアウト後、通信を許可した環境で再実行した。
- Bicepコンパイル成功（警告なし）。WorkerのDemo seed正常終了、引数なし／未対応引数の拒否を実プロセスで確認。今回のDockerビルドは未実行で、build/push経路の選択と失敗時停止はモック試験。

[ADR・設定契約・Azure確認手順](development-cost-controls.md)を参照。Azure展開、ACR Tasks／push、有料AI、クラウド0→1→0、Budget通知の到達、費用削減の実測は未実施。既存WorkerはIncremental展開で自動停止されないので、後続の接続検証で確認する。構成図は引き続き目標構成を示し、このIssueでは図の配置・描画を変更していない。
