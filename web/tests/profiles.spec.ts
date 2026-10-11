import { test, expect } from "@playwright/test";
import path from "node:path";

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
  test(`${extension}から本人訂正と手動一次検索までfake AIで通る`, async ({ page }) => {
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
    await expect(page.locator(".profiles")).toContainText("日付から算出した経験月数（重複除外）：84");
    await page.getByTestId(documentId).getByLabel("経験の説明").first().fill(`本人訂正${extension} ソフトウェア`);
    await page.getByRole("button", { name: "訂正を保存", exact: true }).click();
    await expect(page.locator(".profiles")).toContainText("保存しました");
    await expect.poll(async () => {
      await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
      return await page.locator(".profiles").innerText();
    }).toContain("有効版：");
    await page.getByLabel("検索語", { exact: true }).fill(`本人訂正${extension}`);
    await page.getByRole("button", { name: "相談候補を探す", exact: true }).click();
    await expect(page.getByRole("region", { name: "相談候補と参考事例" })).toContainText(`本人訂正${extension}`, { timeout: 15000 });
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
  await expect.poll(async () => {
    await page.getByRole("button", { name: "プロフィールを更新", exact: true }).click();
    return await page.locator(".profiles").innerText();
  }).toContain("有効版：");
});
