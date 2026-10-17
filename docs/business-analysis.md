# 許可済み課題の手動分析（ローカル版）

## 2026-10-17：発生部署の正本と同意の再確認

部署と履歴、参加epoch、公開・分析・表示名の同意を[手動ダミー部署正本](organization-authority.md)へ接続した。旧tokenの同意は再有効化・値の復元だけでは復活しない。以下の2026-10-11の記録は当時の実装境界であり、現行の確認版・復元手順はリンク先を参照する。実環境受入が完了したという意味ではない。

Issue #15 の Demo/Mock と Live 向け輸送・費用台帳・管理用復旧のコードを実装した。Live は fake IAiTransport とローカル保存でコード経路を検証する。実 Foundry の呼出し、実 AI 品質の測定、実社員データ、Azure Job の配置・起動と Azure 復旧は未実施であり、Issue 全体の完了ではない。実社員用の匿名ワークスペースも引き続き閉じる。

## 利用範囲と同意

相談者のマイページ「許可済み課題の分析（ローカル検証）」から今すぐ分析する。5メニューは維持し、人材検索を中心にしない。Demo の相談者だけに analyst を追加した。本番 Auth bindings は変更していない。

分析は `member` と `analyst` が必要で、実施者の scope 内にある公開知識を使用する。正本の公開版・参加者・双方の公開同意と別の分析同意を毎回確認する。入力は承認済み要約と、その版に含まれる公開申告情報だけ。原会話、個人プロフィール、非公開工数から推測しない。

要約編集で任意の `analysisMetadata` を保存する。公開用部署コード、解決状態、申告工数、発生回数、申告評価は各項目 nullable。空欄は不明、未評価は0点にしない。部署コードは公開可能なIDで、氏名や実部署名を記入しない。正本と catalog の同版コピーが一致する時だけ分析に使う。申告情報だけの編集でも版・権限版を増やし、双方同意・承認・検索公開を取り消す。未保存の編集がある間も同意・承認を禁止する。

## 分析・結果・人の判断

同じ challenge または元 session に由来する要約・週報・改訂版を一つの独立課題にまとめる。承認済み課題本文の正規化語、または現行投影の分類 Probability>=0.3 で候補をグループ化し、Top1だけに絞らない。Mock は共通課題確認・価値仮説の固定応答であり、意味的一致・市場性の実測ではない。

件数、部署数、再発課題数、未解決率、申告工数、低評価課題数はコードで計算する。欠損がある指標は null。初期条件は独立3課題・複数部署で、設定可能なデモ用パラメーター。需要・市場規模の採用基準ではない。表示する仮説は対象・共通課題・既存対応・価値仮説・反証・次に確認すること・許可済み要約ID。根拠にないIDと空の仮説を拒否する。

候補ごとに candidate / investigating / on_hold / rejected を人が保存する。根拠、件数や分析同意が変われば、保存済み仮説・集計・除外理由をまとめて非表示にする。APIは結果の再照合から応答のシリアライズまで tenant fence を保持する。画面も再読・定期確認が失敗したら結果を閉じる。

## APIと上限

- `GET /api/v1/business-analysis/capabilities`: allowed/enabled/reason/configVersion/mode、自己専用cache区画requestOwnerKey、サーバー時計currentTime。cache keyは認可credentialではなく、非analystにはkey/timeを返さない。
- `POST /api/v1/business-analysis`: `{runId,cutoff,configVersion:"business-analysis-demo-v1"}`、Idempotency-Key=runId。
- `GET /api/v1/business-analysis/{runId}`: 実施者本人・権限・現在の正本を再確認する。
- `GET /api/v1/business-analysis/{runId}/sources/{knowledgeId}`: 実施者本人、run内の根拠、全入力の現在の同意・版を確認して公開要約を返す。原会話のID・参加者・principal・部署IDは返さず、公開DTOのdepartmentIdはnull。内部の別部署集計は維持する。UIは本文確認後に候補の判断を保存できる。
- `PATCH /api/v1/business-analysis/{runId}/decision`: `{status,groupId?}`、Idempotency-KeyとIf-Match必須。groupId省略は全候補一括、UIは候補別。

手動分析APIの実行契約は上記とコード・統合テストで確認した。[OpenAPI 3.1](specs/openapi-business-analysis-v1.json)に5経路・入力/応答DTO・権限と同意・冪等と競合・停止境界を記載し、契約検証を追加した。

UIはrunId/cutoff/configVersionを本人cache区画へ保存し、画面移動/reload後は同じrunをGET確認する。停止中も保存済みGETだけ可能で、404時にPOSTしない。400未受理ならserverのcurrentTimeと現在configVersionへ同じrunIdのまま訂正し、次の手動確認でGETを先に行う。既存runがあれば再送せず、bodyが違う既存runはserverの冪等照合で拒否し、別runIdへ自動置換しない。公開・分析同意は同意時Principalと現在Binding/member/Grant範囲/原Profile ownerを再照合する。

runId/cutoff/config/上限/正本manifestとinputHashを固定する。同じ要求は再読し、失敗したAIを自動再送しない。既定は50入力、要約合計8000 UTF-8 bytes、30秒、tenant当たり永続スロット1件、試行1回。最小課題数と部署数は `BusinessAnalysis:MinimumIndependentChallenges` / `MinimumDepartments`。入力・設定・部署情報不足、予算停止、途中失敗、処理中を区別する。Live は配置で `Data:Mode=Azure`、`Ai:Mode=Live`、`BusinessAnalysis:Enabled=true` を明示した時だけ使う（Enabledの既定false）。その上で `AiUsage:Stopped=false`、`LiveOptIn=true`、正の共有予算・FXと承認済み価格/FX版、固定のFoundry model/version/region/SKU/deployment/HTTPS endpointが必要。無効は入口501、停止/未opt-inは409、設定不備は422で、DB読み書き・輸送前に拒否する。今回実配置の値は変更せず、実輸送呼出し0。MockもAiUsage:Stopped未指定・true・不正値で停止する。

タイムアウトでプロバイダーの停止を確証できない場合は `recoveryRequired=true`、永続スロットblocked、新規runを拒否する。送信後の通信切断・応答上限超過でも終結が不明なら保守的に保持する。API/Worker停止とプロバイダー側の旧要求終結を外部の証跡で確認し、正本/同意と保存runを調査してから明示復旧が必要。slotを自動解放したり、ストアを消して再実行したりしない。

## ローカル手動Worker・Job定義

`--analyze-business` は Development・Demo・Mockと永続 `Data__LocalPath` を必要とする。`BusinessAnalysis__PersonId`、`RunId`、`Cutoff` を明示する。fixtureは `BusinessAnalysis__DemoFixtures=true` の時だけ追加し、利用者の訂正・撤回を上書きしない。検索投影のない分析専用ダミー3件を使う。AppHostはこれを有効にするが、通常のE2Eは無効、分析E2Eだけ有効。

```sh
analysis_cutoff=$(date -u +%Y-%m-%dT%H:%M:%SZ)
DOTNET_ENVIRONMENT=Development Data__Mode=Demo Ai__Mode=Mock \
Data__LocalPath="$PWD/.artifacts/manual-analysis" AiUsage__Stopped=false \
BusinessAnalysis__DemoFixtures=true BusinessAnalysis__PersonId=demo-person-01 \
BusinessAnalysis__RunId=analysis-local-example \
BusinessAnalysis__Cutoff="$analysis_cutoff" \
dotnet run --project src/Dse.Worker -- --analyze-business
```

Cutoff は実行時点以前のUTC値を指定する。再実行は同じcutoffと設定を維持する。

Bicep は `deployBusinessAnalysisJob=false` が既定。`DEPLOY_BUSINESS_ANALYSIS_JOB=false` と同期し、明示配置のみ。Manual、並列1、完了1、180秒、再試行0。定期トリガーを作らない。Azureでの `--analyze-business` は本番 identity binding の analyst とmember、Liveの明示配置・予算・固定モデル契約が必要で、repoやcredentialを解決する前に検証する。Jobの配置/起動とLive輸送の有効化は今回行っていない。

再利用する Job テンプレートに実行者・run ID・cutoff を固定保存しない。既定テンプレートをそのまま起動すると、この3値が不足して Worker が拒否する。Azureでは各回の execution override に必須値を渡す。`deploy.py analysis-plan` は、取得・レビュー済みの private ARM Job snapshot から起動用 JSON をローカルに準備する。Azure CLI・認証・`.env` の読み取りや Job 起動は行わない。

```sh
python3 scripts/deploy.py analysis-plan \
  --analysis-job-snapshot /private/path/reviewed-job.json \
  --analysis-person-id REVIEWED_BOUND_ANALYST_PERSON_ID \
  --analysis-run-id APPROVED_FIXED_RUN_ID \
  --analysis-cutoff APPROVED_ISO8601_TIMESTAMP_WITH_OFFSET \
  --analysis-output /private/path/new-execution-template.json
```

対象は `dse-business-analysis`、Manual/180秒/再試行0/並列1、analysis container の既存 command/image/resources と照合する。出力は上書きせず mode 0600 で作り、既存の環境値と secretRef、AI停止・予算・単価/FX・モデル・本人紐付けを保持して3値だけを置き換える。停止解除や実行有効化は自動で行わない。snapshot と出力に private 設定が含まれ得るため Git/通常ログへ保存しない。

別途承認した単一検証枠で、現在の Job/template の一致、本人の analyst/member binding、モデル/予算、累計稼働、独立停止と復元の準備を確認した操作者だけが、承認済み既存 Job の `/start?api-version=2025-07-01` へ出力 JSON を送信する。JSON は `containers`（必要なら `initContainers`）を持つ execution template であり、Job 全体の ARM 定義ではない。[Microsoft の起動 API 契約](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/jobs/start?view=rest-resource-manager-containerapps-2025-07-01)に従う。既定値だけの `az containerapp job start` は使わない。今回確認したのは offline の生成・入力拒否・設定保持だけで、実Azure起動・停止/復元の成功ではない。

前段の検証は [既存結果](validation-results/20261011-remaining-issues/results.md)、Liveアダプター・復旧CLIの追加ローカル検証は [追加結果](validation-results/20261011-production-contracts/results.md)。全体受入と実環境再開条件は [残Issueの棚卸し](remaining-issues-acceptance-20261011.md)。


## Live の輸送と費用境界

Live の設定版は `business-analysis-live-v1`、Demo は互換の `business-analysis-demo-v1`。モデル・価格・FX・入力/出力/予算/時間上限・prompt版をconfigurationHashに固定し、run/cutoff/業務上限と共にrequestHashへ含める。同じrunIdで設定を変えると409になり、成功・失敗・中断runとも再送しない。GETはAI停止中でも保存結果を読めるが、毎回のrole/scope/全入力正本・同意照合で失効した結果を閉じる。

Live用promptは `business-hypothesis-v1`。LLMへ送るのは承認済み公開要約のid/version/textだけ。原チャット、元session/主体ID、プロフィール、非公開情報と集計値を送らない。モデルは全入力に共通課題があるか確認し、なければ `commonProblemConfirmed=false,hypothesis=null` を返す。JSONキー・型・6項目の非空文字列/長さ、根拠IDの全入力一致を厳密に検証する。拒否・打切・モデル違い・不明ID・財務値の追加などを拒否する。財務語を含む引用は公開要約の逐語引用だけ許容する。この検査は意味的妥当性を保証しないため、反証と次の顧客確認を人が読む。件数・部署・工数・比率は引き続きコード計算である。

送信前に、要約正本・公開版・双方の分析同意・scope・membershipを再確認してtenant fenceを保持する。送信本文のUTF-8 byte数を保守的token上界として上限確認し、既存共有AiLedgerへinput/outputの全上限で予約する。AiUsageのMaxAttemptsに関わらず送信1回・retry0。輸送は既存IAiTransportを使い、固定モデル/endpoint、応答byte上限、期限、usageの非負整数・非推定を要求する。実usageが判明すれば共通台帳のJPY/tokensに精算する。usageなし/不正なら全予約額をuncertainとして保持し、上限超過は共有gateをblockedにする。日/週の承認予算・他機能の予約も同じ台帳に合算する。

新Live traceのrequestUnitsは未測定を0とせずnull/not_collected。輸送操作と原本/fence/監査/台帳の各保存操作は既存OperationMeasurementsへ接続する。ローカルfakeのRUはnullで、SDK内部再試行や全費用測定は未完了、Issue #34のcomplete/acceptance gateはfalseのまま。

AiRun監査にはjob ID、モデル/価格/FX/prompt、入力hashと公開根拠版、provider request ID、usage、試行1回/再試行0、時間、失敗コードを保存する。原文/生成仮説をAiRun監査へ重複保存しない。タイムアウトや通信切断で実要求の終結が不明な時はpending予約の額とshared gate占有を維持し、business-analysis-slotもblockedにする。CancellationTokenやWaitAsyncは遠隔停止の証明ではない。

## 証跡を伴うオフライン復旧

HTTP復旧APIは公開しない。Workerの `--inspect-business-analysis-recovery` はrun/slotのETag・状態・一致と対象runのpending予約IDを読み取るだけ。`--recover-business-analysis` は次を明示したoperator+analystの管理操作である。

- `AiUsage:Stopped=true`、`LiveOptIn=false`。全API/Workerの終了とプロバイダー側要求の終結を別手段で確認し、`Recovery:ApiStopped` / `WorkerStopped` / `ProvidersStopped=true` を指定する。
- `Recovery:OperatorPersonId`（Azureは現行binding）、`OwnerPrincipalId`、`RunId`、`RunETag`、`SlotETag`、`ApprovalReference`、`StopEvidenceReference`、64桁SHA256の`StopEvidenceSha256`、正確な`PendingReservationIds`配列。
- DemoはDevelopment・Data:LocalPathと`Recovery:LocalOperatorConfirmed=true`が必須。これはダミー保存先への明示ローカル許可で、本番認証の検証ではない。HTTP Demoのoperator権限は増やさない。

これらのtrueフラグやdigestの形式検査は、外部プロセスが終了した実証そのものではない。operatorが実際の停止証跡を独立に確認し、その参照とdigestを付けてオフライン操作する前提であり、稼働中のAPI/Workerと同時に実行してはならない。実Azureの操作は別途費用上限・停止手順・権限の承認が必要で、今回は実行しない。

復旧serviceはrole/tenant/scope、run所有者、run/slot ETag、slotの対象run、全pending予約IDを照合する。保持中のaccess fenceは勝手に解除せず `RECOVERY_REQUIRES_FENCE_RECOVERY` で拒否し、既存の証跡付きfence復旧を別途要求する。対象runの派生候補・集計・manifestを消してabortedにし、対象slotだけreadyに戻し、対象pending予約を全予約額のuncertainとして共通gateから解除する。日/週の金額・tokens/callsは払い戻さず、他runの予約・slotや共有blocked状態は変更しない。単一catalog batchのCASで保存し、安全な参照・digest・結果だけを監査に残す。同じapprovalと同じ要求の再送は同じreceiptを返す。違う証跡/予約/ETagは競合し、AIの自動再試行や再開は起こさない。

fake輸送とLocalRecordStoreのテストは、Liveモード設定・共有台帳・再起動時再送禁止・不正応答・入力上限・予算停止・timeout保守保持・オフライン復旧のコード契約を確認するためのもの。実AI共通課題判定、事業仮説の品質、実認証とAzureの費用/停止は合格と呼ばない。

識別子検査は許可公開正本に既にあるparticipant/owner/承認者/scope/部署/根拠参照と必要なsession/challenge link、呼出者のIDに限定し、既知literal・email/GUID/内部ID prefix形状を入力/titleと生成本文で拒否する。検査のためにpersonやprivate profile、原チャット、非同意者の資料を追加照会しない。既知でない人名・職務記述による間接特定などの意味的匿名化は保証しない。実社員の匿名ワークスペースを開ける前に、これを別の品質・プライバシー受入条件として検証する。

2026-10-11 の統合再検証で .NET 全531件が成功し、このうち BusinessAnalysisTests は52件すべて成功した。ローカルの独立ダミー保存先で管理CLIのinspect/apply/同じ要求の再送を確認し、同じreceipt、派生情報の消去、slot=ready、払い戻しJPY=0を確認した。これは外部停止の実証ではなく、remoteStopProved=false・Azure操作0・有料AI呼出し0として記録する。
