import {test,expect} from '@playwright/test';
import fs from 'node:fs/promises';
import path from 'node:path';
const output=path.resolve('test-results/skill-dictionary');
test('仮データの収集から人の承認、統合、固定版プレビューまで業務・goldを変更しない',async({page,request})=>{
 await fs.mkdir(output,{recursive:true});await page.goto('/skill-dictionary.html');
 await expect(page.getByText('現在の辞書版 0。',{exact:false})).toBeVisible();
 async function collect(id:string){await page.getByLabel('実績・知識ID').fill('fragment-'+id);await page.getByRole('button',{name:'保存済み抽出を収集',exact:true}).click();await expect(page.getByRole('status')).toContainText('収集状態');}
 async function choose(raw:string,kind?:string){let row=page.locator('tbody tr').filter({hasText:raw});if(kind)row=row.filter({hasText:kind});await row.first().getByRole('button',{name:'この候補を確認'}).click();}
 async function form(action:string,reason='架空の原文と定義・境界を人が確認した操作のテスト'){
  await page.getByLabel('レビュー判断').selectOption(action);await page.getByLabel('確認理由').fill(reason);
  if(action==='new'){await page.getByLabel('技能の定義').fill('変更後に既存動作が保たれるかを試験する具体作業');await page.getByLabel('含む範囲・含めない範囲').fill('大分類だけで統合しない。知識・資格・希望を実務経験とは認定しない。');}
 }
 async function save(version:number){await page.getByRole('button',{name:'判断を承認して保存'}).click();await expect(page.getByRole('status')).toContainText(`辞書版 ${version}`);await expect(page.locator('.candidate-review')).toHaveCount(0);await expect(page.getByRole('button',{name:'保存済み抽出を収集',exact:true})).toBeEnabled();}
 await collect('regression');await choose('回帰試験');await form('new');
 // Server commits once, but the client loses the response. The retry must use
 // the same key/body/ETag and receive the original approval, not create a second.
 await page.route('**/api/v1/skill-candidates/*/review',async route=>{const r=await route.fetch();expect(r.status()).toBe(200);await route.abort('failed');},{times:1});
 await page.getByRole('button',{name:'判断を承認して保存'}).click();await expect(page.getByRole('alert')).toContainText('結果を確認できません');
 await page.getByRole('button',{name:'同じ操作を再送'}).click();await expect(page.getByRole('status')).toContainText('辞書版 1');
 // The status is set before refresh finishes. Wait for the whole UI operation
 // before a separate API reader can compete for the serialization fence.
 await expect(page.locator('.candidate-review')).toHaveCount(0);await expect(page.getByRole('button',{name:'保存済み抽出を収集',exact:true})).toBeEnabled();
 let dictionary=await (await request.get('/api/v1/skill-dictionary')).json();expect(dictionary.version).toBe(1);expect(dictionary.concepts).toHaveLength(1);const canonical=dictionary.concepts[0].id;
 await collect('alias');await choose('リグレッション試験');await form('alias');await page.getByLabel('比較・統合先の技能').selectOption(canonical);await save(2);
 await collect('regression-alt');await choose('退行試験');await form('new');await save(3);
 const list=await (await request.get('/api/v1/skill-candidates')).json();const alt=list.items.find((c:{item:{raw:string}})=>c.item?.raw==='退行試験');const oldId=alt.conceptId;
 await choose('退行試験');await form('merge','同じ回帰試験を指す仮の重複技能。定義と境界を照合して統合する。');await page.getByLabel('比較・統合先の技能').selectOption(canonical);await save(4);
 await collect('knowledge');await choose('性能計測');await form('new','知識資料の技能概念を登録する。本人経験は認定しない。');await page.getByLabel('標準名').fill('知識資料の性能計測');await save(5);
 await collect('performance');await choose('性能計測','実務');await form('defer');await save(5);
 await collect('noise');await choose('補足メモ');await form('not_skill');await save(5);
 await collect('unknown');await expect(page.getByRole('status')).toContainText('判断不能（unknown）');await collect('empty');await expect(page.getByRole('status')).toContainText('該当なし（空集合）');
 await page.getByLabel(`候補 ${alt.id} をプレビュー`).check();await page.getByLabel('再分類に使う辞書版').fill('3');await page.getByRole('button',{name:'選択候補を固定版でプレビュー'}).click();
 await expect(page.getByRole('heading',{name:'辞書版 3 の固定プレビュー'})).toBeVisible();await expect(page.locator('table').last()).toContainText(oldId);
 await page.getByLabel('再分類に使う辞書版').fill('5');await page.getByRole('button',{name:'選択候補を固定版でプレビュー'}).click();await expect(page.getByRole('heading',{name:'辞書版 5 の固定プレビュー'})).toBeVisible();await expect(page.locator('table').last()).toContainText(canonical);
 await choose('退行試験');await page.getByRole('button',{name:'承認履歴を見る'}).click();await expect(page.locator('.candidate-review')).toContainText('merge');
 await page.screenshot({path:path.join(output,'dictionary-review.png'),fullPage:true});
 const knowledge=(await (await request.get('/api/v1/skill-candidates')).json()).items.find((c:{fragmentId:string})=>c.fragmentId==='fragment-knowledge');
 const preview=await (await request.post('/api/v1/skill-dictionary/preview',{data:{version:5,candidateIds:[knowledge.id]}})).json();expect(preview.goldChanged).toBe(false);expect(preview.businessRecordsChanged).toBe(false);expect(preview.items[0].experienceEligibility).toBe('content_only');
 await page.getByLabel(`候補 ${alt.id} をプレビュー`).uncheck();await page.getByLabel(`候補 ${knowledge.id} をプレビュー`).check();
 const knowledgeRow=page.locator('tbody tr').filter({hasText:'知識'}).first();await knowledgeRow.getByRole('button',{name:'この候補を確認'}).click();await page.getByRole('button',{name:'選択候補を固定版でプレビュー'}).click();await expect(page.locator('.candidate-review')).toContainText('知識内容のみ');
 await page.screenshot({path:path.join(output,'knowledge-not-experience.png'),fullPage:true});
 // A concurrent curator changes the selected candidate and dictionary. The UI
 // must discard the stale decision and require a fresh human selection.
 const perf=(await (await request.get('/api/v1/skill-candidates')).json()).items.find((c:{fragmentId:string})=>c.fragmentId==='fragment-performance');
 const perfRow=page.locator('tbody tr').filter({hasText:'性能計測'}).filter({hasText:'実務'}).first();await perfRow.getByRole('button',{name:'この候補を確認'}).click();await form('defer');
 dictionary=await (await request.get('/api/v1/skill-dictionary')).json();const external=await request.post(`/api/v1/skill-candidates/${perf.id}/review`,{headers:{'Idempotency-Key':crypto.randomUUID(),'If-Match':perf.etag},data:{action:'new',reason:'別の確認者による仮データ承認',expectedVersion:5,dictionaryETag:dictionary.etag,name:'性能計測',definition:'実測により性能を確認する作業',boundary:'知識資料の記述や資格とは区別する'}});expect(external.status()).toBe(200);
 await page.getByRole('button',{name:'判断を承認して保存'}).click();await expect(page.getByRole('alert')).toContainText('再確認してください');await expect(page.locator('.candidate-review')).toHaveCount(0);
 const after=await (await request.get('/api/v1/skill-dictionary')).json();expect(after.version).toBe(6);expect(after.concepts.find((c:{id:string})=>c.id===oldId).redirectId).toBe(canonical);
});
test('既存権限のない利用者と閲覧範囲外は候補・根拠・辞書の承認を読めない',async({request})=>{
 const member=await request.get('/api/v1/skill-candidates',{headers:{'X-Test-Actor':'member'}});expect(member.status()).toBe(403);
 const outside=await request.get('/api/v1/skill-candidates',{headers:{'X-Test-Actor':'other'}});expect(outside.status()).toBe(200);expect((await outside.json()).items).toHaveLength(0);
 const concepts=await request.get('/api/v1/skill-dictionary',{headers:{'X-Test-Actor':'other'}});expect((await concepts.json()).concepts).toHaveLength(0);
});
test('既存カタログ画面から製品API・実行ガード・監査を通り意味候補を提示し、人の承認と分離する',async({page,request})=>{
 const out=path.resolve('test-results/skill-semantic');await fs.mkdir(out,{recursive:true});const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto('/skill-dictionary.html');await expect(page.getByText('現在の辞書版 6。',{exact:false})).toBeVisible();
 async function collect(id:string,raw:string){await page.getByLabel('実績・知識ID').fill('fragment-'+id);await page.getByRole('button',{name:'保存済み抽出を収集',exact:true}).click();await expect(page.getByRole('status')).toContainText('収集状態');await page.locator('tbody tr').filter({hasText:raw}).first().getByRole('button',{name:'この候補を確認'}).click();await expect(page.locator('.skill-suggestion')).toContainText('まだ照会していません');}
 await collect('semantic-proposal','回帰試験の実施確認');
 const before=await (await request.get('/api/test/semantic-audit')).json();const dict=await (await request.get('/api/v1/skill-dictionary')).json();
 // Commit succeeds but the response is lost. Retry has identical key/body/ETag.
 let originalKey='';await page.route('**/api/v1/skill-candidates/*/suggest',async route=>{originalKey=route.request().headers()['idempotency-key'];const r=await route.fetch();expect(r.status()).toBe(200);await route.abort('failed');},{times:1});
 await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();await expect(page.getByRole('alert')).toContainText('結果を確認できません');
 const retried=page.waitForRequest(r=>r.url().endsWith('/suggest'));await page.getByRole('button',{name:'同じ操作を再送'}).click();expect((await retried).headers()['idempotency-key']).toBe(originalKey);await expect(page.getByRole('status')).toContainText('候補提案を保存しました');
 await page.locator('tbody tr').filter({hasText:'回帰試験の実施確認'}).first().getByRole('button',{name:'この候補を確認'}).click();await expect(page.locator('.skill-suggestion')).toContainText('既存技能の候補（未承認）');
 const panel=page.locator('.skill-suggestion');await expect(panel).toContainText('0.99');await panel.getByRole('button',{name:'この候補IDと比較'}).first().click();expect(await page.getByLabel('比較・統合先の技能').inputValue()).not.toBe('');await expect(page.getByLabel('レビュー判断')).toHaveValue('defer');await expect(page.getByRole('button',{name:'判断を承認して保存'})).toBeDisabled();
 const after=await (await request.get('/api/test/semantic-audit')).json();expect(after.calls-before.calls).toBe(1);expect(after.runs.length-before.runs.length).toBe(1);expect(after.runs.at(-1).status).toBe('succeeded');expect(await (await request.get('/api/v1/skill-dictionary')).json()).toEqual(dict);
 await page.screenshot({path:path.join(out,'product-semantic-proposal.png'),fullPage:true});
 const list=(await (await request.get('/api/v1/skill-candidates')).json()).items;const c=list.find((x:{fragmentId:string})=>x.fragmentId==='fragment-semantic-proposal');expect(c.state).toBe('pending');
 for(const actor of ['member','other']){const denied=await request.get(`/api/v1/skill-candidates/${c.id}/suggestion?version=6`,{headers:{'X-Test-Actor':actor}});expect(denied.status()).toBe(actor==='member'?403:404);}
 const missingHeaders=await request.post(`/api/v1/skill-candidates/${c.id}/suggest`,{data:{version:6,dictionaryETag:dict.etag}});expect(missingHeaders.status()).toBe(400);
 const foreign=await request.post(`/api/v1/skill-candidates/${c.id}/suggest`,{headers:{'If-Match':c.etag,'Idempotency-Key':crypto.randomUUID()},data:{version:6,dictionaryETag:dict.etag,targetId:'foreign-id'}});expect(foreign.status()).toBe(400);
 const stale=await request.post(`/api/v1/skill-candidates/${c.id}/suggest`,{headers:{'If-Match':c.etag,'Idempotency-Key':crypto.randomUUID()},data:{version:5,dictionaryETag:dict.etag}});expect(stale.status()).toBe(412);
 await collect('semantic-failure','応答不正の試験');await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();await expect(panel).toContainText('AI_INVALIDJSON');await expect(panel).toContainText('要確認');await page.screenshot({path:path.join(out,'product-semantic-failure.png'),fullPage:true});
 const failed=await (await request.get('/api/test/semantic-audit')).json();expect(failed.calls-after.calls).toBe(1);expect(errors).toEqual([]);
 await fs.writeFile(path.join(out,'browser-results.json'),JSON.stringify({productPathConnected:true,externalAiRequests:0,scenarios:['existing product UI/API/AiService guard audit','lost response same-key replay','read source/operator boundaries','unexpected ID rejected','stale version rejected','human approval separate','failed AI not new skill'],simulatedWireCalls:failed.calls-before.calls,pageErrors:errors},null,2));
});

test('入力枠を超える比較は省略も送信もせず要確認を表示し、再送しても課金しない',async({page,request})=>{
 await page.goto('/skill-dictionary.html');
 await page.getByLabel('実績・知識ID').fill('fragment-semantic-oversize');await page.getByRole('button',{name:'保存済み抽出を収集',exact:true}).click();await expect(page.getByRole('status')).toContainText('収集状態');
 await page.locator('tbody tr').filter({hasText:'装置の検証'}).first().getByRole('button',{name:'この候補を確認'}).click();
 const before=await(await request.get('/api/test/semantic-audit')).json();const panel=page.locator('.skill-suggestion');
 await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();
 await expect(panel).toContainText('AIには送信していません');await expect(panel).toContainText('要確認');await expect(panel).toContainText('有効なAI返値 0 件');
 const replay=page.waitForResponse(r=>r.url().endsWith('/suggest')&&r.request().method()==='POST');await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();await(await replay).finished();await expect(page.getByRole('button',{name:'AIで既存技能の候補を照会'})).toBeEnabled();await expect(panel).toContainText('AIには送信していません');
 const after=await(await request.get('/api/test/semantic-audit')).json();expect(after.calls).toBe(before.calls);
});

test('Choiceの該当なし・複合技能の保留を人の承認と分け、過去noulは新方式へ流用しない',async({page,request})=>{
 await page.goto('/skill-dictionary.html');await expect(page.getByLabel('実績・知識ID')).toBeVisible();
 async function choose(fragment:string,label:string){await page.getByLabel('実績・知識ID').fill('fragment-'+fragment);await page.getByRole('button',{name:'保存済み抽出を収集',exact:true}).click();await expect(page.getByRole('status')).toContainText('収集状態');await page.locator('tbody tr').filter({hasText:label}).first().getByRole('button',{name:'この候補を確認'}).click();await expect(page.locator('.skill-suggestion').first().getByText('まだ照会していません。',{exact:true})).toBeVisible();}
 const before=await(await request.get('/api/v1/skill-dictionary')).json();await choose('choice-outside','辞書外');
 await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();await expect(page.locator('.skill-suggestion')).toContainText('新規候補（未確定）');await expect(page.locator('.skill-suggestion')).toContainText('該当なしを選択');
 await choose('choice-compound','回帰試験と性能計測');await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();await expect(page.locator('.skill-suggestion')).toContainText('要確認');await expect(page.locator('.skill-suggestion')).toContainText('判断保留');
 await page.locator('.candidate-review').screenshot({path:'test-results/skill-dictionary/choice-uncertain.png'});
 await choose('choice-legacy','過去方式');const candidates=(await(await request.get('/api/v1/skill-candidates')).json()).items;const c=candidates.find((x:{fragmentId:string})=>x.fragmentId==='fragment-choice-legacy');
 expect((await request.post('/api/test/seed-noul?candidateId='+encodeURIComponent(c.id))).status()).toBe(200);
 await page.reload();await page.locator('tbody tr').filter({hasText:'過去方式'}).first().getByRole('button',{name:'この候補を確認'}).click();
 const panel=page.locator('.skill-suggestion').first();await expect(panel.getByRole('heading',{name:'Choiceによる既存技能の候補'})).toBeVisible();await expect(panel.getByText('まだ照会していません。',{exact:true})).toBeVisible();
 const audit=await(await request.get('/api/test/semantic-audit')).json();await page.getByRole('button',{name:'AIで既存技能の候補を照会'}).click();await expect(panel).toContainText('小数2桁の丸めを許容して受理');await expect(panel.getByRole('columnheader',{name:'最小値'})).toHaveCount(0);
 const after=await(await request.get('/api/test/semantic-audit')).json();expect(after.calls-audit.calls).toBe(1);expect(await(await request.get('/api/v1/skill-dictionary')).json()).toEqual(before);
 await panel.screenshot({path:'test-results/skill-dictionary/choice-rounded.png'});
 await panel.locator('summary').click();await expect(panel.getByRole('heading',{name:'過去のnoul系提案'})).toBeVisible();await expect(panel.getByRole('columnheader',{name:'最小値'})).toBeVisible();await panel.locator('details').screenshot({path:'test-results/skill-dictionary/choice-noul-history.png'});
});

test('古い候補と収集保留を現在の原文から再確認し、二重再収集・途中の原文変更・権限撤回を安全に扱う',async({page,request})=>{
 const errors:string[]=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('/skill-dictionary.html');
 await expect(page.getByRole('button',{name:'保存済み抽出を収集',exact:true})).toBeVisible();
 const old=(await(await request.get('/api/v1/skill-candidates')).json()).items.find((x:{fragmentId:string})=>x.fragmentId==='fragment-recovery-stale');
 expect(old.sourceStatus).toBe('changed');expect(old.item).toBeNull();
 const row=page.locator('tbody tr').filter({hasText:'fragment-recovery-stale'}).first();await expect(row).toContainText('再収集が必要');await expect(row.getByRole('checkbox')).toBeDisabled();
 await row.getByRole('button',{name:'この候補を確認'}).click();const panel=page.locator('.source-review');await expect(panel).toContainText('私は現在の候補を実施した。');await expect(panel).not.toContainText('旧い候補');await expect(page.getByRole('button',{name:'判断を承認して保存'})).toHaveCount(0);
 await panel.getByRole('button',{name:'一覧へ戻る'}).click();await expect(panel).toHaveCount(0);
 await row.getByRole('button',{name:'この候補を確認'}).click();await expect(panel).toBeVisible();
 await request.post('/api/test/recovery-change');await panel.getByRole('button',{name:'現在の根拠で再収集'}).click();await expect(page.getByRole('alert')).toContainText('再確認');
 await row.getByRole('button',{name:'この候補を確認'}).click();await expect(panel).toBeVisible();
 // Lose the committed response; retry keeps the exact idempotency key and fingerprint.
 let count=0;let firstKey='';await page.route('**/api/v1/skill-candidates/collect',async route=>{count++;const key=route.request().headers()['idempotency-key'];if(count===1){firstKey=key;const response=await route.fetch();expect(response.status()).toBe(200);await route.abort('failed');}else{expect(key).toBe(firstKey);await route.continue();}});
 await panel.getByRole('button',{name:'現在の根拠で再収集'}).evaluate((b:HTMLButtonElement)=>{b.click();b.click();});await expect(page.getByRole('alert')).toContainText('結果を確認できません');expect(count).toBe(1);
 await page.getByRole('button',{name:'同じ操作を再送'}).click();await expect(page.getByRole('status')).toContainText('収集状態');await expect(panel).toHaveCount(0);await expect(page.getByRole('button',{name:'この候補を確認'}).first()).toBeEnabled();expect(count).toBe(2);await page.unroute('**/api/v1/skill-candidates/collect');
 const current=(await(await request.get('/api/v1/skill-candidates')).json()).items.filter((x:{fragmentId:string})=>x.fragmentId==='fragment-recovery-stale');expect(current).toHaveLength(2);expect(current.filter((x:{sourceStatus:string})=>x.sourceStatus==='current')).toHaveLength(1);
 await page.locator('tbody tr').filter({hasText:'fragment-recovery-held'}).getByRole('button',{name:'この候補を確認'}).click();await expect(panel).toContainText('引用が原文と一致しない');await expect(panel).not.toContainText('捏造技能');await expect(panel).toContainText('保留 1 件');await panel.screenshot({path:'test-results/skill-dictionary/recovery-held.png'});
 await panel.getByRole('button',{name:'現在の根拠で再収集'}).click();await expect(panel).toHaveCount(0);await expect(page.getByRole('status')).toContainText('新規候補 0 件');
 const revokeRow=page.locator('tbody tr').filter({hasText:'権限試験'}).first();await revokeRow.getByRole('button',{name:'この候補を確認'}).click();await expect(page.getByRole('button',{name:'承認履歴を見る'})).toBeEnabled();await request.post('/api/test/recovery-revoke');await page.getByRole('button',{name:'承認履歴を見る'}).click();await expect(page.getByRole('alert')).toContainText('閲覧条件');await expect(page.getByText('私は権限試験を実施した。',{exact:true})).toHaveCount(0);
 await page.getByRole('button',{name:'権限を確認して読み込む'}).click();await expect(page.locator('tbody tr').filter({hasText:'権限試験'})).toHaveCount(0);expect(errors).toEqual([]);
 await page.locator('tbody tr').filter({hasText:'fragment-recovery-stale'}).first().getByRole('button',{name:'この候補を確認'}).click();await expect(panel).toBeVisible();await panel.screenshot({path:'test-results/skill-dictionary/recovery-stale.png'});
});
