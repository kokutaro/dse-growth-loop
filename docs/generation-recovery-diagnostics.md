# 再抽出世代の停止時診断

`Dse.Worker --inspect-generation`（`Recovery__PersonId` と `Recovery__GenerationId` を既存設定に追加指定） は、ACCESS_BUSY 時に fence を新たに取得せず、既存 operator/owner/scope と原本の現行アクセス権を確認して運用メタデータを読み取る。生成元・世代・aggregate付きJob・AI実行と使用量・予算dispatch・検索head・committed receipt・fence ETag・gateを照合する。本文、引用、モデル応答、認証情報は出力しない。書込みは行わない。

通常稼働中の結果は非原子的な診断スナップショットであり、単独ではロック解除の根拠にならない。既存 `docs/persistence.md` の復旧手順に従い、同じtenantのAPI/Worker/手動処理停止、実replica停止、提供元AI終端、未確定保存の照合を別途確認する。現在の権限が失効した原本・人物は診断対象にできない。

復旧は既存 `FenceRecovery` の期待ETag付き処理を使用する。未知のAI送信を未送信扱いにせず、消費済み費用枠やdispatchを戻さない。失敗世代を成功に変更せず、旧検索headを保持する。この診断追加は再抽出の自動再試行、期限延長、権限変更、AI実行枠の自動解除を提供しない。
