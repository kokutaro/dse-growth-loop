// Actual headed Chrome on Mac. Demo/Mock only; use an isolated local store.
import { chromium } from '../../../web/node_modules/@playwright/test/index.mjs';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
const output = resolve('docs/validation-results/20261011-production-contracts/screenshots');
mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: false });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
const page = await context.newPage();
const checks = [], errors = [];
page.on('pageerror', e => errors.push(e.name));
async function capture(mode, route, fullPage = false) {
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.evaluate(() => new Promise(requestAnimationFrame));
  const layout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
  if (layout.scrollWidth > layout.width) throw new Error(`${mode}/${route}: overflow`);
  await page.screenshot({ path: `${output}/${mode}-${route}.png`, fullPage });
  checks.push({ mode, route, ...layout });
}
async function actor(person) {
  await page.getByLabel('ローカル検証の役割').selectOption(person);
  await page.getByRole('link', { name: 'ルームを開く', exact: true }).first().click();
  await page.getByRole('region', { name: '実名開示の相互同意' }).waitFor();
}
async function grant() {
  await page.getByLabel('この活動の参加者に本人プロフィールの表示名を開示することに同意').check();
  await page.getByRole('button', { name: '実名開示への同意を保存', exact: true }).click();
}
try {
  await page.goto('http://127.0.0.1:5173/');
  await page.getByRole('heading', { name: 'ホーム', exact: true }).waitFor();
  await page.getByRole('link', { name: '課題・相談を投稿する', exact: true }).click();
  await page.getByLabel('課題のタイトル').fill('調査記録の引き継ぎ');
  await page.getByLabel('困っていること・相談したいこと').fill('再現条件と試したことを一緒に整理したいです。');
  await page.getByRole('button', { name: '匿名名で投稿する（ローカル）' }).click();
  await page.getByLabel('メッセージ', { exact: true }).waitFor();
  await page.getByText('匿名で協力を依頼する', { exact: true }).click();
  await page.getByRole('button', { name: '匿名の候補を確認' }).click();
  await page.getByRole('button', { name: /に参加依頼$/ }).first().click();
  await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
  await page.getByRole('link', { name: /^参加依頼/ }).click();
  await page.getByRole('link', { name: '内容を確認' }).first().click();
  await page.getByRole('button', { name: '参加する', exact: true }).click();
  await page.getByLabel('メッセージ', { exact: true }).waitFor();
  await page.getByLabel('メッセージ', { exact: true }).fill('確認した手順と、次に試すことを整理しました。');
  await page.getByRole('button', { name: '送信する（ローカル）' }).click();
  await page.getByRole('list', { name: '保存済みの会話' }).getByText('確認した手順と、次に試すことを整理しました。').waitFor();
  await actor('demo-person-01');
  const roomUrl = page.url();
  for (const [mode, viewport] of [['pc', { width: 1440, height: 1000 }], ['mobile', { width: 390, height: 844 }]]) {
    await page.setViewportSize(viewport);
    for (const route of ['home', 'tasks', 'invitations', 'rooms', 'knowledge', 'profile']) {
      await page.goto(`http://127.0.0.1:5173/#${route}`);
      await page.locator('#stage h1').waitFor();
      // Wait for real API panels/empty state, not only the static heading.
      await page.waitForTimeout(500);
      await capture(mode, route);
    }
    await page.getByText('許可済み課題の分析（ローカル検証）', { exact: true }).click();
    await page.getByRole('button', { name: '今すぐ分析（ローカル）' }).click();
    await page.getByRole('heading', { name: '共通課題の候補', exact: true }).waitFor();
    await page.getByRole('button', { name: '候補1の公開要約1を確認', exact: true }).click();
    await page.getByRole('region', { name: '候補1の公開要約1を確認の本文', exact: true }).waitFor();
    await capture(mode, 'analysis-evidence', true);
    await page.goto(roomUrl);
    await page.getByRole('region', { name: '実名開示の相互同意' }).waitFor();
    if (await page.getByRole('list', { name: 'この活動で共有した表示名' }).count()) throw new Error('Names visible before mutual grants');
    await capture(mode, 'room-anonymous', true);
    await grant();
    await page.getByText('相手の確認を待っています', { exact: false }).waitFor();
    await capture(mode, 'identity-one-consent', true);
    await actor('demo-person-02'); await grant();
    await page.getByRole('list', { name: 'この活動で共有した表示名' }).waitFor();
    await capture(mode, 'identity-shared', true);
    await page.getByRole('button', { name: '実名開示への同意を撤回', exact: true }).click();
    await page.getByText('現在は匿名', { exact: false }).waitFor();
    if (await page.getByRole('list', { name: 'この活動で共有した表示名' }).count()) throw new Error('Names visible after withdrawal');
    await capture(mode, 'identity-revoked', true);
    await actor('demo-person-01');
  }
  if (errors.length) throw new Error(`Browser runtime errors: ${errors.join(',')}`);
  writeFileSync(resolve(output, '../browser-review.json'), JSON.stringify({ capturedAt: new Date().toISOString(), browser: 'Google Chrome', version: browser.version(), headed: true, userAgent: await page.evaluate(() => navigator.userAgent), checks, pageErrors: errors, fixture: 'isolated Demo/Mock', externalModelCalls: 0, azureOperations: 0 }, null, 2) + '\n');
} finally { await context.close(); await browser.close(); }
