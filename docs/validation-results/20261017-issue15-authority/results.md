# Issue15：手動ダミー部署正本・履歴・同意失効のローカル検証

2026-10-17。基点main `ca75ded43bd0a93a73ff58059633bc74ed21e8d4`。[仕様と再現手順](../../organization-authority.md)。2026-10-17の実装承認・クラウドへの引継ぎ後に実行した。過去の証跡を今回の実行結果へ流用していない。

## 検証対象

架空の部署・社員・同意・活動だけを使った。通常異動は参加を維持し、公開・分析・表示名を再確認する。退職や認可のA→B→Aで旧epochの参加が復活しないこと、元参加者を除外せず再同意すること、古い確認版・receipt・検索候補を再利用しないことを検証する。発生時点の主部署を期間から解決し、架空の自己申告IDで部署数を増やせないことを確認する。

復元世代の変更では全員のepochと同意tokenを変え、一般利用者を無効のままにする。途中失敗はmaintenanceとフェンスを閉じたままにし、通常seedや再起動で開放しない。正本とControlの同一batch失敗・応答喪失、最終表示中の変更、親の終了時に残るネスト処理の保持もfixtureで検証する。

## 実行結果

確定件数と対応する入力hashは [summary.json](summary.json)、[source-hashes.json](source-hashes.json) に記録する。

- .NET Release：全体回帰（`Category!=LargeEvaluation`）と部署正本専用試験を実行。変更前のmainは595件成功。現在の件数はsummary参照。
- Python：スクリプト241件、契約20件、評価fixture5件成功。契約ルールの行カバレッジ100%。追加したorganization/knowledge/workspace OpenAPIの構造検査も成功。
- UI：認証7件、型検査・本番ビルド成功。知識公開・分析・表示名・匿名性12件とworkspace16件のisolated E2Eが成功。
- 停止下CLI：新規の隔離したローカルpathでbootstrap、inspect、dry-run、確定、同じキーの再送、generation resetを実プロセスで確認。復元後の一般利用者は無効のまま。[CLI結果](cli-summary.json)。
- .NET solutionのRelease build、Linux x64のlocked restore、Bicep 0.47.16のローカルコンパイル、評価器fixture smoke、`git diff --check` を実行。AppHostの既存 `AspireUseCliBundle=false` によるASPIRE010は残る。

## クラウド環境での再現条件

SDK 10.0.400、Python 3.12.14、Node 24.19.0、npm 11.9.0を使用した。SDK/cache/CLI設定・展開先はworkspaceまたはtmpであり、ホームディレクトリーの書換えは要求しない。

```sh
export PATH=/workspace/.dotnet:/workspace/.dse-tools/bin:$PATH
export DOTNET_CLI_HOME=/tmp/dse-dotnet-home
export NUGET_PACKAGES=/workspace/.nuget/packages
export DOTNET_BUNDLE_EXTRACT_BASE_DIR=/workspace/.dse-bundle
export AZURE_CONFIG_DIR=/workspace/.dse-azure

dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test tests/Dse.Tests --filter 'Category!=LargeEvaluation' --no-build -c Release
python -m unittest discover -s tests/scripts
python -m coverage run --source=tests/contracts -m unittest discover -s tests/contracts
npm --prefix web run test:auth
npm --prefix web run build
```

クラウドcontainerのPID1が孤児を回収しない場合、PythonのOSプロセス試験は `tini -s -- python -m unittest discover -s tests/scripts` で実行する。テスト条件やsupervisorの停止ロジックを弱める修正は行っていない。

固定Playwright Chromium 153のCDNと公式storage配布先はネットワーク403だったため、ローカルE2Eは `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium` を指定し、既設Chromium **154.0.8037.57** を使用した。CIではこの変数を設定せず、従来のPlaywright固定ブラウザーを使う。最初のブラウザー起動失敗をアプリの合格には数えない。

Linux用Aspire lockfileの共通依存は既存Mac用と同一。異なるのはDashboard/DCPのOS別2パッケージだけで、既存Mac用lockfileを変更していない。Windows/Macの実行を今回成功したとは扱わない。

## 未完了の境界

実HR／Entra／Cosmos、実権限変更、複数実replicaの競合、実AI・実Job・実環境復旧は実施していない。新しいAzure設定変更・デプロイ・課金呼出しは0。初回検索遅延の追加測定・最適化、価格・費用台帳を作業再開条件にする確認も行っていない。

同じDBだけで未申告の全体巻戻りを自動検出する保証はなく、停止下の世代更新・権限再確認を省略して本番復元を許可しない。Issue15の少量実AI／実Job・復旧受入、Issue16の全体受入は残るため、今回の部分実装でIssue15をCloseしない。
