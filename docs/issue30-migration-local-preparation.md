# Issue #30：VNet移行後のローカル検証準備（2026-10-11）

## 範囲と現状

PR #38 マージ後の `main`（`e61f7a3ccb2dd22edfb2cd0e8e10bce65640df93`）から専用 worktree / branch を作成した。既存 checkout・未コミット変更・検証成果を保持し、今回はローカル実装と Fake/Mock 回帰、ネットワークを持たないfake CLIと実OS子processによる故障試験を実行する。Azure 起動・配置・実AI送信・権限/認証/ネットワーク変更・GitHub push/PR は行わない。

Issue #30 は retrieval-v2 の Recall と分類漏れを扱う。保存された独立v3サービス48検索の合格と、残る認証付き warm24/cold3 の時間・画面・停止計測は別々に扱う。正常な分類/サービス応答を再送せず、旧評価を新しい独立品質評価へ転用しない。[既存の受入棚卸し](remaining-issues-acceptance-20261011.md) を参照。

移行の引継ぎは `/Users/nyanpass/Documents/Codex/2026-10-11/task-2/migration-handoff.md`。そこにある12:43–12:46 UTCの読戻しでは旧API27・旧Worker4・新API1・新Worker2の計34 revision が inactive / Stopped、実replica一覧は空。初回ローカル準備ではAzure状態を取得せず、その後17:23:59–17:25:07 UTCにARMメタデータ53チェックを別成果物へ取得し、全34revision inactive/Stopped・実replica0を確認した。9 Jobは旧環境のManual、返却された71実行記録にRunning0だが9件のendTimeが欠落しており、累計runtimeの再構成ではない。今回のsupervisor追加試験ではAzureへアクセスしていない。どのsnapshotも取得後の状態保証ではない。

| 対象 | 移行後の固定値／注意 |
| --- | --- |
| Subscription / RG | `e17a163e-1f3f-4a5d-8709-b8697066a8e5` / `dse-growth-loop-rg` |
| Environment | `dse-environment-vnet-jpe`、internal、publicNetworkAccess Disabled |
| API | `dse-api-vnet`、min0/max1。新URLは引継ぎを参照 |
| Worker | `dse-worker-vnet`、min1/max1だが全revision停止。起動すると共有Queueを処理する |
| 旧Manual Jobs | 9 Jobは旧 `dse-environment` 所属。新環境の検証に流用しない |
| 配置コード | `infra` / `deploy.py` は旧環境参照を含む。今回配置しない |
| データ／操作 | ダミーのみ、開発者1名、APIの全revision合計で同時最大1 replica |
| AI | 今回はMock・Stopped=true・LiveOptIn=false・Warmup=false。実AI送信0 |

移行時に固定したAPI/Worker image digestを、PR #38 マージ後ソースの配置証拠とみなさない。実測前に source SHA・実image digest・設定/dataset/gold hash を結び付ける。WorkerはAPIの `Ingestion__RunWorker=false` では止まらない。

## ローカル実装

- `scripts/azure_validation.py`：旧/新AppとEnvironmentの明示組を検証。App実参照先が違えばrevision読取前に止まる。`--all` の全revisionと各revisionの実replica一覧を取得後、`all-revisions-final` で全revisionを再読する。初回/最終の名前・active・replicas・statusが一致し、active=false・runningState=Stopped・metadata replica=0・実一覧0の全条件が揃う場合だけ `stopSnapshotConfirmed=true`。最終読戻しのない過去snapshot、取得中の追加/削除/状態変化、欠落・重複・未知値は停止未確認。新Environmentのpreflightは旧Environmentの存在からquota再利用を認めない。
- `scripts/validation_safety/`：移行時API用post watchdogとWorker用queued watchdogの原本バイトは `provenance.json` の `sourceText` に変更せず保存し、元のSHAを検証する。原本には空の再取得を停止成功にする既知の問題があるため、公開 `.py` 入口はAzure呼出し0の実行拒否stubとし、運用は新supervisorのみを使う。出典付き原本はMock回帰の参照データであり、実行・復元する経路ではない。
- `scripts/issue30_timing_report.py`：ネットワークを使わない保存記録の採点器。既存warm24の品質/双方の線形p95採点と、別のcold3の完全性を集約。coldは固定3問の順序、単一試行、各回別の停止snapshot、時刻順、事前API要求0、HTTP/UI時間、失敗標本保持を検証する。coldに合否時間閾値を作らない。固定3問の既存@5品質基準（Precision/Recall各90%以上）と基本応答契約を確認する保存応答回帰であり、新規独立評価ではない。
- `scripts/issue30_safety_supervisor.py`：新API/Workerの両方を固定対象にし、親PIDをMacのkqueue（process handle）で監視。親終了・SIGTERM/SIGINT・実行期限から停止へ移る。実行窓+停止余裕の単調時計期限を固定し、停止段階の共通残量を全App/revision/CLIへ渡す。各CLI timeoutはmin(設定値,残量)、期限後の新規呼出しをしない。共通残量を未準備App数で分け、各Appの枠の2/3を対象/環境/revisionの準備に、少なくとも1/3を初回停止要求に確保する。確認できたAppから初回停止を要求し、残りrevisionもApp間で予算を分けて交互に要求した後に停止確認する。APIの準備・要求・確認でWorkerの停止要求を遅らせない。準備が期限内に完了しなければ未確認を保ち、対象未確認のまま停止を要求しない。CLI不在・権限拒否・不正JSON/shapeは停止未確認。成功/失敗いずれも専用CLI process groupを回収し、標準出力/エラーを閉じた子も残さない。有界waitを使う。
- `scripts/issue30_restore_plan.py`：private元snapshotをtarget照合後に0600/exclusiveで保存。既存file/symlinkを上書きしない。SHA・新App/Environment・Single/max1・両App停止証跡を照合するオフライン計画検証。未確認はplanValid=false、keepStoppedRequired=true、executionEnabled=false。実復元もrestorationConfirmedの成立も行わない。
- v3 warm/cold採点：bool・負数・NaN・Infinity・未取得を時間として採用せず、HTTP応答より早いUI完了時間も拒否する。候補IDには独立v3のscope prefixを必須とし、原本のraw IDを付けただけの別scope応答は受理しない。応答のrun/group順序/assessment一意性と原本・対象entityへ結び付いた根拠を共用契約で確認。coldは空本文、不正/借用根拠、HTTPエラー、UI alert、正解ありの空候補、既存@5品質基準未達を失敗として保持する。

preflightのreportもApp/Environmentの組を固定して照合する。新Environmentを点検する際は新Appを明示し、旧Appとの混在はCLI読取前に拒否する。停止CLIの成功応答が空の場合はdeactivateに限りnullとして受け取り、後続の全revision/実replica確認で判断する。読取CLIの空応答を停止証跡にはしない。

採点器が扱う値は提供された記録であり、操作者の申告だけで独立証跡が成立するとは判定しない。warmのHTTP200応答runIdも非空・一意を必須とし、異なるquestionへの応答流用を拒否する。warmの入れ子null・不正shapeや、計測記録の不正なUTC/時間値/停止snapshotは、入力内容やtracebackを出さずCLI exit2、summaryなしで拒否する。一方、正しい計測構造で記録されたcoldの実HTTP応答/画面の契約違反（HTTP200でもprimary=nullや空本文など）は観測された失敗標本として保持し、CLI exit1のsummaryで3件の時間とfailedQuestionsを残す。元の入力記録は書き換えず、どちらも再送や試行の除外を承認しない。非succeededのreranking応答に候補が残る場合もwarmでは不正応答、coldでは失敗標本とする。`liveAcceptance=pending`・請求額nullを維持する。Mockの `qualityPassed` やCLI exit0は実Entra/実Cosmos/実AI・費用・停止保証の合格ではない。

## warm24 / cold3 の具体手順（実行は別途承認後）

1. **起動前**：開発者が操作可能なMacブラウザーと本人認証、sanitized HTTP/UI収集、別プロセス/別操作経路の停止担当を準備。検索性能計測にWorker起動は不要なので、Workerのpolling停止・処理lease/in-flight 0を既存権限の独立経路で確認し、全revision inactive/Stopped・実replica0へ停止済みに固定して計測中も起動しない。Azure Manual JobのRunning0はqueue処理lease0の代用ではない。現在のアプリにはpolling停止/lease集計APIがなく、信頼できる静止確認経路と有限の待機期限・未確認時の中止手順が整うまで実測を開始しない。旧/新の全revision、Job実行0、schedule、AI停止、maxReplica、原本/同意状態、復元先、最新共有台帳と累計稼働を確認・保存する。認証準備が未完なら課金起動しない。
2. **固定**：対象source/image/settings/dataset/gold/candidate hash、warmの24原文、coldの3 question ID と順序、上限時間/費用、停止復元の余裕をレビュー可能な計画へ保存する。この資料は新しい3問を選定・ラベル承認していない。既存固定3問の確定は再開前に必要。
3. **warm**：SPA/認証を用意済みで、固定24原文をtarget=`all`、1名・逐次・各1回。期待422の2件と検索22件を区別し、失敗/取消/timeoutも母集団へ残す。全24応答と検索22件の線形p95がそれぞれ15秒以下か採点。HTTP開始/終了、UI完了、内部処理時間は別に保存する。
4. **cold**：読み込み済みSPAを保持。各回APIを停止し、全revisionの実replica0とStoppedを確認。確認後に別のcycleとして起動し、GET/health/ready等で温めず固定問を最初のAPI要求として送る。3回をwarmに混ぜず、失敗も残す。実停止が確認できない回はcold扱いにせず中断する。
5. **通常終了**：Workerは起動前の静止/停止済みを維持し、基準のlease/in-flight 0と停止状態を再照合する。運用のWorkerを通常停止する場合も先にpolling停止→有限の期限でlease/in-flight 0確認→deactivateの順が必要で、静止を確認できなければ通常のgraceful終了とは扱わない。API停止要求→全revision読戻し→各実replica一覧0、Running Job0 / schedule / AI停止 / private設定 / ingress / 同意状態も保存した基準と照合する。

**緊急中断**（親終了・signal・費用/実行期限）：独立supervisorはresource停止を優先し、Worker lease解放を待って共通期限を延長しない。処理中のcancelでattempt/leaseが残る可能性があるため、台帳・原本を保護し、再送/attemptリセットをせず独立担当が復旧要否を確認する。`stopConfirmed` は後方互換のresource停止snapshotのみで、同値の `resourceStopConfirmed` を追加する。`workerQuiescenceConfirmed=false`, `gracefulStopConfirmed=false`, `inFlightRecoveryRequired=true` を返し、resource0を業務静止・graceful終了・復元完了へ昇格しない。未確認のresource停止はkeepStoppedRequiredを維持し、別hostの停止担当が既存権限でPortal/CLIを確認する。このPRは静止APIや新権限を実装・許可していない。

`http-cold.json` は3件の配列。各件には `questionId`, `cycleId`, `attempt=1`, `request={query,target:all}`, `httpStatus`（transport失敗はnullと `TIMEOUT` / `NETWORK_ERROR` / `CANCELLED` のいずれかの文字列 `errorCode`）, 有限・非負の `networkMs`, `uiCompletionUpperBoundMs`（未完了はnull）, UTCの `startedAtUtc`/`finishedAtUtc`, `preSearchRequestCount=0`, `zeroSnapshot`, `body`, `uiObservation` が必要。`networkMs` は開始/終了UTCの差と1ms以内で一致させる。subscriptionは上表の固定GUID以外を拒否し、操作者がsnapshotと期待先を一緒に変更しても通さない。`uiObservation` はwarmと同じquestionId/runId/groups/status/alertsとUI時間のshapeで、同じcaptureへ対応させる。応答runIdはcold3件の間とwarmとの間で重複を拒否する。同じ応答を別cycle/zero snapshotへ付け替えて完了にしない。`zeroSnapshot` は新APIを対象にした `azure_validation.py replicas` の完全なreportであり、`finishedAt` と `checks` を保持する。生HAR/JWT/token/秘密envを含めない。

保存記録だけを採点する例：

```sh
python3 scripts/issue30_timing_report.py --directory /path/to/sanitized-run \
  --subscription e17a163e-1f3f-4a5d-8709-b8697066a8e5 \
  --cold-question-ids <固定問1> <固定問2> <固定問3>
```

既存 `timing-summary.json` を上書きしない。保存されたservice48をこのコマンドが再送することはない。

## 独立停止・復元の条件と制限

watchdogの既定plan-onlyは安全にローカル確認できる。実行flagは今回使用しない。親の終了、ブラウザー認証失効/準備不能、CLI/計測故障、想定外revision/replica/Job、AI停止設定のずれ、未承認モデル要求、予算/累計時間の上限到達、復元失敗のいずれでも計測を中断し、独立停止経路へ移る。自動再送や短時間の起動→ログイン待ちの再試行をしない。

移行watchdog原本は出典とSHAを付けて参照データとして保持し、実行入口は無効化する。旧900秒待機と外側75秒ループを全体hard deadlineとして採用しない。新supervisorを、承認済みrun-secondsと停止余裕stop-seconds、計測controllerの親PIDを明示し、別session・privateログ出力・stdin切離しで**起動前に**用意する。controllerはSUPERVISOR_ARMEDを確認してから計測を開始する。supervisorはrun中にCLIを呼ばず、trigger後に停止のみを実行する。既定はplan-only、今回の実行flag付き試験はfake CLIのみ。

停止期限は `min(起動時の全体期限, trigger時刻+承認停止余裕)` に固定する。signal/親終了/再確認で期限を延長しない。全CLIへ残量を渡し、revision追加/変化を最後にも再確認する。deadline/CLI故障で確認できなければexit2・stopConfirmed=false・restorationConfirmed=false。OSのprocess終了後の回収に有界50msの余裕を持ち、scheduler遅延を0秒と保証しない。これを課金hard capとは呼ばない。

Mac kqueueを使う実親子processで、親終了後のdetached supervisor継続、SIGTERM、停止中の追加SIGTERM、実行期限、fake CLIの認証失効/権限拒否/不正JSON/無応答/停止不成立、および成功CLIから標準出力/エラーを閉じて残る子の回収をローカル確認する。API4revision/Worker1revision、CLI各1秒、共通8秒のMockでもWorker停止要求を確認する。API準備の無応答でWorkerの予算を消費しないことをMockと実OS fake CLIでも確認し、期限切れを停止完了へ昇格しない。Linuxのpidfd分岐はこのMacで実OS確認していない。

**Mac sleep・電源断・supervisor自身のSIGKILL・Azure/credential不通では停止を保証できない。別hostの停止担当が必要。** 別hostの既存権限・対象・絶対期限・連絡/停止未確認時の手順を別途確認する。sleep中のmonotonic期限の進行や、resume後の費用は未検証で、実測承認へ代用しない。

元テンプレートのprivate snapshot本体と正確なrevision/設定へ復元する。旧URLへ戻すだけではprivate依存の疎通や共有Blob/Cosmosへの書込を復元できない。データ復元は隔離先・保存範囲・撤回再適用を別途定義し、今回行わない。

## 累計予算と未完了

引継ぎの**過去推計**では、移行のcompute上界 `$0.03248236` + 変動項目留保 `$0.040` = `$0.07248236` / 旧移行承認 `$0.10`。算術差 `$0.02751764` は最新実請求/残高でも、Issue #30の新しい支出承認でもない。新ネットワーク増分 `$22.40/月`、旧PE/Environmentと共有VM・Cosmos・Storage・ACR等の固定費はこの短時間枠に含まれない。

同じ歴史資料のAPI 0.5CPU/1GiB単価 `$0.054/hour` とWorker `$0.027/hour` を使うなら、API追加T分のcompute参考上界は `0.054×T/60`（起動/停止/復元と全revisionの同時稼働を含む）。Workerは停止維持し、誤起動分も留保/計上する。これは現在価格の照会・総費用保証ではない。価格/契約/通貨/FX/税、RU・保存・転送・ログ・失敗留保と実請求照合が必要。

旧Issue30計画のAPI `14340/18000秒`・Job `6249/9000秒` は過去引継ぎ値。今回移行の稼働を含む原本再構成がまだない。週をまたいでもサイクル累計はリセットしない。旧週2,000円・FX162・未精算留保1,200円と移行USD枠を混ぜて残額を作らない。

warm24/cold3の実AIが必要なら、最新台帳から分類/再順位付けの最大54試行（warm48+cold6）、旧計画にある手動予備12（今回追加/自動利用しない）、最大token、適用単価、失敗留保を再計算し、**正常保存応答の再送禁止と新規送信範囲**を明示して承認を得る。過去承認が現行価格・新環境・今回実行に自動継承されたとは扱わない。

残りは最新台帳/稼働原本・完全な総費用上界と明示承認、本人認証準備、sourceと配置imageの対応、独立停止期限の値/別host担当と実credential、private復元基準、実JWT/Cosmos/RU/AI/HTTP/UI/cold3/停止復元。実施結果ではなく、具体化した再開条件として親へ返す。

## 保存したローカル実装の確認方法

```sh
python3 scripts/issue30_safety_supervisor.py
python3 -m unittest discover -s tests/scripts -p test_issue30_supervisor.py
python3 -m unittest discover -s tests/scripts -p test_issue30_restore_plan.py
python3 -m unittest discover -s tests/scripts -p test_issue30_timing_report.py
```

復元snapshotのラッパーは schemaVersion=1、固定target、apps配列（新2Appのnameとprivate ARM template本体）。秘密値を含む場合は公開成果物へ入れない。freezeはsourceと新destinationを明示し、reviewはsnapshot SHAと両Appのsanitized停止reportを明示する。review合格でも復元実行/起動は認可しない。

計測件数はIssue30のwarm24/cold3を維持し、サービス48検索や正常分類を再送しない。追加したOS/fake試験による実モデル試行は0。既存要求の最大54試行/1,026,000tokenは旧per-call capによる参考上界であり、新しい試行枠を増やしていない。実請求・適用価格・最新台帳・移行分込み累計と総上界が揃うまで、有料実測の承認額と全体期限を確定しない。
