import { test, expect } from "@playwright/test";
import path from "node:path";

for (const extension of ["docx", "xlsx"]) {
  test(`${extension}をアップロードして機械抽出結果と出典を確認できる`, async ({ page }) => {
    await page.goto("/validation.html");
    await page.getByLabel("文書名", { exact: true }).fill(`架空資料 ${extension}`);
    await page.getByLabel("原本ファイル").setInputFiles(path.resolve(`../evaluation/documents/tuning.${extension}`));
    await page.getByRole("button", { name: "アップロード", exact: true }).click();
    await expect(page.getByRole("status")).toContainText("機械抽出済み", { timeout: 20000 });
    await expect(page.getByRole("status")).toContainText("AI構造化：");
    await expect(page.getByRole("heading", { name: "出典プレビュー", exact: true })).toBeVisible();
    await expect(page.locator(".evidence-text").first()).not.toBeEmpty();
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "原本を取得", exact: true }).click();
    expect((await download).suggestedFilename()).toMatch(new RegExp(`\\.${extension}$`));
    await page.screenshot({ path: `test-results/ingestion-${extension}.png`, fullPage: true });
  });
}
test("偽装Officeファイルは拒否して入力を保持する", async ({ page }) => {
  await page.goto("/validation.html"); await page.getByLabel("文書名", { exact: true }).fill("壊れたダミー");
  await page.getByLabel("原本ファイル").setInputFiles({ name: "bad.docx", mimeType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document", buffer: Buffer.from("invalid") });
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("INVALID_PACKAGE");
  await expect(page.getByLabel("文書名", { exact: true })).toHaveValue("壊れたダミー");
});

test("処理待ちと失敗を区別して手動再実行できる（状態API fixture）", async ({ page }) => {
  let state = "queued";
  await page.route("**/api/v1/documents", route => route.fulfill({ status: 202, json: { documentId: "fixture-doc", jobId: "fixture-job", version: 1 } }));
  await page.route("**/api/v1/documents/fixture-doc", route => route.fulfill({ json: { warnings: [] } }));
  await page.route("**/api/v1/jobs/fixture-job", route => route.fulfill({ headers: { ETag: '"fixture"' }, json: { status: state, lastErrorCode: state === "failed" ? "RETRY_EXHAUSTED" : null } }));
  await page.route("**/api/v1/jobs/fixture-job/retry", async route => { state = "queued"; await route.fulfill({ status: 202, json: { jobId: "fixture-job" } }); });
  await page.goto("/validation.html"); await page.getByLabel("文書名", { exact: true }).fill("状態試験");
  await page.getByLabel("原本ファイル").setInputFiles(path.resolve("../evaluation/documents/tuning.docx"));
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("処理待ち");
  state = "failed"; await page.getByRole("button", { name: "進捗を更新", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("失敗");
  await expect(page.getByText("詳細：RETRY_EXHAUSTED")).toBeVisible();
  await page.getByRole("button", { name: "再実行・再通知", exact: true }).click();
  await expect(page.getByRole("status")).toContainText("処理待ち");
});

test("Azureの取込再開・アップロード・再実行はサーバーの本人IDで分離する（認証・API fixture）", async ({ page }) => {
  let person = "account-a";
  const requestedJobs: string[] = [];
  const storageKey = (owner: string) => `dse-upload-job:v2:Azure:fixture-tenant:${owner}`;
  // No Entra or paid service is contacted. Exercise the actual workspace, whose
  // client-side person remains demo-person-01 even when the Azure account changes.
  await page.route("**/src/auth.ts*", route => route.fulfill({ contentType: "application/javascript", body: 'export async function initializeAuth() {} export async function getToken() { return "fixture-token"; } export async function endAuth() {}' }));
  await page.route("**/api/config", route => route.fulfill({ json: { mode: "Azure", tenantId: "fixture-tenant", clientId: "fixture", scope: "fixture" } }));
  await page.route("**/api/v1/workspace/**", route => route.fulfill({ json: { available: false, items: [] } }));
  await page.route("**/api/v1/upload-context", route => route.fulfill({ json: { personId: person, departmentId: "fixture", scopeIds: [], maxBytes: 1000000 } }));
  await page.route("**/api/v1/documents/**", route => route.fulfill({ json: { warnings: [] } }));
  await page.route("**/api/v1/documents", route => route.fulfill({ status: 202, json: { documentId: `doc-${person}-new`, jobId: `job-${person}-new`, version: 1 } }));
  await page.route("**/api/v1/jobs/**", async route => {
    const id = new URL(route.request().url()).pathname.split('/')[4]; requestedJobs.push(id);
    if (route.request().method() === "POST") await route.fulfill({ status: 202, json: { jobId: `job-${person}-retry` } });
    else await route.fulfill({ headers: { ETag: '"fixture"' }, json: { status: "failed", lastErrorCode: "FIXTURE", resultId: null } });
  });
  await page.goto("/#profile");
  await page.evaluate(({ a, b }) => {
    sessionStorage.setItem("dse-upload-job", JSON.stringify({ documentId: "legacy-doc", jobId: "legacy-job", version: 1 }));
    sessionStorage.setItem("dse-upload-job:fixture-tenant:demo-person-01", JSON.stringify({ documentId: "shared-doc", jobId: "shared-job", version: 1 }));
    sessionStorage.setItem(a, JSON.stringify({ documentId: "doc-account-a", jobId: "job-account-a", version: 1 }));
    sessionStorage.setItem(b, JSON.stringify({ documentId: "doc-account-b", jobId: "job-account-b", version: 1 }));
  }, { a: storageKey("account-a"), b: storageKey("account-b") });
  await page.getByRole("button", { name: "登録先を確認する", exact: true }).click();
  await expect(page.locator(".documents")).toContainText("文書ID：doc-account-a ／");
  await page.getByLabel("文書名", { exact: true }).fill("アカウントAのダミー");
  await page.getByLabel("原本ファイル").setInputFiles(path.resolve("../evaluation/documents/tuning.docx"));
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  await expect(page.locator(".documents")).toContainText("文書ID：doc-account-a-new ／");
  await page.getByRole("button", { name: "再実行・再通知", exact: true }).click();
  await expect.poll(() => page.evaluate(key => JSON.parse(sessionStorage.getItem(key)!).jobId, storageKey("account-a"))).toBe("job-account-a-retry");
  person = "account-b"; requestedJobs.length = 0;
  await page.reload();
  await page.getByRole("button", { name: "登録先を確認する", exact: true }).click();
  await expect(page.locator(".documents")).toContainText("文書ID：doc-account-b ／");
  await expect(page.locator(".documents")).not.toContainText("doc-account-a");
  expect(requestedJobs).toEqual(["job-account-b"]);
  expect(await page.evaluate(() => sessionStorage.getItem("dse-upload-job:fixture-tenant:demo-person-01"))).toBeNull();
  expect(await page.evaluate(key => JSON.parse(sessionStorage.getItem(key)!).jobId, storageKey("account-a"))).toBe("job-account-a-retry");
});
