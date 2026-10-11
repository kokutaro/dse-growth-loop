# Issue #31 / #32 のオフライン品質確認

2026-10-11、Mac mini、基準 main `78f182a`。今回の新規AI送信・Azureアクセス・課金操作は0件。fixtureの成功を実AI品質に置き換えない。

## #31：保存再順位応答の再集計

`python3 scripts/offline_quality.py rerank` は既存 .NET `RerankComparisons` が候補hash・根拠ID・引用を検証した保存評価結果と、同じ質問・候補hash・モデル・プロンプト・根拠長・Probability提示・順番・反復番号の生応答を照合する。dataset SHA-256、各入力ファイルSHA-256、K3/K5の順位・成功状態を一致確認し、欠落・重複条件は拒否する。候補hashの再算出と根拠検証は既存.NET評価器が担当する。このPython集計はその代替ではなく、保存された検証結果から条件別の分析表を作る。

```sh
python3 scripts/offline_quality.py rerank \
  --scored docs/validation-results/20261003-issue-14-luna6/scored-report.json \
  --responses docs/validation-results/20261003-issue-14-luna6/comparisons.json \
  --dataset evaluation/expansion-v2/dataset.json \
  --out .artifacts/offline-quality-rerank-new-run
python3 scripts/offline_quality.py faults --out .artifacts/offline-quality-faults-new-run
```

最新コードの.NET評価器で168行の一次候補と全再順位を再検証した [dotnet-replay/report.json](validation-results/20261011-offline-quality/dotnet-replay/report.json) から作った集計は [rerank-current/report.json](validation-results/20261011-offline-quality/rerank-current/report.json)、表は [rows.csv](validation-results/20261011-offline-quality/rerank-current/rows.csv)。既存の歴史的scored-reportからの初回集計も `rerank/` に保存し、比較順位・指標が今回の再検証と一致することを確認した。保存された実応答69回と空候補の3条件、24質問、original / reversed / original反復を再生した。新規実行ではなく2026-10-03の実応答の再分析である。

- 実応答の条件は `gpt-6-luna-2026-09-22` / `rerank-eval-v1` / 根拠1,000 bytes / Probability非提示の1組。別モデル・プロンプト・根拠長・Probability提示条件を比較したことにはならない。非AI一次順位は別のbaselineであり、別モデルと数えない。
- original、K5のHitRateは人物10/10、事例9/10、合計19/20。正解ありの分母20と全質問24を区別する。Precisionは表示0の質問をnullとして分母を残す。
- `v2f-software-04-query`（二重請求）でoriginalと反復は空、reversedは `v2f-software-04-knowledge-negative` を推薦した。逆順一致は23/24、同順反復は24/24。正解あり質問の非該当推薦を `falsePositiveItems` / `falsePositiveCases` に出した。従来の「正解0の質問の誤推薦率0」と両立するため、後者だけで安全と判断しない。
- 保存成功応答の使用量は入力65,763、出力8,836、cached 7,138、reasoning 0 tokens。今回の使用量は0。保存成功69回だけを見て失敗0としない。旧 `final-summary.json` のHTTP429 130回・中断1件・失敗予約 `$0.2830`、推定総額 `$0.29335188` を併記して利用する。請求額との照合は未完了。複数captureディレクトリは同じ試行を含むため合算しない。

[fixed-faults/report.json](validation-results/20261011-offline-quality/fixed-faults/report.json) は、既存の再順位送信プロトコルへローカルの固定callbackだけを渡す10条件の回帰。空成功、JSON不正、重複キー、架空候補ID、架空根拠ID、根拠外引用、拒否、truncation、HTTP429、HTTP401を区別する。JSON解析失敗だけ最大4試行（追加3回）、その他は1試行で停止する。実際のネットワーク障害や実モデルの耐性は測っていない。truncationは失敗のまま保持し、空成功にしない。

追加比較は全組合せに広げず、同じ一次候補hashで1軸ずつ少数条件を事前固定する。比較に使うモデル版、prompt/hash、根拠長、Probability提示、対象質問、原順・逆順・反復、最大試行数、トークン予約、課金失敗分、停止方法を承認後に固定する。既存保存応答と同条件の正常応答は再送しない。品質採用は独立確認集合を別に用意してから行う。二重請求の逆順誤推薦は必須回帰条件として残す。

## #32：主要項目の独立semantic scorer

確認対象案は現行プロフィール取込の主要項目に合わせた8項目。対象項目・分母・同義表現・独立分割は人による承認待ち。`description`（本人の担当内容）、`start`、`end`、`precision`、`months`、`skills`（正規化技能集合）、`domain`（分野ID）、`evidenceIds`（根拠集合）。期間は現行 `ExperiencePeriod` の日付/精度/重複排除月数定義に従い、Probabilityを経験年数や本人担当に変換しない。業務・人物・事例で適用不能な項目は人が明示する。分類ProbabilityのJSON妥当性、Office XMLの文字一致、根拠ID妥当性、検索Recallはこの意味一致率の代替にならない。

[レビュー草案](validation-results/20261011-offline-quality/semantic-review-draft/report.json) は入力原文・source SHA-256・tuning/finalを含むが、全項目 `unreviewed`、承認pendingである。AIから正解ラベルを生成していない。検索用に承認済みのgrade/関連度を、主要項目の人承認に流用しない。

scorer入力契約：

```json
{
  "version": "offline-quality-v1",
  "fields": ["description", "start", "end", "precision", "months", "skills", "domain", "evidenceIds"],
  "approval": {
    "status": "human-approved",
    "reviewer": "実際の確認者",
    "reviewedAt": "実際の確認日時",
    "evidenceReference": "確認記録の参照",
    "recordsSha256": "records をUTF-8/キー順/compact JSONにしたSHA-256"
  },
  "records": [{
    "id": "職歴単位ID", "split": "final",
    "sourceSha256": "原文SHA-256", "configSha256": "期間/技能/分類/取込設定SHA-256",
    "modelVersion": "使用モデルの固定版", "promptVersion": "固定prompt版",
    "expected": {"description": {"state": "known", "value": "本人が実施したこと", "acceptedValues": ["人が承認した同義表現"]}}
  }]
}
```

全8項目を明示する。`known` は値と、人が列挙した意味的に同じ表現のみを許す。自由文はNFKC・空白正規化後にその候補と照合し、AI judge/embeddingで正誤を生成しない。期間・分類は型を含め固定値照合。技能・根拠IDは重複なし集合一致であり、余分な値も誤り。

- `unknown` は明示nullのみ一致。未出力・未記載・勝手な推測は誤りにして分母へ含める。
- `not_applicable` は人が明示して分母から除外。項目すべてが除外なら達成率nullで合格しない。
- `unreviewed` は達成率へ含めず、未確認件数を別計上し、gateを停止する。
- 保存応答の `origin=recorded-structured-responses`、各recordの `sourceSha256` / `modelVersion` / `promptVersion` / `configSha256` がgoldと一致する場合のみ再利用可能。欠落応答・不一致は誤りにして分母へ含める。source/configは64桁SHA-256形式を要求する。これらの同一性は入力契約の確認であり、モデルの実送信履歴の真正性は保存manifest/承認記録から別に確認する。
- 人承認にreviewer・日時・参照・records hashを要求する。ラベルを書き換えたら承認hashが無効になる。全recordの確認と保存応答の再利用が揃い、finalの全体と各8項目でそれぞれ90%以上のときだけ `gatePassed=true`。fixture originでは常にfalse。表と失敗例はJSON/CSVに出る。

予測入力は `origin` と `records` のJSONで、各recordはgoldと同じID/4bindingsと `fields`（8項目の値）を持つ。既存JEV保存応答はdomain/Noul分類、Foundry保存応答はrerankなので、8項目の構造化予測として扱えない。[現在のreadiness](validation-results/20261011-offline-quality/semantic-readiness/report.json) は、意味精度null・人承認false・gate falseを保存している。

```sh
python3 scripts/offline_quality.py draft --dataset evaluation/expansion-v2/dataset.json \
  --out .artifacts/semantic-review-new-run
python3 scripts/offline_quality.py semantic --gold <人が確認した項目別gold.json> \
  --predictions <同条件の保存構造化応答.json> --out .artifacts/semantic-score-new-run
python3 -m unittest discover -s tests/scripts -p test_offline_quality.py -v
```

残りは、8項目の人による原本照合・独立したtuning/final分割と分母の確認、gold/ゲートの事前固定、再利用可能な構造化保存応答の収集（なければ別承認済み少数実AI）、項目別90%と失敗例の評価。goldのhuman-approvedという自己申告だけを本番の承認としない。実際の確認記録をレビューで照合する。今回は#31/#32をcloseできる品質達成の主張をしない。
