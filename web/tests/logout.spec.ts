import { test, expect } from "@playwright/test";

test("Azure logout removes protected panels even when the provider is unavailable", async ({ page }) => {
  await page.route("**/api/config", route => route.fulfill({ json: {
    mode: "Azure", tenantId: "11111111-1111-1111-1111-111111111111",
    clientId: "22222222-2222-2222-2222-222222222222", scope: "api://dummy/access_as_user",
  } }));
  await page.route("https://login.microsoftonline.com/**", route => route.abort());
  await page.goto("/");
  await page.getByRole("button", { name: "ログアウト", exact: true }).click();
  await expect(page.getByRole("heading", { name: "接続を終了しました" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "課題と相談チャット" })).not.toBeVisible();
  await expect(page.getByRole("button", { name: "相談候補を探す" })).not.toBeVisible();
  await expect(page.getByRole("button", { name: "ログインし直す" })).toBeVisible();
});
