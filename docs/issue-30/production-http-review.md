# Issue 30 本番HTTP検証用の一時API更新

2026-10-03。実HTTP/JWTとcold/warmを測るための具体的変更案。ユーザーはこの一時更新・検証後復帰を明示承認済み。独立集合24問の正解ラベルも承認済み。Managed Identity専用Jobと実JWTのHTTP24問を実施し、[結果](../validation-results/20261003-issue-30-independent-production/results.md)へ記録した。

## 対象・変更

- 対象：`dse-growth-loop-rg`の`dse-api`。
- イメージ：`dse-api:issue9-final5` → ビルド・332テスト検証済み`dse-api:issue30-independent1`。ローカルimage IDは`sha256:c417cea051d3b0736e8e655a50976a08c263b1a51c2f56c92de4d2a0e234716d`。
- 検索：人物10件、事例15件、救済2。副業務断片追加を有効化。
- 認証：Entra tenant/client ID、JWT検証、許可ユーザーは維持。既存テスターの業務公開範囲を`demo`から`issue30-independent-20261003-v1`へ一時的に切り替える。実社員を追加しない。
- ログイン準備時はAI停止・opt-in false・日次予算0。認証確認後、検証時だけ`jev-1.13.0`と既存`dse-luna6-eval`を利用する。1回の試行、日次台帳240呼出／推計200円、入力16,000／出力3,000、並列1、60秒を上限とする。
- 通常Workerは停止のまま。HTTP scalerのmin0/max1・元metadataを保持。旧公開範囲のデータは変更しない。

## 影響・復帰

切替中、許可ユーザーの検索は独立集合に限定され、通常の`demo`範囲を見られなくなる。イメージと認可設定を変更するので明示承認が必要と自動承認レビューが判断した。

変更前の完全な設定と停止状態を非公開の`.artifacts/issue30-production-preflight/`へ保存済み。API用イメージをACRへpushし、承認後に一時更新を適用した。Jev旧キーの認証失敗は、別の明示承認を受け`.env`の`TYPE_SAFE_API_KEY`を既存秘密の新しい版へ登録し、検証だけで一時参照して修復した。旧版を保持し、通常Workerと復帰用API設定は元の参照版を維持する。

検証後は元のイメージ・業務公開範囲・AI停止設定・スケール定義を復帰し、元と同じactive revisionなし／稼働0状態を確認する。評価集合は専用ID・専用公開範囲で残し、通常の`demo`からは見えない。データ削除・認証迂回・ロール追加は行わない。

HTTP検証では実アプリのブラウザサインインを使う。Azure CLIによる委任トークン取得は`AADSTS65001`で不可。認証できない場合、その経路を未達とし、Managed Identity検証の時間をHTTPのcold/warm合格として扱わない。

## 実施後の確認

本番サービスJobと実JWT付きHTTPで24問ずつ実施。冷起動はレプリカ0から公開SPA初回GETの33.41秒を別測定し、JWT検索cold全体とは区別した。元イメージ・全env・認証／許可ユーザー／公開範囲・scaleを復帰し、実レプリカ0、通常Workerの非変更、専用Job終了・AI停止／予算0を[照合済み](../validation-results/20261003-issue-30-independent-production/restoration.json)。品質不合格のため改善設定の恒久採用は行っていない。
