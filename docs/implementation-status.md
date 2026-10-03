# 初期実装の状況

更新日：2026-10-03。設計版0.2の実装着手分＋Issue #2〜#8の基盤・契約・評価・Office取込・会話・AIアダプター、およびIssue #10のプロフィール処理、#11の自然文検索、#12の解決評価・公開知識、#13のチャット事例自動支援。Issue #9で承認済みの固定ダミーAzure展開と3推論試行を実施した。[実環境結果](validation-results/20261001-issue-9-live/results.md)の未実施・失敗・費用照合待ちは残る。以下の各Issue当時の検証記録は履歴として区別する。

## 実装した範囲

| 領域 | 現在の状態 |
|---|---|
| 実行単位 | .NET 10 API/Worker、Domain/Infrastructure、Aspire AppHost |
| UI | React/TypeScript/Vite。自然文・条件編集、人物・事例、適合理由・不足情報・出典・処理段階、未確認/失敗状態 |
| 一次検索 | Probability/Noul/正規化語のOR検索、同じ分類要素の照合、テナント・公開範囲・状態・参加可否・分類体系版の除外 |
| 候補統合 | 人物・事例単位の重複排除、根拠保持、安定ID順、救済枠、上限前後の件数 |
| データ接続 | Cosmosパラメーター化クエリ、Managed Identity、ダミーデータの非上書き投入 |
| 認証 | Entra JWT、許可ユーザー、委任スコープ。Development＋Demoのみ匿名 |
| 設定 | 環境変数／`.env`、Bicepパラメーター変換。秘密ファイルをgit/Dockerから除外 |
| 配置・監視 | Bicep、Dockerfile、HTTPゼロスケール、手動seed、ローカルbuild／明示push、有料操作の承認フラグ、Budget通知・ログ保持設定 |
| CI | .NET・UIビルド、単体/API/E2Eテスト、Bicepコンパイル。クラウド展開なし |
| 実装契約 | [正本・API・権限・状態遷移・分類体系](specs/README.md)のJSON Schema/OpenAPI、正常/不正例、契約テスト。正本保存・認可基盤はIssue #5、Office取込は#6、課題・承諾・会話は#7。AI支援等は後続 |

固定ダミーは架空社員3名＋架空事例1件。分類値・Noul値はfixtureであり、Jev実測値ではない。画面では固定データ・未確認状態を区別し、手動一次検索またはAI未実施時に再順位付け未実施を明示する。検索投影には全カテゴリ値を保持する。#8でAiRun追跡、全分布/Confidence/Noulの型付き応答と予算制御を実装。分類正本への保存・プロフィールへの業務結合は#10で追加した。生応答の保存は無効。

従来の`/api/search`では条件は手動指定。`retrieval-v1` の分類閾値0.2、Noul閾値0.5、候補上限20は仮値。候補枠の約20%（最低1件）をProbability以外で一致する候補へ予約する。最適値・品質達成を意味しない。Cosmos取得が5,000単位または1,000RUを超えたときは途中結果を正常な候補集合として返さずエラーにする。

## Issue #4 のオフライン評価基盤

[実行・指標・正解確認手順](offline-evaluation.md)を追加。調整/確認各12名＋3事例と、大集合用の各150名の難しい非該当例、Office原本・抽出期待値・保存応答を版管理する。既存Retrieverを呼ぶCLIがRecall/HitRate/Precision/nDCG/Noul救済/打切り損失をJSON・CSVへ出す。AI送信機能はない。人の正解確認と既存試験受領は待機中、最終確認ゲートは未確定。固定fixtureの回帰試験であり、実AI品質達成ではない。

## 次工程

後続Issueは[実装契約 v1](specs/README.md)を共有する。公開承認者・保持期限・評価尺度・承認予算は[決定ログ](specs/decisions.md)に未承認として残しており、この仕様PRによって運用承認されたとは扱わない。

1. 初期Azure接続の残りの拒否ケース、プローブ原因確定、請求照合（#9）。Blob/Queue/Cosmos/RBACの主要経路、3推論試行、Worker 0→1→0は実測済み。
2. 自然文検索のライブRecall/順位品質/待ち時間/費用を測る（#14）。#11のローカル実装・fake AI検証は[自然文検索手順](natural-search.md)を参照。
3. [AI事例自動支援](chat-assistance.md)の一般化・運用範囲の確認。#13の固定ダミーライブ評価と、ローカルfake AI/Mock試験を区別する。
4. 事業仮説生成と通知漏れ回復の手動実行。日次スケジュールは配置しない。

本人訂正・プロフィール公開撤回と正本読取APIを公開した。知識公開・撤回APIは#12で追加した。Entra実テナント・部署別ACL・実AI・実Cosmosの統合試験は今後実施する。構成図は目標構成であり稼働済み構成図ではない。

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

[Office文書取込と回復手順](document-ingestion.md)を追加。docx/xlsxの型・展開制限、段落/表/シート/セル抽出、非公開Blob、原本hash/処理版重複検出、永続Job＋Queue通知、ETagリース/不可視期限更新、有限再試行/poison、reconcile、明示一時Blob回収を実装した。UIでアップロード・進捗・失敗・再実行・出典・原本取得を提供する。当時はAI構造化未実施を表示していた。#10で後続ジョブと状態表示を追加した。Aspireではファイル保存のローカル正本/Blob/Queueを使う。

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

## Issue #10 プロフィール処理（2026-09-30）

[プロフィール構造化・分類・本人訂正](profile-pipeline.md)を追加。Office原本→機械抽出→根拠付きJSON検証→人物/断片別の全分布・Confidence・Noul保存→検索投影を接続した。部署実績は別正本。日付からの期間計算、本人訂正優先と再取込差分、公開撤回、最新取込系列・ETagの照合、全投影の原子的公開を実装。管理画面は処理段階・根拠・版・再実行とfixture/実AIを表示する。

入力上限と保留・復旧条件は上記手順を参照。原本から完全一致する抽出引用のみを検証済みとし、自由な言い換えの意味一致は認めない。実Foundry/Jevの品質・費用・待ち時間、Azure実接続は未実施。

今回のローカル検証（2026-09-30、外部推論0回）：

- locked-mode restore、Release全体ビルド成功。既存のAspire CLI bundle無効化警告 `ASPIRE010` が1件、コンパイルエラーなし。
- .NET単体/API：186件成功。最終レビューの再実行履歴・部署断片・日付修正後は関連69件も再実行して成功。
- 追加したProfilePipelineの行カバレッジ98.41%（186/189）、ProfileExtractionは96.30%（26/27）。実Azure通信や実AI品質を含まない。
- JSON Schema/OpenAPI・実生成レコードの契約テスト13件成功。契約不変条件の行カバレッジ100%。
- React型検査・Vite build成功。Playwrightはプロフィール3、文書4、検索3、会話4の計14件成功。
- 設定・Bicep関連テスト26件、評価fixtureテスト4件、ローカルBicep検証、`git diff --check` 成功。Azure展開・有料呼出しは実施していない。

## Issue #9 承認前の実行準備（2026-10-01）

[Azure接続検証手順](azure-validation.md)と、配置／秘密取得／推論をしない`azure_validation.py`を追加。明示subscriptionでRG／Provider／ARM permissions／モデル一覧／地域quotaを読み、全active revisionのレプリカ数をアプリへのHTTP要求なしで記録する。日時・commit・コマンド・合否を保存し、ライブscenarioはnot_run、欠測値はnullとする。失敗時のstdout／stderrを記録せず、既存runを上書きしない。apps／Bicepの許可ユーザーを1名に制限した。

AIスモークは既存basicの2呼出しを維持し、connectionsの3件承認でChoice/Noul 1リクエスト＋Foundry構造化1＋再順位付け1を確認できる。再順位付けは固定ダミーの最新認可・正本・根拠を照合し、欠落／変更／撤回時は全推論前に拒否する。自動seedやJob retryを許可しない運用手順を定義した。再配置、旧image復帰、全revision／Job停止、消費停止、請求後日照合の手順も追加した。

今回の[検証記録とTodo照合](validation-results/20261001-issue-9-preparation/results.md)：.NET196件、設定／Bicep38件、契約13件、評価fixture4件が成功。locked restore／Releaseビルド／React型検査・Vite build／Bicep構文・差分／相対リンク確認が成功。AiSmoke行カバレッジ100%、記録ツール90%、契約不変条件100%。既存ASPIRE010警告1件。ローカルPlaywrightは今回未実行。

現在のCLI選択subscriptionを読み取った結果、対象RG不存在と必要Provider6種の未登録を確認した。要求モデル／SKUの確定・quota照合、対象環境・予算・実行者・操作範囲の承認が残る。Provider登録、Azure配置、Entra変更、秘密投入、外部推論は0回。Entra実トークン、MIのデータ権限、0→1→0、実障害回復、実請求は未検証。Issue #9はopenを維持し、手順作成と未承認を未完了として記録する項目以外をチェックしない。ローカル結果でG1完了を代替しない。

## Issue #9 承認後の実環境検証（2026-10-01）

[実環境結果](validation-results/20261001-issue-9-live/results.md)に対象・承認・image digest・実コマンド・MI21件・提供元usage付き3推論試行・API/Worker縮退・通知再送・再起動後の正本／会話再読取・停止・費用照合待ちを記録した。Foundryのproject/model同時更新競合を直列化し、地域quotaの残量ガード、MI専用接続診断、秘密を除くAiRun測定、ログアウトと初回認証要求の共有を追加した。

ローカル.NET215件、設定/Bicep41件、契約13件、評価fixture4件、認証4件、ログアウトPlaywright1件、ビルド成功。別テナント／許可外ユーザー等の実拒否、MIの未測定範囲、プローブ失敗原因、実請求の差分は未完了。全体受入／実AI検索品質を完了とせず、#9はopenを維持する。

## Issue #11：自然文検索・再順位付け

`/api/v2`に条件生成・同期検索・検索履歴を追加。複数Probability/Noul/語のOR、人物/事例別枠、ルール版、認可・有効版の送信前/表示前確認、切詰めとRU/AI追跡、根拠引用に限定した理由、失敗時の一次候補表示を実装した。従来`/api/search`とv1計画契約は維持する。[設定・API・制約](natural-search.md)を参照。

検証はローカルのfake AI/fixtureであり、ライブ品質・Recall・実RU/費用の実測は今回未実施。2026-10-02の検証：.NET 239件（CIと同じ`Category!=LargeEvaluation`）、Playwright 18件（検索6・取込4・プロフィール3・会話4・ログアウト1）、契約14件、スクリプト41件、評価fixture4件、認証単体4件が成功。ロック復元、ソリューション/UIビルド、Bicepローカルコンパイル、文書リンク/JSON/差分検査が成功。既存のASPIRE010警告1件あり。初回のsandbox IPC/ローカルCLI設定制限は、許可されたローカル実行で再検証した。大集合オフライン評価・クラウド展開・有料AI・ライブ品質は今回実施していない。

## Issue #12：2026-10-02の解決評価・公開知識

[解決評価・六項目要約・版指定承認・撤回](knowledge-publication.md)をAPI/UIに追加した。Outcomeと評価者ごとのFeedback（1〜5/null、尺度版、任意コメント）、私的要約正本、版/scope付き公開・分析同意、publisher承認、公開Jobをconversationsに保存する。既存のJev分類とSearchUnitで承認要約だけをcatalogへ投影し、新規質問の事例検索へ再利用する。公開撤回・編集・参加者変更は正本照合で検索/AI/引用から遮断する。旧会話の退会者も確認対象から落とさず、DB間の確定前/後障害から冪等に回復する。

[今回のTodo照合・検証記録](validation-results/20261002-issue-12-local/results.md)：.NET256件、Playwright20件、契約16件、設定/Bicep41件、評価fixture4件、認証4件が成功。全体行カバレッジ89.76%、KnowledgeService98.19%、API95.83%。ソリューション/UIビルド、JSON・相対リンク・差分検査成功。既存ASPIRE010とVite bundleサイズ警告あり。実AI応答は固定HTTP/Mock検証で、ライブ品質・クラウド展開・有料呼出しは未実行。公開責任者・尺度・分析方針の運用承認は[決定ログ](specs/decisions.md)に暫定として残す。

## Issue #13：チャット事例自動支援

人間のMessageCommittedから既存Queue/Workerと自然文検索へ接続。全参加者の権限をAI入力前と保存前に検証し、共有可能な事例だけを通常Messageに保存する。デバウンス・クールダウン・事例/論点/イベント重複抑止、Session/tenant日次上限、有限回復、ON/OFF、出典、役立った/不要を追加。[契約・設定・検証境界](chat-assistance.md)を参照。今回の新たな有料実行承認で固定ダミーのライブ検証を追加した。過去の実AI結果で代替していない。

Issue #13の今回の検証は[2026-10-03のローカル結果](validation-results/20261003-issue-13-local/results.md)を参照。.NET 279件、追加Chat 22件、Playwright 21件、契約17件を確認。続いて[ライブ検証](validation-results/20261003-issue-13-live/results.md)を実行し、不要20例で誤投稿0、語gate通過14例のJev判定成功、有用controlの引用付き投稿を確認。v1の有用性見送りを記録して判定文をv2に明確化し、閾値0.7を維持した。追加後の.NET295件、契約17件、支援Playwright1件を再確認。

実Queue計測はWorker 0→1を含むcold 37.047秒、正常warm 14.476秒（目標15秒以内、各1標本）。次のwarmはFoundryレート制限で投稿を見送り、成功時間に含めない。全28外部試行、推定AI費用1.2812708円（失敗1件の上限計上を含み、実請求未照合）。終了時の停止状態はライブ検証記録を参照。

## Issue #14：検索比較基盤と承認済みfixture確認（2026-10-03）

ユーザーが正解ラベル、一次Recall90%、ウォームp95 15秒、冷起動の別報告、予算8,000円を承認し、不足は未達としてPRへ残す方針を確認した。[比較手順](retrieval-experiments.md)と[今回の結果・Todo照合](validation-results/20261003-issue-14-local/results.md)を追加。A〜F、閾値/質問Top/Noul/N探索、上限前後、同一候補hash・根拠付き保存応答比較、K=3/5、順位変動・失敗例・合否JSON/CSV、最終ゲートへの実験入力hashを実装した。検索監査はCosmosページ別/正本read別RUを保存する。

ゲートコミット後に小集合・大集合それぞれ2,164行を確認。E（閾値0.2/Top-3/Noul0.5/N=20）の大集合人物Recallは40%で未達、閾値0.3の診断条件は100%。合成集合だけで採用せず本番設定を維持する。全.NET304件、契約17件、設定/Bicep41件、評価fixture5件が成功し、全体行カバレッジ90.18%。ビルドは既存ASPIRE010警告のみ。

この基盤段階では実AI・実Cosmos・cold/warm・費用・20問の品質判定・採用設定は未完了。外部有料実行は0回、ライブ測定値はnull。ラベル承認やfixture内の100%を実サービス品質と扱わない。Issue #14はopenを維持する。

### 実JEV分類による一次検索比較の追加

ユーザー承認の$5クレジットから最大$1・350回を予約し、モデル/送信計画/ゲートを送信前に固定。jev-1.13.0で330件を分類し、応答検証で停止した4件を各1回の手動診断で回復した。合計334試行、成功入力186,664トークン、成功usageの推計$0.007839888、失敗予約を含む保守値$0.018591888（実請求未照合）。

同じ原本語抽出のLと実JEV分布のA〜Fを比較し、確認集合の人物RecallはL=80%、D/E=100%、事例はいずれも100%。一方、調整集合は人物L/E=100%、D=80%。Noulは確認集合で正解1名と非該当3名を追加救済し、正解なし質問でも候補を返す。独立した正解あり20問・実LLM再順位付け・実Cosmos・検索cold/warm・月額採用は未完了。本番設定は未変更。[再現と証跡](validation-results/20261003-issue-14-jev/results.md)。

### 追加20問の実JEV評価

ユーザーが追加48質問（各分割正解あり20＋正解なし4）と原本120資料のラベルを承認。送信前に選定手順を固定し、調整のEで90%を満たす条件からN・候補数優先で選定した。確認前に閾値0.3／Top-1／Noul0.3／N=20をhash固定し、確認で人物Recall95%、事例90%、一次HitRate19/20。現行語一致Lはこの言い換えの多い集合で0%。質問側の分野選択とNoul条件で3正解候補を落とし、正解なし4問中3問で候補を返した。

168件完了・失敗した診断を含む170試行、推計$0.00406224、失敗予約を含む$0.00943824。前回と合わせた保守費用は$0.028030128、実請求未照合。全.NET309件、Python/Bicep47件、契約17件、評価fixture5件成功。独立した作業課題20問を用意したが、合成集合の実務代表性・統計的独立性は未検証で、最終HitRate@5・実LLM・実Cosmos・検索SLOは残る。[結果・再現・Todo](validation-results/20261003-issue-14-jev-v2/results.md)。
