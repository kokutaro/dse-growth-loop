import { test, expect, Page } from '@playwright/test';
import path from 'node:path';
const output = path.resolve('../docs/design-preview/revised');
async function post(page: Page) { await page.locator('#post-form button[type=submit]').click(); }
async function join(page: Page) { await post(page); await page.locator('[data-notify="0"]').click(); await page.locator('#accept').click(); await page.locator('#join').click(); }
async function approveBoth(page: Page) { await page.locator('#review-role').selectOption('投稿者A'); await page.locator('#approve').click(); await page.locator('#review-role').selectOption('Expert-A17'); await page.locator('#approve').click(); }
test('参加・実名・公開の同意を分離し、変更時は公開確認を取り消す', async ({page}) => {
  await page.goto('/design-preview/index.html');
  await expect(page.locator('input[name=identity]').nth(1)).toBeDisabled();
  await page.locator('[data-step="4"]').click(); await expect(page.getByRole('heading', {name:'まず、困っていることを教えてください'})).toBeVisible();
  await page.locator('[data-next="0"]').click(); await post(page);
  await page.locator('[data-notify="0"]').click(); await page.locator('#decline').click();
  await page.locator('[data-step="4"]').click(); await expect(page.locator('#message')).toHaveCount(0);
  await page.locator('[data-step="2"]').click(); await page.locator('#retry-decision').click(); await page.locator('#accept').click();
  await page.locator('[data-step="4"]').click(); await expect(page.locator('#message')).toHaveCount(0);
  await page.locator('[data-step="3"]').click(); await page.locator('#join').click();
  await page.locator('[data-next="5"]').click(); await page.locator('#publish').check(); await page.locator('#close').click();
  await expect(page.locator('#close-error')).toBeVisible();
  await page.locator('#approve').click(); await page.locator('#close').click(); await expect(page.locator('#close-error')).toBeVisible();
  await approveBoth(page); await page.locator('#result').fill('匿名の解決要約'); await expect(page.locator('#approval-status')).not.toContainText('確認済み');
  await approveBoth(page); await page.locator('#reuse').check(); await expect(page.locator('#approval-status')).not.toContainText('確認済み');
  await approveBoth(page); await page.locator('#close').click(); await expect(page.locator('.state-label')).toContainText('社内公開を想定');
  await expect(page.locator('.summary-grid')).toContainText('事業分析利用しない');
  await page.locator('[data-next="0"]').click(); await post(page); await page.locator('[data-step="4"]').click(); await expect(page.locator('#message')).toHaveCount(0);
});
test('API通信・永続化なし、入力をHTMLとして実行しない', async ({page}) => {
  const requests: string[]=[]; page.on('request', r=> { if(r.url().includes('/api/') || !r.url().startsWith('http://127.0.0.1:5173/'))requests.push(r.url()); });
  await page.goto('/design-preview/index.html'); await join(page);
  const payload='<img src=x onerror="window.__unsafe=true">';
  await page.locator('#message').fill(payload); await page.locator('#message-form button').click();
  await expect(page.locator('.mine')).toContainText(payload); await expect(page.locator('.mine img')).toHaveCount(0);
  expect(await page.evaluate(()=>({local:localStorage.length,session:sessionStorage.length}))).toEqual({local:0,session:0});
  expect(requests).toEqual([]); await page.reload(); await page.locator('[data-step="4"]').click(); await expect(page.locator('#message')).toHaveCount(0);
});
for (const width of [1440,390,320]) test(`7段階の表示・スクリーンショット ${width}px`,async({page})=>{
  await page.setViewportSize({width,height:900}); const errors:string[]=[]; page.on('pageerror',e=>errors.push(e.message));
  await page.goto('/design-preview/index.html');
  async function shot(name:string) { await page.evaluate(()=>window.scrollTo(0,0)); await expect(page.locator('h1')).toBeVisible(); expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy(); await page.screenshot({path:path.join(output,`${name}-${width}.png`),fullPage:true}); }
  await shot('01-post'); await post(page); await shot('02-matching'); await page.locator('[data-notify="0"]').click(); await shot('03-notification');
  await page.locator('#accept').click(); await shot('04-join'); await page.locator('#join').click(); await shot('05-chat');
  await page.locator('[data-next="5"]').click(); await page.locator('#publish').check(); await approveBoth(page); await shot('06-review');
  await page.locator('#close').click(); await shot('07-knowledge'); expect(errors).toEqual([]);
  await expect(page.locator('a[href*="people"],a[href*="profile"],a[href*="documents"]')).toHaveCount(0);
});
test('キーボードで投稿でき、段階変更後に本文へフォーカスする', async ({page}) => {
  await page.goto('/design-preview/index.html');
  await page.keyboard.press('Tab'); await expect(page.locator('.skip')).toBeFocused();
  await page.keyboard.press('Enter'); await expect(page.locator('#stage')).toBeFocused();
  await page.keyboard.press('Tab'); await expect(page.locator('#title')).toBeFocused();
  await page.keyboard.press('Tab'); await expect(page.locator('#detail')).toBeFocused();
  await page.keyboard.press('Tab'); await expect(page.locator('#category')).toBeFocused();
  await page.keyboard.press('Tab'); await expect(page.locator('input[name=identity]').first()).toBeFocused();
  await page.keyboard.press('Tab'); await expect(page.locator('#post-form button')).toBeFocused();
  await page.keyboard.press('Enter'); await expect(page.locator('#stage')).toBeFocused();
  await expect(page.locator('h1')).toHaveText('AIマッチング・通知');
});
