import { test, expect } from '@playwright/test';

test('許可済みダミー課題を手動分析し、人の判断を保存する', async ({ page, request }) => {
  await page.goto('/#profile');
  await page.getByText('許可済み課題の分析（ローカル検証）', { exact: true }).click();
  await page.getByRole('button', { name: '今すぐ分析（ローカル）' }).click();
  await expect(page.getByRole('heading', { name: '共通課題の候補' })).toBeVisible();
  await expect(page.getByText('独立した課題：3 件', { exact: false })).toBeVisible();
  await expect(page.getByText('申告工数：12 時間', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: '候補1の公開要約1を確認', exact: true }).click();
  await expect(page.getByRole('region', { name: '候補1の公開要約1を確認の本文', exact: true })).toContainText('架空の星灯台点検');
  await page.getByLabel('事業仮説の判断').selectOption('investigating');
  await expect(page.getByLabel('事業仮説の判断')).toHaveValue('investigating');
  await page.getByRole('button', { name: '許可と最新の結果を確認' }).click();
  await expect(page.getByLabel('事業仮説の判断')).toHaveValue('investigating');
  const denied = await request.post('/api/v1/business-analysis', { headers: { 'X-Demo-Person': 'demo-person-02', 'Idempotency-Key': 'analysis-unauthorized' }, data: { runId: 'analysis-unauthorized', cutoff: new Date().toISOString(), configVersion: 'business-analysis-demo-v1' } });
  expect(denied.status()).toBe(403);
});

test('分析後に許可確認が失敗すると候補と集計を閉じる', async ({ page }) => {
  await page.goto('/#profile');
  await page.getByText('許可済み課題の分析（ローカル検証）', { exact: true }).click();
  await page.getByRole('button', { name: '今すぐ分析（ローカル）' }).click();
  await expect(page.getByRole('heading', { name: '共通課題の候補' })).toBeVisible();
  await page.route('**/api/v1/business-analysis/*', route => route.fulfill({ status: 503, json: { code: 'DEPENDENCY_UNAVAILABLE' } }));
  await page.getByRole('button', { name: '許可と最新の結果を確認' }).click();
  await expect(page.getByRole('heading', { name: '共通課題の候補' })).not.toBeVisible();
  await expect(page.getByText('独立した課題：3 件', { exact: false })).not.toBeVisible();
  await expect(page.getByRole('alert')).toBeVisible();
});

for (const committed of [true, false]) {
  test(`分析の応答が失われても画面移動と再読み込み後に同じ要求を確認する (${committed ? '保存済み' : '未保存'})`, async ({ page }) => {
    const bodies: string[] = [], keys: string[] = [];
    await page.route('**/api/v1/business-analysis', async route => {
      if (route.request().method() !== 'POST') return route.continue();
      bodies.push(route.request().postData()!);
      keys.push(route.request().headers()['idempotency-key']);
      if (bodies.length === 1) {
        if (committed) { const response = await route.fetch(); expect(response.status()).toBe(200); }
        return route.abort('failed');
      }
      await route.continue();
    });
    await page.goto('/#profile');
    await page.getByText('許可済み課題の分析（ローカル検証）', { exact: true }).click();
    await page.getByRole('button', { name: '今すぐ分析（ローカル）' }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await page.getByRole('link', { name: 'ホーム', exact: true }).first().click();
    await page.goto('/#profile');
    await page.reload();
    await page.getByText('許可済み課題の分析（ローカル検証）', { exact: true }).click();
    await page.getByRole('button', { name: '同じ分析の結果を確認する', exact: true }).click();
    await expect(page.getByRole('heading', { name: '共通課題の候補' })).toBeVisible();
    expect(bodies).toHaveLength(committed ? 1 : 2);
    expect(keys.every(key => key === JSON.parse(bodies[0]).runId)).toBe(true);
    expect(bodies.every(body => body === bodies[0])).toBe(true);
    expect(await page.evaluate(() => Object.keys(sessionStorage).filter(key => key.startsWith('dse-business-analysis-request:')).length)).toBe(0);
  });
}
