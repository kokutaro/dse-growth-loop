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
