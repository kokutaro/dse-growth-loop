import { test, expect, type APIRequestContext } from '@playwright/test';
async function read(request: APIRequestContext, url: string, options: Parameters<APIRequestContext['get']>[1], expected = 200) {
  for (let attempt = 0; ; attempt++) {
    const response = await request.get(url, options);
    const value = await response.json().catch(() => ({}));
    if (response.status() === 503 && value.code === 'ACCESS_BUSY' && attempt < 3) {
      await new Promise(resolve => setTimeout(resolve, 200 * 2 ** attempt)); continue;
    }
    expect(response.status(), `GET ${url}: ${value.code ?? response.status()}`).toBe(expected);
    return response;
  }
}

const owner = { 'X-Demo-Person': 'demo-person-01' };

test('社員ワークスペースのリクエストと応答は匿名専用契約に収まり、内部識別情報を返さない', async ({ page, request }) => {
  const profiles = await Promise.all(['demo-person-01', 'demo-person-02'].map(async person => (await read(request, `/api/v1/people/${person}`, { headers: { 'X-Demo-Person': person } })).json()));
  const protectedNames = profiles.map(p => p.displayName).filter(Boolean);
  const paths: string[] = [], requestBodies: string[] = [], responseBodies: string[] = [], reads: Promise<void>[] = [];
  page.on('request', r => { if (r.url().includes('/api/v1/') || r.url().includes('/api/search')) { paths.push(new URL(r.url()).pathname); if (r.postData()) requestBodies.push(r.postData()!); } });
  page.on('response', r => { if (r.url().includes('/api/v1/workspace/')) reads.push(r.text().then(body => { responseBodies.push(body); }).catch(() => {})); });
  await page.goto('/');
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await page.getByLabel('課題のタイトル').fill('調査記録の引き継ぎを匿名で相談');
  await page.getByLabel('困っていること・相談したいこと').fill('確認の観点を共同で整理したいです。');
  await page.getByRole('button', { name: '匿名名で投稿する（ローカル）' }).click();
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
  await page.getByText('匿名で協力を依頼する', { exact: true }).click();
  await page.getByRole('button', { name: '匿名の候補を確認' }).click();
  await page.getByRole('button', { name: /に参加依頼$/ }).first().click();
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
  await page.getByRole('link', { name: /^参加依頼/ }).click();
  await page.getByRole('link', { name: '内容を確認' }).click();
  await page.getByRole('button', { name: '参加する', exact: true }).click();
  await page.getByLabel('メッセージ', { exact: true }).fill('根拠を確認して整理しました。');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' })).toContainText('根拠を確認して整理しました。');
  await page.getByText('振り返りと事例公開', { exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toBeVisible();
  await Promise.all(reads);
  expect(paths.length).toBeGreaterThan(8);
  expect(paths.every(p => p.startsWith('/api/v1/workspace/'))).toBe(true);
  const wire = [...requestBodies, ...responseBodies].join('\n');
  expect(wire).not.toMatch(/demo-person-|demo-principal-|"ownerId"|"personId"|"participantIds"|"sourceRefs"|"departmentId":"demo-department"/);
  for (const name of protectedNames) expect(wire).not.toContain(name);
  expect(await page.locator('#stage').innerText()).not.toMatch(/demo-person-|demo-principal-/);
  const rooms = await (await read(request, '/api/v1/workspace/rooms', { headers: owner })).json();
  expect((await read(request, `/api/v1/workspace/rooms/${rooms.items[0].id}/messages`, { headers: { 'X-Demo-Person': 'demo-person-03' } }, 404)).status()).toBe(404);
});

test('相談投稿の応答消失でも内容とキーを固定し、再確認後の保存は一件だけ', async ({ page, request }) => {
  await page.goto('/');
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await page.getByLabel('課題のタイトル').fill('保存確認のための相談');
  await page.getByLabel('困っていること・相談したいこと').fill('応答を確認しながら作業する。');
  const keys: string[] = []; let first = true;
  await page.route('**/api/v1/workspace/challenges', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    keys.push(route.request().headers()['idempotency-key']);
    if (first) { first = false; await route.fetch(); return route.abort(); }
    return route.continue();
  });
  await page.getByRole('button', { name: '匿名名で投稿する（ローカル）' }).click();
  await expect(page.getByRole('button', { name: '同じ内容で投稿を再確認' })).toBeVisible();
  await expect(page.getByLabel('課題のタイトル')).toBeDisabled();
  await page.getByRole('link', { name: 'ホーム', exact: true }).click(); await page.goBack();
  await expect(page.getByLabel('困っていること・相談したいこと')).toBeDisabled();
  await page.getByRole('button', { name: '同じ内容で投稿を再確認' }).click();
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
  expect(new Set(keys).size).toBe(1);
  const rows = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
  expect(rows.items).toHaveLength(1);
});

test('識別情報を含む本文の拒否を保存済みと扱わず、匿名の内容へ修正できる', async ({ page, request }) => {
  await page.goto('/');
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await page.getByLabel('課題のタイトル').fill('匿名の記録');
  await page.getByLabel('困っていること・相談したいこと').fill('手順を共同で整理します。');
  await page.getByRole('button', { name: '匿名名で投稿する（ローカル）' }).click();
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
  await page.getByLabel('メッセージ', { exact: true }).fill('連絡先 secret-person@example.invalid');
  const denied = page.waitForResponse(r => r.url().endsWith('/messages') && r.request().method() === 'POST');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  expect((await denied).status()).toBe(422);
  await expect(page.getByRole('list', { name: '保存済みの会話' }).locator('li')).toHaveCount(0);
  await expect(page.getByLabel('メッセージ', { exact: true })).toHaveValue('連絡先 secret-person@example.invalid');
  await page.getByLabel('メッセージ', { exact: true }).fill('識別情報を省いた確認結果です。');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' })).toContainText('識別情報を省いた確認結果です。');
  const rooms = await (await read(request, '/api/v1/workspace/rooms', { headers: owner })).json();
  const messages = await (await read(request, `/api/v1/workspace/rooms/${rooms.items[0].id}/messages`, { headers: owner })).json();
  expect(messages.items).toHaveLength(1); expect(JSON.stringify(messages)).not.toContain('secret-person');
});
