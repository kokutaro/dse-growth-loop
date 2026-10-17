# Issue15：部署正本・異動履歴・同意失効の設計案

2026-10-17。基点main `ca75ded43bd0a93a73ff58059633bc74ed21e8d4`。[Issue15](https://github.com/kokutaro/dse-growth-loop/issues/15) の設計レビュー用。07:20:54 UTCのユーザー承認は設計案作成の承認であり、下記の採用・実装・実権限変更の承認ではない。アプリ・同意データ・Azure設定は変更していない。

## 推奨と根拠

**初版は既存catalog内の手動管理ダミー正本を使い、主部署を1人1つ、所属履歴は有効期間で管理する。部署と認可scopeは同一視しない。** 新しい外部連携、日次同期、兼務、多段階承認、既存データの移行互換は追加しない。未リリースのため、後続実装時は対象を明示したローカルダミーseedを新契約で作り直し、旧同意の変換・自動承認は行わない。既存の証跡・原本ファイルは保持する。

管理元は未確定。[IdentityResolver](../src/Dse.Infrastructure/IdentityResolver.cs) はEntraのtid/oidを設定のBindings/Grantsへ対応させる認証・認可処理であり、部署の正しさを保証しない。[BusinessAnalysisMetadata](../src/Dse.Domain/BusinessAnalysis.cs) のdepartmentIdは形式検査だけで、現在の[分析サービス](../src/Dse.Infrastructure/BusinessAnalysisService.cs)は同版の公開要約とcatalogが一致する場合に利用する。`departmentActivity`は業務実績用で、所属マスタではない。調べたコード・仕様に公式HR／Graph／SCIM同期や、その管理責任者を確定する証拠はなかった。

| 選択肢 | 扱い |
| --- | --- |
| 手動ダミー正本 | **今回の推奨**。既存operatorが架空部署・所属を明示登録。実組織の正本とは呼ばない |
| 公式HR等の管理元から取込 | 実組織対応時の候補。管理元・責任者・訂正／退職／遡及履歴の提供方法が確認できてから別設計 |
| Entraの部署属性を利用 | 管理元がその属性を正本と認めた場合のみ候補。ログインできることや属性が存在することだけで採用しない |

## 現行との差分と同意の区別

| 項目 | 現行コードで確認した動作 | 提案する追加 |
| --- | --- | --- |
| 課題依頼への参加 | [Conversations.DecideAsync](../src/Dse.Infrastructure/Conversations.cs)の本人acceptedで即参加。依頼者の再承認なし。accepted/leftでmembershipVersionを更新 | 参加時の本人accessEpochを固定し、失効した参加資格の復活を防ぐ。部署内異動だけで再招待を要求しない |
| 要約の公開 | [KnowledgeService](../src/Dse.Infrastructure/Knowledge.cs)で元参加者全員（退会者も含む）が同じ要約版・scopeにpublication同意し、publisherが承認。編集で版・同意・承認を失効 | authorityTokenを同意・承認・公開原本／投影へ固定し、古い権限状態での利用を拒否 |
| 分析利用 | 公開同意とは別のanalysis値。公開済み・全員の分析同意・現在の主体／scopeを再照合 | 同意対象に検証済み発生部署snapshotを含める。申告departmentIdで部署数を増やせなくする |
| 表示名開示 | [IdentityDisclosureService](../src/Dse.Infrastructure/IdentityDisclosureService.cs)で現参加者全員の独立同意。profile/binding/grant等の指紋不一致を観測すると全grantを失効 | 指紋にauthorityTokenと各人accessEpochを含め、変更→復元も別の状態として扱う |

**参加承諾は公開・分析・表示名開示の同意を代替しない。** 公開同意も分析・表示名開示を代替しない。管理者の部署確認も本人同意ではない。本文の完全匿名化や、既に閲覧／送信された情報の回収は別問題として残る。

## 最小データモデル（新規kindの案）

既存のcatalog `/tenantId`、ETag、監査、[同一partition batch制約](../src/Dse.Domain/Records.cs)を再利用する。外部DBや別マイクロサービスは作らない。

| 正本 | 最小項目・不変条件 |
| --- | --- |
| AuthorityControl（tenantごと1件） | generation（新規生成ID）、revision（正の増分値）、state（active/maintenance）。`authorityToken = generation + revision`。部署・所属・binding/grantの確定変更はrevisionを必ず増やす。初版は影響範囲を細分化せず、全公開／分析／表示名同意を保守的に無効化する |
| Department | 安定したdepartmentId、管理者用名称、validFrom/validTo、改版、変更理由。廃止は終了日で表しIDを再利用しない。単なる改称は同じID、分割／統合は新ID。後継関係は記録するが自動的な権限継承はしない |
| PersonAuthority | personId、既存のtid/oid/principal対応、enabled、roles、scopeIds、accessEpoch、主部署の期間配列（termId/departmentId/from/to）、改版。期間はUTCの半開区間[from,to)、重複不可。所属不明はnull、退職後はenabled=false。空欄を架空部署や0で埋めない |

既存Auth設定は初期seedの入力に限定し、初期化後はPersonAuthorityを判定正本とする案。通常起動で古い設定を上書き適用せず、不一致は停止する。認可設定の変更は停止下の明示CLIを通し、前後hash・理由とrevision/accessEpoch更新を保存する。初版にroles/scopeを編集するWeb APIは設けない。本人による所属・権限の書換えも許可しない。

主体解決は全経路で同じAuthorityProviderへ統一し、未登録・読取失敗時に設定やJWT内の古い権限へfallbackしない。参加accepted要求は確認済みaccessEpoch、他の同意／承認要求は確認済みauthorityTokenと対象版を必須にする。変更されていれば409/412で再表示し、新tokenへサーバーが黙って同意を付け替えない。

参加資格に影響するenabled・binding・member資格・scope変更では該当者のaccessEpochも増やす。単純な異動・部署改称ではaccessEpochを変えず、公開等のauthorityTokenだけを変える。初版は未来日予約を設けず、適用時刻以前の履歴確定／訂正を明示操作する。遡及訂正も新revisionで、過去の判断を黙って書き換えない。

課題の**発生部署**は「課題作成者の課題createdAt時点の主部署」を推奨する。後続実装で作成者personIdを課題へサーバー側固定する（現在は初期化JobのownerPersonId等に分散）。検証済みの`departmentId/termId/asOf/source/generation/revision`を要約の同意対象snapshotに含める。異動後の現在部署で過去の課題を自動再分類しない。不明／履歴不足は分析の部署条件未達とし、LLMや本人申告で補完しない。再編をまたぐ別IDの数は「発生当時の組織単位数」であり、現在部署数や市場需要ではない。横断案件・兼務・再編後への統合集計は初版対象外。

## 管理操作・検証API案

既存operatorだけがtenant内の管理操作を行う。analyst/publisher/memberには管理権限を自動追加しない。実アカウントへの役割付与はこの設計の対象外。

| 操作案 | 検証・出力 |
| --- | --- |
| GET `/api/v1/admin/organization` | operator専用。部署・期間・版の必要最小限だけ。社員UIに組織／人物一覧を追加しない |
| POST `/api/v1/admin/organization/validate` | 保存しないdry-run。存在しない部署、期間重複、tenant違い、退職状態、古いETag/token、未知フィールドを拒否し、影響と再同意要否を返す |
| POST `/api/v1/admin/organization/changes` | 1人または1部署の確定変更。If-Match、Idempotency-Key、理由、期待authorityToken必須。既存tenant AccessFence内で再検証し、正本・Control・監査を1batchで更新。大量一括同期は設けない |
| GET `/api/v1/me/organization` | 本人の現所属・確認状態のみ。任意personIdの問合せを一般利用者へ公開しない |

バインディング／grant変更・退職適用は停止下のoperator CLI案に限定し、管理画面の便宜で権限を変更しない。変更前後のhash・主体・時刻・理由を監査し、通常ログに部署名・氏名・本文を追加しない。冪等再送は現在の状態を返し、過去の開示応答や同意を再生しない。

ここでのoperatorは既存の権限種別であり、実際の管理担当者の任命・付与を確認したという意味ではない。部署正本・oid・異動履歴をAIへ追加送信せず、社員向け共有DTOにも追加しない。公開根拠のdepartmentId=null、ルーム局所のopaque tokenという既存境界を維持する。

## 異動・失効・復元の規則

| 変更 | 参加・会話 | 公開／分析／表示名 |
| --- | --- | --- |
| 主部署異動、scope等は同じ | 有効な本人の参加は継続。部署を理由に会話権限を自動拡大しない | authorityToken不一致で停止。全必要者の新しい同意、公開にはpublisher再承認。発生部署の履歴は維持 |
| 退職・無効化、binding/roles/scope変更 | accessEpoch更新。旧参加履歴が残っても本人の読取／送信／共有を拒否。再有効化だけでは旧参加資格を戻さず、所有者の新しい招待と本人acceptedが必要 | 旧同意は無効。管理者が退職者の同意を代行しない。元参加者の再同意を得られない要約は公開・分析を停止したままにする |
| 部署改称・分割・統合・所属履歴訂正 | 再編だけでscope・role・参加資格を移し替えない | 新tokenで再確認。旧部署ID／履歴を消さず、元データと新集計を区別。既存結果は再照合まで非表示 |
| 設定A→B→A、退職→再有効化 | 各確定変更でrevision/accessEpochを増やし、値がAと同じでも旧参加epochを復活させない | 内容hashだけで有効化せず、旧tokenの同意は再利用不可。新しいkey・現tokenへの本人同意が必要 |
| バックアップ復元・ロールバック | maintenance＋API/Worker停止下で実施。復元された権限／参加ピンは再確認まで無効 | **同じDB内の増分値だけではDB全体の巻戻りを検出できない。** 初版は全同意を破棄し、新generationでauthority／ダミーデータを再生成してから開放。旧同意の復元互換は作らない |

旧Participationのepoch不一致は履歴をacceptedのまま残しても有効な参加者として扱わない。再招待で新しい版のrequestedを作り、本人accepted時に現epochを固定しmembershipVersionを更新する案。複数partitionの全Sessionを一括更新したと仮定せず、現在epochとの照合で即時遮断する。会話の保存履歴の保持／削除は[OWNER-02](specs/decisions.md)の別判断であり、退職で物理削除しない。

古い公開原本は、再レビュー開始時に同意とpublisher承認を消してdraftへ戻し、現tokenでの全員同意・再承認を要求する。publishedの既存承認や同じsourceVersionだけを理由に処理を短絡しない。退職者を同意対象集合から自動で取り除くこともしない。

復元を旧generationのまま自動開放する経路は禁止する案。初版のダミー再初期化では、未確認の復元と通常起動をソフトウェアだけで必ず識別できるとは保証しない。実データを保持してオンライン復元する将来要件が出た場合だけ、復元対象外の世代アンカー・権限回復手順を別途設計する。

## 検索・共有・分析への適用と受入試験

検索候補の取得前、AI入力消費直前、最終HTTP応答／名前開示、共有確定、分析保存・再表示で現Authorityと原本のpinを照合する。現actorのroles/scope、参加accessEpoch、原本／根拠版、公開／分析／表示名それぞれのtokenを別々に検証する。古いActorAccess・projection・キャッシュ・冪等receiptを認可根拠にしない。不明・読取障害・欠落はfail-closed、部署情報の欠落は0部署の実測とはしない。

管理変更と最終消費は既存[AccessFence](../src/Dse.Infrastructure/AccessFence.cs)を共用し、管理変更確定後に古いpinで送信／保存／表示しない。正本とControlは同じcatalog batch、会話側のpinは別partitionであり全件原子的更新とは呼ばない。古い公開投影は直ちにガードで遮断し、投影の後処理成功を遮断条件にしない。既に外部へ送信済みのAI入力は取り消せない。権威チェックを通さないconfig直編集や過去DBの独断復元を自動検出・安全化したとは扱わない。

後続実装の受入は次のFake/ローカル試験を先に行い、実環境受入とは分ける。

1. 参加acceptedだけで公開／分析／表示名が許可されない。公開可・分析不可も維持。退会者を含む公開同意と、現参加者だけの表示名同意を混同しない。
2. 同scope異動では会話参加継続、開示・公開・分析は再同意待ち。発生部署は異動前を維持し、再編跨ぎ・重複課題の既知集計が一致する。
3. 退職・member剥奪・scope/binding変更直後は旧JWT／ActorAccess／alias／candidate token／projection／receiptでも利用不可。再有効化で旧参加・旧同意が復活しない。
4. 誰もアクセスしない間のA→B→Aでも旧同意は無効。古いIf-Match/token、同意の再送、別ルーム／別tenantへの流用を拒否する。
5. 改称はID維持、統合／分割は新ID、履歴重複・不明部署・権限のない管理更新・未来日予約を拒否する。管理者による同意代行も拒否する。
6. 分類中・rerank中・共有確定直前・分析中・名前応答直前に変更し、無許可AI入力／表示／共有0、古い集計非表示を検証する。障害時は旧情報へfallbackしない。
7. 複数プロセス競合、batch失敗、後処理停止でもControlと正本の版が矛盾せず、fence未回復時は停止を維持する。
8. 古いDBを復元したmaintenance状態、旧epochのアプリ再起動、新generation再初期化を試験する。旧同意の自動取込・roleの自動再付与がないことを確認する。

## ユーザーに決めていただく事項

- **管理元と担当者**：初版の手動ダミー正本でよいか。実組織を扱う将来の正本管理元・権威を持つ担当者は未確認。Entraや特定HR製品とは決めつけない。
- **集計の意味**：課題作成者の発生時主部署1つを採用するか。現在所属や協力者の部署へ置き換えず、再編跨ぎを現在部署数と呼ばない案を推奨する。
- **失効範囲**：初版はtenant全体の公開／分析／表示名同意を失効させる簡単な方式を採るか。同scope異動は参加継続、退職・認可変更後は新しい招待／acceptedと別同意を要求する案を推奨する。
- **復元方針**：ダミー環境では旧同意を救済せず、新generationで再初期化するか。既に保存した検証証跡を消す提案ではない。実データの保持・削除・正式な公開／分析責任者は既存OWNER-01/02/05の未確定事項として残す。

設計レビュー後の候補PRは、(1)正本・検証とダミーseed、(2)認可／参加pinと同意失効、(3)発生部署snapshotと分析集計の順に分けられる。今回はこの文書だけを作成し、いずれも実装・実行・外部送信しない。Issue15の少量実AI評価・実Job／復旧受入や、Issue16の全体受入は引き続き未完了である。
