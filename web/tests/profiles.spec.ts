import { test, expect } from "@playwright/test";
import path from "node:path";

test("全セル上限を超えるXLSXは原文を保持して理由付き保留となり部分案件を公開しない", async ({ page, request }) => {
  await page.goto("/validation.html");
  await page.getByLabel("文書名", { exact: true }).fill("全テキスト上限の架空プロフィール");
  await page.getByLabel("原本ファイル").setInputFiles(path.resolve("tests/fixtures/internal-resume.xlsx"));
  const accepted = page.waitForResponse(r => r.url().endsWith("/api/v1/documents") && r.request().method() === "POST");
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  const { documentId } = await (await accepted).json();
  await expect(page.locator(".documents")).toContainText("WORKBOOK_AI_EVIDENCE_LIMIT", { timeout: 30000 });
  await expect(page.locator(".documents")).toContainText("説明タブ専用スキル");
  await expect(page.locator(".documents")).toContainText("Postgre SQL 16");
  const pipeline = await (await request.get(`/api/v1/documents/${documentId}/pipeline`)).json();
  expect(pipeline.workbookCapture.sheets).toEqual(["説明専用", "02. スキル記入シート(例つき)", "説明補足"]);
  expect(pipeline.workbookCapture.spanCount).toBeGreaterThan(100);
  expect(pipeline.job.status).toBe("needs_review");
  expect(pipeline.job.lastErrorCode).toBe("WORKBOOK_AI_EVIDENCE_LIMIT");
  await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
  await expect(page.getByTestId(documentId)).toHaveCount(0);
});

test("副業務を原文付きの検索断片として追加し経験月数を重複加算しない", async ({ page }) => {
  await page.goto("/validation.html");
  await page.getByLabel("文書名", { exact: true }).fill("副業務を含む架空プロフィール");
  await page.getByLabel("原本ファイル").setInputFiles(path.resolve("tests/fixtures/secondary-work.docx"));
  const accepted = page.waitForResponse(r => r.url().endsWith("/api/v1/documents") && r.request().method() === "POST");
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  const { documentId } = await (await accepted).json();
  await expect.poll(async () => {
    await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
    return await page.getByTestId(documentId).count();
  }, { timeout: 30000 }).toBe(1);
  await expect(page.getByTestId(documentId).getByLabel("経験の説明")).toHaveCount(1);
  await expect(page.locator(".profiles")).toContainText("日付から算出した経験月数（重複除外）：48");
  await page.getByLabel("検索語", { exact: true }).fill("副業務として次の対応を共同実施した");
  const searchResponse = page.waitForResponse(r => r.url().includes("/api/") && r.request().method() === "POST" && r.url().includes("search"));
  await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
  const response = await (await searchResponse).json();
  const candidates = (response.primary ?? response).candidates;
  const secondary = candidates.flatMap((candidate: { units: { summary: string; sourceRefs: { text: string }[] }[] }) => candidate.units)
    .find((unit: { summary: string }) => unit.summary.startsWith("副業務として"));
  expect(secondary).toBeDefined();
  expect(secondary.summary).toContain("共同実施した");
  expect(secondary.sourceRefs.some((source: { text: string }) => source.text.includes(secondary.summary))).toBe(true);
  await expect(page.getByRole("region", { name: "相談候補と参考事例" })).toContainText("共同実施した");
});

for (const extension of ["docx", "xlsx"]) {
  test(`${extension}の原文・本人訂正を保ち、mockの案件外文脈を検索経験へ昇格しない`, async ({ page }) => {
    await page.goto("/validation.html");
    await page.getByLabel("文書名", { exact: true }).fill(`プロフィール統合 ${extension}`);
    await page.getByLabel("原本ファイル").setInputFiles(path.resolve(`tests/fixtures/profile.${extension}`));
    const acceptedResponse = page.waitForResponse(r => r.url().endsWith("/api/v1/documents") && r.request().method() === "POST");
    await page.getByRole("button", { name: "アップロード", exact: true }).click();
    const { documentId } = await (await acceptedResponse).json();
    await expect(page.locator(".documents")).toContainText("検索投影済み", { timeout: 30000 });
    await expect(page.locator(".documents")).toContainText("fixture（fake AI）");
    await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
    await expect(page.getByTestId(documentId).getByLabel("経験の説明").first()).toBeVisible();
    await expect(page.locator(".profiles")).toContainText(`日付から算出した経験月数（重複除外）：${extension === "xlsx" ? "不明" : "84"}`);
    await page.getByTestId(documentId).getByLabel("経験の説明").first().fill(`本人訂正${extension} ソフトウェア`);
    await page.getByRole("button", { name: "訂正を保存", exact: true }).click();
    await expect(page.locator(".profiles")).toContainText("保存しました");
    await expect.poll(async () => {
      await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
      return await page.locator(".profiles").innerText();
    }).toContain("有効版：");
    await page.getByLabel("検索語", { exact: true }).fill(`本人訂正${extension}`);
    await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
    if (extension === "docx") await expect(page.getByRole("region", { name: "相談候補と参考事例" })).toContainText(`本人訂正${extension}`, { timeout: 15000 });
    else {
      // The canned workbook mock selects context; editing text does not turn it into a project.
      await expect(page.getByRole("region", { name: "相談候補と参考事例" })).not.toContainText(`本人訂正${extension}`);
      await expect(page.locator(".profiles")).toContainText("案件外の原文文脈（自己PR・記入例・補足等）。案件経験や具体技能の検索には公開しません。");
    }
    await page.screenshot({ path: `test-results/profile-${extension}.png`, fullPage: true });
  });
}

test("公開撤回を検索に反映し、明示再公開で復旧する", async ({ page }) => {
  await page.goto("/validation.html");
  await page.getByLabel("文書名", { exact: true }).fill("公開撤回用の架空プロフィール");
  await page.getByLabel("原本ファイル").setInputFiles(path.resolve("tests/fixtures/profile.docx"));
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  await expect(page.locator(".documents")).toContainText("検索投影済み", { timeout: 20000 });
  await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
  const name = await page.getByLabel("表示名", { exact: true }).inputValue();
  await page.getByRole("button", { name: "公開を撤回", exact: true }).click();
  await expect(page.locator(".profiles")).toContainText("公開を撤回しました");
  await page.getByLabel("検索語", { exact: true }).fill("ソフトウェア");
  await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
  await expect(page.getByRole("region", { name: "相談候補と参考事例" }).getByRole("heading", { name, exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "再公開する", exact: true }).click();
  await expect(page.locator(".profiles")).toContainText("保存しました");
  await expect.poll(async () => {
    await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
    return await page.locator(".profiles").innerText();
  }).toContain("有効版：");
});

test("再公開のACCESS_BUSYだけを同じ冪等キーで再試行し、412は本人確認に戻す", async ({ page }) => {
  let state = "withdrawn", attempts = 0, conflict = false;
  const writes: { key: string | undefined; etag: string | undefined; body: unknown }[] = [];
  await page.route("**/api/v1/upload-context", route => route.fulfill({ json: { personId: "fixture-person", departmentId: "fixture", scopeIds: [], maxBytes: 1000000 } }));
  await page.route("**/api/v1/people/fixture-person/experiences*", route => route.fulfill({ json: { items: [], continuationToken: null } }));
  await page.route("**/api/v1/people/fixture-person", async route => {
    const request = route.request();
    if (request.method() === "PATCH") {
      attempts++;
      writes.push({ key: request.headers()['idempotency-key'], etag: request.headers()['if-match'], body: request.postDataJSON() });
      if (conflict) { await route.fulfill({ status: 412, json: { code: "ETAG_MISMATCH" } }); return; }
      if (attempts <= 2) { await route.fulfill({ status: 503, json: { code: "ACCESS_BUSY", retryable: true } }); return; }
      state = "published";
    }
    await route.fulfill({ headers: { ETag: '"fixture-etag"' }, json: { id: "fixture-person", displayName: "ダミー", available: true, publicationState: state, sourceVersion: 3, activeVersion: state === "published" ? 3 : null, confirmationState: "self_corrected" } });
  });
  await page.goto("/validation.html");
  await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
  await expect(page.locator(".profiles")).toContainText("撤回済み");
  await page.getByRole("button", { name: "再公開する", exact: true }).click();
  await expect(page.locator(".profiles")).toContainText("有効版：3");
  expect(attempts).toBe(3);
  expect(writes[0].key).toBeTruthy(); expect(writes[0].etag).toBeTruthy();
  expect(writes[1]).toEqual(writes[0]); expect(writes[2]).toEqual(writes[0]);
  conflict = true;
  await page.getByRole("button", { name: "公開を撤回", exact: true }).click();
  await expect(page.locator(".profiles").getByRole("alert")).toContainText("別の更新があります");
  expect(attempts).toBe(4);
  await expect(page.locator(".profiles")).toContainText("公開状態：公開");
});

for (const boundary of ['logout', 'navigation', 'account'] as const) {
  test(`プロフィールPATCHのACCESS_BUSY再送を${boundary}後に中止する`, async ({ page }) => {
    let attempts = 0;
    if (boundary === 'logout') {
      // Keep the real auth module so its synchronous session abort is exercised.
      // Only MSAL is a fixture; no Entra request or real token is used.
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
    await page.route('**/api/v1/upload-context', route => route.fulfill({ json: { personId: 'fixture-person', departmentId: 'fixture', scopeIds: [], maxBytes: 1000000 } }));
    await page.route('**/api/v1/people/fixture-person/experiences*', route => route.fulfill({ json: { items: [], continuationToken: null } }));
    await page.route('**/api/v1/people/fixture-person', async route => {
      if (route.request().method() === 'PATCH') {
        attempts++;
        if (boundary === 'logout') expect(route.request().headers().authorization).toBe('Bearer fixture-token');
        return route.fulfill({ status: 503, json: { code: 'ACCESS_BUSY', retryable: true } });
      }
      return route.fulfill({ headers: { ETag: '"fixture-etag"' }, json: { id: 'fixture-person', displayName: 'ダミー', available: true, publicationState: 'withdrawn', sourceVersion: 3, activeVersion: null, confirmationState: 'self_corrected' } });
    });
    await page.goto('/#profile');
    await page.getByRole('button', { name: 'プロフィールを更新', exact: true }).click();
    await expect(page.locator('.profiles')).toContainText('撤回済み');
    await page.clock.install({ time: new Date('2026-10-11T12:00:00Z') });
    await page.clock.pauseAt(new Date('2026-10-11T12:00:01Z'));
    const first = page.waitForResponse(r => r.request().method() === 'PATCH' && r.url().endsWith('/api/v1/people/fixture-person'));
    await page.getByRole('button', { name: '再公開する', exact: true }).click();
    expect((await first).status()).toBe(503);
    if (boundary === 'logout') {
      await page.getByRole('button', { name: 'ログアウト', exact: true }).click();
      await expect(page.getByRole('heading', { name: '接続を終了しました' })).toBeVisible();
    } else if (boundary === 'navigation') {
      await page.getByRole('link', { name: 'ホーム', exact: true }).click();
      await expect(page.locator('.profiles')).toHaveCount(0);
    } else {
      await page.getByLabel('ローカル検証の役割').selectOption('demo-person-02');
      await expect(page.locator('.profiles')).toHaveCount(0);
      await page.getByRole('link', { name: 'マイページを開く', exact: true }).click();
      await expect(page.getByRole('button', { name: 'プロフィールを更新', exact: true })).toBeEnabled();
      await expect(page.getByRole('button', { name: '再公開する', exact: true })).toHaveCount(0);
    }
    // Advance through the entire bounded backoff without wall-clock timing races.
    await page.clock.fastForward(10000);
    await page.evaluate(() => Promise.resolve());
    expect(attempts).toBe(1);
  });
}


test("月精度と継続中・期間確認警告を日精度に見せず表示する", async ({ page }) => {
  await page.route('**/api/v1/upload-context', route => route.fulfill({ json: { personId: 'period-fixture', departmentId: 'fixture', scopeIds: [] } }));
  await page.route('**/api/v1/people/period-fixture', route => route.fulfill({ headers: { ETag: '"period-etag"' }, json: { id: 'period-fixture', displayName: '架空期間', available: true, publicationState: 'published', sourceVersion: 1, activeVersion: 1, confirmationState: 'ai_unconfirmed' } }));
  await page.route('**/api/v1/people/period-fixture/experiences*', route => route.fulfill({ json: { items: [{ id: 'period-1', documentId: 'period-doc', description: '原文を保持', period: { start: '2024-01', end: null, precision: 'month', months: null }, periodEndState: 'ongoing', periodWarnings: ['WORKBOOK_PRECISION_FROM_SOURCE'], confirmationState: 'ai_unconfirmed', correctionState: 'none', evidenceRefs: [{ id: 'date-source' }] }], continuationToken: null } }));
  await page.goto('/#profile');
  await page.getByRole('button', { name: 'プロフィールを更新', exact: true }).click();
  const row=page.getByTestId('period-doc');
  await expect(row.getByLabel('開始月', {exact: true})).toHaveAttribute('type','month');
  await expect(row.getByLabel('終了月（この月を含む）', {exact: true})).toHaveValue('');
  await expect(row).toContainText('継続中（終了日・月数は未確定）');
  await expect(row).toContainText('期間は原文確認が必要：WORKBOOK_PRECISION_FROM_SOURCE');
  await expect(row).toContainText('月数：不明');
});
