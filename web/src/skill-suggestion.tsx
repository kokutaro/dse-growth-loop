import './skill-suggestion.css';
export type SkillSuggestion={
 phase:string;outcome:string;reason:string;dictionaryVersion:number;threshold?:number;clearNegativeRouting?:number;basis?:string;stale?:boolean;
 shortlistComplete?:boolean;selectedCount?:number;judgedCount?:number;applicableCount?:number;aiRunId?:string|null;providerExecutionUncertain?:boolean;
 comparisons?:{conceptId:string;forward:number|null;reverse:number|null;minimum:number|null;equivalent:boolean}[];
 concepts?:{id:string;name:string;definition:string;boundary:string}[];
};
const reasons:Record<string,string>={NOT_REQUESTED:'まだ照会していません。',EXACT_ALIAS_PROPOSAL_ONLY:'登録済み別名の完全一致。AIの意味判定は実行していません。',SEMANTIC_PROPOSAL_ONLY:'双方向の参考値が0.8以上です。人の確認が必要です。',NO_COMPATIBLE_CONCEPTS:'同じ共有範囲・種別の既存概念がありません。新規技能の確定ではありません。',NO_EQUIVALENT_IN_COMPLETE_SCOPED_CATALOGUE:'同じ範囲・種別の全概念を確認し、明確な不一致の参考値でした。新規候補として人が確認します。',INCOMPLETE_SHORTLIST:'絞り込んだ範囲で一致なし。未確認の辞書項目があり、新規技能とは確定できません。',COMPETING_CONCEPTS:'複数の概念が基準を満たしています。統合・選択には人の確認が必要です。',AMBIGUOUS_OR_ASYMMETRIC:'参考値が曖昧、または双方向で一致していません。',DICTIONARY_CHANGED:'辞書版が変わりました。現在版で候補と根拠を確認してください。',UNKNOWN_AI_ANSWER:'AIの返値が未判定でした。',INVALID_AI_SHAPE:'AIの返値を検証できませんでした。'};
const probability=(n:number|null|undefined)=>n==null?'未判定':n.toFixed(2);
// Pure view: it cannot send an API request, approve vocabulary or publish experience.
export function SkillSuggestionPanel({value,currentVersion,disabled,onRequest,onCompare,onReload}:{value?:SkillSuggestion;currentVersion:number;disabled:boolean;onRequest:()=>void;onCompare:(id:string)=>void;onReload?:()=>void}){
 const pending=value?.phase==='dispatching'||value?.phase==='prepared';
 const title=pending?'実行中または結果不明':value?.outcome==='existing_candidate'?'既存技能の候補（未承認）':value?.outcome==='new_candidate'?'新規候補（未確定）':'要確認';
 return <section className="panel skill-suggestion"><h3>意味的に近い既存技能の候補</h3>
 <p>固定した辞書版の定義・境界・対象・条件を比較します。同じ大分類や似た名前だけでは同一技能にしません。本人経験の認定・辞書の承認・別名追加は行いません。</p>
 <button disabled={disabled||pending} onClick={onRequest}>AIで既存技能の候補を照会</button>
 {pending&&onReload&&<button disabled={disabled} onClick={onReload}>保存された照会状態を再読込</button>}
 <p>必要な場合のみ既存Jevへ追加APIを送信します。同じ候補・同じ辞書版の結果は再利用し、自動再送はしません。</p>
 {value&&<><h4>{title}</h4><p>辞書版 {value.dictionaryVersion} ／ 現在版 {currentVersion} ／ {value.basis==='exact_alias'?'完全一致・登録済み別名':value.basis==='empty_scoped_catalogue'?'範囲・種別の確認のみ（AI未実行）':'双方向noulの最小値 >= 0.8'}</p>
 <p>{pending?'照会後の結果を確認しています。新しいキーでも同じ推論を再送しません。':reasons[value.reason]??`判定を完了できませんでした：${value.reason}`}</p>
 {value.providerExecutionUncertain&&<p role="alert">プロバイダー側の実行結果が不明です。新規技能とは扱わず、再実行を停止しています。</p>}
 {value.stale&&<p role="alert">過去版の提案です。現在版の確認なしに承認へ使えません。</p>}
 {value.basis==='semantic_noul'&&value.applicableCount!=null&&<p>同じ範囲・種別 {value.applicableCount} 件中 {value.selectedCount} 件を比較対象へ選定。有効なAI返値 {value.judgedCount??0} 件。{value.shortlistComplete?'全項目が比較対象':'未選定項目あり'}</p>}
 {!!value.concepts?.length&&<div style={{overflowX:'auto'}}><table><thead><tr><th>既存技能と定義・境界</th><th>正順</th><th>逆順</th><th>最小値</th><th>参考結果</th><th>比較</th></tr></thead><tbody>{value.concepts.map(c=>{
  const score=value.comparisons?.find(x=>x.conceptId===c.id);return <tr key={c.id}><td><strong>{c.name}</strong><p>{c.definition}</p><p>境界：{c.boundary}</p><small>{c.id}</small></td><td>{probability(score?.forward)}</td><td>{probability(score?.reverse)}</td><td>{probability(score?.minimum)}</td><td>{value.basis==='exact_alias'?'登録済み別名':score?.equivalent?'基準以上・未承認':'未一致／未判定'}</td><td><button disabled={disabled||pending||value.stale||currentVersion!==value.dictionaryVersion} onClick={()=>onCompare(c.id)}>この候補IDと比較</button></td></tr>;
 })}</tbody></table></div>}
 <p>0.8は参考の同等性基準です。人の正解・本人関与・熟練度を保証しません。新規候補への振分けの0.2も不一致の確定ではありません。</p>
 {value.aiRunId&&<small>照会記録：{value.aiRunId}</small>}
 </>}
 </section>;
}
