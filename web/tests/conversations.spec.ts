import { test, expect, type Page } from "@playwright/test";

async function create(page: Page, title: string) {
  await page.goto("/validation.html");
  await page.getByLabel("課題名", { exact: true }).fill(title);
  await page.getByLabel("困っていること").fill("架空の相談です");
  await page.getByRole("button", { name: "課題を作成", exact: true }).click();
  await expect(page.getByLabel("メッセージ", { exact: true })).toBeVisible();
}
async function send(page: Page, body: string) {
  await page.getByLabel("メッセージ", { exact: true }).fill(body);
  await page.getByRole("button", { name: "送信", exact: true }).click();
}
test("履歴取得中に相談を依頼しても参加者一覧を更新する", async ({ page }) => {
  await page.route("**/api/hubs/**", route => route.abort());
  let release!: () => void;
  const blocked = new Promise<void>(resolve => { release = resolve; });
  let started!: () => void;
  const historyStarted = new Promise<void>(resolve => { started = resolve; });
  await page.route("**/api/v1/sessions/*/messages?**", async route => {
    const response = await route.fetch();
    started();
    await blocked;
    await route.fulfill({ response });
  });
  try {
    await create(page, `取得中の依頼 ${Date.now()}`);
    await historyStarted;
    await page.getByLabel("相談する人物ID").fill("demo-person-02");
    const saved = page.waitForResponse(response => response.url().endsWith("/participations") && response.request().method() === "POST");
    await page.getByRole("button", { name: "相談を依頼" }).click();
    expect((await saved).ok()).toBeTruthy();
    await page.evaluate(() => new Promise(requestAnimationFrame));
    release();
    await expect(page.getByText("demo-person-02：依頼中", { exact: true })).toBeVisible();
  } finally {
    release();
    // Finish delayed handlers before Playwright disposes their fetched responses.
    await page.unrouteAll({ behavior: 'wait' });
  }
});
test("二人で承諾・会話・退会し、AI停止中も保存できる", async ({ page, browser }) => {
  // Two independent 10s database replays, creation/acceptance and leaving share this test.
  // Keep each remote-message assertion at 20s; allow the complete workflow to finish.
  test.setTimeout(60000);
  const title = `二人の相談 ${Date.now()}`;
  await create(page, title);
  await page.getByLabel("相談する人物ID").fill("demo-person-02");
  await page.getByRole("button", { name: "相談を依頼" }).click();
  await expect(page.getByText("demo-person-02：依頼中", { exact: true })).toBeVisible();
  const context = await browser.newContext(); const guest = await context.newPage();
  await guest.goto("/validation.html"); await guest.getByLabel("ローカルテスト主体").selectOption("demo-person-02");
  await guest.getByRole("button", { name: title, exact: true }).click();
  await expect(guest.getByLabel("メッセージ", { exact: true })).not.toBeVisible();
  await guest.getByRole("button", { name: "参加を承諾" }).click();
  await expect(guest.getByLabel("メッセージ", { exact: true })).toBeVisible();
  await expect(guest.getByLabel("自動支援 ON")).not.toBeChecked();
  await send(guest, "承諾した人からの架空メッセージ");
  await expect(guest.getByRole("list", { name: "保存済みの会話" })).toContainText("承諾した人からの架空メッセージ");
  await expect(page.getByRole("list", { name: "保存済みの会話" })).toContainText("承諾した人からの架空メッセージ", { timeout: 20000 });
  await send(page, "AIなしでも返信できます");
  await expect(guest.getByRole("list", { name: "保存済みの会話" })).toContainText("AIなしでも返信できます", { timeout: 20000 });
  await guest.getByRole("button", { name: "退会する" }).click();
  await expect(guest.getByLabel("メッセージ", { exact: true })).not.toBeVisible();
  await expect(guest.getByRole("list", { name: "保存済みの会話" })).not.toBeVisible();
  await page.screenshot({ path: "test-results/conversation.png", fullPage: true });
  await context.close();
});

test("DB失敗と応答消失は未保存表示になり、同じキーで再送して一件だけ保存する", async ({ page }) => {
  await create(page, `再送 ${Date.now()}`);
  const keys: string[] = []; let count = 0;
  await page.route("**/api/v1/sessions/*/messages", async route => {
    if (route.request().method() !== "POST") return route.continue();
    keys.push(route.request().headers()["idempotency-key"]); count++;
    if (count === 1) return route.fulfill({ status: 503, json: { code: "SAVE_FAILED" } });
    if (count === 2) { await route.fetch(); return route.abort("failed"); }
    return route.continue();
  });
  // Delay history recovery so the response-loss state remains observable until retry.
  await page.route("**/api/v1/sessions/*/messages?**", route => count > 0 && count < 3 ? route.abort() : route.continue());
  await send(page, "一度だけ保存する架空メッセージ");
  await expect(page.getByRole("list", { name: "未保存のメッセージ" })).toContainText("未保存（結果未確認）");
  await expect(page.getByRole("list", { name: "保存済みの会話" })).not.toContainText("一度だけ保存する架空メッセージ");
  await page.getByRole("button", { name: "同じIDで再送" }).click();
  await expect(page.getByRole("button", { name: "同じIDで再送" })).toBeVisible();
  await page.getByRole("button", { name: "同じIDで再送" }).click();
  await expect(page.getByRole("list", { name: "保存済みの会話" }).locator("li")).toHaveCount(1);
  expect(keys).toHaveLength(3); expect(new Set(keys).size).toBe(1);
});

test("通知なしでも履歴をページング回復し、画面の開き直しで重複しない", async ({ page }) => {
  const title = `再接続 ${Date.now()}`; await create(page, title);
  await page.route("**/api/hubs/**", route => route.abort());
  await send(page, "切断中にHTTPで保存");
  await expect(page.getByRole("list", { name: "保存済みの会話" })).toContainText("切断中にHTTPで保存");
  await page.getByRole("button", { name: "相談画面を閉じる" }).click();
  await page.unroute("**/api/hubs/**");
  await page.getByRole("button", { name: title, exact: true }).click();
  await expect(page.getByRole("list", { name: "保存済みの会話" }).locator("li")).toHaveCount(1);
  await expect(page.getByTestId("chat-connection")).toContainText("接続済み", { timeout: 15000 });
});

test("表示済みAI引用は新着なしでも履歴を再照合して撤回表示へ更新する（応答fixture）", async ({ page }) => {
  let withdrawn = false;
  await page.route("**/api/v1/sessions/*/messages?**", route => {
    const after = new URL(route.request().url()).searchParams.get("afterSequence");
    return route.fulfill({ json: { items: after === "0" ? [{ id: "fixture-ai-message", sessionId: "fixture", ownerId: "ai", sequence: 1,
      authorType: "ai", body: withdrawn ? "参照知識が撤回されたため表示できません。" : "公開済み知識の引用", createdAt: "2026-10-02T00:00:00Z" }] : [], lastSequence: 1, continuationToken: null } });
  });
  await create(page, `引用再照合 ${Date.now()}`);
  const history = page.getByRole("list", { name: "保存済みの会話" });
  await expect(history).toContainText("公開済み知識の引用");
  withdrawn = true;
  await page.getByRole("button", { name: "会話を更新", exact: true }).click();
  await expect(history).toContainText("参照知識が撤回されたため表示できません。");
  await expect(history).not.toContainText("公開済み知識の引用");
});
