import { test, expect } from "@playwright/test";

test("ダミーの相談候補と根拠を検索できる", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByText("ローカル・ダミーデータ")).toBeVisible();
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(
    page.getByRole("heading", { name: "水野 そうた" }),
  ).toBeVisible();
  await expect(
    page.getByText("再順位付け未実施", { exact: true }),
  ).toBeVisible();
  await page.getByText("根拠を見る").first().click();
  await expect(page.getByText("段落1", { exact: false }).first()).toBeVisible();
});

test("副分類のソフトウェア経験を拾う", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("分野").selectOption("software");
  await page.getByLabel("検索語").fill("");
  await page.getByLabel("引き継ぎ経験の補助判定を加える").uncheck();
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByRole("heading", { name: "森 あかり" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "青葉 はるか" })).toHaveCount(
    0,
  );
});

test("空結果と障害を区別する", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("分野").selectOption("");
  await page.getByLabel("検索語").fill("該当しない語");
  await page.getByLabel("引き継ぎ経験の補助判定を加える").uncheck();
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("条件に合う候補はありません。")).toBeVisible();
  await page.route("**/api/search", (route) => route.fulfill({ status: 503 }));
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByRole("alert")).toContainText("検索に失敗しました");
});
