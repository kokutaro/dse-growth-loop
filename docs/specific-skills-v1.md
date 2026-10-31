> 現行の抽出設計は [specific-skills-v2.md](specific-skills-v2.md)。本書は旧v1の契約・評価経緯として保持する。

# 大分類と具体スキルの分離

## 契約

`taxonomy-v1` と `profile-v1` のJev Choiceは、機械設計・ソフトウェア開発・営業・製造・業務改善・不明という大分類の分布を維持する。Probability/Confidenceは経験年数・割合・熟練度に変換しない。`ProfileExtraction.DomainHints` は語による大分類のヒントであり具体スキル抽出ではない。旧 `Skills()` は既存fixture等の互換用ラッパーのみ。

具体スキルは独立した `specific-skills-v1`。通常の `IConcreteSkillExtractor` → `AiService` → Foundry `extractSkills` strict JSON Schema → 根拠検証 → `ProfilePipeline` → experience/searchUnit/API/画面という経路を使う。評価だけの抽出特例は設けない。descriptionの原文保持や日付の機械処理は変更しない。

|値|意味|
|---|---|
|specificSkills属性なし|旧データ。具体スキルへ移行したとは扱わない|
|state=not_evaluated, items=null|この版の具体スキル抽出は未実施|
|state=unknown, items=null|処理したが情報不足|
|state=extracted, items=[], rejectedItemsなし|評価済み、候補なし|
|state=extracted, items=[], rejectedItemsあり|候補はすべて保留。該当なしの確定ではない|
|state=extracted, items=[...]|根拠・関与を保持した候補あり。全候補が経験タグになるわけではない|

候補は `raw`（原文表記）、`normalizationCandidate`（文字列またはnull）、`evidenceId`、`quote`（原文の文全体または根拠全体）、`participation` を持つ。大分類IDは具体スキルにしない。正規化候補は未承認の提案として表示する。検索キーは候補のrawからNFKC・前後空白・小文字化および明示した固定同義語で機械的に生成し、AIの正規化提案を直接インデックスしない。バージョンや未知の表記を推測して削らない。

本人実施 `performed`、共同実施 `joint`、補助 `assisted` のみが経験タグ対象。相談 `consultation`、希望 `aspiration`、他人の実績 `other_person`、否定 `negated`、関与不明 `unclear` はレビュー可能な候補として残し、本人経験タグは作らない。共同／補助は原文中の当該節または直前から継続する実施枠の明示を要求し、単独実施へ丸めない。

## 根拠と変更処理

Foundryへは権限確認済みの原文・根拠と対象の説明だけを渡す。正解や確認者メタデータは含めない。モデルID・prompt・taxonomy・dataVersion・入力hash・根拠ID・usage・request IDは既存AiRunへ `extractSkills` として保存し、既存の停止・dispatch・不明結果処理を通す。費用台帳やper-run予算承認ゲートの追加はない。

rawは引用の部分文字列、引用はそのevidenceIdの原文と完全一致する連続した完全な文（複数文可）または全文であることを確認する。肯定的な本人実施の記述がない語の断片、列見出し、技術名の羅列、相談・希望・否定・他人の記述を経験タグとして採用しない。`scoped-assertion-v2` は引用一致と経験成立を分けて判定する。技術動詞の列挙ではなく、文・対比節ごとの完結した過去／継続の叙述、主語、否定・条件・意図・伝聞のスコープを検査する。引用内の発言は本人の実施証明にしない。後続の撤回や本人の担当限定も確認する。現在の機械検証は限定的な日本語構文と一部英語に限る。省略、未対応の主語・否定・伝聞、複雑な連用節では誤採用・過剰保留が残り得る。これは全言語の意味判定を保証するものではなく、実AI精度の別検証が必要。

候補は採用・保留の合計40件上限。状態・型・長さ・上限などの構造不正は処理全体を失敗にする。引用不一致、未知根拠ID、経験不成立、大分類混入、重複などの候補不正は `rejectedItems: [{item, reason}]` に隔離する。隔離した候補のraw・引用・元の関与は診断用で、本人の経験と認定せず検索キーにも使わない。他の有効候補を失わせず、原本と投影のatomic publicationは維持する。`AiStatus.Succeeded` は全候補の認定を意味しない。`guardVersion` は適用した検証版で、抽出promptの版と別に記録する。部門の活動を本人経験へ転用しない。集約が40件を超える場合は集約をunknownとして明示し、個別断片の結果は保持する。

同じ原文・根拠版・抽出版・モデル・処理モードではfragmentの候補を再利用し、現行ガードと権威ある原文で採用・保留を再判定する。モデルのpromptと出力schemaは今回変更しない。既存の処理成功結果はprojection時と検索直前に再検証できる。以前の処理失敗で候補が保存されていないレコードは、今回の変更だけでは復元できない。本人が説明を訂正すると古い抽出結果をnot_evaluatedにし、古い検索投影をただちに無効にする。再抽出した本人タグは訂正後の説明にもrawがあることを要求する。検索前にもSourceGuardが現在の原文と権限で再検証する。

## 社内経歴書と検索

社内経歴書の `ResumeSkill` / `skillCandidates` は入力された未確認のヒント。セル位置と表記を保持する。同じ通常抽出経路で原本セルの本文と本人の担当記述を検証してから具体スキルにする。技術欄に名前があるだけでは経験タグを作らない。既存の `ResumeProject.skills` は旧クライアント向けの未確認語彙であり、具体スキル・熟練度の確定値ではない。

従来の社内経歴書候補の語彙一致は `normalizedTerms` / `terms` に互換性のため保持するが、これは説明文中の通常の語検索と同じ経路であり、本人経験の確定には使わない。専用の `SearchRequest.specificSkills` は `specificSkills.searchTerms` だけを検索し、応答のrouteも `specificSkills` と明示する。一致件数も `specificSkillMatches` と通常の `termMatches` を別に保持する。大分類の `categories`、補助判定の `signalIds`、通常語の `terms` と混ぜない。NFKC等による同じ正規化をメモリ検索とCosmosクエリに適用する。

本人画面と検索結果には、候補の原文・正規化候補・関与・引用・根拠ID、未評価／情報不足／候補なし／候補保留を表示する。候補がすべて保留の場合に「該当なし」とは表示しない。既存の匿名協力者探索は従来どおり大分類・語検索であり、匿名APIに生のスキル根拠や新たな個人情報を追加公開しない。

## 互換性と移行

`SearchUnit.skillIds` はdeprecatedの旧大分類キーワードID。読取互換性のみ維持し、新規profile投影では書かず、具体スキルへ自動コピーしない。既存のcategories・signals・termsだけのAPIリクエストはそのまま使用可能。新プロパティは任意・追加型だが、未知プロパティを拒否する旧クライアントは更新が必要。

データベースやAzureの自動変更はない。公開前に、対応するAPI/Worker/画面を揃え、対象と費用を確認して既存人物のprojectionジョブを通常経路で再実行する。各fragmentにFoundry抽出が原則1回追加される。新しい版の投影がatomicに有効化されるまで旧分類・語検索は維持され、旧データが具体スキル検索へ勝手に現れることはない。インデックスポリシーを限定している環境では `specificSkills.searchTerms` の対象確認が必要（今回は設定変更なし）。

## 評価版

旧 `scripts/offline_quality.py` と確認済み120件の正解は変更しない。新しい `scripts/specific_skill_quality.py` は `specific-skills-eval-v1` の確認済み候補・関与・根拠契約だけを受け付け、旧8項目の正解を読み替えない。null、空集合、unknown、not_evaluatedを別として比較し、raw・正規化候補・根拠ID・引用・関与の集合一致、原文・モデル・prompt・設定hashの一致を検査する。

新しい具体スキル契約の合格閾値は未設定であり、旧432/480の結果を新しい値で上書きしない。新契約の人の正解ラベルが必要。今回、すでにfinal60の診断を見たため、今後同じ120件で測定しても独立した未見テストとは呼べない。fixture/Mockによるユニット・契約・統合・ブラウザの成功と、実AI精度の測定を区別する。
