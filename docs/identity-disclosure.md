# ルーム内の独立した表示名開示同意

## 2026-10-17：正本の世代と参加epoch

部署と履歴、参加epoch、公開・分析・表示名の同意を[手動ダミー部署正本](organization-authority.md)へ接続した。旧tokenの同意は再有効化・値の復元だけでは復活しない。以下の2026-10-11の記録は当時の実装境界であり、現行の確認版・復元手順はリンク先を参照する。実環境受入が完了したという意味ではない。

更新日：2026-10-11。実装対象はプロフィールの `displayName` の相互開示。公的な本人確認・正式氏名の照合を保証しない。部署、連絡先、メール、プロフィールへのリンク、人事評価・ランキングは追加しない。

## 公開 API と同意

`GET /api/v1/workspace/rooms/{roomId}/identity-consent` は不透明な公開ルーム ID を現在の参加者として解決する。応答は `roomId`, `reviewedVersion`, `fieldSet`, `etag`, `selfConsent`, `state`, `participants` の許可項目だけ。参加者にはそのルームに保存したランダムな `aliasId`, `aliasLabel`, `sharedName` を返す。現在の全参加者の有効同意が揃うまでは全員の `sharedName` が null。内部 person/principal/tenant/session ID、所属、連絡先は返さない。

`POST` は同じ URL に `{ "granted": true, "reviewedVersion": "GETで受け取った版", "fieldSet": "profile-display-name-v1" }` を送る。`If-Match` と `Idempotency-Key` が必須。本人が自分の同意だけを書き込める。未知フィールド（対象 person ID など）、弱い ETag、未確認の版を拒否する。参加の accepted、事例公開、分析同意からの転用はない。`granted: false` は開示を撤回し、全員の既存同意を失効させる。

## 再確認と認可

同意は現在の参加者集合・membershipVersion・ルーム scope/state・各人の現在プロフィール全文/版/状態/owner・現在 enabled な Auth binding と scope grant の指紋に束縛する。氏名が変わった場合だけでなく、プロフィール版や参加者、状態、認可が変わった場合も以前の版では開示しない。変更後に戻した内容も、版が進んでいれば以前の同意を再利用できない。氏名開示可能な状態でも人物ディレクトリーや他ルームの共通 ID は渡さない。

指紋不一致を観測した GET/POST は、ルーム内の全 grant を永続的に失効させ、generation を更新する。その後に enabled/scope 等を元へ戻しても旧 grant は復活しない。Auth binding/grant の設定モデルには単調な変更版がまだないため、一度も観測されずに変更・復元された設定履歴まで判定できるとは扱わない。実環境の設定変更・ロールバック時には、同意の一括失効または単調な認可設定版の運用が別途必要。この管理側の受入条件を省略して本番完成とはしない。

プロフィールは本人 binding の principal と owner が一致し、tenant/scope、member 権限、available、非 withdrawn、現在 source/access version、表示名の長さを検査する。全員について条件が満たせなければ `unavailable` とし、名前は返さない。非参加者、別 tenant/scope、無効本人 binding は取得・書込を拒否する。

GET と POST の応答生成から JSON シリアライズまで、共有の永続 AccessFence と同じルームの ConversationLocks を保持する。通常のプロフィール訂正・参加変更・撤回はこの境界と競合し、古い名前を並行して公開しない。冪等性 receipt は私的な指紋・操作・本人・確認版を保存し、古い表示名応答を保存・再送しない。再送も現在状態を再照合し、撤回済み同意を復活させない。永続フェンスは期限切れで自動解除せず、異常終了時は既存の停止・回復手順を使う。

Auth binding/grant は毎回現在設定から読み直す。Azure 向け endpoint は JWT の現在 OID binding も再解決し、古い OID の再紐付け後の要求を 403 で拒否する。未認証は 401、認証対応の欠落は 403、現在ルームの非参加者・scope 不一致は 404 と区別する。運用設定の置換は API/Worker を停止して行い、送信中の構成ファイルを書き換える運用は採用しない。config secret、JWT、本文・表示名をログへ追加していない。

## 検証と限界

ローカルテストはメモリーの正本アダプターと、Azure モード用の binding/grant 解決を使う。実 Cosmos の条件付きバッチ、複数 replica、実 JWT、RBAC、実端末間の通信を成功したとは扱わない。Azure の起動・課金 AI 呼び出し・認証/権限変更は行わない。

自由入力の課題、会話、公開要約、本人が設定した表示名には識別情報が入り得る。許可項目の DTO と相互同意は、自由入力から識別情報を完全排除する保証ではない。撤回以前に相手が見た名前や保存した画像を取り消すことも保証しない。社員向け運用では通知・ログ・既存検証 API の露出も含めた別の受入確認が必要。
