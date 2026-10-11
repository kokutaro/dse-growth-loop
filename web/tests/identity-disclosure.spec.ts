import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
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

const actor = (person: string) => ({ 'X-Demo-Person': person });
async function roomWithGuest(page: Page) {
  await page.goto('/');
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await page.getByLabel('課題のタイトル').fill('調査記録の引き継ぎ');
  await page.getByLabel('困っていること・相談したいこと').fill('再現条件と試したことを一緒に整理したいです。');
  await page.getByRole('button', { name: '匿名名で投稿する（ローカル）' }).click();
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
  await page.getByText('匿名で協力を依頼する', { exact: true }).click();
  await page.getByRole('button', { name: '匿名の候補を確認' }).click();
  await page.getByRole('button', { name: /に参加依頼$/ }).first().click();
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
  await page.getByRole('link', { name: /^参加依頼/ }).click();
  await page.getByRole('link', { name: '内容を確認' }).click();
  await page.getByRole('button', { name: '参加する', exact: true }).click();
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
}
async function asParticipant(page: Page, person: string) {
  await page.getByLabel('ローカル検証の役割').selectOption(person);
  await page.getByRole('link', { name: 'ルームを開く', exact: true }).click();
  await expect(page.getByRole('region', { name: '実名開示の相互同意' })).toBeVisible();
}
async function grant(page: Page) {
  await page.getByLabel('この活動の参加者に本人プロフィールの表示名を開示することに同意').check();
  await page.getByRole('button', { name: '実名開示への同意を保存', exact: true }).click();
}

test('独立した相互同意だけで表示名を開示し、撤回後は全員の再確認が必要', async ({ page, request }) => {
  await roomWithGuest(page);
  const rooms = await (await read(request, '/api/v1/workspace/rooms', { headers: actor('demo-person-02') })).json();
  const path = `/api/v1/workspace/rooms/${rooms.items[0].id}/identity-consent`;
  const before = await (await read(request, path, { headers: actor('demo-person-02') })).json();
  expect(before.participants.every((p: any) => p.sharedName === null)).toBe(true);
  await grant(page);
  await expect(page.getByTestId('identity-status')).toContainText('相手の確認を待っています');
  const oneGrant = await (await read(request, path, { headers: actor('demo-person-02') })).json();
  expect(oneGrant.participants.every((p: any) => p.sharedName === null)).toBe(true);
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
  await asParticipant(page, 'demo-person-01'); await grant(page);
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' }).locator('li')).toHaveCount(2);
  const shared = await (await read(request, path, { headers: actor('demo-person-01') })).json();
  expect(shared.state).toBe('shared');
  expect(shared.participants.every((p: any) => p.sharedName && !('personId' in p))).toBe(true);
  const knowledge = await (await read(request, `/api/v1/workspace/rooms/${rooms.items[0].id}/knowledge`, { headers: actor('demo-person-01') })).json();
  expect(knowledge.consents).toEqual({}); // Identity disclosure never grants publication or analysis.
  await page.getByRole('button', { name: '実名開示への同意を撤回' }).click();
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
  await grant(page);
  await expect(page.getByTestId('identity-status')).toContainText('相手の確認を待っています');
  await asParticipant(page, 'demo-person-02');
  await expect(page.getByLabel('この活動の参加者に本人プロフィールの表示名を開示することに同意')).not.toBeChecked();
  await grant(page);
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).toBeVisible();
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-03');
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
  expect((await read(request, path, { headers: actor('demo-person-03') }, 404)).status()).toBe(404);
});

test('プロフィール変更・読み直し失敗で共有名を消し、古い同意を再利用しない', async ({ page, request }) => {
  await roomWithGuest(page); await grant(page); await asParticipant(page, 'demo-person-01'); await grant(page);
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).toBeVisible();
  const rooms = await (await read(request, '/api/v1/workspace/rooms', { headers: actor('demo-person-01') })).json();
  const identityPath = `/api/v1/workspace/rooms/${rooms.items[0].id}/identity-consent`;
  const previous = await (await read(request, identityPath, { headers: actor('demo-person-01') })).json();
  await page.getByRole('link', { name: '本人プロフィールの現在の表示名を確認' }).click();
  await page.getByRole('button', { name: 'プロフィールを更新', exact: true }).click();
  await page.getByLabel('表示名', { exact: true }).fill('本人が訂正した表示名');
  await page.getByRole('button', { name: '訂正を保存', exact: true }).click();
  await expect(page.locator('.profiles')).toContainText('保存しました');
  await page.getByRole('link', { name: '活動ルーム', exact: true }).click();
  await page.getByRole('link', { name: 'ルームを開く', exact: true }).click();
  await expect(page.getByTestId('identity-status')).toContainText('現在は匿名');
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
  const changed = await (await read(request, identityPath, { headers: actor('demo-person-01') })).json();
  expect(changed.reviewedVersion).not.toBe(previous.reviewedVersion);
  expect(changed.participants.every((p: any) => p.sharedName === null)).toBe(true);
  await grant(page); await asParticipant(page, 'demo-person-02'); await grant(page);
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).toContainText('本人が訂正した表示名');
  await page.route('**/identity-consent', route => route.fulfill({ status: 503, json: { code: 'READ_FAILED' } }));
  await page.getByRole('button', { name: '実名開示の状態を再確認' }).click();
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
  await expect(page.getByRole('region', { name: '実名開示の相互同意' }).getByRole('alert')).toBeVisible();
});
