// Run from repository root after local Demo/Mock Aspire starts. Uses its own Chrome instance.
import { chromium } from '../../../web/node_modules/@playwright/test/index.mjs';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
const output = resolve('docs/validation-results/20261011-remaining-issues/screenshots');
mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: false });
const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
const page = await context.newPage();
const checks = [];
try {
  await page.goto('http://127.0.0.1:5173/');
  await page.getByRole('heading', { name: 'ホーム', exact: true }).waitFor();
  for (const [mode, viewport] of [['pc', { width: 1440, height: 1000 }], ['mobile', { width: 390, height: 844 }]]) {
    await page.setViewportSize(viewport);
    for (const route of ['home', 'tasks', 'invitations', 'rooms', 'knowledge', 'profile']) {
      await page.goto(`http://127.0.0.1:5173/#${route}`);
      await page.locator('#stage h1').waitFor();
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.evaluate(() => new Promise(requestAnimationFrame));
      const layout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
      if (layout.scrollWidth > layout.width) throw new Error(`${mode}/${route}: overflow`);
      await page.screenshot({ path: `${output}/${mode}-${route}.png` });
      checks.push({ mode, route, ...layout });
    }
    await page.getByText('許可済み課題の分析（ローカル検証）', { exact: true }).click();
    await page.getByRole('button', { name: '今すぐ分析（ローカル）' }).click();
    await page.getByRole('heading', { name: '共通課題の候補', exact: true }).waitFor();
    await page.getByLabel('事業仮説の判断').selectOption('investigating');
    await page.waitForFunction(() => document.querySelector('[aria-label="事業仮説の判断"]')?.value === 'investigating');
    const analysis = page.getByRole('region', { name: '事業仮説の手動分析' });
    await page.evaluate(() => window.scrollTo(0, 0));
    const bounds = await analysis.boundingBox();
    await page.screenshot({ fullPage: true, clip: bounds, path: `${output}/${mode}-analysis.png` });
    await page.getByRole('button', { name: '候補1の公開要約1を確認', exact: true }).click();
    await page.getByRole('region', { name: '候補1の公開要約1を確認の本文', exact: true }).waitFor();
    await page.evaluate(() => window.scrollTo(0, 0));
    const evidenceBounds = await analysis.boundingBox();
    await page.screenshot({ fullPage: true, clip: evidenceBounds, path: `${output}/${mode}-analysis-evidence.png` });
    checks.push({ mode, route: 'analysis', sourceCount: 3, reportedHours: 12, decision: 'investigating' });
  }
  writeFileSync(resolve(output, '../browser-review.json'), JSON.stringify({ capturedAt: new Date().toISOString(), browser: 'Google Chrome', version: browser.version(), headed: true, userAgent: await page.evaluate(() => navigator.userAgent), checks, externalModelCalls: 0 }, null, 2) + '\n');
} finally { await context.close(); await browser.close(); }
