# Issue #34 保存観測のオフライン変換（2026-10-17）

`scripts/issue34_saved_measurements.py` は保存済みの数値観測を既存 `issue34-measurement-v1` schemaへ変換する。collector・監視基盤・段階別計測は追加せず、Azure・実AI・台帳・価格APIへのアクセスも行わない。#30/#33の実環境受入がpendingであることを保持し、#34を完了扱いにしない。

## 再現

```sh
python3 scripts/issue34_saved_measurements.py \
  --capture docs/validation-results/20261017-issue34-saved-evidence/capture.json \
  --output /tmp/issue34-new-report.json
```

出力先は新規ファイルに限る。既存ファイル・入力自身・symlinkの置換は拒否し、元のbyteを保持する。構造不正はexit2・固定エラーだけで、入力値/パス/例外詳細を出さない。exit0は変換成功であり、SLO・総合測定・採用の合格ではない。

| 対象 | 変換後の観測数 | p50 / p95 | 判定 |
| --- | ---: | --- | --- |
| warm全応答（初回・対象外422を含む） | 24 | 10.063253 / 17.403440秒 | 15秒未達 |
| 固定した実検索母集団（対象外2問を除く） | 22 | 10.303480 / 18.002211秒 | 15秒未達 |
| cold HTTP | 2/3 | null / null | cold02欠落、report_only |

全24問と実検索22問は凍結datasetのIDで固定し、HTTP200だけを選び直して失敗を隠さない。誤って対象外問が200になっても22問の分母は変わらず、正常合格にはしない。両起動窓の初回も含む。p50/p95は位置 `(n−1)×p` の線形補間で入力の集計値・合格フラグを信用せず再計算する。時刻欠落のある母集団のp50/p95はnullにし、部分集合の統計を全件の統計に見せない。

coldは「停止・再有効化後の初回検索」で、起動待ち包含は未確認。UI完了や応答品質はこのCLIの再検証対象ではない。cold02のサーバ内部時間や他runの証跡をHTTP時間へ借用しない。初回遅延の追加計測・最適化は、ユーザーの06:06:41 UTCの判断どおり既知課題として後回しにする。

操作別RU・AI token/失敗留保・保存/索引・runtime・全費用・請求照合の証拠は今回のcaptureにはない。`operations/storage` は空、設定/サービス状態hashと費用総額はnull、`state=incomplete`・`evidenceKind=offline_replay`・`monthlyGate=pending`を維持する。過去の投影RUだけで総RUや月8,000円以内を判定せず、集計値からservice request/correlation IDも作らない。

## 検証と出典

追加回帰15件、全Python143件成功。入力不正・重複question/sequence/response runId・warm/cold間の応答流用・真偽値/非有限/負の時間・HTTP矛盾・欠落cold・既存出力保護・原子的書込失敗・既存schema適合を検証した。新CLIの行カバレッジ99%（未集計1行は子プロセスで実行した`__main__`入口）。既存.NET全586件も成功。追加回帰はfixtureであり、新しい実AI品質測定ではない。

[capture.json](capture.json) はPR39の公開数値要約をbyteのまま保持した入力。[measurement-record.json](measurement-record.json) は変換結果。[replay-manifest.json](replay-manifest.json) に元の公開commit URL、入力・dataset・generator・出力のSHA256を固定した。raw HAR、応答本文、private ARM snapshot、ブラウザ保存情報は含まない。保存27観測を新しい27実測として数えず、元観測の出典と変換処理を区別する。
