import './skill-suggestion.css';
export type SkillSuggestion={
 phase:string;outcome:string;reason:string;dictionaryVersion:number;method?:string;policyVersion?:string;threshold?:number|null;clearNegativeRouting?:number|null;basis?:string;stale?:boolean;
 shortlistComplete?:boolean;selectedCount?:number;judgedCount?:number;applicableCount?:number;aiRunId?:string|null;providerExecutionUncertain?:boolean;inferenceNotDispatched?:boolean|null;
 comparisons?:{conceptId:string;forward:number|null;reverse:number|null;minimum:number|null;equivalent:boolean}[];
 choice?:{selectedId:string;probabilities:Record<string,number>;confidence:number;topProbability:number;secondProbability:number;gap:number;probabilityValidation:{reason:string;sum:number;allowedError:number;roundingDecimals:number|null;normalized:boolean}}|null;
 concepts?:{id:string;name:string;definition:string;boundary:string}[];previousProposals?:SkillSuggestion[];
};
const reasons:Record<string,string>={
 AI_INPUTLIMIT:'比較する原文・定義・条件が入力上限に収まりませんでした。内容を省略せず、人が確認してください。',NOT_REQUESTED:'まだ照会していません。',EMPTY_CHOICE_CATALOGUE:'同じ共有範囲で閲覧可能な技能がありません。AI未送信の新規候補です。登録の判断は人が行います。',CHOICE_OPTION_LIMIT:'全技能と確認用の2状態が選択肢数の上限を超えるため、AIには送信していません。辞書は省略していません。',
 CHOICE_PROPOSAL_ONLY:'Choiceが選んだ対応候補です。確率が高くても自動承認はしません。',CHOICE_NONE:'辞書内の該当なしを選択しました。新規候補として根拠と既存辞書を人が確認します。',CHOICE_UNCERTAIN:'根拠不足・複合技能・競合などの判断保留です。新規技能とは確定しません。',CHOICE_KIND_MISMATCH:'選択先と実務／知識／資格の種別が一致しません。人の再確認が必要です。',CHOICE_TIE:'複数の選択肢が同じ最大値です。1つに確定せず、人が確認します。',
 EXACT_ALIAS_PROPOSAL_ONLY:'過去の登録済み別名の完全一致です。',SEMANTIC_PROPOSAL_ONLY:'過去のnoul双方向の参考値が0.8以上でした。',NO_COMPATIBLE_CONCEPTS:'過去の範囲・種別の確認結果です。',NO_EQUIVALENT_IN_COMPLETE_SCOPED_CATALOGUE:'過去のnoulでは同じ範囲・種別で同等候補なしでした。',INCOMPLETE_SHORTLIST:'過去の文字top3に未選定項目があります。',COMPETING_CONCEPTS:'過去のnoulで複数候補が基準を満たしました。',AMBIGUOUS_OR_ASYMMETRIC:'過去のnoulで曖昧、または双方向が非対称でした。',DICTIONARY_CHANGED:'辞書版が変わりました。現在版で候補と根拠を確認してください。',UNKNOWN_AI_ANSWER:'AIの返値が未判定でした。',INVALID_AI_SHAPE:'AIの返値を検証できませんでした。'
};
const probability=(n:number|null|undefined)=>n==null?'未判定':n.toLocaleString('en-US',{maximumFractionDigits:6,useGrouping:false});
// Pure view: no automatic approval, dictionary write or recognition of experience.
export function SkillSuggestionPanel({value,currentVersion,disabled,onRequest,onCompare,onReload}:{value?:SkillSuggestion;currentVersion:number;disabled:boolean;onRequest:()=>void;onCompare:(id:string)=>void;onReload?:()=>void}){
 const legacy=value?.method==='noul';const pending=value?.phase==='dispatching'||value?.phase==='prepared';const score=value?.choice;
 const title=pending?'実行中または結果不明':value?.outcome==='existing_candidate'?'既存技能の候補（未承認）':value?.outcome==='new_candidate'?'新規候補（未確定）':'要確認';
 return <section className="panel skill-suggestion"><h3>{legacy?'過去のnoul系提案':'Choiceによる既存技能の候補'}</h3>
 <p>固定した辞書版の定義・境界・対象・条件を比較します。同じ大分類や似た名前だけでは同一技能にしません。本人経験の認定・辞書の承認・別名追加は行いません。</p>
 {!legacy&&<><button disabled={disabled||pending} onClick={onRequest}>AIで既存技能の候補を照会</button>
 {pending&&onReload&&<button disabled={disabled} onClick={onReload}>保存された照会状態を再読込</button>}
 <p>閲覧可能で原文と同じ共有範囲の辞書全体をChoiceへ渡します。文字top3で絞り込みません。同じ方式・候補・辞書版の結果を再利用し、自動再送はしません。</p></>}
 {value&&<><h4>{title}</h4><p>辞書版 {value.dictionaryVersion} ／ 現在版 {currentVersion} ／ {legacy?'過去のnoul系処理（Choiceとは別方式）':'Choice・単一選択の参考結果'}</p>
 <p>{pending?'照会後の結果を確認しています。新しいキーでも同じ推論を再送しません。':value.reason==='AI_INPUTLIMIT'&&value.inferenceNotDispatched?'比較する原文・定義・条件を省略せず送信枠に収められませんでした。AIには送信していません。内容を人が確認してください。':value.reason==='AI_OUTPUTLIMIT'&&value.inferenceNotDispatched?'全選択肢の応答が既存の出力枠に収まることを確認できませんでした。AIには送信していません。':reasons[value.reason]??`判定を完了できませんでした：${value.reason}`}</p>
 {value.providerExecutionUncertain&&<p role="alert">プロバイダー側の実行結果が不明です。新規技能とは扱わず、再実行を停止しています。</p>}
 {value.stale&&<p role="alert">過去版の提案です。現在版の確認なしに承認へ使えません。</p>}
 {!legacy&&value.applicableCount!=null&&<p>対象の全技能 {value.applicableCount} 件。比較対象 {value.selectedCount} 件、有効なAI返値 {value.judgedCount??0} 件。{value.shortlistComplete?'全件を選択肢に含む':'上限により未送信'}</p>}
 {score&&!legacy&&<><p>選択：{value.concepts?.find(c=>c.id===score.selectedId)?.name??({none_of_the_above:'該当なし',insufficient_evidence:'根拠不足／判断保留'} as Record<string,string>)[score.selectedId]??score.selectedId} ／ confidence {probability(score.confidence)} ／ 1位−2位 {probability(score.gap)}</p>
 <p>該当なし {probability(score.probabilities.none_of_the_above)} ／ 根拠不足・判断保留 {probability(score.probabilities.insufficient_evidence)}</p>
 <p>生の確率合計 {score.probabilityValidation.sum.toFixed(4)}。{score.probabilityValidation.reason.startsWith('rounded_')?`小数${score.probabilityValidation.roundingDecimals}桁の丸めを許容して受理しました。`:'合計を検証して受理しました。'}確率の再正規化はしていません。</p></>}
 {!!value.concepts?.length&&<div style={{overflowX:'auto'}}><table><thead><tr><th>既存技能と定義・境界</th>{legacy?<><th>正順noul</th><th>逆順noul</th><th>最小値</th></>:<><th>Choiceの生の確率</th><th>選択状態</th></>}<th>比較</th></tr></thead><tbody>{value.concepts.map(c=>{
  const pair=value.comparisons?.find(x=>x.conceptId===c.id);return <tr key={c.id}><td><strong>{c.name}</strong><p>{c.definition}</p><p>境界：{c.boundary}</p><small>{c.id}</small></td>{legacy?<><td>{probability(pair?.forward)}</td><td>{probability(pair?.reverse)}</td><td>{probability(pair?.minimum)}</td></>:<><td>{probability(score?.probabilities[c.id])}</td><td>{score?.selectedId===c.id?'選択・未承認':score?.probabilities[c.id]!=null?'未選択':'未判定'}</td></>}<td><button disabled={disabled||pending||legacy||value.stale||currentVersion!==value.dictionaryVersion} onClick={()=>onCompare(c.id)}>この候補IDと比較</button></td></tr>;
 })}</tbody></table></div>}
 {legacy?<p>過去のnoulの0.8・双方向minを、Choiceの確率やconfidenceの基準へ流用しません。</p>:<p>確率・confidence・順位差は参考値です。自動承認の閾値は設けず、人が原文と辞書の定義を確認します。</p>}
 {value.aiRunId&&<small>照会記録：{value.aiRunId}</small>}
 </>}
 {!!value?.previousProposals?.length&&<details><summary>過去のnoul提案を見る（新しいChoiceに流用しません）</summary>{value.previousProposals.map((p,i)=><SkillSuggestionPanel key={i} value={p} currentVersion={currentVersion} disabled onRequest={()=>{}} onCompare={()=>{}}/>)}</details>}
 </section>;
}
