# 初期実装の状況

更新日：2026-09-29。設計版0.2の実装着手分＋Issue #2〜#8の基盤・契約・評価・Office取込・会話・AIアダプター。Azure展開と有料AI呼出しは未実施。

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
| 実装契約 | [正本・API・権限・状態遷移・分類体系](specs/README.md)のJSON Schema/OpenAPI、正常/不正例、契約テスト。正本保存・認可基盤はIssue #5、Office取込は#6、課題・承諾・会話は#7。AI支援等は後続 |

固定ダミーは架空社員3名＋架空事例1件。分類値・Noul値はfixtureであり、Jev実測値ではない。画面でも固定データと再順位付け未実施を明示する。検索投影には全カテゴリ値を保持する。#8でAiRun追跡、全分布/Confidence/Noulの型付き応答と予算制御を実装。分類正本への保存・業務結合は#10以降。生応答の保存は無効。

条件は手動指定。`retrieval-v1` の分類閾値0.2、Noul閾値0.5、候補上限20は仮値。候補枠の約20%（最低1件）をProbability以外で一致する候補へ予約する。最適値・品質達成を意味しない。Cosmos取得が5,000単位または1,000RUを超えたときは途中結果を正常な候補集合として返さずエラーにする。

## Issue #4 のオフライン評価基盤

[実行・指標・正解確認手順](offline-evaluation.md)を追加。調整/確認各12名＋3事例と、大集合用の各150名の難しい非該当例、Office原本・抽出期待値・保存応答を版管理する。既存Retrieverを呼ぶCLIがRecall/HitRate/Precision/nDCG/Noul救済/打切り損失をJSON・CSVへ出す。AI送信機能はない。人の正解確認と既存試験受領は待機中、最終確認ゲートは未確定。固定fixtureの回帰試験であり、実AI品質達成ではない。

## 次工程

後続Issueは[実装契約 v1](specs/README.md)を共有する。公開承認者・保持期限・評価尺度・承認予算は[決定ログ](specs/decisions.md)に未承認として残しており、この仕様PRによって運用承認されたとは扱わない。

1. 実AzureのBlob/Queue/Cosmos/RBAC・Worker 0→1→0と強制停止の結合検証（#9）。
2. #8のFoundry/Jevポートを用いた構造化・分類正本への全分布保存、本人訂正と再取込競合（#10）。
3. 質問のJev分類、Foundry再順位付けと画面統合（#11）。#8の候補／根拠ID検証、有効版・認可照合を利用する。
4. 事例自動投稿、公開承認、解決評価（依頼・参加承諾・SignalR会話は#7で実装）。
5. 事業仮説生成と通知漏れ回復の手動実行。日次スケジュールは配置しない。

本人訂正と正本読取APIを公開した。撤回・追加の業務APIは後続Issue。Entra実テナント・部署別ACL・実AI・実Cosmosの統合試験は今後実施する。構成図は目標構成であり稼働済み構成図ではない。

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
- 待機WorkerはAzure／AppHostとも初期配置しない。Issue #2時点のWorkerは手動seed専用で、引数なし起動は失敗して終了する。Queue＋MIの0台起動構成は条件付き定義として用意し、取込ハンドラー実装まで無効。
- buildはローカル既定。push／ACR Tasks／展開／seedには正の承認予算と `--confirm-cost` が必要。
- RG Budget通知、ログレベル・収集cap・保持・サンプリング設定、Blob／ACRの手動保持方針を追加。月1万円税別は申請案で未承認。
- 後続AIのUTC日次トークン／回数／推定費用上限、停止、単価・為替版の設定契約を追加。実行時の台帳・上限制御は #8。
- 設定・展開・コンパイル済みARMのテスト：21件成功。Python行カバレッジ全体91%、追加の `cost_config.py` は100%、`deploy.py` は98%。外部コマンドの試験はモック、Bicepコンパイルは実行。
- .NET単体/API/サンプリング設定テスト：22件成功、行カバレッジ86.09%。ソリューションビルド成功（警告なし）。
- UIの型検査・Vite本番ビルド、Playwright Chromiumの3シナリオ成功。ローカル通信制限による初回起動タイムアウト後、通信を許可した環境で再実行した。
- Bicepコンパイル成功（警告なし）。WorkerのDemo seed正常終了、引数なし／未対応引数の拒否を実プロセスで確認。今回のDockerビルドは未実行で、build/push経路の選択と失敗時停止はモック試験。

[ADR・設定契約・Azure確認手順](development-cost-controls.md)を参照。Azure展開、ACR Tasks／push、有料AI、クラウド0→1→0、Budget通知の到達、費用削減の実測は未実施。既存WorkerはIncremental展開で自動停止されないので、後続の接続検証で確認する。構成図は引き続き目標構成を示し、このIssueでは図の配置・描画を変更していない。

## Issue #3：2026-09-23の仕様と検証

- `docs/specs/`に20エンティティの最小スキーマ、共通メタデータ、34操作のOpenAPI 3.1、56件の型/状態例、28件の不正JSON例、62件の許可/拒否遷移例を追加。
- 本人訂正競合、公開撤回、Queue通知漏れ、AI予算停止、未保存メッセージを別型・状態で区別。公開確認と分析同意も別々に版・主体・範囲・時刻を保持。
- 契約テスト9件成功。JSON Schema、OpenAPI構造/参照/型一致、追加不変条件、遷移の主体/guard、正規化辞書、実際のDemoDataの互換性を検証。追加の仕様検証器の行カバレッジ100%。DB/JWT/ACL/AI処理の実装試験を意味しない。
- .NETテスト23件成功。ソリューションビルド成功（既存`AspireUseCliBundle=false`によるASPIRE010警告1件）。React型検査・Vite本番ビルド、Playwright Chromium 3件、既存Pythonスクリプト21件、Bicepコンパイル成功。BicepはCLIのローカル設定通知のみでコンパイラー警告なし。
- Markdownのローカルリンク、仕様/構成図JSON構文、差分の空白を検査。構成図の描画・画像は変更しておらず、視覚検証の再実行とは扱わない。
- 最初の.NET/Bicep試行はローカルIPC/CLI設定領域のsandbox制限で失敗し、許可されたローカル実行環境で再検証した。Azure展開、有料呼出し、実認可・実Cosmos・失効競合の統合検証は未実施。

## Issue #5：2026-09-26の正本保存・認可基盤

- 正本／投影repository、版付きJSON、ETagと同一パーティションbatch、業務更新＋Job＋24時間冪等記録を実装。全20kindの往復と既存検索fixture互換を検証。
- Entra tid/oidからサーバーBinding／Grantを解決。本人以外の訂正、非参加者、未公開知識、別tenant/scope、クライアント権限指定を拒否。プロフィール訂正／正本・有効根拠・Job取得と保護付きページングを公開。
- 正本有効版／公開状態を検索・AI入力境界・表示・共有確定で照合。tenantフェンスとSessionのETagを併用し、失効と配信／保存の競合を制御。クラッシュ後のreceipt／フェンス手動回復を追加。
- API／Worker別のCosmos、Blob、Queue権限を必要なコンテナ単位で定義。CosmosのRU・TraceId・処理時間を記録し、readinessを60秒キャッシュ。
- Office／Blob／Queue処理、実AI、再投影Worker、会話／公開承認APIは後続Issue。内部サービスの契約試験は実AI投稿成功を意味しない。

実装境界・API一覧・Binding設定・RBAC表・停止後の復旧は[正本永続化手順](persistence.md)を参照。実Cosmos、実Entra、実RBAC、Azure展開、有料AI呼出しは未実施で、#9の接続検証に残す。構成図は目標構成のままで描画を変更していない。

今回の検証結果：

- `dotnet restore --locked-mode` とソリューションビルド成功。既存のASPIRE010警告1件（CLI bundle設定）、エラーなし。
- .NET全75件成功（大集合評価の回帰を含む）。カバレッジを収集。メモリーadapter・HTTPテストであり実Cosmosの通信試験ではない。
- Python契約9件、評価fixture4件、設定／Bicep ARM23件成功。Bicepコンパイル成功。
- React型検査／Vite本番ビルド成功、Playwright Chromiumの既存3シナリオ成功。
- Markdownローカルリンク、構成図JSON構文、`git diff --check` 成功。図の視覚検証は再実行していない。
- 最初の.NET試行はローカルIPCのsandbox制限で失敗し、許可されたローカル実行環境で再実行。Python契約試験は不足依存のある既定Pythonから既存の契約用venvへ切り替えて成功。

## Issue #6：Office取込

[Office文書取込と回復手順](document-ingestion.md)を追加。docx/xlsxの型・展開制限、段落/表/シート/セル抽出、非公開Blob、原本hash/処理版重複検出、永続Job＋Queue通知、ETagリース/不可視期限更新、有限再試行/poison、reconcile、明示一時Blob回収を実装した。UIでアップロード・進捗・失敗・再実行・出典・原本取得を提供する。AI構造化は未実施を明示する。Aspireではファイル保存のローカル正本/Blob/Queueを使う。

本人資料・サーバー指定scopeという既存認可を維持。形式/上限はIssue #6のdocx/xlsx・10 MiB/50 MiB・1万エントリ/120秒に統一した。実Azure・実Queue・RBACと有料呼出しは未実施。旧節の検証件数は当時の記録。

2026-09-27のIssue #6ローカル検証（今回実行）：

- .NET全108件成功、カバレッジ収集。API認証/認可、正常docx/xlsx、ZIP bomb/暗号化/偽装/展開上限、通知漏れ、同時消費、20秒周期リース更新、途中停止後の再開、poison再実行、旧版破棄、一時Blob回収を含む。
- Python契約10件（実取込出力もSchema照合）、評価fixture4件、設定/Bicep24件成功。契約追加検証ロジックのカバレッジ100%。Bicepコンパイル成功。
- React型検査/Viteビルド成功。Playwright Chromium 7件成功。正常docx/xlsxは実API/ローカルQueueによるE2E、失敗状態の表示・再実行1件は状態API fixtureとして区別。出典表示のスクリーンショットを目視確認。
- ソリューションビルド成功（既存ASPIRE010警告のみ）。Markdownリンク、仕様/構成図JSON構文、差分空白の検査成功。構成図の描画は変更しておらず視覚再検証の対象外。
- 実Queue/Cosmos/RBAC、Azureでの0→1→0・障害試験は未実施で #9 に残す。AI品質評価・性能上限の達成を意味しない。

## Issue #7：課題・参加承諾・永続チャット（2026-09-27〜28）

課題作成/一覧/詳細、依頼/承諾/辞退/退会、チャット、AI設定保存、保存/未保存/再送/接続状態を実装。[操作・API・障害回復](conversations.md)を参照。Session・Participation・MessageとMessageCommitted Jobをconversationsに保存し、単一APIレプリカのSignalR通知をDB連番取得で補完する。ローカル切替はDevelopment＋Demoだけ。本番は既存JWT/主体対応と現在の参加者を検査する。

今回実行：.NET全体124件成功（接続制限の分離・冪等キー境界・現在scopeの再検査を含む）。Playwright Chromium 10件（3 worker）、契約11件、設定/Bicep24件、評価fixture4件が成功。契約検証器カバレッジ100%。locked-mode restore・全体ビルド・React型検査/Vite本番ビルド・リンク/差分検査も成功（既存のAspire ASPIRE010警告あり）。画面のスクリーンショットを確認。再接続試験で検出した通知失敗による履歴取得停止と、接続維持による業務APIレート消費を修正して再実行した。旧試験記録の流用ではない。

実CosmosのTransactional Batch/RBAC、Entraトークン更新/失効、Container Apps再配置/0→1→0のライブ検証は未実施。AI投稿・messageCommitted Jobの消費は#13。実環境の作成、有料API呼出し、既存データ削除は実行していない。

## Issue #8：型付きAI・予算予約（2026-09-28〜29）

4つの型付きポート、公式Jev/Foundry HTTPアダプターとMock、Key Vault固定版取得/MI認証、Choice全分布・Confidence・Noulの検証、拒否/打切り/異常JSON/ID捏造の失敗型を追加した。AiRunは入力ハッシュ・正本版・モデル/プロンプト/分類体系版・使用量・試行回数・時間・推定費用を保存する。本文・秘密・生応答は通常ログへ出さない。

UTC日次の回数/トークン/推定円と実行枠を送信前に同一パーティションのETag付きbatchで予約する。再起動や競合、再試行で予約を回避せず、不確定課金は全予約を保持。停止・実送信opt-in、版付きキャッシュと再認可、承認情報必須の別スモークコマンド、停止後の実行枠回復を追加した。[設定・台帳・推定誤差・接続手順](ai-adapters.md)を参照。

今回の最終検証結果：

- .NET全174件成功（AI追加50件）。公式応答fixture/fake HTTP、Key Vault/MIのfake、予算各上限、同時予約、ファイル永続台帳の再生成、日跨ぎ、キャンセル、停止/承認、旧版/認可、キャッシュ撤回を含む。カバレッジを収集。実Cosmos/実APIの試験ではない。
- 契約12件、設定/Bicep26件、評価fixture4件成功。実際に保存したAiRunもJSON Schemaに照合した。
- locked-mode restore、ソリューションビルド、React型検査/Vite本番ビルド、編集Markdownのローカルリンク、差分空白検査が成功。既存ASPIRE010警告1件。
- 初回のPlaywright一括実行は9/10件成功。履歴/参加者取得のHTTP 429はmain `07578954cd69d5fddac17174d86d9761c17fd71f` の未変更スナップショットでも再現した。CI再検証では、履歴取得中の操作後更新を捨てる別の競合も確認した。
- CI修正では更新要求を集約して再取得し、待機中の操作も完了を待つよう変更。応答を保留して競合を再現する回帰テストは修正前に失敗、修正後に成功した。CIは会話・文書・検索ごとにAPIとレート枠を分離し、失敗時のPlaywright traceを保存する。本番のレート制限は変更していない。共有APIでの一括実行は引き続き429になり得るため、再現手順はREADMEの分離実行を使う。
- 2026-09-29の修正後ローカル検証：上記の分離実行で会話4件・文書4件・検索3件の計11件が成功。React型検査/Vite本番ビルド、差分空白検査も成功。

実行モデルの契約ID/版/リージョン/SKU、Key Vault/RBAC、ダミー各1件のライブ接続、実請求との照合は#9の承認後。今回の外部推論は0回。分類正本への保存と新規文書取込/検索画面/自動投稿への結合は#10・#11・#13。生応答Blob保存は無効のまま。
