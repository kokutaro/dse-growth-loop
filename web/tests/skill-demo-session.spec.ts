import {test,expect} from '@playwright/test';

test('共有環境用の操作デモで以前の資料を承認し、共有辞書や他者へ影響せず通常画面から検索できる',async({page,request})=>{
 const before=await(await request.get('/api/v1/skill-dictionary')).json();
 expect((await request.post('/api/v1/skill-demo/start',{headers:{'X-Test-Actor':'member'}})).status()).toBe(403);
 const legacy=await(await request.post('/api/v1/skill-demo/start')).json();await page.goto('/skill-dictionary.html?skillDemo='+legacy.id);
 await expect(page).toHaveURL(/skill-dictionary.html\?skillDemo=[a-f0-9]{32}$/);
 const id=new URL(page.url()).searchParams.get('skillDemo')!;const api='/api/v1/skill-demo/'+id;
 await expect(page.getByText('操作用デモ：変更は自分のメモリ内だけに保存します。',{exact:true})).toBeVisible();
 const rows=page.locator('tbody tr');await expect(rows).toHaveCount(8);
 const ids:Record<string,string>={};
 for(const name of ['Git','SQLite','Linux']){
  const candidates=(await(await request.get(api+'/skill-candidates')).json()).items.filter((c:{item?:{raw:string}})=>c.item?.raw===name);
  for(const candidate of candidates){
   await page.getByLabel(`候補 ${candidate.id} をプレビュー`).locator('..').locator('..').getByRole('button',{name:'この候補を確認'}).click();
   if(ids[name]){await page.getByLabel('レビュー判断').selectOption('alias');await page.getByLabel('比較・統合先の技能').selectOption(ids[name]);}
   else{await page.getByLabel('レビュー判断').selectOption('new');await page.getByLabel('技能の定義').fill(name+'の記載知識');await page.getByLabel('含む範囲・含めない範囲').fill('本人経験を認定しない');}
   await expect(page.getByRole('button',{name:'AIで既存技能の候補を照会'})).toBeDisabled();
   await page.getByLabel('確認理由').fill('以前の架空原文と知識種別を確認');await page.getByRole('button',{name:'判断を承認して保存'}).click();
   await expect(page.locator('.candidate-review')).toHaveCount(0);
   const updated=(await(await request.get(api+'/skill-candidates')).json()).items.find((c:{id:string})=>c.id===candidate.id);ids[name]=updated.conceptId;
  }
 }
 await page.getByRole('link',{name:'この辞書版で資料を探す',exact:true}).click();
 await expect(page).toHaveURL(new RegExp('skillDemo='+id));await page.getByRole('button',{name:'この版を読み込む',exact:true}).click();
 for(const name of ['Git','SQLite','Linux']){
  await page.getByLabel('承認済み技能',{exact:true}).selectOption(ids[name]);await page.getByRole('button',{name:'承認済み技能で検索',exact:true}).click();
  await expect(page.locator('article.candidate')).toHaveCount(name==='Git'?2:1);
 }
 expect(await(await request.get('/api/v1/skill-dictionary')).json()).toEqual(before);
 expect((await request.get(api+'/skill-dictionary',{headers:{'X-Test-Actor':'other'}})).status()).toBe(404);
 expect((await request.post(api+'/skill-candidates/anything/suggest')).status()).toBe(409);
 expect((await request.post(api+'/skill-candidates/collect',{data:{fragmentId:'fragment-technical-list-a'}})).status()).toBe(409);
});
