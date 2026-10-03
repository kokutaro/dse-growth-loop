# 追加質問・正解ラベルのレビュー（承認済み）

2026-10-03、リポジトリ所有者が本レビュー票の追加質問・正解ラベル案をレビュー済みとして明示承認した。既存evaluation-v1への承認とは別の承認。全件架空、ラベル案の作成者はAI。datasetはevaluation-v2／human-approvedとして固定する。

調整・確認それぞれ正解あり20問（人物10／事例10）＋正解なし4問（各2）。各分割は5分野×4課題、異なる課題群・原本・人物で構成。確認集合の語句から閾値を選ばない。

関連度は3=直接、2=共同実施／参考事例、1=周辺参考、0=要求を満たす記録なし。各質問で同種別の30候補すべてをdataset.jsonに仮判定した。各課題の直接候補と補助候補を正解案とし、同じ語があるだけの案内・会議記録は非該当案。他の課題群も原本を見て、横断して該当する候補が漏れていないか確認する。特に補助候補を2と扱うかは人による判断が必要。

単なる件数増加や同じ質問の言い換えを独立20問として数えない。既存v1からの流用は0。すべて合成のため、実務代表性と問間の依存は別途確認が必要。

[全原本](sources.md)／[全候補の暫定判定](dataset.json)

## 正解ありの質問と正解案

### v2t-software-01-query

- 分割：tuning／種別：person／分野：ソフトウェア
- 質問：外部通知が一時的に失敗しても、時間を空けて再送できる仕組みを作った人を探したい。
- 直接候補（3）：`v2t-software-01-person-direct` — 通知APIの開発で、一過性の通信エラーを識別し、指数バックオフと送信回数上限を実装した。失敗を再現するテストも作成した。
- 補助候補（2）：`v2t-software-01-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-software-01-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-software-01-person-direct-evidence)／[補助](sources.md#v2t-software-01-person-support-evidence)／[非該当](sources.md#v2t-software-01-person-negative-evidence)

### v2t-software-02-query

- 分割：tuning／種別：person／分野：ソフトウェア
- 質問：サービスを止めずにデータベースの構造を変更した経験がある人に相談したい。
- 直接候補（3）：`v2t-software-02-person-direct` — ソフトウェア開発で、新旧両方の列へ書き込み、既存データの移行後に読み取り先を切り替えた。稼働中の業務システムを停止せずテーブルを変更した。
- 補助候補（2）：`v2t-software-02-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-software-02-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-software-02-person-direct-evidence)／[補助](sources.md#v2t-software-02-person-support-evidence)／[非該当](sources.md#v2t-software-02-person-negative-evidence)

### v2t-software-03-query

- 分割：tuning／種別：knowledge／分野：ソフトウェア
- 質問：同じ人が複数回ログインすると認証状態が消える問題を解決した事例がほしい。
- 直接候補（3）：`v2t-software-03-knowledge-direct` — ソフトウェア運用の解決事例。認証状態が端末間で上書きされる不具合を調査し、セッション識別子を端末ごとに分離して再ログイン要求を解消した。
- 補助候補（2）：`v2t-software-03-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-software-03-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-software-03-knowledge-direct-evidence)／[補助](sources.md#v2t-software-03-knowledge-support-evidence)／[非該当](sources.md#v2t-software-03-knowledge-negative-evidence)

### v2t-software-04-query

- 分割：tuning／種別：knowledge／分野：ソフトウェア
- 質問：一度変えた認証用の文字列にすぐ戻せないようにした実装例を探したい。
- 直接候補（3）：`v2t-software-04-knowledge-direct` — ソフトウェア開発の事例。過去に使用した認証秘密のハッシュ履歴を照合し、同一の秘密への変更を拒否する処理と検証手順を追加した。
- 補助候補（2）：`v2t-software-04-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-software-04-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-software-04-knowledge-direct-evidence)／[補助](sources.md#v2t-software-04-knowledge-support-evidence)／[非該当](sources.md#v2t-software-04-knowledge-negative-evidence)

### v2t-mechanical-05-query

- 分割：tuning／種別：person／分野：機械設計
- 質問：ポンプの軸がぶれて短期間で壊れる問題を、設計変更で直した人を探したい。
- 直接候補（3）：`v2t-mechanical-05-person-direct` — 機械設計で、回転体の偏心と支持剛性を測定した。軸受間隔と支持部の寸法を変更し、振動耐久試験で寿命改善を確認した。
- 補助候補（2）：`v2t-mechanical-05-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-mechanical-05-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-mechanical-05-person-direct-evidence)／[補助](sources.md#v2t-mechanical-05-person-support-evidence)／[非該当](sources.md#v2t-mechanical-05-person-negative-evidence)

### v2t-mechanical-06-query

- 分割：tuning／種別：person／分野：機械設計
- 質問：油圧機器の継ぎ目から液体が漏れる問題を解決した経験者に相談したい。
- 直接候補（3）：`v2t-mechanical-06-person-direct` — 機械設計で、シール溝の加工公差と温度による膨張を解析した。溝寸法と密封材を変更し、高温での漏洩試験に合格した。
- 補助候補（2）：`v2t-mechanical-06-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-mechanical-06-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-mechanical-06-person-direct-evidence)／[補助](sources.md#v2t-mechanical-06-person-support-evidence)／[非該当](sources.md#v2t-mechanical-06-person-negative-evidence)

### v2t-mechanical-07-query

- 分割：tuning／種別：knowledge／分野：機械設計
- 質問：繰り返し荷重でボルトが折れる問題の対策事例がほしい。
- 直接候補（3）：`v2t-mechanical-07-knowledge-direct` — 機械設計の解決事例。締結部の疲労亀裂を破面観察で特定し、初期締付力と荷重経路を見直した。繰り返し負荷による破断が再発しなかった。
- 補助候補（2）：`v2t-mechanical-07-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-mechanical-07-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-mechanical-07-knowledge-direct-evidence)／[補助](sources.md#v2t-mechanical-07-knowledge-support-evidence)／[非該当](sources.md#v2t-mechanical-07-knowledge-negative-evidence)

### v2t-mechanical-08-query

- 分割：tuning／種別：knowledge／分野：機械設計
- 質問：組み付けると部品同士が当たって動かない問題を、図面段階で防いだ事例を探したい。
- 直接候補（3）：`v2t-mechanical-08-knowledge-direct` — 機械設計の事例。公差の累積による摺動部の干渉を解析し、基準面と逃げ寸法を改訂した。量産前の組立検証で干渉を検出しないことを確認した。
- 補助候補（2）：`v2t-mechanical-08-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-mechanical-08-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-mechanical-08-knowledge-direct-evidence)／[補助](sources.md#v2t-mechanical-08-knowledge-support-evidence)／[非該当](sources.md#v2t-mechanical-08-knowledge-negative-evidence)

### v2t-sales-09-query

- 分割：tuning／種別：person／分野：営業
- 質問：案件ごとに値引きの判断がばらつく問題を改善した担当者を探したい。
- 直接候補（3）：`v2t-sales-09-person-direct` — 営業で、案件の粗利と契約規模に基づく割引承認表を作成した。承認者と例外条件を定め、見積の利益率を維持した。
- 補助候補（2）：`v2t-sales-09-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-sales-09-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-sales-09-person-direct-evidence)／[補助](sources.md#v2t-sales-09-person-support-evidence)／[非該当](sources.md#v2t-sales-09-person-negative-evidence)

### v2t-sales-10-query

- 分割：tuning／種別：person／分野：営業
- 質問：販売代理店が新製品を説明できない状況を改善した経験者に相談したい。
- 直接候補（3）：`v2t-sales-10-person-direct` — 営業で、代理店向けの製品説明研修を企画し、実演と理解度確認を行った。商談同席で説明内容を確認し、担当者へ営業資料を引き継いだ。
- 補助候補（2）：`v2t-sales-10-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-sales-10-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-sales-10-person-direct-evidence)／[補助](sources.md#v2t-sales-10-person-support-evidence)／[非該当](sources.md#v2t-sales-10-person-negative-evidence)

### v2t-sales-11-query

- 分割：tuning／種別：knowledge／分野：営業
- 質問：契約更新直前に顧客が離れてしまう問題を減らした事例がほしい。
- 直接候補（3）：`v2t-sales-11-knowledge-direct` — 営業の解決事例。更新失注を顧客別に分析し、利用低下と未解決の問い合わせを早期に検知した。更新前の面談を実施して継続率を改善した。
- 補助候補（2）：`v2t-sales-11-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-sales-11-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-sales-11-knowledge-direct-evidence)／[補助](sources.md#v2t-sales-11-knowledge-support-evidence)／[非該当](sources.md#v2t-sales-11-knowledge-negative-evidence)

### v2t-sales-12-query

- 分割：tuning／種別：knowledge／分野：営業
- 質問：受注後に仕様の認識違いが判明する問題を防いだ商談事例を探したい。
- 直接候補（3）：`v2t-sales-12-knowledge-direct` — 営業の事例。提案段階で顧客要件と対象外範囲を一覧にし、技術担当と顧客が共同確認した。合意書を納入担当へ引き継ぎ、追加要求の扱いを明確にした。
- 補助候補（2）：`v2t-sales-12-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-sales-12-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-sales-12-knowledge-direct-evidence)／[補助](sources.md#v2t-sales-12-knowledge-support-evidence)／[非該当](sources.md#v2t-sales-12-knowledge-negative-evidence)

### v2t-manufacturing-13-query

- 分割：tuning／種別：person／分野：製造
- 質問：似た部品を取り違えて組み立てるミスを防いだ担当者を探したい。
- 直接候補（3）：`v2t-manufacturing-13-person-direct` — 製造で、外観が似た二種類の部品の取り違えを分析した。専用治具と部品照合の工程を導入し、誤組立を検出できるようにした。
- 補助候補（2）：`v2t-manufacturing-13-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-manufacturing-13-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-manufacturing-13-person-direct-evidence)／[補助](sources.md#v2t-manufacturing-13-person-support-evidence)／[非該当](sources.md#v2t-manufacturing-13-person-negative-evidence)

### v2t-manufacturing-14-query

- 分割：tuning／種別：person／分野：製造
- 質問：出荷時に別の製品のラベルを貼ってしまう問題を改善した人に相談したい。
- 直接候補（3）：`v2t-manufacturing-14-person-direct` — 製造で、梱包対象の識別番号と印字内容を読み合わせる工程を設計した。照合不一致では印刷を止め、誤表示による出荷を防いだ。
- 補助候補（2）：`v2t-manufacturing-14-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-manufacturing-14-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-manufacturing-14-person-direct-evidence)／[補助](sources.md#v2t-manufacturing-14-person-support-evidence)／[非該当](sources.md#v2t-manufacturing-14-person-negative-evidence)

### v2t-manufacturing-15-query

- 分割：tuning／種別：knowledge／分野：製造
- 質問：工程の間に仕掛品が積み上がって納期が延びる問題を改善した事例がほしい。
- 直接候補（3）：`v2t-manufacturing-15-knowledge-direct` — 製造の解決事例。工程ごとの処理時間を計測し、律速工程へ人員を再配置した。投入量の上限を定め、滞留数と納期を短縮した。
- 補助候補（2）：`v2t-manufacturing-15-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-manufacturing-15-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-manufacturing-15-knowledge-direct-evidence)／[補助](sources.md#v2t-manufacturing-15-knowledge-support-evidence)／[非該当](sources.md#v2t-manufacturing-15-knowledge-negative-evidence)

### v2t-manufacturing-16-query

- 分割：tuning／種別：knowledge／分野：製造
- 質問：塗装後に表面が剥がれる不良を減らした事例を探したい。
- 直接候補（3）：`v2t-manufacturing-16-knowledge-direct` — 製造の事例。前処理の洗浄濃度と乾燥時間を追跡し、付着不良の原因を特定した。管理条件を統一し、密着試験で再発を確認しなかった。
- 補助候補（2）：`v2t-manufacturing-16-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-manufacturing-16-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-manufacturing-16-knowledge-direct-evidence)／[補助](sources.md#v2t-manufacturing-16-knowledge-support-evidence)／[非該当](sources.md#v2t-manufacturing-16-knowledge-negative-evidence)

### v2t-operations-17-query

- 分割：tuning／種別：person／分野：業務運用
- 質問：問い合わせが誰にも割り当てられず放置される状況を改善した人を探したい。
- 直接候補（3）：`v2t-operations-17-person-direct` — 業務運用で、受付した依頼に担当者と回答期限を付与する手順を設計した。未割当の一覧を毎日確認し、代理担当への振り分けを導入した。
- 補助候補（2）：`v2t-operations-17-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-operations-17-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-operations-17-person-direct-evidence)／[補助](sources.md#v2t-operations-17-person-support-evidence)／[非該当](sources.md#v2t-operations-17-person-negative-evidence)

### v2t-operations-18-query

- 分割：tuning／種別：person／分野：業務運用
- 質問：担当者の異動後も毎月の締め処理を継続できるようにした経験者に相談したい。
- 直接候補（3）：`v2t-operations-18-person-direct` — 業務運用で、月次締めの作業順、照合方法、例外時の連絡先を手順書へ記載した。後任と並走して処理し、単独で実施できることを確認して引き継いだ。
- 補助候補（2）：`v2t-operations-18-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-operations-18-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-operations-18-person-direct-evidence)／[補助](sources.md#v2t-operations-18-person-support-evidence)／[非該当](sources.md#v2t-operations-18-person-negative-evidence)

### v2t-operations-19-query

- 分割：tuning／種別：knowledge／分野：業務運用
- 質問：倉庫の帳簿にある数量と実際の在庫が合わない問題を直した事例がほしい。
- 直接候補（3）：`v2t-operations-19-knowledge-direct` — 業務運用の解決事例。入出庫の登録時刻と現物移動を照合し、未入力の返品を特定した。返品受付と記帳を同時に行う手順へ変更した。
- 補助候補（2）：`v2t-operations-19-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-operations-19-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-operations-19-knowledge-direct-evidence)／[補助](sources.md#v2t-operations-19-knowledge-support-evidence)／[非該当](sources.md#v2t-operations-19-knowledge-negative-evidence)

### v2t-operations-20-query

- 分割：tuning／種別：knowledge／分野：業務運用
- 質問：複数の拠点で同じ設備点検を重複して予約する問題を整理した事例を探したい。
- 直接候補（3）：`v2t-operations-20-knowledge-direct` — 業務運用の事例。設備番号ごとに点検予定を集約し、拠点横断の予約窓口を一本化した。同一設備の重複予約を照合し、役割分担を周知した。
- 補助候補（2）：`v2t-operations-20-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2t-operations-20-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2t-operations-20-knowledge-direct-evidence)／[補助](sources.md#v2t-operations-20-knowledge-support-evidence)／[非該当](sources.md#v2t-operations-20-knowledge-negative-evidence)

### v2f-software-01-query

- 分割：final／種別：person／分野：ソフトウェア
- 質問：利用者ごとに見えてよいデータを分ける不具合を修正した開発者を探したい。
- 直接候補（3）：`v2f-software-01-person-direct` — ソフトウェア開発で、検索時に組織識別子が抜ける経路を調査した。全取得処理でテナント条件を必須にし、他組織の記録が返らない回帰試験を追加した。
- 補助候補（2）：`v2f-software-01-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-software-01-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-software-01-person-direct-evidence)／[補助](sources.md#v2f-software-01-person-support-evidence)／[非該当](sources.md#v2f-software-01-person-negative-evidence)

### v2f-software-02-query

- 分割：final／種別：person／分野：ソフトウェア
- 質問：通信できない現場でも記録を続け、復旧後にまとめて送れるアプリを作った人に相談したい。
- 直接候補（3）：`v2f-software-02-person-direct` — ソフトウェア開発で、現場端末の入力をローカルに永続化した。再接続時に未送信データを同期し、送信済み状態を保存して記録の欠落を防いだ。
- 補助候補（2）：`v2f-software-02-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-software-02-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-software-02-person-direct-evidence)／[補助](sources.md#v2f-software-02-person-support-evidence)／[非該当](sources.md#v2f-software-02-person-negative-evidence)

### v2f-software-03-query

- 分割：final／種別：knowledge／分野：ソフトウェア
- 質問：分析用データから個人を特定できる情報を消した実装事例がほしい。
- 直接候補（3）：`v2f-software-03-knowledge-direct` — ソフトウェア開発の解決事例。氏名と連絡先を分析用出力から除外し、人物キーを不可逆な値へ置換した。再識別に使える自由記述も検査して除去した。
- 補助候補（2）：`v2f-software-03-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-software-03-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-software-03-knowledge-direct-evidence)／[補助](sources.md#v2f-software-03-knowledge-support-evidence)／[非該当](sources.md#v2f-software-03-knowledge-negative-evidence)

### v2f-software-04-query

- 分割：final／種別：knowledge／分野：ソフトウェア
- 質問：同じ処理依頼を二度受けても、二重に請求しない仕組みの事例を探したい。
- 直接候補（3）：`v2f-software-04-knowledge-direct` — ソフトウェア開発の事例。決済要求の識別子に一意制約を設け、処理済み要求には保存済みの結果を返した。並行した重複要求でも課金を一度だけ行うことを検証した。
- 補助候補（2）：`v2f-software-04-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-software-04-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-software-04-knowledge-direct-evidence)／[補助](sources.md#v2f-software-04-knowledge-support-evidence)／[非該当](sources.md#v2f-software-04-knowledge-negative-evidence)

### v2f-mechanical-05-query

- 分割：final／種別：person／分野：機械設計
- 質問：熱で筐体が変形してふたが閉まらなくなる問題を直した設計者を探したい。
- 直接候補（3）：`v2f-mechanical-05-person-direct` — 機械設計で、筐体の熱膨張と温度分布を評価した。材料の組合せと固定位置を変更し、高温でも開閉できる隙間を確保した。
- 補助候補（2）：`v2f-mechanical-05-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-mechanical-05-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-mechanical-05-person-direct-evidence)／[補助](sources.md#v2f-mechanical-05-person-support-evidence)／[非該当](sources.md#v2f-mechanical-05-person-negative-evidence)

### v2f-mechanical-06-query

- 分割：final／種別：person／分野：機械設計
- 質問：重い装置を手作業で持ち上げる作業を減らす治具を設計した人に相談したい。
- 直接候補（3）：`v2f-mechanical-06-person-direct` — 機械設計で、装置の重心と吊上げ荷重を計算した。専用の保持治具を製作し、落下防止機構と負荷試験を含む移送方法を引き継いだ。
- 補助候補（2）：`v2f-mechanical-06-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-mechanical-06-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-mechanical-06-person-direct-evidence)／[補助](sources.md#v2f-mechanical-06-person-support-evidence)／[非該当](sources.md#v2f-mechanical-06-person-negative-evidence)

### v2f-mechanical-07-query

- 分割：final／種別：knowledge／分野：機械設計
- 質問：配管が共振して大きな音が出る問題を改善した事例がほしい。
- 直接候補（3）：`v2f-mechanical-07-knowledge-direct` — 機械設計の解決事例。流体の脈動周期と管路の固有振動数を計測した。支持間隔と緩衝部を変更し、運転条件下で騒音を低減した。
- 補助候補（2）：`v2f-mechanical-07-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-mechanical-07-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-mechanical-07-knowledge-direct-evidence)／[補助](sources.md#v2f-mechanical-07-knowledge-support-evidence)／[非該当](sources.md#v2f-mechanical-07-knowledge-negative-evidence)

### v2f-mechanical-08-query

- 分割：final／種別：knowledge／分野：機械設計
- 質問：カバーの縁で作業者がけがをする危険を、形状変更で減らした事例を探したい。
- 直接候補（3）：`v2f-mechanical-08-knowledge-direct` — 機械設計の事例。板金カバーの接触箇所を調査し、端部に折返しと丸みを追加した。組立や清掃時の接触試験で鋭利な部分を除いたことを確認した。
- 補助候補（2）：`v2f-mechanical-08-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-mechanical-08-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-mechanical-08-knowledge-direct-evidence)／[補助](sources.md#v2f-mechanical-08-knowledge-support-evidence)／[非該当](sources.md#v2f-mechanical-08-knowledge-negative-evidence)

### v2f-sales-09-query

- 分割：final／種別：person／分野：営業
- 質問：顧客の要求が多すぎて提案範囲が決まらない商談をまとめた担当者を探したい。
- 直接候補（3）：`v2f-sales-09-person-direct` — 営業で、顧客の要求を必須項目と将来項目に分けた。予算と納期に合う提案範囲を合意し、対象外の項目を契約に明記した。
- 補助候補（2）：`v2f-sales-09-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-sales-09-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-sales-09-person-direct-evidence)／[補助](sources.md#v2f-sales-09-person-support-evidence)／[非該当](sources.md#v2f-sales-09-person-negative-evidence)

### v2f-sales-10-query

- 分割：final／種別：person／分野：営業
- 質問：既存顧客の別部署へ製品を広げた経験がある人に相談したい。
- 直接候補（3）：`v2f-sales-10-person-direct` — 営業で、既存部門の利用効果を聞き取り、別部門の業務課題に合わせた試行提案を実施した。利用部門を横断する担当者と調整し、追加導入を受注した。
- 補助候補（2）：`v2f-sales-10-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-sales-10-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-sales-10-person-direct-evidence)／[補助](sources.md#v2f-sales-10-person-support-evidence)／[非該当](sources.md#v2f-sales-10-person-negative-evidence)

### v2f-sales-11-query

- 分割：final／種別：knowledge／分野：営業
- 質問：顧客が価格だけで比較する入札で、提案の価値を伝えた事例がほしい。
- 直接候補（3）：`v2f-sales-11-knowledge-direct` — 営業の解決事例。稼働停止と保守作業の削減量を顧客と確認し、購入費用だけでなく運用費用を含めた比較資料を作成した。効果の根拠を説明して受注した。
- 補助候補（2）：`v2f-sales-11-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-sales-11-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-sales-11-knowledge-direct-evidence)／[補助](sources.md#v2f-sales-11-knowledge-support-evidence)／[非該当](sources.md#v2f-sales-11-knowledge-negative-evidence)

### v2f-sales-12-query

- 分割：final／種別：knowledge／分野：営業
- 質問：長く返事がない商談について、失注か継続かを判断した事例を探したい。
- 直接候補（3）：`v2f-sales-12-knowledge-direct` — 営業の事例。意思決定者と調達時期を再確認し、導入計画が消えた案件を見込みから除外した。継続する案件には次回判断日を合意し、予測を更新した。
- 補助候補（2）：`v2f-sales-12-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-sales-12-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-sales-12-knowledge-direct-evidence)／[補助](sources.md#v2f-sales-12-knowledge-support-evidence)／[非該当](sources.md#v2f-sales-12-knowledge-negative-evidence)

### v2f-manufacturing-13-query

- 分割：final／種別：person／分野：製造
- 質問：生産する品種が変わるたびに長く機械を止める問題を改善した人を探したい。
- 直接候補（3）：`v2f-manufacturing-13-person-direct` — 製造で、段取り替えの各作業を計測した。機械を止めずにできる準備を外段取りに分け、工具と材料を事前配置して停止時間を短縮した。
- 補助候補（2）：`v2f-manufacturing-13-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-manufacturing-13-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-manufacturing-13-person-direct-evidence)／[補助](sources.md#v2f-manufacturing-13-person-support-evidence)／[非該当](sources.md#v2f-manufacturing-13-person-negative-evidence)

### v2f-manufacturing-14-query

- 分割：final／種別：person／分野：製造
- 質問：検査員ごとに良品と不良品の判断が変わる問題を直した担当者に相談したい。
- 直接候補（3）：`v2f-manufacturing-14-person-direct` — 製造で、外観検査の境界見本を作成した。欠陥の大きさと位置による合否基準を定め、検査員の一致率を確認して教育内容を改訂した。
- 補助候補（2）：`v2f-manufacturing-14-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-manufacturing-14-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-manufacturing-14-person-direct-evidence)／[補助](sources.md#v2f-manufacturing-14-person-support-evidence)／[非該当](sources.md#v2f-manufacturing-14-person-negative-evidence)

### v2f-manufacturing-15-query

- 分割：final／種別：knowledge／分野：製造
- 質問：加工した穴の寸法が徐々にずれていく問題を防いだ事例がほしい。
- 直接候補（3）：`v2f-manufacturing-15-knowledge-direct` — 製造の解決事例。加工回数と工具摩耗の関係を測定した。寸法の傾向を管理図で監視し、不良発生前に補正と工具交換を行う条件を定めた。
- 補助候補（2）：`v2f-manufacturing-15-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-manufacturing-15-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-manufacturing-15-knowledge-direct-evidence)／[補助](sources.md#v2f-manufacturing-15-knowledge-support-evidence)／[非該当](sources.md#v2f-manufacturing-15-knowledge-negative-evidence)

### v2f-manufacturing-16-query

- 分割：final／種別：knowledge／分野：製造
- 質問：包装機で製品が詰まって停止する問題を減らした事例を探したい。
- 直接候補（3）：`v2f-manufacturing-16-knowledge-direct` — 製造の事例。搬送速度と投入間隔を記録し、詰まりが発生する組合せを特定した。整列ガイドと速度同期を変更し、連続運転で停止頻度を低減した。
- 補助候補（2）：`v2f-manufacturing-16-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-manufacturing-16-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-manufacturing-16-knowledge-direct-evidence)／[補助](sources.md#v2f-manufacturing-16-knowledge-support-evidence)／[非該当](sources.md#v2f-manufacturing-16-knowledge-negative-evidence)

### v2f-operations-17-query

- 分割：final／種別：person／分野：業務運用
- 質問：夜間当番が障害の連絡先を判断できない状況を改善した人を探したい。
- 直接候補（3）：`v2f-operations-17-person-direct` — 業務運用で、障害種別と影響度ごとの連絡順を整理した。夜間の模擬訓練を行い、当番交代時に判断表と未解決事項を引き継いだ。
- 補助候補（2）：`v2f-operations-17-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-operations-17-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-operations-17-person-direct-evidence)／[補助](sources.md#v2f-operations-17-person-support-evidence)／[非該当](sources.md#v2f-operations-17-person-negative-evidence)

### v2f-operations-18-query

- 分割：final／種別：person／分野：業務運用
- 質問：備品購入の申請が複数の部署で止まる問題を改善した人に相談したい。
- 直接候補（3）：`v2f-operations-18-person-direct` — 業務運用で、購入申請から承認までの滞留を調査した。金額別の承認経路と代理承認者を定め、差戻し理由を一覧化して処理期間を短縮した。
- 補助候補（2）：`v2f-operations-18-person-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-operations-18-person-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-operations-18-person-direct-evidence)／[補助](sources.md#v2f-operations-18-person-support-evidence)／[非該当](sources.md#v2f-operations-18-person-negative-evidence)

### v2f-operations-19-query

- 分割：final／種別：knowledge／分野：業務運用
- 質問：災害時に一つの拠点が使えなくても業務を続けた事例がほしい。
- 直接候補（3）：`v2f-operations-19-knowledge-direct` — 業務運用の解決事例。重要業務の代替拠点と担当者を定め、通常拠点を使用しない訓練を行った。受注と連絡の手順を切り替え、継続できることを確認した。
- 補助候補（2）：`v2f-operations-19-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-operations-19-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-operations-19-knowledge-direct-evidence)／[補助](sources.md#v2f-operations-19-knowledge-support-evidence)／[非該当](sources.md#v2f-operations-19-knowledge-negative-evidence)

### v2f-operations-20-query

- 分割：final／種別：knowledge／分野：業務運用
- 質問：請求書と入金の突合せに時間がかかる問題を整理した事例を探したい。
- 直接候補（3）：`v2f-operations-20-knowledge-direct` — 業務運用の事例。入金名義と請求番号の対応表を整備し、照合できない取引を例外一覧へ分けた。確認窓口を統一して未消込の滞留を減らした。
- 補助候補（2）：`v2f-operations-20-knowledge-support` — 同じ対応を副業務／補助事例として記載。
- 非該当候補（0）：`v2f-operations-20-knowledge-negative` — 相談文を記録した案内だけで、実施・結果なし。
- 原本：[直接](sources.md#v2f-operations-20-knowledge-direct-evidence)／[補助](sources.md#v2f-operations-20-knowledge-support-evidence)／[非該当](sources.md#v2f-operations-20-knowledge-negative-evidence)

## 正解なしの確認

| 分割 | 質問ID | 質問 | 仮判定 |
|---|---|---|---|
| tuning | v2t-no-answer-01 | 量子計算向けの誤り訂正回路を実装した経験者を探したい。 | 同種別30候補すべて0。原本に記録なし |
| tuning | v2t-no-answer-02 | 動物用ワクチンの治験を設計した経験者を探したい。 | 同種別30候補すべて0。原本に記録なし |
| tuning | v2t-no-answer-03 | 海底探査で鉱物を採掘した実践事例がほしい。 | 同種別30候補すべて0。原本に記録なし |
| tuning | v2t-no-answer-04 | 古文書の年代を放射性同位体で測定した実践事例がほしい。 | 同種別30候補すべて0。原本に記録なし |
| final | v2f-no-answer-01 | 宇宙機の姿勢制御を軌道上で検証した経験者を探したい。 | 同種別30候補すべて0。原本に記録なし |
| final | v2f-no-answer-02 | 植物の遺伝子編集を実施した経験者を探したい。 | 同種別30候補すべて0。原本に記録なし |
| final | v2f-no-answer-03 | 深海の有人潜水艇を回収した実践事例がほしい。 | 同種別30候補すべて0。原本に記録なし |
| final | v2f-no-answer-04 | 核融合炉のプラズマ閉じ込めを実証した事例がほしい。 | 同種別30候補すべて0。原本に記録なし |

## 人が確認する事項

- 直接・補助の関連度と根拠、他課題の候補が正解から漏れていないか。
- 調整/確認の課題が意味的に重複していないか、実際に探したい相談として自然か。
- 正解なし8問に原本上の正解がないこと。
- 修正があれば質問ID・候補ID・修正関連度を指定する。承認後に新計画/ゲートを固定して測定する。

承認後の送信前検証で、架空質問の一般語「パスワード」が秘密情報検証に一致したため、「認証用の文字列」へ同義で置換した。課題・原本の実施内容・候補関連度は変更していない。秘密情報検証は緩和していない。
