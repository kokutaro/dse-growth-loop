type EstimateState={availability?:string;alreadyApplied?:boolean};
type ReceiptState={acceptance?:string;generationId:string};
export function generationCanStart(q:EstimateState){return q.availability==='new'&&!q.alreadyApplied;}
export function generationAcceptanceMessage(r:ReceiptState){
 const labels:Record<string,string>={new:'新規受付',existing_success:'既存の完了世代',existing_failed:'既存の失敗世代（新規実行なし）',existing_pending:'既存の処理を追跡（新規実行なし）',already_applied:'適用済み（新規実行なし）'};
 const label=labels[r.acceptance??''];if(!label)throw new Error('受付結果の状態を確認できません。再送せず最新状態を確認してください');
 return `${label} ${r.generationId}。原本・旧検索と消費済み枠を保持します。`;
}
