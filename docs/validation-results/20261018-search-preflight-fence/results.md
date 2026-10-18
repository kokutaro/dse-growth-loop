# 自然文検索preflight・既知の競合拒否の回帰検証

2026-10-18、Mac mini上。基点 `1242fe1`。Refs #16。

## 再現と変更

所有フェンス内から別取得を試すと、拒否されたコールバックは未実行なのに、外側も503を不確定障害と扱って所有ロックを残留させた。直接境界・明示的入れ子境界の2ケースを修正前に再現した（[失敗ログ](fence-before-fix.log)）。既知の拒否を両境界で共通判定し、未知の依存先障害やキャンセル（捕捉された入れ子障害を含む）のfail closedを回帰確認した。実環境で先行したロック残留の初因がこの経路だったとは断定しない。

自然文の質問定義とJevの出力境界を共有し、設定だけのpreflightを追加した。1,000は不足、現在の境界2,160は受理する。モデル契約・実行設定・形式不備をまとめて報告し、設定値や例外本文は返さない。Live検索の不備はdecisions/fence/AiRun作成前に専用422となる。UIは運用者の設定確認を案内し、前回条件を再送しない。

## 検証

- [.NET回帰777件](dotnet-tests.log)：`dotnet test tests/Dse.Tests --no-restore --filter 'Category!=LargeEvaluation' --collect:'XPlat Code Coverage'`。失敗0。
- [検索Playwright15件](search-playwright-summary.txt)：ローカルAPI・Mock・MacのChrome。追加した設定不足422も成功。
- [Worker build](worker-build.log)と `npm --prefix web run build` 成功。
- [CLI 3ケース](cli-checks.json)：無効Cosmos接続先を持つ正常候補は終了0、複数不備と形式不備は終了1。storage/credentials/transportを構築する通常経路に入らない。
- [集計・カバレッジ・対象ソースSHA256](summary.json)。新preflight・共通質問定義は行100%、AccessFenceのRunAsyncは行96.4%。

## 実環境との境界

この作業ではAzure変更・デプロイ・実AI送信を行っていない。CLIは使用量・残額・予約件数を参照せず、追加承認ゲートや都度の予算質問を作らない。既存実行器の制限を維持する。`externalStateChecked=false`であり、readyは実配置、権限/RBAC、接続先、永続先の可用性、応答品質や実環境受入の合格を意味しない。Issue #16全体および実環境の先行障害初因は未完了。
