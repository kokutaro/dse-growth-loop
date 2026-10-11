# PR #38 レビュー後の検証証跡

2026-10-11。初回253ファイルcommit `5b8c282` のログは当時の結果として保持する。本節はレビュー後の実装 `5a55f5d527d04467c6a5f61392d1cf59f3af3f00` と、今回追加した配置契約の回帰2件に対する実測。証跡追加commitのCI結果はPRのChecksで確認する。

| 検証 | 実測 | 証跡 |
|---|---|---|
| .NET全体・再実行 | 546成功、失敗0、skip0 | [最新ログ](dotnet-review-proof-final.log) |
| Python scripts・再実行 | 127成功（初回121→設定伝搬122→offline plan125→今回2回帰127） | [最新ログ](python-auth-propagation-final.log) |
| 認証単体 | 5成功（初回4、今回logout履歴消去回帰を追加） | [最新ログ](auth-account-republish-final.log) |
| 文書/プロフィールE2E | 10成功、失敗0。fixture認証で本人A→Bの分離、再公開503同一key再試行、412の非再試行を含む | [最新ログ](e2e-account-republish-final.log) |
| TypeScript/Vite | 型チェック・build成功 | [最新ログ](build-account-republish-final.log) |
| 実Google Chrome | headless=false、PC1440×1000/390×844、5メニュー＋マイページ12画面。横はみ出し/pageerror0、skipのhash保持・main focus・スマホ見出し位置を確認 | [最新JSON](headed-5a55.json) |

.NET実装は公開事例上限修正後から変更しておらず、この再実行も同じコードで546件成功。旧540件や認証4件/Python121件のログを新しい件数の根拠として転用しない。初回の出力先不在による失敗と途中CI失敗のtraceも保持する。

## 分析Jobの認可設定伝搬

Copilot `4240540850` の「Auth Bindings/Grantsが渡らない」という指摘を現在コードで確認した。`scripts/deploy.py` の `identity_settings` はBindings/Grantsの対応項目だけをallowlistし、`values["runtimeSettings"].extend(identity_settings(env))` で配置parameterへ渡している。`infra/main.bicep` はruntimeSettingsをapps moduleへ渡し、appsのappEnv→workerEnv→analysis.envへ連結する。重複実装や権限変更は不要。

新しい `test_generated_apps_parameters_include_reviewed_analysis_identity` は、架空member/analyst BindingとGrantがgenerated parameterへすべて含まれ、非対応secretが漏れず、private一時parameterが0600であることを確認。Azure subprocessはmockであり実what-ifも行わない。
新しい `test_analysis_job_receives_server_runtime_identity_settings` は、ローカルBicep compile後のARMに対してmain→apps→workerEnv→analysis container環境の伝搬を確認。これは設定経路のローカル検証であり、実Azureの現在Binding/Grant・配置・Job起動の完了を意味しない。

## 限界

実Entra/実AI/複数replica/最新台帳/累計費用/独立停止・復元は未検証。表示名は自己申告、自由文の完全匿名化は未達。Azure起動・設定/権限変更・有料AIは実施していない。追加設計3文書はPR外、Issueは閉じない。

## 参加依頼の閲覧認可とAzure outboxの追加修正

Copilotレビュー5482213637の指摘に対応。非ownerのInvitation DTOは判断APIと同じDecisionAuthorityAsyncを使用し、現在Binding/Profile/Scopeと保存済みrecipientPrincipalId（旧mappingは既存aliasのPrincipal）を再確認してから本文を構築する。person IDだけの再割当てで過去の依頼詳細を取得できない。通常・旧mappingそれぞれでPrincipal再紐付け、Binding無効化、Profile owner変更、Scope喪失の8回帰を追加。

Azure outboxは毎reloadのUUIDを廃止し、サーバーが認可した部屋内の自己aliasとroom/tenant/modeで分離。raw person/principal IDを追加していない。自己aliasを取得できない場合は保存・送信しない。ログアウト時はpopupを待つ前にprivate outboxも破棄する。

- [.NET全体最新ログ](dotnet-invitation-full.log): 554成功、失敗0、skip0。以前の546件ログはその時点の記録として保持する。
- [outbox E2E](e2e-outbox-final.log): 4成功。Demo/Azure fixtureで503と201応答消失後の同じkey/client ID、reload後の再試行、DB保存1件を確認。Azure fixtureでは同じタブの主体切替後に各自己aliasの未保存本文だけを復元。ETag競合と失権時の画面消去も確認。実Entra/実Azureは呼ばない。
- [認証単体](auth-outbox-final.log): 5成功。popup失敗時もupload resume IDとprivate outboxを直ちに破棄、他用途storageは保持。
- [型チェック/build](build-outbox-final.log): 成功。Python配置契約コードは前節から変更なし、127成功の証跡を保持。

### 課題一覧・詳細の経路も再照合

依頼内容を含むChallenge DTOの非owner読取も、同じ現在recipient/保存済みPrincipalの照合へ接続。参加依頼だけを拒否して別の課題URLから本文を読める経路を残さない。上記8回帰でInvitation/Challengeそれぞれの一覧と詳細を確認し、正常な旧mappingも保持。
[最新.NET全体](dotnet-invitation-all-dto-full.log)は554成功。[正常系ブラウザー回帰](e2e-invitation-reading-final.log)は3成功（社員向け匿名リクエスト/応答、追加承認なしの参加・会話、辞退後の閲覧拒否）。以前のログは当時の結果として保持。

## 公開事例・分析要求・自己プロフィールの追加回帰

レビュー5482258476の4点を現在コードで確認し修正。

- 公開事例は承認した参加者全員の現在Bindingを解決できることを要求し、解決不能なら409 SOURCE_REVOKED。無効参加者を既知表示名チェックから落として本文を返さない。公開後のプロフィール表示名変更でIDENTIFYING_CONTENT、そのBinding無効化後は一覧/詳細ともSOURCE_REVOKEDとなる回帰を追加。
- CutoffはJSON必須。欠落はHTTP400、defaultは保存・drafter・slot獲得前に拒否する回帰を追加。
- Production自己専用プロフィールの他人/存在しないIDは同じ404 NOT_FOUND。既存operator例外は保持。
- 分析要求は本人のサーバー発行cache区画にrunId/cutoff/configVersionだけをsessionStorage保存し、画面移動・reload後はGETで保存済みrunを先に確認。404だけが同じ要求の明示POSTへ進む。処理中や復旧必要なrun、新しいcache区画を確認できない場合は新規開始しない。logoutはprivate要求履歴も直ちに消去。このkeyは認可credentialではなく、APIは常に現在のActorを認可する。

[.NET全体](dotnet-review-four-final.log): 556成功、失敗0、skip0。[分析E2E](business-analysis-resume-final.log): 4成功。保存済み/未保存それぞれの応答消失→画面移動→reload→明示確認で、前者はPOST1回、後者は同じbody/Idempotency-KeyのPOST2回を確認。全てMockで実AI送信なし。[認証5件](auth-analysis-resume-final.log)、[型チェック/build](build-analysis-resume-final.log)成功。古い件数のログは当時の証跡として保持。

### Azure fixtureの準備用承諾PATCHの競合

55b9c02のpush CIは12/12成功、PR CIは11/12成功。workspaceのAzure fixture準備PATCHだけが503 ACCESS_BUSYで失敗した。保持したtraceのresponseはretryable=true、AccessFenceはaction実行前のholder/CAS競合でこの応答を返す。準備要求を同じbody/If-Match/Idempotency-Keyで最大4回、503 ACCESS_BUSY retryable=trueだけ再送するように変更。412や別の失敗は再送しない。検証対象のSAVE_FAILED/201応答消失は従来どおり手動再送として扱い、timeout延長や本番認証変更は行わない。[Azure outbox回帰](e2e-outbox-fixture-accept-final.log)は1成功。旧CI失敗とtraceはローカル補足成果物へ保持する。最終SHAのCIは別途確認する。

## 同意時Principalと現在の本人Bindingの再照合

Copilotレビュー5482327233の2指摘（4240672814/4240672836）を修正。公開同意/分析同意の保存済みprincipalIdは必須、pinのない旧同意を推測で補完せず再同意が必要。現在のIdentityResolverの有効Bindingとmember資格、Grantの範囲、原プロフィールのkind/id/tenant/ownerId/範囲が保存済みpinと一致することを共通関数で確認する。匿名事例、分析入力、保存結果の再読取、Live drafter送信前、従来の分析API、公開承認/分類/投影確定に適用。取得した表示名とPrincipalは内部の識別情報検査にだけ使い、モデル入力や社員DTOへ加えない。

- 公開事例の再割当て/pin欠落/Profile owner変更/Binding無効化/Scope喪失の5回帰。詳細は失効、一覧は未認可本文を返さない（pin欠落は既存SourceGuardで除外）。
- Fake Live分析の同じ5回帰。保存結果・本文を失効させ、旧同意では新しい入力を採用せず、transport呼び出し回数を増やさない。
- Fake Live fixtureは有効なGUID tenant/Binding/Grant/本人プロフィールと保存済みpinを持つように修正。実Entra/実APIの設定を変更していない。

[.NET全体](dotnet-consent-binding-pass.log)566成功、失敗0、skip0。[分析E2E](e2e-consent-analysis-final.log)4成功、[公開同意・編集失効E2E](e2e-consent-publication-final.log)1成功。新回帰の前提不足（outcome）と一覧除外の期待値を直した途中ログもローカルに保持する。現在状態の照合であり、未観測の設定変更後に全状態が復元される問題を検出する認可epoch設計は引き続きPR外・未実装。

## Fixtureの共通停止境界

Copilot5482385732の4240725016に対応。Demo/MockでもAiUsage:Stopped未指定/true/不正値はAPI境界のValidateForStartでAI_BUDGET_STOPPEDとして拒否。capabilitiesはenabled=false、POSTはrun/slot/fence保存とdrafter呼び出し前に409。6回帰（Service3+HTTP3）を追加。通常のFake fixtureで実行するテストだけStopped=falseを明示し、実環境の設定は変更しない。

UIは停止中に未確定の保存済みrunをGET確認できるが、404の未保存runへPOSTを送らない。停止解除後は元のbody/keyを使う。応答消失→画面移動/reload→停止中確認→必要なら停止解除後再送の保存済み/未保存回帰を拡張。[.NET全体](dotnet-fixture-stop-fixed-final.log)572成功、失敗0、skip0。[分析E2E](e2e-fixture-stop-final.log)4成功、[型チェック/build](build-fixture-stop-final.log)成功。

## レビュー本文の指摘: room mapの不要更新と未受理要求

Copilot5482429888はopen findings 0/inline未解決0だが、本文に残る実質的指摘を確認して修正。

- Knowledge GETはTokenが新しいmappingを追加した時だけroom mapをcommit。空のdepartmentTokensも先に作らず必要時だけ追加し、既存tokenを返す時はroom mapのETagを変えない。初回追加→同じ本人の再読→参加者の再読でtoken安定とroom map ETag不変を確認する回帰を追加。tenant fenceの保護・その書込は従来どおり保持し、GET全体が書込0とは扱わない。
- 400の分析要求は現在のcapabilitiesからserver時計/current configを取り直し、同じrunId/冪等keyのまま本文を訂正。次の手動確認はGETを先に行い、保存済みなら再送しない。別IDを自動発行せず、既存runとのbody差異はserverの冪等照合で拒否する。現設定/本人cache区画を確認できなければ元の未確定要求を保持。
- 古いconfigと端末の未来時計による実API400→同じIDの手動再確認→200の2ブラウザー回帰を追加。capabilitiesのrequestOwnerKey/currentTimeをOpenAPIに記載し、非analystにはnull、本人cache keyは認可credentialではないことを検証。

[.NET全体](dotnet-map-request-final-pass.log)573成功、失敗0、skip0。[分析E2E](e2e-map-request-complete.log)6成功。[OpenAPI/JSON契約](contracts-map-request-pass.log)20成功。[型チェック/build](build-map-request-fixed-final.log)成功。Tokenのref引数が変換側へ混ざった途中コンパイル失敗と、contract依存のないPythonでの失敗ログもローカルへ保持。既存contract venvをbytecode無効で読み取り利用し、旧checkoutの依存を変更していない。

## 2026-10-11 09:31 UTC レビューの raw job と logout 証跡

正式レビュー5482763042（対象066d7d2）の本文とinline4241064050を確認した。他社員のjobを同一scopeだけで読めるという指摘は、現在の `AccessPolicy.CanRead` のjob分岐がownerまたはoperatorを要求するため再現しない。自己jobは200/ETagあり、同じscopeの他社員jobは未知jobと同じ404 NOT_FOUND/ETagなし、operatorは既存scope内で200になるHTTP回帰を追加した。traceIdは要求ごとに異なるため、問題応答のcode/status/title/detail/typeを照合する。

追加の防御としてProduction社員の `sessionId` 指定raw job経路を404で遮断する。会話jobは本人所有でも共有活動の内部IDを含み得るため、参加者向けには既存の匿名workspace経路を使う。owner/guest参加者の2回帰でraw session/participant情報とETagを返さないことを確認する。Demoとoperatorの診断経路は既存の認可を維持する。[.NET全体](dotnet-job-privacy-final.log)は586成功/0失敗/0skip。途中のfixture partition不足とtraceId完全一致の失敗はローカル補足証跡へ保持する。

本文にある現行logout証跡の指摘には、現在のauth testを新しく実行し[auth-current-logout.log](auth-current-logout.log)へ保存した（5成功/0失敗）。現在のstorage回帰はuploadの旧key/v2本人key、旧 `dse-outbox:`、workspace outbox、business-analysis要求keyをpopup終了前に消し、無関係なstorageを保持する。popup失敗・遅延token応答も確認する。古い5件ログは当時の記録として維持し、新しい回帰名の証拠に転用しない。

[現在のlogout・文書取込E2E](e2e-job-logout-current.log)は6成功/0失敗。logoutではMicrosoftログイン先をabortするfixtureで保護panelの消去、旧チャット下書き消去、無関係storage保持を確認する。文書では本人別取込再開とjob poll/retryの正常系を確認する。実Entra/Azure/有料AIは呼んでいない。

## プロフィール再送のセッション終了・主体切替境界

正式レビュー5482799921の本文/inline4241100553を確認し、ACCESS_BUSY再送が保持済みAuthorizationを使い続け得る点を修正。authはlogout開始時とinitializeAuthによる主体再初期化時に旧セッションのAbortSignalを同期abortする。旧token応答は新セッションが開始していても拒否し、旧token取得のfinallyが新しいpending tokenを消さない。

Profilesはコンポーネント/主体のlifetimeとauth sessionのsignalを統合し、fetch・backoff待機・各再送直前・レスポンスからのstate更新前に検査する。unmount/主体変更は旧controllerをabortし、timerを解放。503 ACCESS_BUSYかつretryable=trueだけ同じbody/If-Match/keyで最大4回再送し、412は再送しない。既に受理済みの書込みを取り消す機能ではない。

実auth moduleを使用しMSALだけをfixtureにしたAzure logout、Demo画面移動・主体切替の3ブラウザー回帰を追加。時計を停止して初回503を受け取り、終了操作後に全backoff区間を進め、PATCHが1回だけであることを確認する。旧fixtureのauth moduleにもsession signal契約を反映した。実Entra/Azure/有料AIは呼ばない。

[現行auth unit](auth-profile-abort-final.log)7成功/0失敗、[TypeScript/Vite](build-profile-abort-final.log)成功。[プロフィール/文書/logout/匿名workspace E2E](e2e-profile-abort-final.log)28成功/0失敗。3新回帰は再送全backoff区間を越えてPATCH1回を確認。途中の時計停止下RAF待機と主体切替後Home遷移のfixture期待値不備は補足ログに保持する。Backendソースは83bf4e3から変更なく.NET586/Python128の証跡を保持し、最終SHAのCIは別途確認する。
