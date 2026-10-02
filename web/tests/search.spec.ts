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

test("fake AIで自然文・条件編集・結果・出典を確認できる", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("自然文の課題").fill("ソフトウェアの引き継ぎを改善したい");
  await page.getByRole("button", { name: "自然文から条件を生成" }).click();
  await expect(page.getByText("条件を生成しました。", { exact: false })).toBeVisible();
  await page.getByLabel("複数分野・技術要素の分類ID").fill("software operations");
  await page.getByLabel("補助経験条件ID").fill("handover");
  await page.getByLabel("検索語").fill("引き継ぎ");
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
  await expect(page.getByText("適合理由（根拠引用）：", { exact: false }).first()).toBeVisible();
  await expect(page.getByText("不足情報：", { exact: false }).first()).toBeVisible();
  await page.getByText("根拠を見る").first().click();
  await expect(page.locator("blockquote cite").first()).toBeVisible();
  await expect(page.getByText("完了・表示前の権限確認済み", { exact: false })).toBeVisible();
});

test("分類停止時に手動条件へ戻せる", async ({ page }) => {
  await page.route("**/api/v2/search/conditions", route => route.fulfill({ json: { conditions: null, status: "manual_required:stopped" } }));
  await page.goto("/");
  await page.getByLabel("自然文の課題").fill("引き継ぎの改善");
  await page.getByRole("button", { name: "自然文から条件を生成" }).click();
  await expect(page.getByText("分類できませんでした。", { exact: false })).toBeVisible();
  await page.getByLabel("複数分野・技術要素の分類ID").fill("operations");
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
});

test("自然文だけでも一度の検索で条件生成から結果へ進む", async ({ page }) => {
  await page.goto("/");
  await page.getByLabel("自然文の課題").fill("ソフトウェアの引き継ぎを改善したい");
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
  await page.getByText("根拠を見る").first().click();
  await expect(page.locator("blockquote cite").first()).toBeVisible();
});
