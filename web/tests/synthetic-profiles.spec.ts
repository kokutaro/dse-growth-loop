import {test,expect} from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
const root=path.resolve('test-results/synthetic-profiles');
test.describe.serial('検証人物の製品APIとモック取込',()=>{
 test('登録・二重クリック・応答消失・取込・検索・停止と通常検索非混入',async({page,request})=>{
  const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await fs.mkdir(root,{recursive:true});
  await page.goto('/test-profiles.html');await expect(page.getByRole('button',{name:'最新状態を確認'})).toBeEnabled();
  await page.getByLabel('管理参照（例 hk-a）').fill('browser-a');await page.getByLabel('架空表示名',{exact:true}).fill('合成 A');await page.getByLabel('実名・実案件を含まない合成データです').check();
  await page.route('**/api/v1/admin/test-people',async route=>{if(route.request().method()==='POST'){const r=await route.fetch();expect(r.status()).toBe(201);await route.abort('failed');}else await route.continue();},{times:1});
  await page.getByRole('button',{name:'登録',exact:true}).click();await expect(page.getByRole('alert')).toBeVisible();
  await page.getByRole('button',{name:'登録',exact:true}).dblclick();await expect(page.getByRole('status')).toContainText('登録済み');
  const list=await (await request.get('/api/v1/admin/test-people')).json();expect(list.items).toHaveLength(1);const person=list.items[0];
  await page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true}).click();
  const workbook=await (await request.get('/api/test/workbook')).body();
  await page.getByLabel('合成スキルシート（XLSX）').setInputFiles({name:'synthetic.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:workbook});
  await page.getByRole('button',{name:'資料を取り込む',exact:true}).dblclick();await expect(page.getByRole('status')).toContainText('取込受付');
  await request.post('/api/test/drain');await page.getByRole('button',{name:'処理・根拠を更新',exact:true}).click();
  await expect(page.getByRole('heading',{name:'抽出・保留'})).toBeVisible();await expect(page.getByText('取得範囲：全',{exact:false})).toBeVisible();await expect(page.locator('article')).toHaveCount(3);
  const current=await (await request.get('/api/v1/admin/test-people')).json();const doc=Object.values(current.items[0].documentHeads)[0];
  const pipeline=await (await request.get(`/api/v1/documents/${doc}/pipeline`)).json();expect(pipeline.aiState).toBe('complete');expect(pipeline.job.status).toBe('ready');
  await page.getByLabel('原文語または具体技能').fill('回帰試験');await page.getByRole('button',{name:'テスト検索',exact:true}).click();await expect(page.getByText('検証専用・架空',{exact:true})).toBeVisible();
  await page.screenshot({path:path.join(root,'registered-and-ingested.png'),fullPage:true});
  const replay=await request.post(`/api/v1/admin/test-people/${person.id}/documents`,{headers:{'Idempotency-Key':crypto.randomUUID()},multipart:{syntheticDeclaration:'true',file:{name:'synthetic.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:workbook}}});expect(replay.status()).toBe(202);expect((await replay.json()).documentId).toBe(doc);
  await page.getByRole('button',{name:'再抽出の費用と版を確認',exact:true}).click();
  const start=page.getByRole('button',{name:'見積内で新しい抽出世代を開始',exact:true});await expect(start).toBeDisabled();
  await page.getByLabel('再抽出理由',{exact:true}).fill('全セル新版の再確認');await page.getByLabel('この原本と版・新しい費用上限を確認しました',{exact:true}).check();
  await start.dblclick();await expect(page.getByRole('status')).toContainText('再抽出受付');
  const pending=(await (await request.get('/api/v1/admin/test-people')).json()).items[0];expect(pending.pendingGenerationId).toBeTruthy();expect(Object.values(pending.documentHeads)[0]).toBe(doc);
  await page.getByRole('button',{name:'テスト検索',exact:true}).click();await expect(page.getByText('検証専用・架空',{exact:true})).toBeVisible();
  await request.post('/api/test/drain');await page.getByRole('button',{name:'処理・根拠を更新',exact:true}).click();
  const published=(await (await request.get('/api/v1/admin/test-people')).json()).items[0];expect(Object.values(published.documentHeads)[0]).toBe(pending.pendingGenerationId);expect(published.pendingGenerationId).toBeUndefined();
  await expect(page.locator('article')).toHaveCount(3);await page.getByRole('button',{name:'再抽出の費用と版を確認',exact:true}).click();await expect(page.getByText('この版は適用済みです。新しい費用枠は作成しません。',{exact:true})).toBeVisible();
  await page.screenshot({path:path.join(root,'generation-published.png'),fullPage:true});
  await page.getByRole('button',{name:'一覧に戻る',exact:true}).click();await expect(page.locator('article')).toHaveCount(0);
  await page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true}).click();await page.getByRole('button',{name:'利用停止（削除しない）',exact:true}).click();await expect(page.getByRole('status')).toContainText('利用停止');
  await page.getByRole('button',{name:'テスト検索',exact:true}).click();await expect(page.getByText('検証専用・架空',{exact:true})).toHaveCount(0);
  const original=await request.get(`/api/v1/documents/${doc}/original`);expect(original.status()).toBe(409);
  expect(errors).toEqual([]);await fs.writeFile(path.join(root,'browser-results.json'),JSON.stringify({productEndpoints:true,normalIngestion:true,realAi:false,paidCalls:0,readyProjects:2,contextSections:1,errors,withdrawnOriginalDenied:original.status()},null,2));
 });
 test('非operatorは登録・アップロード・テスト検索を利用できない',async({page})=>{
  await page.route('**/api/v1/**',route=>route.continue({headers:{...route.request().headers(),'X-Test-Actor':'member'}}));
  await page.goto('/test-profiles.html');await expect(page.getByRole('alert')).toContainText('403');
  await expect(page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true})).toHaveCount(0);
 });
 test('再試行の画面：明示確認・二重クリック・応答不明の同一キー再送・戻る',async({page})=>{
  // UI-only transport fixture. Actual authorization/atomic persistence is covered by PreDispatchRetryTests.
  const p={id:'retry-ui-person',displayName:'[検証専用・架空] 再試行UI',etag:'person-etag',publicationState:'published',syntheticTest:true,documentHeads:{fixture:'old-doc'},pendingGenerationId:'same-generation'};
  const q={estimateId:'f'.repeat(64),sourceEtag:'generation-etag',generationId:'same-generation',previousJobId:'failed-job',maximumUsd:0.02914304,remainingCalls:7,spentCalls:0,implementationVersion:'fixture-build',implementationSha256:'a'.repeat(64)};
  let posts=0;const keys:string[]=[];
  await page.route('**/api/v1/**',async route=>{
   const url=new URL(route.request().url());let body:unknown={};
   if(url.pathname.endsWith('/reextraction-retries')){posts++;keys.push(route.request().headers()['idempotency-key']);expect(route.request().headers()['if-match']).toBe(q.sourceEtag);expect(route.request().postDataJSON().maximumUsd).toBe(q.maximumUsd);if(posts===1){await route.abort('failed');return;}body={generationId:q.generationId,jobId:'retry-job'};}
   else if(url.pathname.endsWith('/reextraction-retry-estimate'))body=q;
   else if(url.pathname.endsWith('/admin/test-people'))body={items:[p],scopeIds:['demo'],importAllowance:{mode:'Mock'}};
   else if(url.pathname.endsWith('/pipeline'))body={job:{status:'failed',lastErrorCode:'ACCESS_BUSY'}};
   else if(url.pathname.endsWith('/experiences'))body={items:[]};
   await route.fulfill({status:200,json:body});
  });
  await page.goto('/test-profiles.html');await page.getByRole('button',{name:p.displayName,exact:true}).click();await page.getByRole('button',{name:'未送信の失敗を同じ費用枠で再確認',exact:true}).click();
  const send=page.getByRole('button',{name:'未消費枠内で1回だけ再試行',exact:true});await expect(send).toBeDisabled();await expect(page.getByText('元の失敗Job：',{exact:false})).toContainText('消費済み0回・残り7回');
  await page.getByLabel('再試行理由',{exact:true}).fill('未送信の確認済み競合を再確認');await page.getByLabel('原本・実装・未消費の上限を確認しました').check();
  await send.evaluate((button:HTMLButtonElement)=>{button.click();button.click();});await expect(page.getByRole('alert')).toBeVisible();expect(posts).toBe(1);
  await send.click();await expect(page.getByRole('status')).toContainText('同じ世代の再試行受付 retry-job');expect(posts).toBe(2);expect(keys[0]).toBe(keys[1]);
  await page.getByRole('button',{name:'未送信の失敗を同じ費用枠で再確認',exact:true}).click();await page.screenshot({path:path.join(root,'same-generation-retry-confirmation.png'),fullPage:true});await page.getByRole('button',{name:'一覧に戻る',exact:true}).click();await expect(send).toHaveCount(0);
 });
 test('再試行の確認中に権限撤回された場合は根拠・見積をクリア',async({page})=>{
  const p={id:'revoked-retry-person',displayName:'[検証専用・架空] 撤回UI',etag:'etag',publicationState:'published',syntheticTest:true,documentHeads:{fixture:'old'},pendingGenerationId:'generation'};
  await page.route('**/api/v1/**',async route=>{const path=new URL(route.request().url()).pathname;if(path.endsWith('/reextraction-retry-estimate')){await route.fulfill({status:403,json:{code:'FORBIDDEN'}});return;}await route.fulfill({status:200,json:path.endsWith('/admin/test-people')?{items:[p],scopeIds:['demo']}:path.endsWith('/pipeline')?{job:{status:'failed',lastErrorCode:'ACCESS_BUSY'}}:{items:[]}});});
  await page.goto('/test-profiles.html');await page.getByRole('button',{name:p.displayName,exact:true}).click();await page.getByRole('button',{name:'未送信の失敗を同じ費用枠で再確認',exact:true}).click();await expect(page.getByRole('alert')).toContainText('403');await expect(page.getByRole('heading',{name:p.displayName,exact:true})).toHaveCount(0);await expect(page.getByRole('button',{name:'未消費枠内で1回だけ再試行',exact:true})).toHaveCount(0);
 });
 test('閲覧中にoperator権限が撤回されたら機微な表示をクリア',async({page,request})=>{
  await page.goto('/test-profiles.html');await expect(page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true})).toBeVisible();
  await request.post('/api/test/revoke');await page.getByRole('button',{name:'最新状態を確認'}).click();await expect(page.getByRole('alert')).toContainText('403');await expect(page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true})).toHaveCount(0);
 });
});
