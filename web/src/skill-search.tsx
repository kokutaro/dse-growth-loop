import {useEffect,useRef,useState} from 'react';
import {authSessionSignal,getToken,type AppConfig} from './auth';
import {skillApi,skillDemoId} from './skill-demo-context';

export type SkillMatch={conceptId:string;eligibility:string;reviews:{action:string;reason:string;previousVersion:number;approvedVersion:number;createdAt:string}[]};
type Hit={candidateId:string;fragmentId:string;conceptId:string;skillName:string;raw:string;quote:string;evidenceId:string;experienceEligibility:string;reviews:SkillMatch['reviews']};
type Result={dictionaryVersion:number;beforeLimit:number;items:Hit[]};
export type SkillSearchResult={candidates:{entityId:string;entityType:string;title:string;routes:string[];units:{summary:string;sourceRefs:{id:string;documentTitle:string;location:string;text:string}[];confirmationState:string;skillMatch:SkillMatch}[]}[];matchedUnits:number;beforeLimit:number;rerankingStatus:string;skillDictionaryVersion:number};

export function SkillSearch({config,onResult,onInvalidate}:{config:AppConfig;onResult:(value:SkillSearchResult)=>void;onInvalidate:()=>void}){
 const [version,setVersion]=useState(()=>new URLSearchParams(location.search).get('skillVersion')??'');
 const [loaded,setLoaded]=useState<number>();const [concepts,setConcepts]=useState<{id:string;name:string}[]>([]);
 const [selected,setSelected]=useState('');const [eligibility,setEligibility]=useState('content_only');const [busy,setBusy]=useState(false);const [error,setError]=useState('');
 const epoch=useRef(0);useEffect(()=>()=>{epoch.current++;},[]);
 const invalidate=()=>{epoch.current++;onInvalidate();setError('');};
 async function request(path:string,body?:unknown){
  const headers:Record<string,string>={'Content-Type':'application/json'};
  if(config.mode==='Azure')headers.Authorization=`Bearer ${await getToken(config)}`;
  const response=await fetch(skillApi(path),{method:body===undefined?'GET':'POST',headers,...(body===undefined?{}:{body:JSON.stringify(body)}),signal:authSessionSignal(),cache:'no-store'});
  if(response.status===410)throw new Error('デモは終了しました。以前の2資料でデモを開き直してください。');
  if(!response.ok)throw new Error(response.status===403?'この検索には既存のoperator権限が必要です。':response.status===404?'指定した辞書版を確認できません。':response.status===422?'検索対象が上限を超えました。部分結果は表示しません。':'検索を完了できません。辞書版と現在の根拠を確認してください。');
  return response.json();
 }
 async function load(){
  invalidate();setLoaded(undefined);setConcepts([]);setSelected('');const n=Number(version);
  if(!version||!Number.isInteger(n)||n<0||n>2147483647){setError('辞書版を0以上の整数で指定してください。');return;}
  const current=epoch.current;setBusy(true);
  try{const data=await request('skill-dictionary?version='+n);if(current!==epoch.current)return;
   const active=data.concepts.filter((c:{state:string})=>c.state==='active');setConcepts(active);setSelected(active[0]?.id??'');setLoaded(data.version);
  }catch(e){if(current===epoch.current)setError(e instanceof Error?e.message:'辞書を読み込めません。');}finally{if(current===epoch.current)setBusy(false);}
 }
 async function search(e:React.SubmitEvent){
  e.preventDefault();invalidate();if(loaded===undefined||!selected)return;const current=epoch.current;setBusy(true);
  try{const data:Result=await request('skill-dictionary/search',{version:loaded,conceptId:selected,eligibility});if(current!==epoch.current)return;
   // These are evidence records, not fabricated people or endorsements of proficiency.
   onResult({skillDictionaryVersion:data.dictionaryVersion,matchedUnits:data.items.length,beforeLimit:data.beforeLimit,rerankingStatus:'not_performed',
    candidates:data.items.map(h=>({entityId:h.candidateId,entityType:'knowledge',title:h.skillName+'を含む資料',routes:['skillDictionary'],units:[{
     summary:h.raw,confirmationState:'dictionary_evidence',sourceRefs:[{id:h.evidenceId,documentTitle:h.fragmentId,location:'原文根拠',text:h.quote}],
     skillMatch:{conceptId:h.conceptId,eligibility:h.experienceEligibility,reviews:h.reviews}
    }]}))});
  }catch(e){if(current===epoch.current)setError(e instanceof Error?e.message:'検索できません。');}finally{if(current===epoch.current)setBusy(false);}
 }
 return <form onSubmit={e=>void search(e)}>
  {skillDemoId()!==undefined&&<p>操作用デモ：変更は自分のメモリ内だけに保存します。<a href="/skill-demo.html">デモを開き直す</a></p>}
  <p>承認済みの技能IDで資料を探します。既存operatorの閲覧範囲に限り、AIを呼ばずに検索します。</p>
  <label>固定する辞書版<input type="number" min="0" step="1" value={version} disabled={busy} onChange={e=>{invalidate();setVersion(e.target.value);setLoaded(undefined);setConcepts([]);setSelected('');}}/></label>
  <button type="button" disabled={busy} onClick={()=>void load()}>この版を読み込む</button>
  {loaded!==undefined&&<p>読み込んだ辞書版：{loaded}（根拠・承認状態は検索時点で再確認）</p>}
  <label htmlFor="skill-search-concept">承認済み技能</label><select id="skill-search-concept" disabled={busy||loaded===undefined} value={selected} onChange={e=>{invalidate();setSelected(e.target.value);}}><option value="">選択してください</option>{concepts.map(c=><option key={c.id} value={c.id}>{c.name} ／ {c.id}</option>)}</select>
  <label htmlFor="skill-search-content">検索する内容</label><select id="skill-search-content" disabled={busy} value={eligibility} onChange={e=>{invalidate();setEligibility(e.target.value);}}><option value="content_only">知識の記載</option><option value="eligible">実務経験の候補</option></select>
  <small>知識の記載は本人の実務経験・熟練度の認定ではありません。指定版にない技能は最新版へ自動変換しません。</small>
  {error&&<p role="alert">{error}</p>}
  <button disabled={busy||loaded===undefined||!selected}>{busy?'検索中…':'承認済み技能で検索'}</button>
 </form>;
}
