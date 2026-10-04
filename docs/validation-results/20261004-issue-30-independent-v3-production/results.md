# Issue #30：独立v3の実サービス評価と再開地点

新規承認済み24問・60原本を、固定した実装と既存AzureのJev／Luna6／Cosmosで評価した。48検索（全対象・対象指定）が完了し、**サービス品質ゲートは合格**。認証付きHTTP・画面・coldの新規計測は未実施で、Issue #30の完了・恒久設定採用は保留する。

## 独立性と変更内容

[原文・ラベル](../../../evaluation/issue-30/independent-v3/review.md)と[新規承認記録](../../../evaluation/issue-30/independent-v3/approved/approval.json)を参照。新しい質問は正解あり20問・正解なし4問、10シナリオ。各質問は直接経験grade3／補助経験grade2／負例grade0で固定した。ラベルは過去の承認から継承していない。合成ダミー集合であり、実務代表性や統計的独立性は未検証。

分類プロンプトで業務の目的・対象を判定するよう修正し、対象外を`CLASSIFICATION_OUT_OF_SCOPE`として障害と区別した。UIは人物・事例を別欄に表示し、対象外で古い検索条件を再送しない。原本の参照を同じ検証フェーズ内だけで共有し、AI処理後の訂正・撤回確認は新しく読み直す。分類応答の不一致は限定した検証タグで記録し、検証を緩めない。

これらの実装・モデル・検索設定・ラベルはv3実行前にhash固定した。v3の結果を見た調整は行っていない。v2に対する[観測後回帰](../20261004-issue-30-regression/results.md)とは別の結果として扱う。

## 結果

| 指標 | 全対象：人物／事例 | 対象指定：人物／事例 |
|---|---:|---:|
| 一次Recall | 100%／100% | 100%／100% |
| 種別欄HitRate@5 | 100%／100% | 100%／100% |
| 種別欄Precision@5 | 100%／100% | 100%／100% |
| 種別欄Recall@5 | 100%／100% | 100%／100% |
| 正解なしの誤推薦 | 0/4 | 0/4 |
| 根拠ID・原文引用の整合 | 60/60 | 60/60 |

指定複数職歴の補助正解取得漏れ、上限打切りによる正解損失、対象種別違反、検索失敗は0。正解なしのうち2問は全件不適合、残り2問は分類対象外で検索未実行。種別別表示による指標を、以前の混在先頭5件のPrecisionと混同しない。

[全応答・取得診断](production-report.json)、[再採点](summary.json)、[gzip分割出力の照合](service-export-provenance.json)。CLI再現は有料推論を行わない：

```sh
python3 scripts/issue30_independent_v3_report.py \
  --capture docs/validation-results/20261004-issue-30-independent-v3-production/production-report.json \
  --output /tmp/issue30-v3-summary.json
```

## HTTPの残課題と停止状態

Macが再ロックされ、Entra認証済みChromeを操作できなかった。解除依頼後もロック状態を確認したため、APIを再展開せず停止した。v3のHTTP成功件数・p95・cold測定値は未測定。旧v2のp95 18.622秒は未達の履歴であり、v3の合格根拠に転用しない。

[復帰確認](restoration.json)でAPI・Workerの元イメージ、環境変数、公開scope、CPU／メモリー、scale、および検証Jobの元イメージ・環境・引数の一致を確認した。実レプリカ0、実行中Job0。検証用APIイメージは既存ACRへpush済みだが未展開。新規リソース・RBAC・クラウド作業環境の作成は行っていない。

残る手順は[引継ぎ](handoff.md)。Draft PRはレビュー用の区切りであり、マージ・恒久展開は別判断。

## 費用と検証

原本分類70＋検索68＝新規138モデル試行、キャッシュ54件、推計3.270987036円。週次台帳は703試行、固定162円/USDで20.119735476円。非runtime予約1,200円を含む保守集計1,220.119735476円は週2,000円内。実請求は未照合。[費用・累計時間](cost-final.json)。

.NET368、Python/Bicep104、検索Playwright14、認証unit4、OpenAPI契約17、評価fixture5が成功。ロックファイル復元・ソリューション/UIビルド成功（既存ASPIRE010警告）。[検証記録](checks.json)。原本引用との整合は意味的なLLM品質全般を証明しない。
