# 構成図の検証記録

対象：設計版0.2の`architecture.json` → `architecture.html`。図の種別はarchitecture。Archifyスキルに従い、機械的検証、実ブラウザ測定、画像による確認を分けて実施した。AI Search/PostgreSQLを除き、Cosmos DB・Probability/Noul・Foundry再順位付け・Queueへ更新。

| 項目 | 結果 |
|---|---|
| diagram_type | architecture |
| output | /Users/nyanpass/source/repos/dse-growth-loop/docs/diagrams/architecture.html |
| specification_sha256 | d785ff3775775e8f8ff5ec0210251b57184267a6ed81290fe3eead82e874cb7a |
| artifact_sha256 | 21ae65377c2f1696a52e599c9eeaf6f4cff01af9729797b02cbfc921ba6d5be3 |
| validation | 9/9 showcase、0 errors、0 warnings |
| browser_evidence | passed |
| visual_review | passed（1440×900と2048×1320、それぞれライト/ダークの生成画像で確認） |
| correction_rounds | 0（0.1の検証済み配置を継承し、今回の表示レビュー修正なし） |

ブラウザ測定は1440×900、1600×1000、1920×1080、2048×1320で実施し、横・縦のはみ出しなし。端点サイズのライト/ダークの画像を保存した。許可された環境のChromeで測定して成功した。これは構成図の検証であり、検索システムの精度・性能試験ではない。

画像レビューでは図中のノード、線、ラベルの重なりと大きい画面での配置を確認した。インタラクティブ操作や全形式のエクスポートを手動で網羅検証したとは主張しない。図の固定ビューアUIとHTMLのlangは英語、日本語の構成説明はそのまま表示される。

ブラウザ検証記録： [architecture.visual-check.json](architecture.visual-check.json)。記録中のファイルパスは作成時の作業場所。保存先へは同じバイト列をコピーし、ハッシュで確認する。
