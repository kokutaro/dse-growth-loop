import {test,expect} from '@playwright/test';
import {mkdir} from 'node:fs/promises';
const folder='screenshots/'+new Date().toISOString().replace(/[-:]/g,'').replace('T','-').slice(0,15)+'-durable-workflow';
test.beforeAll(async()=>{await mkdir(folder,{recursive:true});});

for(const change of ['document','return','version','failure','withdraw'])test(`遅い断片応答を現在の資料へ混ぜない（${change}）`,async({page,request})=>{
 let sourceVersion=1;let release!:()=>void;let arrived!:()=>void;
 let withdrawn=false;let releaseDocument!:()=>void;let documentArrived!:()=>void;
 const heldDocument=new Promise<void>(r=>{releaseDocument=r;});const documentRequested=new Promise<void>(r=>{documentArrived=r;});
 const held=new Promise<void>(r=>{release=r;});const requested=new Promise<void>(r=>{arrived=r;});
 await page.route('**/api/v1/documents**',async route=>{
  const url=new URL(route.request().url());const id=url.pathname.split('/')[4];
  if(url.pathname==='/api/v1/documents'){await route.fulfill({json:{items:['doc-delayed-a','doc-delayed-b'].map(id=>({id,title:id,sourceVersion:1,status:'ready',currentVersionId:id+'-v1',etag:'one'})),continuationToken:null}});return;}
  if(url.pathname.endsWith('/withdraw')){withdrawn=true;await route.fulfill({json:{}});return;}
  if(url.pathname.endsWith('/skill-fragments')){arrived();await held;await route.fulfill(change==='failure'?{status:400,json:{code:'STALE_FRAGMENT_ERROR'}}:{json:{items:[{id:'stale-a',description:'遅れた旧資料の断片',contentOnly:true,historyOnly:false}]}});return;}
  if(url.pathname.endsWith('/evidence')){await route.fulfill({json:{items:[],continuationToken:null}});return;}
  if(withdrawn){documentArrived();await heldDocument;}
  await route.fulfill({headers:{ETag:'one'},json:{id,sourceVersion,activeVersion:withdrawn?null:sourceVersion,accessVersion:sourceVersion,status:withdrawn?'withdrawn':'ready',warnings:[]}});
 });
 const workflow=(await(await request.post('/api/v1/demo-workflows/start')).json()).id;
 await page.goto('/validation.html?workflow='+workflow);await page.getByRole('button',{name:'資料一覧を更新',exact:true}).click();
 await page.getByRole('button',{name:'doc-delayed-a（第1版）',exact:true}).click();
 await page.getByRole('button',{name:'技能候補の元になる断片を確認',exact:true}).click();await requested;
 if(change==='withdraw'){await page.getByLabel('訂正・撤回の理由').fill('架空資料の遅延応答試験');await page.getByRole('button',{name:'この資料を撤回する',exact:true}).click();await documentRequested;}
 else if(change==='version'){sourceVersion=2;const refreshed=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/documents/doc-delayed-a/evidence');await page.getByRole('button',{name:'進捗を更新',exact:true}).click();await refreshed;}
 else{await page.getByRole('button',{name:'doc-delayed-b（第1版）',exact:true}).click();if(change==='return')await page.getByRole('button',{name:'doc-delayed-a（第1版）',exact:true}).click();}
 const response=page.waitForResponse(r=>r.url().endsWith('/doc-delayed-a/skill-fragments'));release();await response;
 await page.screenshot({path:folder+'/late-fragments-'+change+'.png'});
 await expect(page.getByText('遅れた旧資料の断片',{exact:false})).toHaveCount(0);
 await expect(page.getByRole('link',{name:'この断片の保存済み抽出を確認する',exact:true})).toHaveCount(0);
 await expect(page.getByText('STALE_FRAGMENT_ERROR',{exact:false})).toHaveCount(0);
 releaseDocument();
});

test('以前の架空原文の承認を通常の永続検索・画面・撤回につなぐ',async({page,request})=>{
 await page.setViewportSize({width:1920,height:1080});
 await page.goto('/skill-demo.html');await page.getByRole('button',{name:'以前の2資料でデモを開く',exact:true}).click();
 await expect(page).toHaveURL(/skill-dictionary.html\?workflow=wf-[a-f0-9]{48}$/);
 await expect(page.locator('tbody tr')).toHaveCount(8);
 const workflow=new URL(page.url()).searchParams.get('workflow')!;const headers={'X-Dse-Workflow':workflow};
 const list=await request.get('/api/v1/skill-candidates',{headers});expect(list.ok(),await list.text()).toBeTruthy();const candidates=(await list.json()).items.filter((c:{item?:{raw:string}})=>c.item?.raw==='Git');let concept='';
 for(const candidate of candidates){
  await page.getByLabel(`候補 ${candidate.id} をプレビュー`).locator('..').locator('..').getByRole('button',{name:'この候補を確認'}).click();
  await page.getByLabel('レビュー判断').selectOption(concept?'alias':'new');
  if(concept)await page.getByLabel('比較・統合先の技能').selectOption(concept);
  else{await page.getByLabel('技能の定義').fill('Gitの記載知識');await page.getByLabel('含む範囲・含めない範囲').fill('本人の経験を認定しない');}
  await page.getByLabel('確認理由').fill('以前の架空原文の知識であることを確認');await page.getByRole('button',{name:'判断を承認して保存'}).click();await expect(page.locator('.candidate-review')).toHaveCount(0);
  concept=(await(await request.get('/api/v1/skill-candidates',{headers})).json()).items.find((c:{id:string})=>c.id===candidate.id).conceptId;
 }
 await page.reload();await expect(page.getByText(/現在の辞書版 1。/)).toBeVisible();await page.screenshot({path:folder+'/01-approved.png',fullPage:true});
 await page.getByRole('link',{name:'この辞書版で資料を探す',exact:true}).click();
 await expect(page).toHaveURL(new RegExp('workflow='+workflow));await page.getByRole('button',{name:'この版を読み込む',exact:true}).click();
 await page.getByLabel('承認済み技能',{exact:true}).selectOption(concept);await page.getByRole('button',{name:'承認済み技能で検索',exact:true}).click();
 await expect(page.locator('article.candidate')).toHaveCount(2);
 await page.screenshot({path:folder+'/02-normal-search.png',fullPage:true});
 const response=await request.post('/api/search',{headers,data:{categories:[],signalIds:[],terms:[],skillIds:[concept],skillDictionaryVersion:1}});expect(response.ok()).toBeTruthy();
 const result=await response.json();expect(result.candidates).toHaveLength(2);expect(result.candidates.every((c:{entityType:string})=>c.entityType==='document')).toBeTruthy();
 const doc=result.candidates[0];await page.getByRole('button',{name:'資料一覧を更新',exact:true}).click();await page.getByRole('button',{name:doc.title+'（第1版）',exact:true}).click();
 await page.getByLabel('訂正・撤回の理由').fill('架空資料の撤回を検証');await page.getByRole('button',{name:'この資料を撤回する',exact:true}).click();await expect(page.getByText('この資料は撤回済みです。以前の原文は検索・候補確認に使いません。')).toBeVisible();
 await page.getByRole('button',{name:'承認済み技能で検索',exact:true}).click();await expect(page.locator('article.candidate')).toHaveCount(1);
 await page.screenshot({path:folder+'/03-withdrawn-search.png',fullPage:true});
 expect((await(await request.get('/api/v1/skill-dictionary')).json()).version).toBe(0);
});

test('同じ架空原本を通常画面で取り込み、抽出を候補へ収集し訂正版を処理できる',async({page,request})=>{
 await page.setViewportSize({width:1920,height:1080});const workflow=(await(await request.post('/api/v1/demo-workflows/start')).json()).id;const headers={'X-Dse-Workflow':workflow};
 // Each isolated run explicitly approves its own fictional Git definition;
 // never depend on a preceding browser test having changed the dictionary.
 const initialDictionary=await(await request.get('/api/v1/skill-dictionary',{headers})).json();
 const gitCandidate=(await(await request.get('/api/v1/skill-candidates',{headers})).json()).items.find((c:{fragmentId:string;item?:{raw:string}})=>c.fragmentId==='fragment-technical-list-a-distinct'&&c.item?.raw==='Git');
 const approval=await request.post('/api/v1/skill-candidates/'+gitCandidate.id+'/review',{headers:{...headers,'If-Match':gitCandidate.etag,'Idempotency-Key':crypto.randomUUID()},data:{action:'new',reason:'このテスト専用の架空Git知識を明示承認',expectedVersion:initialDictionary.version,dictionaryETag:initialDictionary.etag,name:'Git',definition:'Gitの記載知識',boundary:'本人の実務経験を認定しない'}});expect(approval.ok(),await approval.text()).toBeTruthy();
 // Reuse the existing fictional source bytes, never a new real document or AI call.
 const original=await request.get('/api/v1/documents/doc-technical-list-a/original',{headers});expect(original.ok()).toBeTruthy();const bytes=await original.body();
 await page.goto('/validation.html?workflow='+workflow);await page.getByLabel('文書名',{exact:true}).fill('以前の架空資料・通常取込');
 await page.getByLabel('原本ファイル',{exact:true}).setInputFiles({name:'fictional.docx',mimeType:'application/vnd.openxmlformats-officedocument.wordprocessingml.document',buffer:bytes});
 await page.getByRole('button',{name:'アップロード',exact:true}).click();await expect(page.locator('.documents')).toContainText('AI構造化：検索投影済み',{timeout:20000});
 await expect(page.getByRole('heading',{name:'技能ID検索への次の手順',exact:true})).toBeVisible();
 await expect(page.getByRole('link',{name:'この断片の保存済み抽出を確認する',exact:true})).toHaveCount(1);
 await page.screenshot({path:folder+'/04-normal-ingestion.png',fullPage:true});
 await page.getByRole('link',{name:'この断片の保存済み抽出を確認する',exact:true}).click();await expect(page).toHaveURL(/workflow=wf-/);
 await page.getByRole('button',{name:'保存済み抽出を収集',exact:true}).click();await expect(page.getByRole('status')).toContainText('収集状態：');
 await expect(page.getByRole('button',{name:'保存済み抽出を収集',exact:true})).toBeEnabled();
 const document=(await(await request.get('/api/v1/documents',{headers})).json()).items.find((d:{title:string})=>d.title==='以前の架空資料・通常取込');
 const dictionary=await(await request.get('/api/v1/skill-dictionary',{headers})).json();const git=dictionary.concepts.find((c:{name:string})=>c.name==='Git').id;
 const search=async()=>{const r=await request.post('/api/search',{headers,data:{categories:[],signalIds:[],terms:[],skillIds:[git],skillDictionaryVersion:dictionary.version}});expect(r.ok()).toBeTruthy();return(await r.json()).candidates;};
 expect((await search()).some((c:{entityId:string})=>c.entityId===document.id)).toBeTruthy();
 await page.getByRole('link',{name:'通常の取込・検索画面へ',exact:true}).click();
 await page.getByRole('button',{name:'資料一覧を更新',exact:true}).click();await page.getByRole('button',{name:'以前の架空資料・通常取込（第1版）',exact:true}).click();await page.getByLabel('訂正・撤回の理由').fill('同じ架空原文の版管理を検証');
 await page.getByLabel('訂正版の原本',{exact:true}).setInputFiles({name:'fictional-revised.docx',mimeType:'application/vnd.openxmlformats-officedocument.wordprocessingml.document',buffer:bytes});
 await page.getByRole('button',{name:'訂正版を取り込む',exact:true}).click();await expect(page.getByText('文書ID：'+document.id+' ／ 版：2',{exact:true})).toBeVisible();await expect(page.locator('.documents')).toContainText('AI構造化：検索投影済み',{timeout:20000});
 expect((await search()).some((c:{entityId:string})=>c.entityId===document.id)).toBeFalsy();
 const correctedDocument=await request.get('/api/v1/documents/'+document.id,{headers});expect(correctedDocument.ok(),await correctedDocument.text()).toBeTruthy();const correctedState=await correctedDocument.json();expect(correctedState.activeVersion,JSON.stringify(correctedState)).toBe(2);
 const currentFragments=await request.get('/api/v1/documents/'+document.id+'/skill-fragments',{headers});expect(currentFragments.ok(),await currentFragments.text()).toBeTruthy();expect((await currentFragments.json()).items).toHaveLength(1);
 await page.getByRole('button',{name:'技能候補の元になる断片を確認',exact:true}).click();await page.getByRole('link',{name:'この断片の保存済み抽出を確認する',exact:true}).click();
 await page.getByRole('button',{name:'保存済み抽出を収集',exact:true}).click();await expect(page.getByRole('status')).toContainText('収集状態：');
 await expect(page.getByRole('button',{name:'保存済み抽出を収集',exact:true})).toBeEnabled();
 const corrected=(await search()).find((c:{entityId:string})=>c.entityId===document.id);expect(corrected).toBeDefined();expect(corrected.units.every((u:{evidenceRefs:{sourceVersion:number}[]})=>u.evidenceRefs.every(e=>e.sourceVersion===2))).toBeTruthy();
 await page.screenshot({path:folder+'/05-normal-correction.png',fullPage:true});
});

for(const pending of ['shown','terms','natural','skills','conditions'] as const)test(`撤回成功で原文と検索を即時失効し遅い応答を戻さない（${pending}）`,async({page,request})=>{
 await page.setViewportSize({width:1920,height:1080});
 const workflow=(await(await request.post('/api/v1/demo-workflows/start')).json()).id;
 let withdrawn=false,searches=0,conditions=0,collections=0;
 let searchArrived!:()=>void,releaseSearch!:()=>void,documentArrived!:()=>void,releaseDocument!:()=>void;
 const searching=new Promise<void>(r=>{searchArrived=r;}),heldSearch=new Promise<void>(r=>{releaseSearch=r;});
 const reading=new Promise<void>(r=>{documentArrived=r;}),heldDocument=new Promise<void>(r=>{releaseDocument=r;});
 page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname.endsWith('/skill-candidates/collect'))collections++;});
 await page.route('**/api/v1/documents**',async route=>{
  const path=new URL(route.request().url()).pathname;
  if(path==='/api/v1/documents'){await route.fulfill({json:{items:[{id:'doc-lifecycle',title:'架空の失効対象',sourceVersion:1,status:'ready',currentVersionId:'doc-lifecycle-v1'}],continuationToken:null}});return;}
  if(path.endsWith('/withdraw')){withdrawn=true;await route.fulfill({json:{}});return;}
  if(path.endsWith('/pipeline')){await route.fulfill({json:{aiState:'complete',processingMode:'fixture',sourceVersion:1,resultId:'demo-person-01',job:{id:'job-lifecycle',status:'ready'},runs:[]}});return;}
  if(path.endsWith('/skill-fragments')){await route.fulfill({json:{items:[{id:'fragment-lifecycle',description:'架空の失効対象原文',contentOnly:true,historyOnly:false}]}});return;}
  if(path.endsWith('/evidence')){await route.fulfill({json:{items:[{id:'ev-lifecycle',location:{label:'架空の原文位置'},text:'架空の失効対象原文'}],continuationToken:null}});return;}
  if(withdrawn){documentArrived();await heldDocument;await route.fulfill({status:503,json:{code:'READ_FAILED_AFTER_WITHDRAW'}});return;}
  await route.fulfill({headers:{ETag:'one'},json:{id:'doc-lifecycle',sourceVersion:1,activeVersion:1,accessVersion:1,status:'ready',enrichmentJobId:'job-lifecycle',warnings:[]}});
 });
 await page.route('**/api/v1/skill-dictionary*',route=>route.fulfill({json:{version:1,concepts:[{id:'skill-lifecycle',name:'Git',state:'active'}]}}));
 await page.route(/\/api\/(?:v2\/search(?:\/conditions)?|search)$/,async route=>{
  const isConditions=new URL(route.request().url()).pathname.endsWith('/conditions');
  if(isConditions)conditions++;else searches++;
  if(isConditions||searches>1){searchArrived();await heldSearch;}
  const currentConditions={categories:[],signalIds:[],terms:['遅れた旧条件'],skillIds:['skill-lifecycle'],skillDictionaryVersion:1};
  const result={candidates:[{entityId:'doc-lifecycle',entityType:'document',title:'旧検索結果',routes:['terms'],units:[{summary:'旧検索の原文',confirmationState:'ai_unconfirmed',sourceRefs:[{id:'ev-lifecycle',documentTitle:'旧資料',location:'旧位置',text:'旧検索の原文'}],skillBindings:[{candidateId:'candidate-lifecycle',conceptId:'skill-lifecycle',eligibility:'content_only'}]}]}],matchedUnits:1,beforeLimit:1,rerankingStatus:'succeeded'};
  await route.fulfill({json:isConditions?{status:'succeeded',conditions:currentConditions}:new URL(route.request().url()).pathname.includes('/v2/')?{primary:result,conditions:currentConditions,rerankingStatus:'succeeded',runId:'retrieval-lifecycle',assessments:[]}:result});
 });
 await page.goto('/validation.html?workflow='+workflow);
 await page.getByRole('button',{name:'資料一覧を更新',exact:true}).click();await page.getByRole('button',{name:'架空の失効対象（第1版）',exact:true}).click();
 const documents=page.getByRole('region',{name:'文書を取り込む',exact:true});
 await expect(documents.getByRole('heading',{name:'出典プレビュー',exact:true})).toBeVisible();
 if(pending==='skills'){
  await page.getByLabel('探し方',{exact:true}).selectOption('skills');await page.getByLabel('固定する辞書版').fill('1');await page.getByRole('button',{name:'この版を読み込む',exact:true}).click();
  await page.getByRole('button',{name:'承認済み技能で検索',exact:true}).click();
 }else{if(pending==='natural')await page.getByLabel('自然文の課題',{exact:true}).fill('架空資料');await page.getByRole('button',{name:'相談候補を探す',exact:true}).click();}
 await expect(page.getByRole('heading',{name:'旧検索結果',exact:true})).toBeVisible();
 await page.locator('article.candidate').getByText('根拠を見る',{exact:true}).click();
 if(pending!=='shown'){
  if(pending==='conditions'){await page.getByLabel('自然文の課題',{exact:true}).fill('架空資料');await page.getByRole('button',{name:'自然文から条件を生成',exact:true}).click();}
  else await page.getByRole('button',{name:pending==='skills'?'承認済み技能で検索':'相談候補を探す',exact:true}).click();
  await searching;
 }
 const requestsBeforeWithdrawal=searches+conditions;
 await page.getByLabel('訂正・撤回の理由').fill('架空資料の即時失効を確認');await page.getByRole('button',{name:'この資料を撤回する',exact:true}).click();await reading;
 // The authoritative POST has succeeded, but the follow-up document GET is still blocked.
 // No AI search is allowed to run automatically to clear the old display.
 try{
  await expect(page.getByText('処理段階：資料が撤回されました。検索結果を再取得してください。',{exact:true})).toBeVisible();
  await expect(page.locator('article.candidate')).toHaveCount(0);
  await expect(documents.getByRole('heading',{name:'出典プレビュー',exact:true})).toHaveCount(0);
  await expect(documents.getByText('架空の失効対象原文',{exact:true})).toHaveCount(0);
 }finally{releaseSearch();releaseDocument();}
 await expect(page.getByText(/READ_FAILED_AFTER_WITHDRAW/)).toBeVisible();
 await expect(documents.getByText('この資料は撤回済みです。以前の原文は検索・候補確認に使いません。',{exact:true})).toBeVisible();
 await expect(page.locator('article.candidate')).toHaveCount(0);
 await expect(page.getByText('処理段階：資料が撤回されました。検索結果を再取得してください。',{exact:true})).toBeVisible();
 await expect(page.getByText(/承認済み技能IDの条件：skill-lifecycle/)).toHaveCount(0);
 expect(searches+conditions).toBe(requestsBeforeWithdrawal);expect(collections).toBe(0);
 await page.screenshot({path:folder+'/withdraw-immediate-'+pending+'.png',fullPage:true});
});
