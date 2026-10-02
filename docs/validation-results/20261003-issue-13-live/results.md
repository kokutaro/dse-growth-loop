# Issue #13 ライブ検証（2026-10-03 JST）

ユーザーの「残りのタスクについて有料タスク実行承認」を受け、固定ダミーだけで実行した。先行Issueの承認を流用していない。[ローカル/fake試験](../20261003-issue-13-local/results.md)とは別の結果で、全体の検索Recallや実社員を対象にした運用品質を示さない。

## 対象・実行上限

既存の `dse-growth-loop-rg`、Cosmos/Blob/Queue、Worker MI、Key Vault内の固定版Jevキー、既存Foundryデプロイを使用。基盤・モデルの新規作成、再seed、既存資料・社員の上書き、APIへの認証バイパスは行っていない。新規の評価Challenge/Session/Messageと監査・結果だけを保存した。

- Jev：`jev-1.13.0`、Foundry応答モデル：`gpt-5.6-luna-2026-07-09`、GlobalStandard/Japan East、NoAutoUpgrade。
- 手動Job：`dse-validation-chat13`、0.25 CPU/0.5Gi、900秒、retry 0、parallelism/completion 1。通常Worker：min 0/max 1、Queue poll 60秒/cooldown 300秒。
- tenant共有のUTC日次台帳：最大80呼出、予約token 1,000,000、推定AI費用100円。1呼出input 8,000/output 3,000、attempt/concurrency 1、timeout 60秒。判定・投稿の既定値は変更しない。
- 単価：Jev input $0.042/M・output 0、Luna input $0.20/M・output $1.20/M。価格根拠は[Typesafe公式](https://docs.typesafe.ai/models)と[Azure公式の価格表](https://azure.microsoft.com/en-us/blog/gpt-5-6-now-available-in-microsoft-foundry/)。USD/JPY 200は予算用の保守的仮値で、実為替ではない。

## 初回の未達と修正

v1の有用会話controlではJevが成功したが投稿0件だった。[初回記録](initial-control.json)の後、同じ有用例だけで[診断](initial-diagnostic.json)し、Noul 0.6 < 閾値0.7による見送りを確認した。この段階では不要20例を実行しておらず、誤投稿率の分母に含めない。

検索前に「共有済み事例を提示する支援が有用か」を問う判定文を、「直近の発言が未解決の業務問題への支援を求めているか」に明確化した。候補の有無は後段の検索で判定する。英語の判定指示で、挨拶・報告・解決済み・不要・引用例・テスト文・不明を除外する。閾値0.7と不要20例は変更せず、ルール版を `chat-assistance-v2` に更新した。判定runId/状態/確率/閾値をJobに保存し、失敗原因を追えるようにした。会話本文を監査値へ複写しない。

## 不要20例の確認

[固定fixture](fixtures.json)は確認前にコードで固定した。語gateを通る不要例14件と、通らない6件を含む。調整には有用controlだけを使い、確認20例で閾値を後付け調整していない。

[実行結果](quality.json)：**誤投稿0/20、暫定目安2件以下を満たした**。6例は外部呼出0、残り14例はJevの判定成功後に検索/Foundryを省略。判定値は0.04〜0.11で、停止・予算超過・プロバイダー失敗による見かけのゼロではない。有用controlは判定0.97、事例 `demo-knowledge-01` を1件保存し、出典APIで現行資料を再検証した。

品質suite全体は提供元17呼出（不要例14＋有用例3）、input 7,060/output 603 token、推定AI費用0.1052584円。usageEstimated=false、retry 0、全AiRun succeeded。有用controlの保存確認まで12.075秒。不要例11は15.401秒かかったが投稿はなく、これは有用例の投稿待ち時間とは別の記録として残す。

## 実行と再現

ローカル：`dotnet test tests/Dse.Tests --no-restore -m:1 -nodeReuse:false --collect:"XPlat Code Coverage"` は295件成功。追加の起動ガード15件、Chat範囲36件、契約17件、Playwright `chat-assistance.spec.ts` 1件成功。PlaywrightはMockで、上記ライブ品質の代替ではない。

ライブ実行：Workerの `--chat-smoke` をAzureの既存Worker MI付きManual Jobで実行した。`ChatSmoke__Approved=true`、`DummyOnly=true`、`OperatorPersonId=demo-person-01`、新規RunId、ApprovalReference、Suite=`quality|queue|probe` を明示する。Azure/Live/正の承認予算、MI/tenant、上記有限上限が必要。実行前に既存公開fixtureと原本を照合し、Queueが空であることをMIで確認する。fixtureをseed・修復するコマンドではない。

`quality` は実サービスを常駐評価Job内で直接処理し、実際の5秒デバウンス後に20例を検証する。`queue` は通常WorkerへQueue通知してcold 1例→warm 2例を処理する。各例を `extracted/validation/issue13/<run>/<suite>-<case>.json` に逐次保存し、完全報告も同prefixに保存する。stdoutと公開JSONはモデル/usage/判定/測定値の許可項目だけで、秘密・実社員ID・プロンプト・生応答を含めない。

イメージ：`dse-worker:issue13-live3`、ローカルimage ID `sha256:0416ef2af2b519db95432295e9ab8b794461f2368c9eb1fe3bac8fb9e2db8698`。ACR manifest digestと時間計測・停止証跡は後掲の最終記録を参照。

Azure CLIのimage/env更新でKEDA metadataが空文字になる変更を検出した。計測前に保存済みのscale全体をARM PATCHで復元し、`dse-worker--issue13-keda` の正しいQueue/MI/poll/cooldownをARM GETで照合した。誤設定リビジョンでの時間を成果に含めない。既存スケール設定は変更対象から外さず、更新後も照合する。

## 実Queueのウォーム／コールド時間

[0台開始証跡](cold-start-before.json)、[0→1起動](queue-worker-running.json)、[各例とAiRun](queue.json)を保存した。正常なQueueスケーラーと全active revisionの実レプリカ0件を確認してから通知し、常駐Workerの通常消費で処理した。

| 例 | 投稿 | 保存確認まで | 結果 |
|---|---:|---:|---|
| cold control | 1 | 37.047秒 | Worker 0→1を含む。出典再検証成功 |
| warm-1 | 1 | 14.476秒 | 稼働中の目標15秒以内。出典再検証成功 |
| warm-2 | 0 | 11.336秒 | Foundry `AI_RATELIMITED`。安全に見送り、成功時の時間に含めない |

`observedMs` は人間のSendAsyncでDB保存が確認された後から、AI Messageと同一batchのJob readyを正本で観測するまでの経過時間。Job readyの1秒pollとCosmos読取を含む保守的な保存確認時間で、DBサーバー内のcommit時計そのものではない。`humanCommittedAt`/`aiCommittedAt` はレコードのcreatedAt（batch直前に付与）で、commit完了時刻として差分を計算していない。qualityのdirect時間も通常WorkerのQueue時間に混ぜない。API HTTPのコールド起動とUI表示までの10秒pollは今回の測定範囲外。成功したwarmは1標本、coldも1標本であり、一般的なSLOやp95達成を示さない。

warm-2では、同じ低容量の固定Foundryデプロイへ短時間に続けた再順位付けがレート制限された。provider usageがない失敗分を8,000/3,000 tokenの上限で保守的に計上した（usageEstimated=true）。MaxAttempts=1を守って再試行や二重投稿はしない。失敗を短い「投稿成功時間」に置き換えずそのまま記録する。今回、モデル容量の増加・上限緩和・再試行設定の変更はしていない。

## 最終確認・使用量・停止

全28外部試行のうち27成功・1レート制限、全てretry 0。提供元usageが得られた27成功分と、失敗分の保守的計上を合計するとinput 20,539/output 4,416 token、推定AI費用 **1.2812708円**。これは実請求額ではなく、先の単価・仮為替による台帳見積り。Cosmos全体の実RU、Azure実請求・インフラ費用照合は未測定／請求反映待ちでnull扱いとする。100円はAI台帳の上限であり、Azure全体の請求を100円以内に保証する値ではない。

使用したACR manifest digest：`sha256:e8f4c0ca08fa512ca0ddc094794f599a522e2519c1255a0b8026f91026963ac8`。[測定時のソースhash](source-hashes.json)も保存した。各Jobは初回control不成立2回、品質suite完了1回、Queue suite完了1回。JobのSucceededは計測コマンドの完了であり、warm-2の投稿成功を意味しない。

Issue #13の残り2Todoは、不要20例の暫定目安評価（0/20）と、fake/liveを区別したwarm/cold計測を確認した上でチェックする。ローカルの重複/権限外/撤回試験をライブで再実測したと主張しない。現在の固定ダミー4件に対する検証であり、全体の検索品質は #14 の評価範囲に残る。

[停止確認](stopped.json)：API/Workerの全active revisionが0件、AI stopped=true/liveOptIn=false、Worker日次予算0、評価JobにRunningなし、ChatSmoke承認=false。停止用設定の新リビジョンもinactiveにした。元のQueueスケール（min0/max1・poll60/cooldown300）を保持。既存モデル・基盤・検証データは削除せず、ACRの一時ログイントークンファイルを除去した。
