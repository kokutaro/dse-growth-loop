import {test,expect} from '@playwright/test';

test('以前の架空原文の承認を通常の永続検索・画面・撤回につなぐ',async({page,request})=>{
 await page.setViewportSize({width:1920,height:1080});
 await page.goto('/skill-demo.html');await page.getByRole('button',{name:'以前の2資料でデモを開く',exact:true}).click();
 await expect(page).toHaveURL(/skill-dictionary.html\?workflow=wf-[a-f0-9]{48}$/);
 const workflow=new URL(page.url()).searchParams.get('workflow')!;const headers={'X-Dse-Workflow':workflow};
 const candidates=(await(await request.get('/api/v1/skill-candidates',{headers})).json()).items.filter((c:{item?:{raw:string}})=>c.item?.raw==='Git');let concept='';
 for(const candidate of candidates){
  await page.getByLabel(`候補 ${candidate.id} をプレビュー`).locator('..').locator('..').getByRole('button',{name:'この候補を確認'}).click();
  await page.getByLabel('レビュー判断').selectOption(concept?'alias':'new');
  if(concept)await page.getByLabel('比較・統合先の技能').selectOption(concept);
  else{await page.getByLabel('技能の定義').fill('Gitの記載知識');await page.getByLabel('含む範囲・含めない範囲').fill('本人の経験を認定しない');}
  await page.getByLabel('確認理由').fill('以前の架空原文の知識であることを確認');await page.getByRole('button',{name:'判断を承認して保存'}).click();await expect(page.locator('.candidate-review')).toHaveCount(0);
  concept=(await(await request.get('/api/v1/skill-candidates',{headers})).json()).items.find((c:{id:string})=>c.id===candidate.id).conceptId;
 }
 await page.reload();await expect(page.getByText('現在版：1',{exact:false})).toBeVisible();
 await page.getByRole('link',{name:'この辞書版で資料を探す',exact:true}).click();
 await expect(page).toHaveURL(new RegExp('workflow='+workflow));await page.getByRole('button',{name:'この版を読み込む',exact:true}).click();
 await page.getByLabel('承認済み技能',{exact:true}).selectOption(concept);await page.getByRole('button',{name:'承認済み技能で検索',exact:true}).click();
 await expect(page.locator('article.candidate')).toHaveCount(2);
 const response=await request.post('/api/search',{headers,data:{categories:[],signalIds:[],terms:[],skillIds:[concept],skillDictionaryVersion:1}});expect(response.ok()).toBeTruthy();
 const result=await response.json();expect(result.candidates).toHaveLength(2);expect(result.candidates.every((c:{entityType:string})=>c.entityType==='document')).toBeTruthy();
 const id=result.candidates[0].entityId;const doc=await request.get('/api/v1/documents/'+id,{headers});
 expect((await request.post('/api/v1/documents/'+id+'/withdraw',{headers:{...headers,'If-Match':doc.headers().etag,'Idempotency-Key':crypto.randomUUID()},data:{reason:'架空資料の撤回を検証'}})).ok()).toBeTruthy();
 await page.getByRole('button',{name:'承認済み技能で検索',exact:true}).click();await expect(page.locator('article.candidate')).toHaveCount(1);
 expect((await(await request.get('/api/v1/skill-dictionary')).json()).version).toBe(0);
});
