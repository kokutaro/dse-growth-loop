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
