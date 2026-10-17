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

## PRレビュー後の追加確認

PR #43のCopilot指摘2件に対応し、無効化した協力者の保存済み別名を履歴用に保持する。残る参加者は課題・ルーム・会話履歴を開けるが、無効化本人の読取、表示名開示、AI共有は許可しない。再有効化しても旧epochの参加を現在の候補から除外せず、新しい候補取得→招待→承諾を要求する。承諾前にはroomIdを返さない。

この2経路を匿名HTTP APIで追加検証した。設定だけのID置換やプロフィール所有者不一致に対する既存の拒否は維持している。部署変更と認可変更の監査hashは保存用に正規化してから計算し、新ETagが付いた保存内容との一致を2ケースで確認した。追加後の.NET Release全件試験は628件成功（通常回帰627件とLargeEvaluation fixture 1件、部署正本関連31件を含む）。実AI呼出しではない。対応するsummaryには初回検証と追加確認を区別して記録する。

`workspace.spec.ts`・`workspace-anonymity.spec.ts`・`identity-disclosure.spec.ts` の画面試験21件も修正後にisolated実行し、全件成功した。前節の初回28件とは重複するため、合算した件数として扱わない。

### 第2回レビュー対応

初期化Jobへ所有者の作成時epochを課題と同じbatchで保存し、Session未作成の中断後に失効・復元を挟んでも現在epochを付け直さない。pinのない旧Jobも拒否する。再有効化した依頼者の旧活動はmap更新前に一覧から除外し、新しい活動の取得を妨げない。19件ある招待の同一人置換では上限を適用せず、新しい人の追加は引き続き拒否する。

追加の5ケース（初期化中断後の再有効化・世代更新・pin欠落、匿名HTTP一覧、招待上限）が成功した。第2回修正後の.NET Release全633件（通常回帰632件とLargeEvaluation fixture 1件、部署正本関連36件を含む）と、生成された最新fixtureによる契約20件が成功した。初期化Jobの保存契約・例もepoch pinを必須にした。

第2回修正後の `workspace.spec.ts`・`workspace-anonymity.spec.ts` のisolated E2E19件も成功した。過去の画面試験とは合算しない。

### 第3回レビュー対応

招待一覧でも失効した所有活動をmap更新前に除外する。再有効化後の匿名HTTP試験を、画面初期読込のcapabilities・課題・ルーム・招待・公開知識の全APIへ広げ、新しい活動が取得できることと旧活動404を同時に確認した。実際の画面で権限を変更する管理フックは追加していない。

部署の新規追加時に読取側と同じ100件上限を確認し、確認・確定の両方で101件目を保存前に422で拒否する。上限到達後も既存部署の更新と管理読取が成功し、拒否時はrevision・監査・receipt・部署行を変更しないことを2ケースで確認した。第3回修正後の.NET Release全635件（通常回帰634件とLargeEvaluation fixture 1件、部署正本関連38件を含む）が成功した。

第3回修正後の契約20件と `workspace.spec.ts` のisolated E2E16件も成功した。過去の実行件数と合算しない。

### 第4回レビュー対応

終了済み部署の `validTo` をJSON表記ではなくDateTimeOffsetとして比較する。同じUTC日時を `Z`・`+00:00` で送る改称・後継ID更新は成功し、終了日時変更と再開は引き続き409になることを2ケースで確認した。第4回修正後の.NET Release全637件（通常回帰636件とLargeEvaluation fixture 1件、部署正本関連40件を含む）が成功した。変更は管理入力の日時比較であり、契約・画面の検証記録は前回実行と区別する。

### 第5回レビュー対応

設定が渡されるAuthorityProviderでもData:Modeの既定値をAzureに揃えた。modeキーを持たない設定と明示Azureの両方で、正本と一致したoperatorが認識されること、設定からoperatorだけを削除した場合やscope/principal/OID/有効状態が異なる場合にPerson・Claims・既存Actorの利用を拒否することを10ケースで確認した。空の設定を使っていたダミー回帰fixtureはDemoを明示し、拒否条件を弱めていない。第5回修正後の.NET Release全647件（通常回帰646件とLargeEvaluation fixture 1件、部署正本関連50件を含む）が成功した。

レビューには同意失効・復元・永続フェンスを横断する変更への人手の安全性確認を求める所見もあった。この記録はローカルダミー検証とコード上の拒否条件の確認であり、人手の安全性承認・実環境の権限変更・実復旧受入の完了を示さない。実環境受入はIssue15に残す。

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
