# 2026-10-11：匿名契約・Live分析・復旧CLIのローカル検証

専用checkout `repo-issues-20261011`、ブランチ `codex/remaining-issues-20261011`、基点 `78f182a30f106a8d34f7989d39d23f622e5d5c08`。前段の[結果](../20261011-remaining-issues/results.md)と成果物は保持する。変更は未コミット。push、PR、Issue更新、デプロイは行っていない。

新しい匿名API、独立した表示名開示、Live分析アダプター・共有台帳・復旧CLI、部分操作計測をローカル検証する。実Entra、実Cosmos、実モデル呼び出しと実環境停止の成功を表すものではない。Azure操作・有料AI呼び出し・GitHub書込みは0。

残る実環境検証と受入は[Issue別棚卸し](../../remaining-issues-acceptance-20261011.md)を参照。

## 変更

- 5メニューとマイページを維持し、匿名専用APIへ接続。課題から匿名候補を依頼し、本人の「参加する」で追加承認なく活動を開始する。内部person/source/scope IDを社員向けDTOへ返さず、不透明tokenとルーム内匿名名を使う。旧raw検索・会話経路はProductionではoperatorに制限し、プロフィールと文書は本人所有を確認する。
- 表示名開示を事例公開・分析同意から独立。全参加者が現プロフィール・認可・参加版へ同意して初めて共有し、撤回や観測した変更で全同意を失効する。表示名は本人プロフィールの自己申告であり、公的氏名の確認ではない。
- 事例編集は公開・分析同意を失効。匿名の確認記録を選ぶ根拠欄を追加し、根拠のない推論・参加者報告の保存を防ぐ。公開一覧・詳細は双方同意と承認を再確認する。
- Live分析アダプターは固定した設定・モデル・価格/FX・上限と許可済み原本を再確認し、共有台帳で予約・精算する。自動再試行なし。タイムアウト・使用量不明は予約とスロットを保持し、未観測費用を0へ変えない。既定は無効で、今回の輸送はFakeのみ。
- 復旧CLIは対象run/slot ETag・予約集合・停止証跡reference/hashを照合し、派生結果を除去して対象だけを解除する。留保は保持し払い戻し0。操作者の停止申告は遠隔停止の証明ではない。
- source・projection・fence・audit・ledger・SDK応答の部分操作計測とoperator専用exportを追加。Demo RUはnull、完全計測/総合ゲートはfalse。空の事例一覧検索には条件なしの専用Cosmos queryを使う。

## ローカル検証

| 検証 | 結果 | 証跡 |
| --- | --- | --- |
| Solution build | 成功、エラー0、既存ASPIRE010警告1 | [build](logs/build-continuation-final.log) |
| .NET単体・統合 | 535成功、失敗0、skip0 | [log](logs/dotnet-tests-production-contracts-final.log)、[TRX](logs/production-contracts-final.trx) |
| Python | 121成功 | [log](logs/python-tests-continuation.log) |
| OpenAPI/JSON契約 | 20成功 | [log](logs/contracts-tests-production-contracts-final.log) |
| fixture | 5成功 | [log](logs/fixtures-tests-continuation.log) |
| 認証設定 | 4成功 | [log](logs/auth-tests-continuation.log) |
| Web TypeScript/Vite | 型チェック・build成功 | [log](logs/web-build-production-contracts-root-final.log) |
| Browser E2E | 49件成功、失敗0（新機能19＋既存30） | [重複除外集計](e2e-production-contracts-49-summary.json)、[既存回帰log](logs/e2e-legacy-continuation.log) |
| Worker復旧CLI | inspect/apply/同一key replay成功 | [検証JSON](recovery-cli-verified.json) |
| Mac Chrome PC/スマホ | Chrome 154.0.8037.99・headed、1440×1000 / 390×844、22画面成功 | [browser JSON](browser-review.json)、[実行script](review-local.mjs) |

専用lint設定・scriptはないためlint成功とは記載しない。Production/Azure向けAPIのHTTPテストはローカル署名JWTとMemory DBで実施し、未認証401・認証成功200/201・旧raw API403・本人紐付け変更拒否を確認した。実Entraや実Cosmosの成功証拠ではない。.NETでは失効、送信直前の再認可、同一keyの辞退・退会再送、台帳・タイムアウト・復旧・測定を含む。CLIは隔離Local storeのみで、停止証跡は明示したダミーである。

初回E2Eの失敗は履歴として保存。候補fixtureと入力の不一致、requester所有の参加recordの誤照合、直接GETとpollのフェンス競合、公開要約と課題タイトルの混同、根拠必須編集のselectorを修正した。サーバーの根拠・同意条件を緩めず、該当テストを再実行した。

## 未完了と再開条件

自由文の既知識別子検査は完全な匿名化ではなく、capabilityは `freeTextAnonymized=false`。実JWT/実Cosmos/複数replica・通知・ログ・保存内容を含む匿名性、未観測の認可設定変更と復元を捕捉する単調な認可版、部署カテゴリの権威ある正本/APIは未完了。新規部署metadataはnullとし、既存の認可済み不透明tokenだけを扱う。

実モデル品質、承認済みgold、認証済みwarm/cold、全SDK retry・AI usage・保存/索引/runtime/ログ/転送/請求の統合、全Azure月額8,000円との比較、実Job・バックアップ・独立停止/復元は未実施。過去の短時間起動方式は再試行していない。最新台帳・累計runtime・価格/FX・費用上界・private復元snapshot・独立停止担当と本人ブラウザー準備を先に整え、親が具体的範囲の承認を得る必要がある。

既存Local fixtureの所有者修正を上書きせず、実画面確認には新しい隔離 `Data:LocalPath` を指定する。[保存確認](preservation.json)で旧checkout59ファイル・旧UI成果物・前段成果物の計66項目のhash一致、元リポジトリcleanを確認した。実画面用に起動したAspireと専用Chromeを終了し、[停止確認](local-stop.json)で5173/5180のlistenerがないことを確認する。

## 実画面の証跡

Macの実Google Chromeを通常ウィンドウで開き、PC1440×1000・スマホ390×844で全5メニューとマイページ、分析根拠、匿名活動、片方の同意、双方同意、撤回の各画面を保存した。22画面すべてdocument幅はviewport内、pageerrorは0。保存画像を目視し、メニュー・カード・フォームの折返し、匿名ラベル、双方同意時だけの表示名、撤回後の消去を確認した。データは隔離した架空Demo/Mockであり、実社員・実モデル品質の証跡ではない。

| 画面 | PC | スマホ |
| --- | --- | --- |
| ホーム | [画像](screenshots/pc-home.png) | [画像](screenshots/mobile-home.png) |
| 自分の課題・相談 | [画像](screenshots/pc-tasks.png) | [画像](screenshots/mobile-tasks.png) |
| 参加依頼 | [画像](screenshots/pc-invitations.png) | [画像](screenshots/mobile-invitations.png) |
| 活動ルーム | [画像](screenshots/pc-rooms.png) | [画像](screenshots/mobile-rooms.png) |
| 事例・ナレッジ | [画像](screenshots/pc-knowledge.png) | [画像](screenshots/mobile-knowledge.png) |
| マイページ | [画像](screenshots/pc-profile.png) | [画像](screenshots/mobile-profile.png) |
| 分析根拠 | [画像](screenshots/pc-analysis-evidence.png) | [画像](screenshots/mobile-analysis-evidence.png) |
| 匿名会話 | [画像](screenshots/pc-room-anonymous.png) | [画像](screenshots/mobile-room-anonymous.png) |
| 片方のみ同意 | [画像](screenshots/pc-identity-one-consent.png) | [画像](screenshots/mobile-identity-one-consent.png) |
| 双方同意 | [画像](screenshots/pc-identity-shared.png) | [画像](screenshots/mobile-identity-shared.png) |
| 撤回後 | [画像](screenshots/pc-identity-revoked.png) | [画像](screenshots/mobile-identity-revoked.png) |
