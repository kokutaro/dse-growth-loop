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


const owner = { 'X-Demo-Person': 'demo-person-01' };
for (const boundary of ['actor', 'logout'] as const) {
  test(`旧主体の遅延ルームGET後に${boundary}を越えてメッセージPOSTを送らない`, async ({ page, request }) => {
    if (boundary === 'logout') {
      await page.route('**/*msal-browser*', route => route.fulfill({ contentType: 'application/javascript', body: `
        export class InteractionRequiredAuthError extends Error {}
        export class PublicClientApplication {
          async initialize() {} getAllAccounts() { return [{ homeAccountId: 'fixture' }]; }
          async acquireTokenSilent() { return { accessToken: 'fixture-token' }; }
          async logoutPopup() { throw new Error('fixture popup blocked'); } async clearCache() {}
        }` }));
      await page.route('https://login.microsoftonline.com/**', route => route.abort());
      await page.route('**/api/config', route => route.fulfill({ json: { mode: 'Azure', tenantId: 'fixture-tenant', clientId: 'fixture', scope: 'fixture' } }));
    }
    await compose(page, '終了境界の架空相談'); await post(page);
    const challenges = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
    const roomId = challenges.items[0].roomId;
    let enter!: () => void, release!: () => void, finish!: () => void;
    const entered = new Promise<void>(resolve => { enter = resolve; });
    const delayed = new Promise<void>(resolve => { release = resolve; });
    const finished = new Promise<void>(resolve => { finish = resolve; });
    let writes = 0;
    page.on('request', req => { if (req.method() === 'POST' && req.url().endsWith(`/rooms/${roomId}/messages`)) writes++; });
    await page.route(`**/api/v1/workspace/rooms/${roomId}`, async route => {
      const response = await route.fetch();
      enter();
      await delayed;
      try { await route.fulfill({ response }); } catch { /* Aborted fetch cannot consume the delayed response. */ }
      finally { finish(); }
    });
    try {
      await page.getByLabel('メッセージ', { exact: true }).fill('旧主体の未保存本文');
      await page.getByRole('button', { name: /^送信する(?:（ローカル）)?$/ }).click();
      await entered;
      if (boundary === 'actor') {
        await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
        await expect(page.getByRole('heading', { name: 'ホーム', exact: true })).toBeVisible();
      } else {
        await page.getByRole('button', { name: 'ログアウト', exact: true }).click();
        await expect(page.getByRole('heading', { name: '接続を終了しました' })).toBeVisible();
      }
      release(); await finished;
      await page.evaluate(() => Promise.resolve());
      expect(writes).toBe(0);
      const messages = await (await read(request, `/api/v1/workspace/rooms/${roomId}/messages`, { headers: owner })).json();
      expect(messages.items).toHaveLength(0);
      await expect(page.getByText('旧主体の未保存本文', { exact: true })).not.toBeVisible();
    } finally { release(); await finished; }
  });
}
test('キーボードの本文スキップは現在のルートを保持して本文へフォーカスする', async ({ page }) => {
  await page.route('**/api/config', route => route.fulfill({ json: { mode: 'Demo' } }));
  await page.route('**/api/v1/workspace/**', route => route.fulfill({ json: route.request().url().endsWith('/capabilities') ? {
    contractVersion: 'anonymous-workspace-v1', available: true, structuredIdentifiersRemoved: true,
    roomLocalAliases: true, opaqueOperationIds: true, freeTextAnonymized: false, legacyApiIsolation: 'operator-or-development',
  } : { items: [], continuationToken: null } }));
  await page.goto('/#tasks');
  const main = page.getByRole('main');
  const skip = page.getByRole('link', { name: '本文へ移動', exact: true });
  await expect(main.getByRole('heading', { name: '自分の課題・相談', exact: true })).toBeVisible();
  await expect(main).toBeFocused();
  // Route navigation focuses the main landmark. Walk backwards to the document's first link,
  // then Tab from the preceding focus boundary to exercise normal keyboard activation.
  for (let step = 0; step < 20 && !await skip.evaluate(element => element === document.activeElement); step++) {
    await page.keyboard.press('Shift+Tab');
  }
  await expect(skip).toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await page.keyboard.press('Tab');
  await expect(skip).toBeFocused();
  const currentUrl = page.url();
  await page.keyboard.press('Enter');
  await expect(main).toBeFocused();
  await expect(page).toHaveURL(currentUrl);
  await expect(main.getByRole('heading', { name: '自分の課題・相談', exact: true })).toBeVisible();
  await expect(page.getByText('対象を開けません', { exact: true })).not.toBeVisible();
  await page.keyboard.press('Tab');
  await expect(main.getByRole('link', { name: '課題・相談を投稿する', exact: true }).first()).toBeFocused();
  await page.setViewportSize({ width: 390, height: 844 });
  await skip.focus();
  await page.keyboard.press('Enter');
  await expect(main).toBeFocused();
  await expect(page).toHaveURL(currentUrl);
  const heading = await main.getByRole('heading', { name: '自分の課題・相談', exact: true }).boundingBox();
  const rail = await page.getByRole('complementary').boundingBox();
  expect(heading).not.toBeNull(); expect(rail).not.toBeNull();
  expect(heading!.y).toBeGreaterThanOrEqual(rail!.y + rail!.height);
  expect(heading!.y + heading!.height).toBeLessThanOrEqual(844);
});

async function compose(page: Page, title = '調査記録の引き継ぎを整理する') {
  await page.goto('/');
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await page.getByLabel('課題のタイトル').fill(title);
  await page.getByLabel('困っていること・相談したいこと').fill('架空の相談です。再現条件と試したことを整理したいです。');
}
async function post(page: Page) {
  await page.getByRole('button', { name: /^匿名名で投稿する(?:（ローカル）)?$/ }).click();
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
}
async function invite(page: Page) {
  await page.getByText('匿名で協力を依頼する', { exact: true }).click();
  await page.getByRole('button', { name: '匿名の候補を確認' }).click();
  const committed = page.waitForResponse(r => r.url().endsWith('/invitations') && r.request().method() === 'POST');
  await page.getByRole('button', { name: /に参加依頼$/ }).first().click();
  expect((await committed).status()).toBe(201);
}
async function asGuest(page: Page) {
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
  await page.getByRole('link', { name: /^参加依頼/ }).click();
  await page.getByRole('link', { name: '内容を確認' }).click();
}

test('下書きと未保存入力を履歴移動で保持し、投稿済みURLから二重投稿しない', async ({ page, request }) => {
  await compose(page);
  const freshUrl = page.url();
  await page.getByRole('link', { name: 'マイページを開く' }).click();
  await page.goBack();
  await expect(page.getByLabel('課題のタイトル')).toHaveValue('調査記録の引き継ぎを整理する');
  await page.getByRole('button', { name: '未保存の入力を破棄' }).click();
  await page.getByRole('button', { name: '入力を続ける', exact: true }).click();
  await expect(page.getByLabel('課題のタイトル')).toHaveValue('調査記録の引き継ぎを整理する');
  await page.getByRole('button', { name: '下書きにする' }).click();
  await page.getByRole('link', { name: '続きを書く' }).click();
  const savedDraftUrl = page.url();
  await post(page);
  for (const url of [freshUrl, savedDraftUrl]) {
    await page.goto(url);
    // Revisiting the stale hash within this document must consume the posted draft entry.
    await expect(page.getByLabel('課題のタイトル')).not.toBeVisible();
  }
  const rows = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
  expect(rows.items).toHaveLength(1);
  await page.getByRole('link', { name: '自分の課題・相談', exact: true }).click();
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await expect(page.getByLabel('課題のタイトル')).toHaveValue('');
});

test('投稿応答が遅れても戻る操作で入力を変更・二重送信できない', async ({ page, request }) => {
  await compose(page);
  const draftUrl = page.url();
  let release!: () => void;
  const delayed = new Promise<void>(resolve => { release = resolve; });
  let writes = 0;
  await page.route('**/api/v1/workspace/challenges', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    writes++; const response = await route.fetch(); await delayed; await route.fulfill({ response });
  });
  try {
    await page.getByRole('button', { name: '匿名名で投稿する（ローカル）' }).click();
    await page.getByRole('link', { name: 'ホーム', exact: true }).click();
    await page.goBack();
    await expect(page.getByLabel('課題のタイトル')).toBeDisabled();
    release();
    await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
    await page.goBack();
    await expect(page.getByLabel('課題のタイトル')).not.toBeVisible();
    expect(writes).toBe(1);
    const rows = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
    expect(rows.items).toHaveLength(1);
    expect(draftUrl).toContain('#compose/new-');
  } finally { release(); }
});

test('参加するだけで追加承認なく会話でき、画面は匿名名を維持する', async ({ page, request }) => {
  await compose(page); await post(page); await invite(page); await asGuest(page);
  const accepted = page.waitForResponse(r => r.url().includes('/workspace/invitations/') && r.request().method() === 'PATCH');
  await page.getByRole('button', { name: '参加する', exact: true }).click();
  expect((await accepted).status()).toBe(200);
  await expect(page.getByLabel('メッセージ', { exact: true })).toBeVisible();
  await page.getByLabel('メッセージ', { exact: true }).fill('再現条件から一緒に整理しましょう。');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' })).toContainText('再現条件から一緒に整理しましょう。');
  const rows = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
  const session = await (await read(request, `/api/v1/workspace/rooms/${rows.items[0].roomId}`, { headers: owner })).json();
  expect(session.participantCount).toBe(2);
  expect(session.participants).toHaveLength(2);
  const body = await page.locator('#stage').innerText();
  expect(body).toMatch(/協力者/);
  expect(body).not.toMatch(/demo-person-|demo-principal-/);
  await expect(page.getByRole('region', { name: '実名開示の相互同意' })).toBeVisible();
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
});

test('辞退した依頼では会話を開けない', async ({ page }) => {
  await compose(page); await post(page); await invite(page);
  const roomUrl = page.url();
  await asGuest(page);
  await page.getByRole('button', { name: '今回は辞退する' }).click();
  await expect(page.getByText('回答待ちの参加依頼はありません。')).toBeVisible();
  await page.goto(roomUrl);
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
  await page.goto(roomUrl);
  // Role stays scoped to the page session; hash navigation preserves the selected guest.
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
  await page.evaluate(url => { location.hash = new URL(url).hash; }, roomUrl);
  await expect(page.getByLabel('メッセージ', { exact: true })).not.toBeVisible();
});

for (const mode of ['Demo', 'Azure'] as const) test(`${mode}: 会話保存の失敗と応答消失で同じキーを保持し、再読込後も一件だけ保存する`, async ({ page, request }) => {
  let azureActor = 'demo-person-01';
  if (mode === 'Azure') {
    // Authenticate only against a local Demo API fixture; no Entra calls.
    await page.route('**/src/auth.ts*', route => route.fulfill({ contentType: 'application/javascript', body: 'let controller = new AbortController(); export function authSessionSignal() { return controller.signal; } export async function initializeAuth() { controller.abort(); controller = new AbortController(); } export async function getToken() { return "fixture-token"; } export async function endAuth() { controller.abort(); }' }));
    await page.route('**/api/config', route => route.fulfill({ json: { mode: 'Azure', tenantId: 'fixture-tenant', clientId: 'fixture', scope: 'fixture' } }));
    await page.route('**/api/v1/**', route => route.continue({ headers: { ...route.request().headers(), 'X-Demo-Person': azureActor } }));
  }
  await compose(page); await post(page);
  const roomUrl = page.url();
  let ownOutbox = '', otherOutbox = '';
  if (mode === 'Azure') {
    await invite(page);
    const guest = { 'X-Demo-Person': 'demo-person-02' };
    const invitations = await (await read(request, '/api/v1/workspace/invitations', { headers: guest })).json();
    const invitation = invitations.items[0];
    const acceptOptions = { headers: { ...guest, 'If-Match': invitation.etag, 'Idempotency-Key': crypto.randomUUID() }, data: { decision: 'accepted' } };
    for (let attempt = 0; ; attempt++) {
      const response = await request.patch(`/api/v1/workspace/invitations/${invitation.id}`, acceptOptions);
      const value = await response.json().catch(() => ({}));
      // ACCESS_BUSY is returned before fence acquisition; reuse the exact setup request.
      if (response.status() === 503 && value.code === 'ACCESS_BUSY' && value.retryable === true && attempt < 3) {
        await new Promise(resolve => setTimeout(resolve, 200 * 2 ** attempt)); continue;
      }
      expect(response.status(), `fixture accept: ${value.code ?? response.status()}`).toBe(200); break;
    }
    await page.getByRole('button', { name: '会話を更新', exact: true }).click();
    const challenges = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
    const room = await (await read(request, `/api/v1/workspace/rooms/${challenges.items[0].roomId}`, { headers: owner })).json();
    ownOutbox = `dse-workspace-outbox:v2:Azure:fixture-tenant:${room.id}:${room.participants.find((p: { isSelf: boolean }) => p.isSelf).aliasId}`;
    otherOutbox = `dse-workspace-outbox:v2:Azure:fixture-tenant:${room.id}:${room.participants.find((p: { isSelf: boolean }) => !p.isSelf).aliasId}`;
    await page.evaluate(key => sessionStorage.setItem(key, JSON.stringify({ body: '別アカウントの未保存本文', key: 'other-key', clientMessageId: 'other-client' })), otherOutbox);
  }
  const keys: string[] = [], clients: string[] = [];
  let attempts = 0;
  let committed!: (status: number) => void;
  const saved = new Promise<number>(resolve => { committed = resolve; });
  await page.route('**/api/v1/workspace/rooms/*/messages', async route => {
    if (route.request().method() !== 'POST') return route.fallback();
    attempts++; keys.push(route.request().headers()['idempotency-key']); clients.push(route.request().postDataJSON().clientMessageId);
    if (attempts === 1) return route.fulfill({ status: 503, json: { code: 'SAVE_FAILED' } });
    if (attempts === 2) { const response = await route.fetch({ headers: { ...route.request().headers(), ...(mode === 'Azure' ? { 'X-Demo-Person': azureActor } : {}) } }); committed(response.status()); return route.abort(); }
    await route.fallback();
  });
  await page.getByLabel('メッセージ', { exact: true }).fill('再送しても重複しない記録');
  await page.getByRole('button', { name: /^送信する(?:（ローカル）)?$/ }).click();
  await expect(page.getByText('未保存（結果未確認）。同じキーで再試行し、重複を防ぎます。')).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('保存を確認できませんでした');
  await page.reload();
  await expect(page.getByRole('button', { name: '同じ内容で保存を再確認' })).toBeVisible();
  await page.getByRole('button', { name: '同じ内容で保存を再確認' }).click();
  expect(await saved).toBe(201);
  await expect(page.getByRole('alert')).toContainText('保存を確認できませんでした');
  // A read can confirm an ambiguous committed write without sending again.
  await page.getByRole('button', { name: '会話を更新' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' }).locator('li')).toHaveCount(1);
  expect(new Set(keys).size).toBe(1); expect(new Set(clients).size).toBe(1);
  const challenges = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
  const rows = await (await read(request, `/api/v1/workspace/rooms/${challenges.items[0].roomId}/messages`, { headers: owner })).json();
  expect(rows.items).toHaveLength(1); expect(page.url()).toBe(roomUrl);
  if (mode === 'Azure') {
    expect(await page.evaluate(key => sessionStorage.getItem(key), ownOutbox)).toBeNull();
    await page.evaluate(key => sessionStorage.setItem(key, JSON.stringify({ body: '相談者だけの未保存本文', key: 'owner-key', clientMessageId: 'owner-client' })), ownOutbox);
    azureActor = 'demo-person-02'; await page.reload();
    await expect(page.getByRole('button', { name: '同じ内容で保存を再確認' })).toBeVisible();
    await expect(page.getByLabel('メッセージ', { exact: true })).toHaveValue('別アカウントの未保存本文');
    await expect(page.getByLabel('メッセージ', { exact: true })).not.toHaveValue('相談者だけの未保存本文');
  }
});

test('ETag競合では入力を保持し、再送で最新版を確認する', async ({ page }) => {
  await compose(page); await post(page);
  let first = true;
  const keys: string[] = [];
  await page.route('**/api/v1/workspace/rooms/*/messages', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    keys.push(route.request().headers()['idempotency-key']);
    if (first) { first = false; return route.fulfill({ status: 412, json: { code: 'PRECONDITION_FAILED' } }); }
    await route.continue();
  });
  await page.getByLabel('メッセージ', { exact: true }).fill('競合しても保持される内容');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await expect(page.getByRole('alert')).toContainText('別の更新');
  await expect(page.getByLabel('メッセージ', { exact: true })).toHaveValue('競合しても保持される内容');
  await page.getByRole('button', { name: '同じ内容で保存を再確認' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' })).toContainText('競合しても保持される内容');
  expect(new Set(keys).size).toBe(1);
});

test('参加権限を失ったルームは本文を消して送信と公開操作を閉じる', async ({ page }) => {
  await compose(page); await post(page);
  await page.getByLabel('メッセージ', { exact: true }).fill('権限のあるときの架空記録');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' })).toContainText('権限のあるときの架空記録');
  await page.route('**/api/v1/workspace/rooms/*', route => route.fulfill({ status: 403, json: { code: 'FORBIDDEN' } }));
  await page.getByRole('button', { name: '会話を更新' }).click();
  await expect(page.getByRole('heading', { name: '対象を開けません' })).toBeVisible();
  await expect(page.getByLabel('メッセージ', { exact: true })).not.toBeVisible();
  await expect(page.getByText('権限のあるときの架空記録', { exact: true })).not.toBeVisible();
});

test('双方同意と承認のない旧サンプル事例を社員画面へ公開しない', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('link', { name: '事例・ナレッジ', exact: true }).click();
  await expect(page.getByText('一致する事例はありません。別の言葉でお試しください。')).toBeVisible();
  await expect(page.getByRole('link', { name: '事例を読む' })).toHaveCount(0);
  await page.evaluate(() => { location.hash = 'case/knowledge-01'; });
  await expect(page.getByRole('alert')).toContainText('公開が撤回されたか');
  await expect(page.getByRole('heading', { name: '担当変更時の確認項目を標準化', exact: true })).not.toBeVisible();
});

for (const width of [320, 390]) {
  test(`${width}pxの全メニューとフォームは横はみ出しなく44pxの操作幅を保つ`, async ({ page }) => {
    await page.setViewportSize({ width, height: 844 }); await page.goto('/');
    const menu = page.getByRole('navigation', { name: 'メインメニュー' });
    await expect(menu.getByRole('link')).toHaveCount(5);
    for (const item of await menu.getByRole('link').all()) {
      const bounds = await item.boundingBox(); expect(bounds!.height).toBeGreaterThanOrEqual(44);
      expect(bounds!.x).toBeGreaterThanOrEqual(0); expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width);
    }
    await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
    await expect(page.getByLabel('課題のタイトル')).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(width);
    await page.screenshot({ path: `test-results/react-composer-${width}.png`, fullPage: true });
  });
}

test('Azureでは匿名契約のないAPIを呼ばず、ログアウトで画面を閉じる', async ({ page }) => {
  await page.route('**/api/config', route => route.fulfill({ json: {
    mode: 'Azure', tenantId: '11111111-1111-1111-1111-111111111111',
    clientId: '22222222-2222-2222-2222-222222222222', scope: 'api://dummy/access_as_user',
  } }));
  await page.route('https://login.microsoftonline.com/**', route => route.abort());
  const protectedCalls: string[] = [];
  await page.route('**/api/v1/**', route => { protectedCalls.push(route.request().url()); return route.abort(); });
  await page.route('**/api/search', route => { protectedCalls.push(route.request().url()); return route.abort(); });
  await page.goto('/');
  await expect(page.getByText('匿名で安心して協力できるよう、接続を準備しています')).toBeVisible();
  for (const name of ['自分の課題・相談', '参加依頼', '活動ルーム', '事例・ナレッジ']) {
    await page.getByRole('link', { name, exact: true }).click();
    await expect(page.getByText('匿名で安心して協力できるよう、接続を準備しています')).toBeVisible();
  }
  expect(protectedCalls).toHaveLength(0);
  await page.getByRole('button', { name: 'ログアウト', exact: true }).click();
  await expect(page.getByRole('heading', { name: '接続を終了しました' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'メインメニュー' })).not.toBeVisible();
});

test('新UIで双方の事例公開同意を必須にし、公開後の編集で同意と検索公開を解除する', async ({ page, request }) => {
  await compose(page, '引き継ぎの双方確認'); await post(page); await invite(page); await asGuest(page);
  await page.getByRole('button', { name: '参加する', exact: true }).click();
  await page.getByLabel('メッセージ', { exact: true }).fill('ソフトウェア 引き継ぎの記録を共同で確認した。');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await expect(page.getByRole('list', { name: '保存済みの会話' })).toContainText('共同で確認した');
  const challenges = await (await read(request, '/api/v1/workspace/challenges', { headers: owner })).json();
  const sid = challenges.items[0].roomId;
  async function openKnowledgeAs(person: string) {
    await page.getByLabel('ローカル検証の役割').selectOption(person);
    await page.getByRole('link', { name: 'ルームを開く', exact: true }).click();
    await page.getByText('振り返りと事例公開', { exact: true }).click();
    await expect(page.getByTestId('knowledge-status')).toBeVisible();
  }
  await openKnowledgeAs('demo-person-01');
  await page.getByLabel('解決状態').selectOption('resolved');
  await page.getByLabel('相談の結果').fill('ソフトウェア 引き継ぎの記録を共同で確認した。');
  await page.getByRole('button', { name: '結果を保存', exact: true }).click();
  await expect(page.getByTestId('saved-outcome')).toContainText('共同で確認した');
  await page.getByRole('button', { name: 'AI下書きを作成', exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toContainText('draft');
  await expect(page.getByRole('textbox', { name: '試したこと', exact: true })).toHaveValue('ソフトウェア 引き継ぎの記録を共同で確認した。');
  await page.getByRole('button', { name: 'レビューを依頼', exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toContainText('review_requested');
  await page.getByLabel('この版と公開先を確認して公開に同意').check();
  await page.getByRole('button', { name: '同意を保存', exact: true }).click();
  await expect(page.getByTestId('knowledge-consents')).toContainText('あなた 公開同意');
  await page.getByRole('button', { name: 'この版を承認', exact: true }).click();
  await expect(page.getByRole('region', { name: '評価と公開知識' }).getByRole('alert')).toContainText('CONSENT_REQUIRED');
  await expect(page.getByTestId('knowledge-status')).toContainText('review_requested');
  await openKnowledgeAs('demo-person-02');
  await page.getByLabel('この版と公開先を確認して公開に同意').check();
  await page.getByRole('button', { name: '同意を保存', exact: true }).click();
  await expect(page.getByTestId('knowledge-consents')).toContainText('あなた 公開同意');
  await openKnowledgeAs('demo-person-01');
  await page.getByRole('button', { name: 'この版を承認', exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toContainText('published');
  const published = await (await read(request, `/api/v1/workspace/rooms/${sid}/knowledge`, { headers: owner })).json();
  expect(Object.values(published.consents).map((c: any) => c.publication)).toEqual([true, true]);
  expect(Object.values(published.consents).map((c: any) => c.analysis)).toEqual([false, false]);
  await expect(page.getByRole('region', { name: '実名開示の相互同意' })).toBeVisible();
  await expect(page.getByRole('list', { name: 'この活動で共有した表示名' })).not.toBeVisible();
  const before = await (await read(request, '/api/v1/workspace/knowledge?query=ソフトウェア', { headers: owner })).json();
  expect(before.items.some((c: { title: string; sections: { text: string }[] }) => c.title === '相談からの知識' && c.sections.some(section => section.text === 'ソフトウェア 引き継ぎの記録を共同で確認した。'))).toBe(true);
  await page.getByRole('link', { name: '事例・ナレッジ', exact: true }).click();
  await page.getByRole('link', { name: '事例を読む' }).click();
  const caseUrl = page.url();
  await expect(page.getByRole('heading', { name: '相談からの知識', exact: true })).toBeVisible();
  await page.getByRole('link', { name: '活動ルーム', exact: true }).click();
  await page.getByRole('link', { name: 'ルームを開く', exact: true }).click();
  await page.getByText('振り返りと事例公開', { exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toContainText('published');
  await page.getByRole('combobox', { name: '集計に公開する結果区分', exact: true }).selectOption('partial');
  await expect(page.getByRole('button', { name: '同意を保存', exact: true })).toBeDisabled();
  await expect(page.getByLabel('この版と公開先を確認して公開に同意')).toBeDisabled();
  await expect(page.getByText('未保存の編集があります。入力を保存すると', { exact: false })).toBeVisible();
  await page.getByRole('textbox', { name: '課題の要約', exact: true }).fill('ソフトウェア 引き継ぎの修正した事例要約');
  const evidencePicker = page.getByText('課題の要約の確認記録を選ぶ', { exact: true }).locator('..');
  await page.getByText('課題の要約の確認記録を選ぶ', { exact: true }).click();
  await evidencePicker.getByRole('checkbox').first().check();
  await page.getByRole('combobox', { name: '課題の要約の情報源', exact: true }).selectOption('inference');
  await page.getByRole('button', { name: '要約を保存', exact: true }).click();
  await expect(page.getByTestId('knowledge-status')).toContainText('draft');
  const edited = await (await read(request, `/api/v1/workspace/rooms/${sid}/knowledge`, { headers: owner })).json();
  expect(edited.sourceVersion).toBe(published.sourceVersion + 1);
  expect(edited.analysisMetadata.resolution).toBe('partial');
  // The source-backed occurrence department is authoritative; user metadata cannot replace it.
  expect(edited.occurrenceDepartmentVerified).toBe(true);
  expect(edited.analysisMetadata.departmentId).toMatch(/^awd-/);
  expect(JSON.stringify(edited)).not.toContain('demo-department-a');
  expect(edited.sections.find((section: { field: string }) => section.field === 'challenge').basis).toBe('inference');
  expect(edited.sections.find((section: { field: string }) => section.field === 'challenge').evidenceIds).toHaveLength(1);
  expect(edited.consents).toEqual({});
  const after = await (await read(request, '/api/v1/workspace/knowledge?query=ソフトウェア', { headers: owner })).json();
  expect(after.items.some((c: { title: string }) => c.title === '相談からの知識')).toBe(false);
  await page.goto(caseUrl);
  await expect(page.getByRole('alert')).toContainText('公開が撤回されたか');
  await expect(page.getByRole('heading', { name: '相談からの知識', exact: true })).not.toBeVisible();
});
