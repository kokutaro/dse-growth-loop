# 再抽出の実装識別・Excel日付・技能候補入力

## 概要

再抽出が過去の失敗世代へ重複判定される問題、Excel ISO日付が不明になる問題、Choiceの重複入力による送信前保留を修正する。権限・原本・旧世代・使用量・人の承認境界は維持する。ロック追跡は [保持元の計測](fence-owner-trace.md) を参照。

## 実装識別と受付

`Directory.Build.targets` は共通のDomain/Infrastructureの実ソース、project/lock、SDK設定、共通props/targets、コンパイル条件をSHA256で識別し、InfrastructureのAssemblyMetadataへ埋め込む。APIとWorkerのDocker buildは同じ入力集合とtargetsを使用する。Git情報のないDocker内でも`1.0.0`へ戻らない。絶対パスや手動の再試行番号を識別子に使わない。モデル・価格・入出力等の実行設定は従来どおり別の対象fingerprintに含める。

メタデータ欠落・重複・不正は`BUILD_IDENTITY_UNAVAILABLE`で拒否。同一の原本・処理内容・設定は同じ世代へ戻り、旧履歴・消費済み予算を補充しない。API/Workerの処理識別が異なる世代はWorkerの既存チェックで公開しない。build manifestは`obj/<Configuration>/net10.0/dse-processing-inputs.txt`で確認できる。

見積の`availability`、受付の`acceptance`は`new / existing_success / existing_failed / existing_pending / already_applied`を区別する。UIは明示的な`new`以外で新規費用の実行を表示しない。既存失敗を新規受付と表示せず、そのJobへ新しい通知も送らない。旧レスポンスで状態が欠ける場合は再送せず再確認する。

## Excel日付

型付きセルのISO日時は小数秒、UTCのZ、明示offsetを読む。Excelの表示対象である記載上の年月日を保持し、UTCやサーバー時刻への変換で前日・翌月へずらさない。表示書式の月/日精度を用い、数値セルの1日というだけで日精度を捏造しない。日付だけ・明確な年月文字列の既存契約を維持し、曖昧・無効・キャッシュなしはunknown。継続中の月数を今日まで補完しない。月端点は終了月を含み、日単位の既存計算と区別する。

## Choice入力

`skill-choice-wire-v2`では全候補を維持し、完全一致する定義・境界・別名の行だけを`dictionaryRows`へ共有する。名前は同じ短縮IDのcriteria全文。根拠の全id/textを保持し、重複するdocumentVersionId/sourceVersionだけを`evidenceDocuments`へ共有する。本文の要約、助詞分割、末尾切捨て、候補数制限の引下げ、入力上限の引上げは行わない。

wire版・固定辞書・候補集合は提案IDとAI入力hashへ反映し、旧wireの結果を新しい評価へ流用しない。旧wire/v1方式の提案は閲覧履歴として残す。空辞書は新規候補、容量超過は理由付き要確認、未知IDや不正な応答は要確認。Choice確率は参考値で、自動承認には使わない。本番の辞書版0と、検証用50技能辞書は別物である。

## 適用上の注意

API/Workerを同じ検証済みbuild条件で構築・配備する。過去generationや検索headは削除・初期化せず、新世代の成功時だけ既存の原子的公開で切り替える。大きな辞書や長文には引き続き理由付き保留があり、50技能での少数検証は一般品質保証ではない。未配備の修正によって本番の通常UI→Queue→Worker経路を検証済みとは扱わない。
