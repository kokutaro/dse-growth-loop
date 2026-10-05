import { test, expect } from "@playwright/test";
import path from "node:path";

const evidence = path.resolve("../docs/design-preview");
for (const width of [1440, 390, 320]) {
  test(`社員向け導線・キーボード・横幅 ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    const context = page.waitForResponse(r => r.url().endsWith("/api/v1/upload-context") && r.ok());
    await page.goto("/");
    await context;
    await expect(page.getByRole("button", { name: "相談候補を探す", exact: true })).toBeEnabled();
    await page.keyboard.press("Tab");
    await expect(page.getByRole("link", { name: "本文へ移動" })).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(page.locator("#main-content")).toBeFocused();
    await page.getByRole("link", { name: "課題を投稿する" }).click();
    await expect(page.getByLabel("課題名", { exact: true })).toBeFocused();
    await page.getByRole("link", { name: "人・事例を探す", exact: true }).click();
    await expect(page.locator(".search-options")).not.toHaveAttribute("open", "");
    await expect(page.locator(".demo-tools")).not.toHaveAttribute("open", "");
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: path.join(evidence, `home-${width}.png`), fullPage: true });
    if (width !== 320) await page.screenshot({ path: path.join(evidence, `home-${width}-viewport.png`) });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByLabel("自然文の課題").fill("ソフトウェアの引き継ぎを改善したい");
    await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
    await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
    await page.getByText("根拠を見る", { exact: true }).first().click();
    await expect(page.locator("blockquote cite").first()).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.evaluate(() => window.scrollTo(0, 0));
    if (width !== 320) await page.screenshot({ path: path.join(evidence, `results-${width}.png`), fullPage: true });
    expect(errors).toEqual([]);
  });
}

test("検索の読込・障害・空状態を区別する", async ({ page }) => {
  await page.goto("/");
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route("**/api/search", async route => { await gate; await route.fulfill({ status: 503 }); });
  await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
  await expect(page.getByRole("heading", { name: "関連する経験を探しています" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "相談できる人と、参考になる事例を一緒に。" })).toHaveCount(0);
  await page.screenshot({ path: path.join(evidence, "loading-1280.png") });
  release();
  await expect(page.locator(".search-results").getByRole("alert")).toContainText("検索に失敗しました");
  await page.screenshot({ path: path.join(evidence, "error-1280.png") });
  await page.unroute("**/api/search");
  await page.route("**/api/search", route => route.fulfill({ json: { candidates: [], matchedUnits: 0, beforeLimit: 0, rerankingStatus: "not_performed" } }));
  await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
  await expect(page.getByRole("heading", { name: "条件に合う候補はありません。" })).toBeVisible();
  await page.screenshot({ path: path.join(evidence, "empty-1280.png") });
});
