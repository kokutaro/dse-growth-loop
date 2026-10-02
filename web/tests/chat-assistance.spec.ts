import { test, expect, type Page } from "@playwright/test";

async function create(page: Page, title: string) {
  await page.goto("/");
  await page.getByLabel("課題名", { exact: true }).fill(title);
  await page.getByLabel("困っていること").fill("架空の相談です");
  await page.getByRole("button", { name: "課題を作成", exact: true }).click();
  await expect(page.getByLabel("メッセージ", { exact: true })).toBeVisible();
}
async function send(page: Page, body: string) {
  await page.getByLabel("メッセージ", { exact: true }).fill(body);
  await page.getByRole("button", { name: "送信", exact: true }).click();
}

test("自動事例は一度だけ保存され、出典・フィードバック・再接続・停止を操作できる", async ({ page }) => {
  const title = `自動事例 ${Date.now()}`;
  await create(page, title);
  await page.getByLabel("自動支援 ON").click();
  await expect(page.getByLabel("自動支援 ON")).toBeChecked();
  await send(page, "引き継ぎの改善で困っています。事例を教えてください。");
  const history = page.getByRole("list", { name: "保存済みの会話" });
  await expect(history.getByText("AIによる参考事例", { exact: false })).toHaveCount(1, { timeout: 25000 });
  await history.getByRole("button", { name: "事例本文と出典を確認" }).click();
  await expect(page.getByRole("region", { name: "引用した事例と出典" })).toContainText("有効版 1");
  await history.getByRole("button", { name: "役立った", exact: true }).click();
  await expect(history.getByRole("status")).toContainText("役立った");
  await history.getByRole("button", { name: "不要", exact: true }).click();
  await expect(history.getByRole("status")).toContainText("不要");
  await page.getByRole("button", { name: "相談画面を閉じる", exact: true }).click();
  await page.getByRole("button", { name: title, exact: true }).click();
  await expect(page.getByLabel("メッセージ", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "会話を更新", exact: true }).click();
  await expect(history.getByText("AIによる参考事例", { exact: false })).toHaveCount(1);
  await page.getByLabel("自動支援 ON").click();
  await expect(page.getByLabel("自動支援 ON")).not.toBeChecked();
  await send(page, "別の対策で困っています。教えてください。");
  await expect(history).toContainText("別の対策で困っています。");
  await expect(history.getByText("AIによる参考事例", { exact: false })).toHaveCount(1);
});
