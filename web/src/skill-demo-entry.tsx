import {useEffect,useState} from 'react';
import {createRoot} from 'react-dom/client';
import {initializeAuth,getToken,authSessionSignal,type AppConfig} from './auth';
import './style.css';

function App(){
 const [config,setConfig]=useState<AppConfig>();const [busy,setBusy]=useState(false);const [error,setError]=useState('');
 useEffect(()=>{void fetch('/api/config').then(async r=>{if(!r.ok)throw new Error('接続設定を確認できません。');const c=await r.json();await initializeAuth(c);setConfig(c);}).catch(()=>setError('接続設定を確認できません。'));},[]);
 async function open(){
  if(!config)return;setBusy(true);setError('');
  try{const headers:Record<string,string>={'Content-Type':'application/json'};if(config.mode==='Azure')headers.Authorization=`Bearer ${await getToken(config)}`;
   const response=await fetch('/api/v1/skill-demo/start',{method:'POST',headers,signal:authSessionSignal()});
   if(!response.ok)throw new Error(response.status===403?'この操作には既存のoperator権限が必要です。':'デモを開始できません。時間をおいて開き直してください。');
   const data=await response.json();location.assign('/skill-dictionary.html?skillDemo='+encodeURIComponent(data.id));
  }catch(e){setError(e instanceof Error?e.message:'開始できません。');setBusy(false);}
 }
 return <main style={{maxWidth:900,margin:'auto',padding:24}}><h1>以前の架空資料で技能辞書を試す</h1>
  <p>以前の2資料から用意した6つの知識候補を、原文と比べて承認し、同じ技能IDで資料を検索できます。旧4案は保留の履歴として残します。</p>
  <ul><li>アプリ開発担当 ／ TypeScript、SQLite、Git</li><li>通信処理の開発担当 ／ Go、Linux、Git</li></ul>
  <p>変更は自分の操作用デモ内だけに保存します。共有辞書・共有資料は変更せず、AIを呼びません。デモは1時間、またはサーバーの停止・再起動で終了します。同じデモを開き直した場合は途中の状態を使います。</p>
  {error&&<p role="alert">{error}</p>}<button disabled={!config||busy} onClick={()=>void open()}>以前の2資料でデモを開く</button><p><a href="/validation.html">共有画面へ戻る</a></p>
 </main>;
}
createRoot(document.getElementById('root')!).render(<App/>);
