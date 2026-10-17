# Issue #35 採用判断用パケット（2026-10-17）

保存済みの#30〜#34の資料と候補設定を固定し、未達・不明・人による判断待ちを一覧にするオフラインコンパイラを追加した。コードのマージと本番採用・展開を分離し、出力は常に `decision=pending-human-decision`、`adoptionStatus=not_adopted`、`productionApplicationAllowed=false`。今回のコードで自動的に採用・受入・展開を許可する経路はない。

## 再現

```sh
python3 scripts/issue35_adoption_packet.py \
  --manifest docs/validation-results/20261017-issue35-adoption-readiness/manifest.json \
  --root . --output /tmp/issue35-new-packet.json
```

PR41のオフライン変換器を再利用する。6資料のpath/SHA256を照合し、root外・symlink経由の範囲外参照、同一資料流用、hash違いを拒否する。#30保存観測を再変換した記録が#34資料と完全一致し、候補source commit/datasetも一致しなければ出力しない。既存出力は上書きせず、異常時はexit2と固定エラーだけで入力値・任意の例外を出さない。

## 現在の判断資料

| 項目 | 記録した状態 | 残条件 |
| --- | --- | --- |
| #30 | warm gate failed、cold HTTP 2/3 | 初回込みp95未達、cold02欠落、UI品質・総合受入pending |
| #31 | 歴史的保存応答の比較1条件、誤推薦のあった固有質問1件 | 現候補の独立条件比較ではない。過去の失敗を消さない |
| #32 | 人承認goldなし・意味的一致未確定 | JSON妥当性や検索Recallで構造化90%を代替しない |
| #33 | ローカル/CI成功の記録 | 実JWT・実Cosmos・複数replica受入pending |
| #34 | incomplete、monthlyGate pending | 操作別RU・全AI・保存/索引・runtime・請求不明 |
| 候補設定 | source/datasetと設定文書のhashを固定 | コード既定値の候補であり、有効な配置設定は未照合 |

候補のモデル・prompt版/実装hash・根拠量・分類/正規化/検索規則・人物/事例上限・救済枠を [候補設定](../../evaluation/issue-35/candidate-settings-20261017.json) に固定した。把握できないrerankモデル版・実prompt hash・有効runtime設定hashはnull。実装ファイルのSHAは保存source commit `743f22f` とbyte照合したが、実promptや配置設定の照合済みとは呼ばない。

月100検索は既存の初期仮定、月8,000円は既存の承認基準を引き継ぐ。全Azure総額と確定請求はnull、未分離項目を列挙し、現在の単価・為替・税/契約・請求を照合したとはしない。価格API・台帳へアクセスせず、部分RUから総月額を外挿せず、新たな支出許可や残額も作らない。

ユーザーが初回遅延の改善を後回しにした判断は、未達の受入承認ではない。入力に別の「承認済み」フラグがあってもそれを採用許可へ変換しない。この版は未完了資料の棚卸しだけを扱い、実受入済み資料や本番採用判断の形式は別のレビュー対象とする。

## 検証と成果物

追加11件、依存統合前の全Python154件、PR39/40/41統合後の全Python241件成功。新CLI行カバレッジ99%（入口1行は親の集計外）。hash改変・候補/dataset不一致・改変#34資料・root逸脱・fixtureの実受入への付替え・不正候補設定・出力保護・本番適用不可のschema不変条件を検証した。新しい実AI・Azure計測・台帳照合・遅延改善は0。

[manifest.json](manifest.json) は6資料の参照とhash、[adoption-packet.json](adoption-packet.json) は生成結果、[issue33-local-receipt.json](issue33-local-receipt.json) はPR40時点の検証記録。[schema](../../evaluation/issue-35/adoption-packet.schema.json) は未採用・費用null・本番適用禁止の不変条件を検証する。入力資料の型・候補と資料のbindingはコンパイラが別途検証する。保存品質reportを新規測定へ付替えず、#35/#14/#16を自動完了にしない。

[replay-manifest.json](replay-manifest.json) に生成器・再利用変換器・schema・入力manifest・出力のSHA256を保存した。別の一時出力へ再生成し、保存パケットとのbyte一致とschema検証を確認した。
