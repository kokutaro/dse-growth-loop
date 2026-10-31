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
  await expect(page.getByRole('heading',{name:'抽出・保留'})).toBeVisible();await expect(page.locator('article')).toHaveCount(2);
  const current=await (await request.get('/api/v1/admin/test-people')).json();const doc=Object.values(current.items[0].documentHeads)[0];
  const pipeline=await (await request.get(`/api/v1/documents/${doc}/pipeline`)).json();expect(pipeline.aiState).toBe('complete');expect(pipeline.job.status).toBe('ready');
  await page.getByLabel('原文語または具体技能').fill('PostgreSQL 16');await page.getByRole('button',{name:'テスト検索',exact:true}).click();await expect(page.getByText('検証専用・架空',{exact:true})).toBeVisible();
  await page.screenshot({path:path.join(root,'registered-and-ingested.png'),fullPage:true});
  const replay=await request.post(`/api/v1/admin/test-people/${person.id}/documents`,{headers:{'Idempotency-Key':crypto.randomUUID()},multipart:{syntheticDeclaration:'true',file:{name:'synthetic.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:workbook}}});expect(replay.status()).toBe(202);expect((await replay.json()).documentId).toBe(doc);
  await page.getByRole('button',{name:'一覧に戻る',exact:true}).click();await expect(page.locator('article')).toHaveCount(0);
  await page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true}).click();await page.getByRole('button',{name:'利用停止（削除しない）',exact:true}).click();await expect(page.getByRole('status')).toContainText('利用停止');
  await page.getByRole('button',{name:'テスト検索',exact:true}).click();await expect(page.getByText('検証専用・架空',{exact:true})).toHaveCount(0);
  const original=await request.get(`/api/v1/documents/${doc}/original`);expect(original.status()).toBe(409);
  expect(errors).toEqual([]);await fs.writeFile(path.join(root,'browser-results.json'),JSON.stringify({productEndpoints:true,normalIngestion:true,realAi:false,paidCalls:0,readyProjects:2,errors,withdrawnOriginalDenied:original.status()},null,2));
 });
 test('非operatorは登録・アップロード・テスト検索を利用できない',async({page})=>{
  await page.route('**/api/v1/**',route=>route.continue({headers:{...route.request().headers(),'X-Test-Actor':'member'}}));
  await page.goto('/test-profiles.html');await expect(page.getByRole('alert')).toContainText('403');
  await expect(page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true})).toHaveCount(0);
 });
 test('閲覧中にoperator権限が撤回されたら機微な表示をクリア',async({page,request})=>{
  await page.goto('/test-profiles.html');await expect(page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true})).toBeVisible();
  await request.post('/api/test/revoke');await page.getByRole('button',{name:'最新状態を確認'}).click();await expect(page.getByRole('alert')).toContainText('403');await expect(page.getByRole('button',{name:'[検証専用・架空] 合成 A',exact:true})).toHaveCount(0);
 });
});
