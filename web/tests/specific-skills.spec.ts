import { test, expect } from "@playwright/test";
import path from "node:path";

test("具体スキルを大分類から分け、本人の実施だけを根拠付きで検索する", async ({ page }) => {
  await page.goto("/validation.html");
  await page.getByLabel("文書名", { exact: true }).fill("具体スキルの架空職歴");
  await page.getByLabel("原本ファイル").setInputFiles(path.resolve("tests/fixtures/specific-skills.docx"));
  const accepted = page.waitForResponse(r => r.url().endsWith("/api/v1/documents") && r.request().method() === "POST");
  await page.getByRole("button", { name: "アップロード", exact: true }).click();
  const { documentId } = await (await accepted).json();
  await expect(page.locator(".documents")).toContainText("検索投影済み", { timeout: 30000 });
  await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
  const rows = page.getByTestId(documentId);
  await expect(rows).toHaveCount(3);
  for (const row of await rows.all()) await row.locator(".specific-skills summary").click();
  await expect(page.locator(".profiles .specific-skills")).toContainText(["具体スキル", "具体スキル", "具体スキル"]);
  // Profiles are ordered by stable generated IDs, not by the input sentence order.
  await expect.poll(async () => (await page.locator(".profiles .specific-skills strong").allTextContents()).sort())
    .toEqual(["C#", "回帰試験", "Azure", "回帰試験"].sort());
  await expect(page.locator(".profiles .specific-skills").filter({ hasText: "C#" })).toContainText("経験として抽出");
  await expect(page.locator(".profiles .specific-skills").filter({ hasText: "C#" })).toContainText("根拠ID：");
  await expect(page.locator(".profiles .quarantined-skills")).toHaveCount(0);
  await expect(page.locator(".profiles .specific-skills").filter({ hasText: "未実施・希望等" })).toHaveCount(2);
  await page.getByLabel("分野", { exact: true }).selectOption("");
  await page.getByLabel("検索語", { exact: true }).fill("");
  await page.getByLabel("引き継ぎ経験の補助判定を加える").uncheck();
  await page.getByLabel("具体スキル（カンマ区切り）", { exact: true }).fill("C#");
  const result = page.waitForResponse(r => r.url().endsWith("/api/search") && r.request().method() === "POST");
  await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
  const body = await (await result).json();
  expect(body.candidates).toHaveLength(1);
  expect(body.candidates[0].routes).toContain("specificSkills");
  expect(body.candidates[0].units.every((u: { skillIds?: string[] }) => u.skillIds === undefined)).toBe(true);
  await expect(page.getByRole("region", { name: "相談候補と参考事例" })).toContainText("具体スキルの経験タグ一致");
  await page.getByLabel("具体スキル（カンマ区切り）", { exact: true }).fill("Azure");
  const absent = page.waitForResponse(r => r.url().endsWith("/api/search") && r.request().method() === "POST");
  await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
  expect((await (await absent).json()).candidates).toHaveLength(0);
  await page.screenshot({ path: "test-results/specific-skills.png", fullPage: true });
});
