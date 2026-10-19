# AIアダプター・使用量台帳

更新日：2026-09-29。Issue #8のローカル実装。全件ダミー限定。実Azure・有料推論・実請求との照合は未実施で、#9の承認ゲートで行う。

## 2026-10-17：送信前の主体設定照合

共通AI実行器は設定付きAuthorityProviderを保持し、tenant fence取得後、入力組立・AiRun作成・使用量予約・HTTP送信・キャッシュ参照より前に正本と現在設定を照合する。既定/明示Azureではrole/scope/principal/OID/enabled不一致を `AUTHORITY_REVOKED` で拒否する。会話参加の照合にも同じProviderを使う。根拠なしの質問分類も対象で、入力の根拠検査や送信後検査だけに依存しない。[再現・回帰証跡](validation-results/20261017-ai-authority-before-send/results.md)。この変更で実主体の権限や同意を付与せず、実AI品質・実環境認可受入も完了扱いにしない。

## 呼出し境界

### 2026-10-18：自然文検索の設定事前検証（Issue #16）

候補設定を読み込んだローカルWorkerで `dotnet run --project src/Dse.Worker -- --ai-search-preflight` を実行する。通常のWorker起動より前に設定だけを検査し、JSONの `ready` と固定コード・設定名を返す。終了コードは準備済み0、不備あり1。認証情報の取得、Key Vault/Cosmos/AIへの接続、使用量台帳の参照・更新は行わない。設定値や例外本文は出力しない。秘密本文をコマンド引数に渡さない。

検索分類と共通の質問定義・上限計算により、現在の `search-query-v2` は保守的な出力枠2,160以上を必要とする。上限1,000では送信前に拒否する。入力は空の課題文でも必要になるJSONフレーミングの最小枠を確認し、実際の課題文・根拠は従来どおり実行時にも上限検査する。プロンプト・分類体系・質問順序は変更しない。

両提供元の固定モデル・単価・出力契約、Live/停止/opt-in、既存実行器が要求する有限の実行設定、データモード、Key Vault参照形式をまとめて検査する。AzureではManaged Identityのclient ID形式、Demo Liveでは永続ローカルストア指定も確認する。使用量・残額・予約件数による追加承認ゲートは作らず、都度の予算確認も要求しない。既存実行器の制御は維持する。

Liveの自然文分類で不備がある場合はAI処理へ進まず、検索APIは422 `AI_SEARCH_CONFIGURATION_INVALID`、UIは運用者の設定確認が必要と表示する。分類の対象外や利用者の条件入力不足とは区別する。

`externalStateChecked=false` は常に維持する。`ready=true` は提供元の実配置、認証/RBAC、永続先の可用性、実行時の利用可否、AI応答品質を証明しない。実環境受入やIssue #16全体の完了判定には使わない。2026-10-18の先行障害における最初のロック残留原因も、この設定検査やローカルの競合再現だけでは確定していない。

Domainの`ITextStructurer`、`IDecisionProvider`、`IReranker`、`ISummarizer`をAPI/WorkerのDIへ登録する。業務層へ提供元JSONを渡さず、`StructuredText`、`Decisions`、`Ranking`、`GroundedText`を返す。実HTTPと決定論的Mockは同じ応答検証を通る。`Ai__Mode=Mock`の結果は`modelId=mock-v1`で区別し、使用トークン・推定費用は0。

機能別プロンプト、分類正本への全分布保存、訂正競合、検索画面との結合は#10・#11、事例自動投稿は#13。現行の検索・Office取込・会話から推論を自動起動しない。既存投影の人物・事例と有効版の根拠を読む境界を提供する。新規文書版の構造化は#10で取込Jobの認可・版照合と統合する。

`AiContext`はサーバー内部で作る。主体、Job ID、プロンプト版、分類体系版、データ版、根拠参照、候補SearchUnitを渡す。外部APIにこの型を直接公開しない。候補IDはSearchUnitのIDで、集約人物IDとは区別する。送信前に正本を読み、投影の要約ではなく検証済み根拠本文を組み立てる。会話内呼出しは現在の参加者・open状態・AI有効設定も確認する。出力の候補ID・根拠ID・候補と根拠の対応を照合するが、内容の意味的正しさや引用の妥当性は後続の品質評価が必要。

Jev Choiceは要求した全選択肢、有限数0〜1、合計1（許容差1e-6）、最大確率のchoice、別のconfidenceを検証する。公式の`noul`を`YesProbability`へ写し、0も有効。`HasEvidence=false`のNoulは送信せず`Unknown/null`、停止・失敗は`AiResult`の状態で未実行と区別する。Noulしかなく全て根拠不足ならHTTP 0回で`NotEvaluated`。根拠充足の判断はサーバー側業務処理の責任で、確率0.5を勝手に未判定へ変換しない。

Foundryは`json_schema`のstrict構造化出力を要求し、ローカルでもキー・型・IDを検証する。拒否、content_filter、length、異常JSON、スキーマ不一致、モデル不一致、ID捏造は成功にしない。上流のエラー本文・例外本文を利用者へ流さない。

## 提供元と設定

| 項目 | 契約 |
|---|---|
| Jev | `https://api.typesafe.ai/v1/systemone`のみ。リダイレクトなし、仲介APIなし。`jev-1.13.0`等の明示版のみ |
| Jev秘密 | `KeyVault__Uri`、`Jev__SecretName`、32桁の`Jev__SecretVersion`を指定。Key Vault REST 2025-07-01で取得。キー本文の環境変数は読まない |
| Foundry | Azure OpenAI v1 Chat Completions。`Foundry__Endpoint`はHTTPSの`*.openai.azure.com/`。Deploymentを指定 |
| 認証 | Azureモードは`AZURE_CLIENT_ID`のUser-assigned Managed Identity。Foundryは`https://ai.azure.com/.default`、Key Vaultは`https://vault.azure.net/.default`。Demoからの明示Liveでは開発者のDefaultAzureCredential |
| モデル固定 | `Foundry__ModelId`は応答の正確なmodel文字列、`ModelVersion`、`Region`、`Sku`、`OutputLimit`を実契約で確認。SKUはStandard/GlobalStandard/DataZoneStandard。Lunaが候補、Terraは明示比較用。未確認の実IDは設定しない |
| 単価 | 提供元ごとの`InputUsdPerMillion`、`OutputUsdPerMillion`、共通の単価版・為替版・USD/JPYを明示。Jev出力単価0も設定値として保持 |

latest/previewエイリアス・別モデルへの黙示フォールバックは拒否する。配置定義の`NoAutoUpgrade`と実配置の固定版が一致していることを#9で確認する。`ModelVersion`だけでサービスの配置内容を証明できるとは扱わない。対象モデルがv1 Chat Completions/strict schema/`max_completion_tokens`に対応しない場合は停止し、API方式・スキーマを別途変更してから再承認する。

`AI_STOPPED=true`が既定。実送信は`AI_STOPPED=false`に加え`Ai__Mode=Live`、`AiUsage__LiveOptIn=true`が必要。UTC日次回数・トークン・推定円上限と単価/為替版は#2の設定を使用する。配置時は`scripts/cost_config.py`が`AiUsage__*`へ変換し、ローカルは`AI_*`も読む。両方ある場合は`AiUsage__*`が優先。API/Workerには同じ予算・単価設定を配布する。

上限の既定は入力8,000、出力1,000、同時実行1、最大試行2、全試行合計30秒。設定可能な絶対上限は16,000/8,000/8/3/120秒。現行のtenant単位AccessFenceは認可変更との競合を防ぐため、実効同時実行をさらに1に制限する。停止設定は試行ごとに再確認し、進行中のリクエストは期限までに終了する。環境変数変更はプロセス再起動が必要。

## 台帳・費用・不確定課金

`catalog`の設定済み単一tenantパーティション内に以下の内部レコードを置く。通常業務読取APIには公開しない。

| kind | ID / 内容 |
|---|---|
| `aiUsageDay` | `ai-day-YYYY-MM-DD`。UTCの予約開始日、確定＋予約トークン・回数・推定円 |
| `aiUsageGate` | `system-ai-gate`。未完了予約ID、提供元の上限超過による停止フラグ |
| `aiReservation` | `ai-res-<UUID>`。runId、日、予約トークン/推定円、モデル版、単価版、為替版/値、pending/settled/uncertain |

送信試行ごとに最大入力＋最大出力トークンとその費用、1回、1実行枠を予約する。day・gate・reservationをETag条件付きの同一Transactional Batchで作成/更新し、競合時は最大20回のCAS再試行後に停止する。再起動・API/Worker並行・異なるモデルでも共通上限を使う。ローカル実送信では`Data__LocalPath`による永続台帳が必須。MemoryRecordStoreはfake HTTPテスト用。

提供元がusageを返せば、推論結果の成否と無関係に精算する。429/5xx/切断/認証失敗/無効JSON/usage欠落等では課金の有無を断定せず、その試行の最大予約額を`uncertain`として残す。SDKの推論再試行は使わず共通実行層だけが429/5xx/切断を有限回再試行し、その都度新規予約する。401/403、スキーマ不一致等は再試行しない。Retry-Afterの秒・日時を尊重し、残り期限より長ければ再送せず失敗にする。

入力の事前検査はリクエストJSONのUTF-8バイト数＋256で保守的に見積もる。Jevにはmax-output指定がないため、質問/選択肢数・IDバイト数から固定応答の上限見積もりを検査する。Foundryには`max_completion_tokens`を渡す。受信本文にもバイト上限を設ける。モデル固有のtokenizerで測定した値ではなく、内部フレーミングや提供元の数え方による誤差は未測定。

usage欠落時の計上は最大入力/出力の全予約量。実使用が契約上限内なら、トークン過大推定は0〜その予約量、円の過大推定は0〜予約額の範囲となる。提供元が予約上限を超えるusageを返した場合は実usageを記録し、将来の日もgateを停止する。料金改定・為替・割引/無料枠・サービス側上限違反を含む実請求の絶対上限は保証しない。推定額を請求額と呼ばず、Azure Budget通知とも区別する。

予約は時間で自動返却しない。プロセス死亡時は実行枠も保持して停止する。API/Workerを全停止し、CosmosでrunIdに対応する予約とAccessFenceを確認した後に以下を実行する。これは使用量の返金・既存データ削除をしない。dayを跨いでも古い予約は元の日に残り、新規枠は新しいUTC日の予算を使う。

```sh
Recovery__ProcessesStopped=true Recovery__AiReservationId=ai-res-対象ID \
  python3 scripts/run-env.py -- dotnet run --project src/Dse.Worker -- --recover-ai-slot
```

クラッシュで残ったAccessFenceは[既存の手動復旧](persistence.md)に従う。台帳精算と別パーティションのAiRun確定の間に死亡した場合、AiRunは`not_evaluated`等の未確定状態で残り得る。課金判断はrunIdで関連する予約を正本として照合し、成功結果を推測しない。上限超過gateの解除や不確定課金の減額は自動提供しない。契約調査・全停止・承認を経た保守作業とし、通常コマンドでは予算を戻さない。

## 追跡・キャッシュ・縮退

対象のcatalog/conversationsへAiRunを保存する。入力ハッシュ、入力正本の版、根拠ID、モデルID/版/リージョン/SKU、プロンプト/分類体系/データ版、トークン、試行・再試行回数、経過時間、成否、推定円、単価/為替版、推定usageフラグを保持する。生応答・入力本文・秘密を通常ログやAiRunに保存しない。現在は生応答Blob保存を有効化していない。将来調査で保存が必要な場合も、専用非公開Blobと保持/アクセス制御を別途用意してから明示的に有効化する。

HTTPヘッダーで取得できた形式検証済みの要求IDを`providerRequestIds`へ保存する。Cosmos RUは既存repositoryの相関ID付きログで計測し、AiRunの`requestUnits`には現在集約していない（既定0をRU実測値と解釈しない）。

成功結果だけを最大128件・5分のプロセス内キャッシュへ保存。入力/根拠版、モデル設定、プロンプト版、分類体系版、データ版、tenant/主体/sessionをキーへ含める。キャッシュ取得前も現在の正本・認可を再確認し、取得後も版を照合する。キャッシュ利用のAiRunは新規送信なしの`not_evaluated`、usage 0、`cachedFromRunId`を記録する。呼出し側の結果は`Succeeded`＋`Cached=true`で区別する。

停止・予算枯渇・実行枠不足は`BudgetStopped`と理由を返す。呼出し開始後の失敗は`Failed`で、消費済み使用量を残す。これらを候補0件と表示しない。Jev停止時は手動条件/正規化語検索、Foundry停止時は一次候補へ縮退し、再順位付け成功や自動投稿として扱わない。この画面への結合は#11。

## 承認後のライブスモーク

通常CIはfake HTTP/Mockのみで外部推論0回。以下は**今回実行していない**。#9で実行者・対象契約・承認参照・承認予算を確定してから、承認された環境で実行する。新規Azure展開をこのコマンドから行わない。

1. モデル固定設定、Jev固定secret版、MIのKey Vault Secrets User/Cognitive Services OpenAI User、Cosmos catalog書込権限、Authの架空人物Binding/Grantを確認する。キー本文を端末やIssueへ貼らない。
2. `Data__Mode=Azure`、`Ai__Mode=Live`、停止解除、実送信opt-in、正のUTC日次上限・単価/為替版、`APPROVED_MONTHLY_BUDGET_JPY`を設定する。承認額内で両提供元の最大予約を確保できる値にする。
3. `AiSmoke__Approved=true`、`AiSmoke__DummyOnly=true`、`AiSmoke__OperatorPersonId`、`AiSmoke__ApprovalReference`、`AiSmoke__Count=2`を明示する。`AiUsage__MaxAttempts=1`、`AiUsage__MaxInputTokens=8000`、`AiUsage__MaxOutputTokens=1000`と、同じ値の`AiSmoke__MaxInputTokens`/`AiSmoke__MaxOutputTokens`を指定する。より小さい上限も可能だが、構造応答見積もりを満たす必要がある。
4. 次を1回実行する。固定ダミーのJev Choice 1件、Foundry構造化1件、最大2推論試行。承認情報をauditに保存する。正常時は終了0、片方でも失敗なら終了1。標準出力は状態とrun IDのみ。

```sh
python3 scripts/run-env.py -- dotnet run --project src/Dse.Worker -- --ai-smoke
```

5. AiRun・予約・プロバイダーusageを照合して結果を#9に記録し、停止とopt-in無効へ戻す。401/403なら認証/RBAC、429なら待機/クォータ、model/schema不一致なら固定設定/契約を調査する。未承認の別モデル・別APIへ切り替えない。

Issue #9追加：`AiSmoke__Suite=connections`、`AiSmoke__Count=3`を明示すると、Jev Choice＋Noul 1リクエスト、Foundry構造化1、固定ダミー根拠の再順位付け1を検証する。既存basicの2件承認を3件承認として使わない。固定fixtureの変更／撤回は全推論前に拒否し、自動seedしない。最大3試行分の日次予約枠と、一時Manual Jobの自動retry=0を確認する。[Azure接続検証の実行手順](azure-validation.md#ai接続suite)を参照。ライブ実行は未実施。

## 公式契約の確認元

2026-09-28確認：[Jev API](https://docs.typesafe.ai/api)、[固定モデル](https://docs.typesafe.ai/models)、[Noul](https://docs.typesafe.ai/primitives/noul)、[Foundry Chat Completions](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/chatgpt?view=foundry-classic)、[構造化出力](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/structured-outputs?view=foundry-classic)、[Key Vault Get Secret](https://learn.microsoft.com/en-us/rest/api/keyvault/secrets/get-secret/get-secret?view=rest-keyvault-secrets-2025-07-01)。公開資料の応答fixtureは実契約の接続成功やAI品質測定を意味しない。

## 会計を使用しない通常運用

`AiUsage__AccountingMode=Disabled` は費用・累積回数の日次／週次台帳を参照・更新せず、予約・精算も行わない。既存の `aiUsageDay` / `aiUsageWeek` / `aiReservation` は削除せず保持し、週替わりの台帳初期化は不要。既定の `Ledger` は既存検証との互換用であり、通常運用へ古い60回／2,000円の検証設定を戻す用途には使用しない。

Disabledでは `MaxAttempts=1`、`MaxConcurrency=1` を必須とし、停止スイッチ・Live opt-in・モデル固定・入出力上限・timeoutを維持する。`aiExecution` はrun IDごとの二重送信防止用の状態で、金額・累積回数・日付別トークンを保存しない。共有 `system-ai-gate.active` は旧会計モードとの同時送信を防ぐ制御状態としてのみ使用し、`weeklyBudgetRequired` はDisabledの送信条件にしない。運用上の `aiRun` 監査（結果・request ID・当該処理の試行数）は残し、Disabledの推定費用はnullにする。

timeout・通信結果不明では実行スロットを自動開放せず、自動再試行しない。既存の停止・提供元終了確認を伴うオフライン復旧でのみ開放する。業務分析復旧は各runに記録したaccountingModeから会計予約／実行状態を区別し、古い予約や別runを変更しない。Unknown mode、複数試行・同時実行設定はfail closedとする。

Disabledの `--recover-ai-slot` には `Recovery__ProcessesStopped=true` に加え、`Recovery__ApiStopped` / `WorkerStopped` / `ProvidersStopped` の明示確認、`Recovery__ApprovalReference`、`StopEvidenceReference`、64桁SHA-256の `StopEvidenceSha256` が必要。API/Workerの停止だけでは、timeout後の提供元処理の終了確認に代えられない。復旧はこれらの証跡参照を実行状態に残す。確定したHTTP拒否では通常AIゲートと業務分析スロットを解放するが、同じ処理の自動再送は行わない。

共有ゲートを使用するLedger/Disabledの両モードで、送信後のキャンセル・timeout・結果不明の通信失敗はpendingを保持し、自動再試行しない。明確な拒否以外の5xxも結果不明として扱う。単独slot復旧の提供元終了・停止証跡は両モードで必須であり、モード切替だけでpendingを解放できない。

認証取得と推論送信はリクエスト単位のdispatch状態で区別する。送信前の認証失敗はスロットを解放し、送信後の結果不明は保持する。キャンセルと実送信の境界はCASで閉じ、非協調の認証取得がtimeout後に完了しても古い処理から推論を送信できない。

既存Ledgerの業務分析設定ハッシュはAccountingMode導入前と同じ形を維持し、保存済みrunの再取得で推論を再送しない。Disabledは別ハッシュとなる。確定拒否は同時キャンセル時も実行ゲートを解放する。業務分析側のdeadlineが先に満了した場合、drafterの終了を最大12秒だけ待って送信前中断／確定拒否を引き継ぐ。確定できない状態は復旧要求を維持する。
