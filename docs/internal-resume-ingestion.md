# 社内職務経歴表のXLSX取り込み

2026-10-24。共有された帳票構造を架空データで再現し、既存の本人アップロード・抽出・案件別分類・検索投影・本人訂正へ接続した。実Google Sheets書き出しファイルの読取や、実データのAI送信・AI品質評価は実施していない。

## 入口と確認方法

Google スプレッドシートの「ファイル → ダウンロード → Microsoft Excel（.xlsx）」で書き出し、既存画面の「職歴・スキル」から登録する。入口は既存 `POST /api/v1/documents` のmultipart（title、documentType=profile、file）。対象者・部署・閲覧範囲は本人のサーバー認可で決まる。

Google Drive URLの直接取得・同期、OAuth追加、元シートの変更は実装していない。XLSXの保存値を読み、数式を再計算しない。数式キャッシュ欠落時は警告し、明示された開始・終了月から独立に月数を算出する。

文書画面の「社内形式の抽出候補（本人未確認）」で案件年月・月数、技術の元表記・正規化候補・記載欄・根拠を確認できる。日付なしの自己申告は別表示する。プロフィールを更新すると案件別経験が表示され、年月入力で訂正できる。本人確認は既存の本人操作だけで行い、正解ラベルの承認とは別である。

## 認識する帳票構造

対象タブは `02. スキル記入シート(例つき)`。他タブ・セルノート・説明サンプルを経験に含めない。連続2行から案件／業務内容、期間／開始、使用言語、要件定義の見出しを検出する。案件開始は見出し後のA列の番号とF列の `■` タイトルで検出し、固定23行目や7案件だけには依存しない。

先頭行rの4行ブロックは次のとおり。

| 位置 | 扱い |
|---|---|
| A[r] | 案件番号。`例` を除外。重複番号・不完全ブロックはエラー |
| B[r] / E[r] | 開始月／終了月。日精度を主張しない |
| F[r] / F[r+1] | タイトル、区切り・改行付き担当業務。習得技能は候補。コメントは元セルに保持し、経験説明・分類用本文から除外 |
| H[r] / H[r+1] | 本人役割、チーム構成・規模 |
| I / J / K / L の先頭行 | 使用言語、DB、サーバOS、FW・MW・ツールの記載ヒント。混在を許し、確定分類・習熟度としない |
| M:Sの先頭行の `●` | 要件定義／基本設計／詳細設計／実装・単体／結合テスト／総合テスト／保守・運用。空欄を経験なしと断定しない |
| B[r+3] | 保存済み月数表示。独立算出値と異なれば警告 |
| D6 / D7 / J5 / D51 | 得意技術／得意業務／資格／自己PR。日付なしの自己申告として別保存し、案件経験・経験月数へ加算しない |
| 雇用歴表 | 案件実績と二重計上しない。雇用歴の別正本化は今回の対象外 |

自己申告の4セルと案件の列・4行構成はこの帳票の明示仕様であり、任意帳票の推定機能ではない。対象タブの見出し欠落・案件欠落・不完全案件は `INTERNAL_RESUME_HEADER_MISSING` / `INTERNAL_RESUME_NO_PROJECTS` / `INTERNAL_RESUME_INVALID` で停止し、汎用の全行経験化へ戻さない。対象タブを持たない通常のWord・Excelは従来経路を使う。

## 保存される8項目と根拠

`GET /api/v1/documents/{id}` の `resumeImport.projects[]` に抽出候補を返す。文書と同じ認可境界を使い、新しい公開APIは増やさない。

| 項目 | 保存内容 |
|---|---|
| description | タイトル＋担当業務の区切り・改行、役割／規模／明示工程、技術欄の記載 |
| start / end | `YYYY-MM`。現在・空欄はnullで、基準日を補わない |
| precision | `month` |
| months | 開始月と終了月を含む機械計算。いずれか不明ならnull。逆転は両端null＋警告とし元セルを保持 |
| skills | 根拠付き技術候補の正規化表記集合。承認済み技能・習熟度ではない |
| domain | null。業務領域の正式分類がなく、技術カテゴリから業界・業務分野を推定しない |
| evidenceIds | 元文書版に属する案件セル範囲のEvidenceSpan ID |

`skillCandidates[]` は元表記・正規化候補・列ヒント・元セル位置・`needsReview=true`、`sourceCells[]` は各案件セルの本文と位置、`warnings[]` は不明期間や月数不一致を保持する。業務領域の確認に使うタイトル・担当業務も保持するが、既存の技術分野分類 `decisions.choices.domain` と業務領域の正解ラベルは別物である。

表記正規化は Mongo DB→MongoDB、Postgre SQL→PostgreSQL、React.Js→React、NodeJS→Node.js、Asp .Net Core→ASP.NET Core、EF Core→Entity Framework Core。NFKC、改行・区切り分割に対応し、元表記とバージョンを残す。.NET Framework、.NET、MAUIは別候補のまま。未知語・誤字を勝手に補正しない。「希望／今後／予定／未経験」を含む候補は実績技能集合から除外して確認用に残す。自由文の希望・実績を意味的に完全判定する機能ではない。

根拠は案件ごとに `xl/worksheets/... / タブ名!A23:S26` のようなセル範囲を持つ。本文内のセル番地・保存値、技能候補の個別セル／結合アンカー位置を保持する。EvidenceReferenceから文書版・原本hash・Documentへたどれる。結合範囲を複製しない。根拠数上限を広げる代わりに案件範囲へまとめ、自己申告欄は別のセル根拠とする。

## 期間、検索、再取り込み

新しい年月表現は `2020-01`〜`2020-03`、precision=month、months=3。既存の `YYYY-MM-DD` は終了日を含まない経過月数を維持する。人物合計は年月を内部の半開区間へ変換して和集合を算出し、並行案件を二重加算しない。不明期間を含めば合計もnull。

社内形式だけからなる人物は案件ごとに分類・検索投影する。人物全体の再分類は全根拠の再連結が既存AI入力上限に当たるため行わず、分類分布を合成もしない。人物への候補統合は従来のRetrieverが担う。技術候補は `normalizedTerms` に追加し、既存 `skillIds` の分類体系と機械ID契約は維持する。通常のWord／汎用Excelを含む人物の全体分類は従来どおりで、入力上限超過なら確認待ちになる。

新規経験・分類は `ai_unconfirmed`。所有者・文書種別・文書名・案件番号で経験IDを安定化し、行移動・古い順／新しい順の変更でも同番号の本人訂正を維持する。番号や文書名の変更は別の同一性になる。相違は既存 `pendingExtraction` に保留し、訂正済み内容を上書きしない。旧ジョブ排除・根拠版・ETag・公開撤回の照合と原子的公開は既存フローを使う。

処理版は `openxml-2`。旧処理版の取得中ジョブは `PROCESSOR_VERSION_UNSUPPORTED` となる。公開時にはAPI/Workerの版と停止・再受付手順を確認する。同ファイルも新処理版として受付できるが、旧汎用形式の本人確認・訂正済み断片を新形式の案件へ自動対応付けはしない。

既存上限の原本10 MiB、展開50 MiB、根拠100、経験20、連結本文8,000文字、AI入力・出力上限を維持する。社内形式では対象タブ非空セル4,096、案件内技能候補100・検索用の1候補100文字で明示停止する。長い習得文章は確認用原文へ分け、技能語として検索に追加しない。範囲本文8,000コードポイント超過や上限超過で一部だけを公開しない。

## Issue #32と実行境界

[Issue #32](https://github.com/kokutaro/dse-growth-loop/issues/32) は未完了。別シートの120件・8項目のAI仮入力は全件人が未確認で、正解承認はない。今回、そのシートを取得・変更・再承認していない。[評価手順](offline-quality.md)の `unreviewed`、approval pending、原文・設定hashとモデル／プロンプト版の条件を維持する。本人確認、機械抽出、根拠整合、mock回帰を意味的一致率90%達成と扱わない。

社内形式の評価を後続で行う場合、両端月・未知値・技術候補・業務領域の定義と形式処理版を設定hashへ固定し、人の確認記録と独立分割を用意する。旧の日精度や経過月数の正解を新しい月表現へ自動承認しない。

基点は `b1a77a528a928d04df34f54c834f6fc6db12ab60`。作業開始時の追跡変更はなし。Git fetchは接続プロキシ503で未取得だったが、GitHub接続ツールで最新履歴SHAと `main` のProfilePipeline blobを照合した。ローカルの `origin/main` 参照はない。

マージ、デプロイ、Azure設定変更、実データのAI外部送信、元シート更新は実施していない。push／PR作成を含む公開は未実施で、次に公開が必要な段階として別途扱う。

## 再現と検証

`web/tests/fixtures/internal-resume.xlsx` は59行×26列・3タブ・7案件を架空本文で再現したもの。実在職歴・人物名・顧客情報は含まない。`python3 web/tests/fixtures/generate-internal-resume.py` で決定的に再生成できる。.NETの `InternalResumeFixture` は行移動・順序・異常値をメモリ上で生成する。

通常の.NET 10／Node環境で既存テストを実行する。この作業環境では `source /workspace/.setup/dse/env.sh`、契約用Pythonは `/workspace/.tools/dse-contracts-venv/bin/python` を使う。

```sh
dotnet test tests/Dse.Tests --filter 'Category!=LargeEvaluation' --no-restore
python3 -m unittest discover -s tests/contracts -v
python3 -m unittest discover -s tests/scripts -p test_offline_quality.py -v
npm --prefix web run build
npm --prefix web run test:e2e -- tests/profiles.spec.ts --workers=1
```

ローカル結果と証跡の場所は [実装状況](implementation-status.md) の2026-10-24項を参照。試験は架空データ・Mock限定であり、実AI品質を主張しない。
