import {useEffect,useRef,useState} from 'react';
import {SkillSuggestionPanel,type SkillSuggestion} from './skill-suggestion';
import {authSessionSignal,getToken,type AppConfig} from './auth';

type Element={label:string;object:string|null;conditions:string[];kind:string;subjectScope:string;participation:string};
type Concept={id:string;name:string;definition:string;boundary:string;scopeIds:string[];state:string;redirectId:string|null;aliases:{text:string;kind:string;object:string|null;conditions:string[]}[]};
type Dictionary={version:number;currentVersion:number;etag:string;concepts:Concept[]};
type Candidate={sourceStatus?:string;reviewable?:boolean;candidateType?:string;sourceInputState?:string;id:string;etag:string;state:string;conceptId:string|null;fragmentId:string;scopeIds:string[];element:Element|null;item:{raw:string;quote:string;evidenceId:string;participation:string;normalizationCandidate:string|null;analysis:{experienceState:string;quoteState:string;semanticSupport:string}}|null};
type ReviewCandidate=Candidate & {element:Element;item:NonNullable<Candidate['item']>};
type SourceReview={candidateId:string;fragmentId:string;sourceStatus:string;currentFingerprint:string;guardVersion:string;inputState:string;acceptedCount:number;currentText:string;evidence:{id:string;text:string}[];rejected:{reason:string;raw:string|null;participation:string}[]};
const holdReasons:Record<string,string>={raw_not_in_quote:'元表現が引用内にない',quote_not_in_source:'引用が原文と一致しない',not_in_fragment:'元表現が現在の実績本文にない',incomplete_passage:'文の一部だけの引用',no_completed_assertion:'実施を確定できない',negated_assertion:'実施の否定',reported_or_intended:'希望・計画・相談・伝聞',other_actor:'他者の実績',duplicate_candidate:'重複候補',unknown_evidence:'根拠IDが現在の原文根拠に含まれない'};
type Write={path:string;key:string;etag?:string;body:unknown};
const labels:Record<string,string>={pending:'未登録・確認待ち',matched:'既存技能に対応',registered:'新規登録済み',alias:'別名承認済み',merged:'統合済み',not_skill:'技能ではない',deferred:'保留',practice:'実務',knowledge:'知識',reference:'参照',qualification:'資格',performed:'実施（抽出値）',joint:'共同（抽出値）',assisted:'補助（抽出値）',aspiration:'希望',consultation:'相談',other_person:'他者経験',negated:'否定',unclear:'不明',eligible:'経験候補・本人未確認',content_only:'知識内容のみ',pending_eligibility:'本人関与未確定'};
const name=(s:string)=>s==='non_practice'?'実務経験としては扱わない':labels[s]??s;
class ApiError extends Error{constructor(public status:number,public code:string){super(code);}}
export function SkillDictionary({config}:{config:AppConfig}){
 const [sourceReview,setSourceReview]=useState<SourceReview>();const sourceEpoch=useRef(0);const sending=useRef(false);
 const [suggestion,setSuggestion]=useState<SkillSuggestion>();
 const [dictionary,setDictionary]=useState<Dictionary>();const [items,setItems]=useState<Candidate[]>([]);const [cursor,setCursor]=useState<string|null>(null);
 const [selected,setSelected]=useState<ReviewCandidate>();const [fragment,setFragment]=useState('');const [action,setAction]=useState('defer');
 const [target,setTarget]=useState('');const [standard,setStandard]=useState('');const [definition,setDefinition]=useState('');const [boundary,setBoundary]=useState('');const [reason,setReason]=useState('');
 const [error,setError]=useState('');const [message,setMessage]=useState('');const [busy,setBusy]=useState(false);const [uncertain,setUncertain]=useState(false);
 const [checked,setChecked]=useState<string[]>([]);const [version,setVersion]=useState('0');const [preview,setPreview]=useState<{dictionaryVersion:number;items:{candidateId:string;state:string;mapping:{state:string;conceptId:string|null};experienceEligibility:string}[]}>();
 const [history,setHistory]=useState<{action:string;reason:string;previousVersion:number;approvedVersion:number;createdAt:string;ownerId:string}[]>();
 const pending=useRef<Write|undefined>(undefined);const lifetime=useRef(new AbortController());
 useEffect(()=>{void read(refresh);return()=>lifetime.current.abort();},[]);
 function clearEvidence(){sourceEpoch.current++;setSourceReview(undefined);setSuggestion(undefined);setSelected(undefined);setItems([]);setPreview(undefined);setHistory(undefined);setChecked([]);}
 async function request(path:string,init?:RequestInit){
  const headers=new Headers(init?.headers);if(init?.body)headers.set('Content-Type','application/json');
  if(config.mode==='Azure')headers.set('Authorization',`Bearer ${await getToken(config)}`);
  const response=await fetch('/api/v1/'+path,{...init,headers,signal:AbortSignal.any([lifetime.current.signal,authSessionSignal()])});
  if(!response.ok){const x=await response.json().catch(()=>({}));throw new ApiError(response.status,x.code??'接続エラー');}return response.json();
 }
 async function refresh(){
  // Both read responses hold the same tenant fence through serialization.
  const d=await request('skill-dictionary');const list=await request('skill-candidates');
  sourceEpoch.current++;setSourceReview(undefined);setSuggestion(undefined);setDictionary(d);setItems(list.items);setCursor(list.continuationToken);setSelected(undefined);setHistory(undefined);setChecked([]);
 }
 function failed(e:unknown){
  if(e instanceof ApiError&&(e.status===403||e.status===404)){clearEvidence();setDictionary(undefined);setError('権限または根拠の閲覧条件を確認できません。');return;}
  clearEvidence();setDictionary(undefined);setError(e instanceof ApiError?`${e.code}（${e.status}）`:e instanceof Error?e.message:'接続できません。');
 }
 async function read(work:()=>Promise<void>){setBusy(true);setError('');try{await work();}catch(e){failed(e);}finally{setBusy(false);}}
 async function send(op:Write){
  if(sending.current)return;sending.current=true;
  pending.current=op;setBusy(true);setError('');setUncertain(false);
  try{
   const data=await request(op.path,{method:'POST',headers:{'Idempotency-Key':op.key,...(op.etag?{'If-Match':op.etag}:{})},body:JSON.stringify(op.body)});
   pending.current=undefined;
   if(op.path.endsWith('/suggest')){if(selected)setSuggestion(data);else await refresh();setMessage('候補提案を保存しました。辞書・人物経験・正解データは変更していません。再送後は一覧から同じ候補を選ぶと保存結果を確認できます。');return;}
   setMessage(op.path.endsWith('collect')?`収集状態：${({undetermined:'判断不能（unknown）',none:'評価済み・該当なし（空集合）',not_evaluated:'未評価',held:'根拠の確認待ち',assessed:'候補を確認可能'} as Record<string,string>)[data.state]??data.state}。新規候補 ${data.created} 件。辞書版 ${data.dictionaryVersion}`:`判断を保存しました。辞書版 ${data.dictionaryVersion}。人物経験や正解ラベルは更新していません。`);
   try{await refresh();}catch(e){failed(e);setMessage('保存は完了しました。表示の再読み込みが必要です。');}
  }catch(e){
   if(e instanceof ApiError&&e.status<500){pending.current=undefined;if(e.status===412||e.code==='SKILL_SOURCE_CHANGED'){setSelected(undefined);setHistory(undefined);await refresh().catch(failed);setError('別の承認または根拠更新があります。最新を読み直し、候補と辞書を再確認してください。');}else failed(e);}
   else{clearEvidence();setUncertain(true);setError('結果を確認できません。同じ操作を同じキーで再送できます。新しい判断は停止しています。');}
  }finally{sending.current=false;setBusy(false);}
 }
 function choose(c:Candidate){
  const epoch=++sourceEpoch.current;setSourceReview(undefined);
  if(c.reviewable===false||!c.item||!c.element){setSelected(undefined);setSuggestion(undefined);setHistory(undefined);void read(async()=>{const data=await request(`skill-candidates/${c.id}/source`);if(sourceEpoch.current===epoch)setSourceReview(data);});return;}
  setSuggestion(undefined);setSelected({...c,item:c.item,element:c.element});setAction('defer');setTarget('');setStandard(c.element.label);setDefinition('');setBoundary('');setReason('');setHistory(undefined);void read(async()=>setSuggestion(await request(`skill-candidates/${c.id}/suggestion?version=${dictionary!.version}`)));}
 const active=dictionary?.concepts.filter(c=>c.state==='active')??[];const targetConcept=active.find(c=>c.id===target);
 const targets=active.filter(c=>!selected||c.scopeIds.length===selected.scopeIds.length&&c.scopeIds.every(s=>selected.scopeIds.includes(s))&&c.aliases.every(a=>a.kind===selected.element.kind));
 const allowed=selected&&['pending','deferred','matched'].includes(selected.state);
 const canSave=selected&&dictionary&&!busy&&!uncertain&&reason.trim()&&(action==='merge'?selected.conceptId&&target&&target!==selected.conceptId:allowed)&&(action==='new'?standard.trim()&&definition.trim()&&boundary.trim():action==='alias'?target:true);
 return <div className="skill-dictionary"><h1>共通技能辞書と候補レビュー</h1>
 {config.mode==='Demo'&&<p className="note">ローカルの仮データ検証画面です。実AIの精度評価ではありません。</p>}
 <p>原文と関与を確認し、人が辞書を育てます。辞書登録は人物経験の認定・公開ではありません。正解データと既存の検索語も変更しません。</p>
 {error&&<p role="alert">{error}</p>}{message&&<p role="status">{message}</p>}
 {uncertain&&<button disabled={busy} onClick={()=>pending.current&&void send(pending.current)}>同じ操作を再送</button>}
 {!dictionary?<button onClick={()=>void read(refresh)} disabled={busy}>権限を確認して読み込む</button>:<>
 <section className="panel"><h2>抽出結果から候補を取り込む</h2><p>現在の辞書版 {dictionary.version}。根拠を閲覧できる既存の実績IDを指定します。AIの追加実行はありません。</p>
 <label>実績・知識ID<input value={fragment} onChange={e=>setFragment(e.target.value)}/></label><button disabled={busy||uncertain||!fragment.trim()} onClick={()=>void send({path:'skill-candidates/collect',key:crypto.randomUUID(),body:{fragmentId:fragment}})}>保存済み抽出を収集</button>
 </section>
 <section className="panel"><h2>候補一覧</h2><p>未登録は誤りではありません。判断不能と該当なしは収集状態で区別します。</p>
 <div style={{overflowX:'auto'}}><table><thead><tr><th>選択</th><th>元表現・比較要素</th><th>種別・関与</th><th>状態</th><th>確認</th></tr></thead><tbody>{items.map(c=><tr key={c.id}>
 <td><input type="checkbox" disabled={c.reviewable===false} aria-label={`候補 ${c.id} をプレビュー`} checked={checked.includes(c.id)} onChange={e=>setChecked(xs=>e.target.checked?[...xs,c.id]:xs.filter(x=>x!==c.id))}/></td>
 <td>{c.item?.raw??'現在の根拠の再確認が必要'}<br/><small>{c.element&&<>要素：{c.element.label}<br/></>}元資料：{c.fragmentId}</small></td><td>{c.element?`${name(c.element.kind)} ／ ${name(c.element.participation)}`:'技能の認定前'}</td><td>{c.sourceStatus==='changed'?'根拠更新・再収集が必要':c.candidateType==='collection'?`収集の確認待ち（${c.sourceInputState}）`:name(c.state)}</td><td><button onClick={()=>choose(c)} disabled={busy||uncertain}>この候補を確認</button></td></tr>)}</tbody></table></div>
 {items.length===0&&<p>現在の権限で確認できる候補はありません。</p>}{cursor&&<button disabled={busy} onClick={()=>void read(async()=>{const list=await request('skill-candidates?continuationToken='+encodeURIComponent(cursor));setItems(xs=>[...xs,...list.items]);setCursor(list.continuationToken);})}>次の候補を読む</button>}
 </section>
 {sourceReview&&<section className="panel source-review"><h2>現在の根拠を再確認・再収集</h2>
 <p>{sourceReview.sourceStatus==='changed'?'根拠または検証版が更新されています。古い引用・提案は表示せず、承認できません。':'保留された抽出の確認です。辞書未登録とは別の状態です。'}</p>
 <p>元資料：{sourceReview.fragmentId} ／ 入力状態：{sourceReview.inputState} ／ 検証版：{sourceReview.guardVersion}</p>
 <h3>現在の本文</h3><blockquote>{sourceReview.currentText}</blockquote>
 <details><summary>現在アクセス可能な原文根拠</summary>{sourceReview.evidence.map(e=><div key={e.id}><p>根拠ID：{e.id}</p><blockquote>{e.text}</blockquote></div>)}</details>
 <p>現在の検証で保持した抽出 {sourceReview.acceptedCount} 件。保留 {sourceReview.rejected.length} 件。</p>
 <ul>{sourceReview.rejected.map((r,i)=><li key={i}>{r.raw??'原文に一致する元表現を確認できません'} ／ {holdReasons[r.reason]??'検証保留'}（{r.reason}） ／ {name(r.participation)}</li>)}</ul>
 <p>原文や保存済み抽出の修正はこの画面では行いません。再収集は現在の根拠で再検証し、解消していない保留は残します。AI追加実行・辞書承認・人物経験の認定は行いません。</p>
 <button disabled={busy||uncertain} onClick={()=>void send({path:'skill-candidates/collect',key:crypto.randomUUID(),body:{fragmentId:sourceReview.fragmentId,expectedFingerprint:sourceReview.currentFingerprint}})}>現在の根拠で再収集</button>
 <button disabled={busy} onClick={()=>{sourceEpoch.current++;setSourceReview(undefined);}}>一覧へ戻る</button>
 </section>}
 {selected&&<section className="panel candidate-review"><h2>根拠と辞書を比較</h2><button disabled={busy} onClick={()=>{sourceEpoch.current++;setSelected(undefined);setSuggestion(undefined);setHistory(undefined);}}>一覧へ戻る</button>
 <div className="skill-review-columns"><div><h3>抽出候補</h3><strong>{selected.item.raw}</strong><p>要素：{selected.element.label} ／ 対象：{selected.element.object??'明記なし'}</p><p>条件：{selected.element.conditions.join('、')||'明記なし'}</p>
 <p>{name(selected.element.kind)} ／ {name(selected.element.participation)} ／ 主体：{selected.element.subjectScope}</p><p>経験判定：{selected.item.analysis.experienceState==='pending'?name('pending_eligibility'):name(selected.item.analysis.experienceState)}</p>
 <blockquote>{selected.item.quote}</blockquote><p>根拠ID：{selected.item.evidenceId}</p><p>引用整合性：{selected.item.analysis.quoteState==='quote_verified'?'原文一致（機械検証）':'未確認'}。意味支持・本人経験の確認とは別です。</p><p>AI正規化案（未承認）：{selected.item.normalizationCandidate??'なし'}</p></div>
 <div><h3>既存の承認済み辞書</h3><p>共有範囲と種別が同じ技能だけを選択できます。範囲は元資料から広げません。</p><label>比較・統合先の技能<select value={target} onChange={e=>setTarget(e.target.value)}><option value="">選択してください</option>{targets.map(c=><option key={c.id} value={c.id}>{c.name} ({c.id})</option>)}</select></label>
 {targetConcept?<><p>標準名：{targetConcept.name}</p><p>定義：{targetConcept.definition}</p><p>境界：{targetConcept.boundary}</p><p>別名：{targetConcept.aliases.map(a=>a.text).join('、')}</p></>:<p>同じ上位カテゴリだけでは統合しません。</p>}
 <p>辞書版 {dictionary.version} に対する判断です。種別・対象・条件・役割は候補に残ります。</p></div></div>
 <SkillSuggestionPanel value={suggestion} currentVersion={dictionary.version} disabled={busy||uncertain||!allowed} onRequest={()=>void send({path:`skill-candidates/${selected.id}/suggest`,key:crypto.randomUUID(),etag:selected.etag,body:{version:dictionary.version,dictionaryETag:dictionary.etag}})} onReload={()=>void read(async()=>setSuggestion(await request(`skill-candidates/${selected.id}/suggestion?version=${dictionary.version}`)))} onCompare={id=>{if(targets.some(c=>c.id===id)&&!suggestion?.stale&&suggestion?.dictionaryVersion===dictionary.version)setTarget(id);}}/>
 <label>レビュー判断<select value={action} onChange={e=>setAction(e.target.value)}><option value="defer">保留</option><option value="alias">既存技能の別名として承認</option><option value="new">新しい技能として登録</option><option value="merge">既存技能を統合（旧IDを保持）</option><option value="not_skill">技能ではない</option></select></label>
 {action==='new'&&<><label>標準名<input value={standard} maxLength={100} onChange={e=>setStandard(e.target.value)}/></label><label>技能の定義<textarea value={definition} maxLength={500} onChange={e=>setDefinition(e.target.value)}/></label><label>含む範囲・含めない範囲<textarea value={boundary} maxLength={500} onChange={e=>setBoundary(e.target.value)}/></label></>}
 {action==='merge'&&<p>統合元ID：{selected.conceptId??'未登録のため統合元なし。まず既存への別名追加を検討してください。'}</p>}
 <label>確認理由<textarea value={reason} maxLength={500} onChange={e=>setReason(e.target.value)}/></label>
 <button disabled={!canSave} onClick={()=>{if(!selected||!dictionary)return;void send({path:`skill-candidates/${selected.id}/review`,key:crypto.randomUUID(),etag:selected.etag,body:{action,reason,expectedVersion:dictionary.version,dictionaryETag:dictionary.etag,...(['alias','merge'].includes(action)?{targetId:target}:{}),...(action==='merge'?{sourceId:selected.conceptId}:{}),...(action==='new'?{name:standard,definition,boundary}:{})}});}}>判断を承認して保存</button>
 <button disabled={busy} onClick={()=>void read(async()=>setHistory((await request(`skill-candidates/${selected.id}/history`)).items))}>承認履歴を見る</button>
 {history&&<ul>{history.map((h,i)=><li key={i}>{h.action} ／ {h.reason} ／ 辞書 {h.previousVersion} → {h.approvedVersion} ／ {h.ownerId} ／ {h.createdAt}</li>)}</ul>}
 </section>}
 <section className="panel"><h2>過去候補の再分類プレビュー</h2><p>指定した承認済みの版で確認します。既存レコード・評価gold・検索投影は書き換えません。</p><label>再分類に使う辞書版<input type="number" min="0" max={dictionary.currentVersion} value={version} onChange={e=>setVersion(e.target.value)}/></label>
 <button disabled={busy||!checked.length||!Number.isInteger(Number(version))} onClick={()=>void read(async()=>setPreview(await request('skill-dictionary/preview',{method:'POST',body:JSON.stringify({version:Number(version),candidateIds:checked})})))}>選択候補を固定版でプレビュー</button>
 {preview&&<><h3>辞書版 {preview.dictionaryVersion} の固定プレビュー</h3><table><thead><tr><th>候補ID</th><th>保存状態</th><th>この版の対応先</th><th>経験適格性（変更なし）</th></tr></thead><tbody>{preview.items.map(x=><tr key={x.candidateId}><td>{x.candidateId}</td><td>{name(x.state)}</td><td>{x.mapping.conceptId??({unregistered:'未登録',undetermined:'判断不能',not_skill:'技能ではない'} as Record<string,string>)[x.mapping.state]??x.mapping.state}</td><td>{x.experienceEligibility}</td></tr>)}</tbody></table></>}
 </section>
 <details><summary>承認済み辞書と旧ID</summary>{dictionary.concepts.length===0?<p>承認済み技能はまだありません。</p>:dictionary.concepts.map(c=><p key={c.id}>{c.name} ／ {c.id} ／ {c.state}{c.redirectId?` → ${c.redirectId}`:''}</p>)}</details>
 </>}
 </div>;
}
