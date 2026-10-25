import {useEffect,useState} from 'react';
import {createRoot} from 'react-dom/client';
import {initializeAuth,type AppConfig} from './auth';
import {SkillDictionary} from './skill-dictionary';
import './style.css';
function App(){const [config,setConfig]=useState<AppConfig>();const [error,setError]=useState('');useEffect(()=>{void fetch('/api/config').then(async r=>{if(!r.ok)throw new Error('接続設定を確認できません。');const c=await r.json();await initializeAuth(c);setConfig(c);}).catch(e=>setError(String(e)));},[]);return <main style={{maxWidth:1160,margin:'0 auto',padding:24}}>{error?<p role="alert">{error}</p>:config?<SkillDictionary config={config}/>:<p>読み込み中</p>}</main>;}
createRoot(document.getElementById('root')!).render(<App/>);
