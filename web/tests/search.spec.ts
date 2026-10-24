import { test, expect } from "@playwright/test";

test("ダミーの相談候補と根拠を検索できる", async ({ page }) => {
  // Initial upload authorization holds the tenant fence. Wait for that read before searching.
  const uploadContext = page.waitForResponse(response => response.url().endsWith("/api/v1/upload-context") && response.ok());
  await page.goto("/validation.html");
  await (await uploadContext).finished();
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
  await page.goto("/validation.html");
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
  await page.goto("/validation.html");
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
  await page.goto("/validation.html");
  await page.getByLabel("自然文の課題").fill("ソフトウェアの引き継ぎを改善したい");
  await page.getByRole("button", { name: "自然文から条件を生成" }).click();
  await expect(page.getByText("条件を生成しました。", { exact: false })).toBeVisible();
  await page.getByLabel("業務・技術分野の大分類ID").fill("software operations");
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
  await page.goto("/validation.html");
  await page.getByLabel("自然文の課題").fill("引き継ぎの改善");
  await page.getByRole("button", { name: "自然文から条件を生成" }).click();
  await expect(page.getByText("分類できませんでした。", { exact: false })).toBeVisible();
  await page.getByLabel("業務・技術分野の大分類ID").fill("operations");
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
});

test("条件生成の設定不備は運用者の確認を案内し、前回条件を消す", async ({ page }) => {
  await page.route("**/api/v2/search/conditions", route => route.fulfill({
    json: { conditions: null, status: "manual_required:configuration" },
  }));
  await page.goto("/validation.html");
  await page.getByLabel("自然文の課題").fill("引き継ぎの改善");
  await page.getByLabel("業務・技術分野の大分類ID").fill("software");
  await page.getByRole("button", { name: "自然文から条件を生成" }).click();
  await expect(page.getByText("検索のAI設定が整っていません。運用者による設定確認後に再実行してください。", { exact: true })).toBeVisible();
  await expect(page.getByText("分類できませんでした。手動で条件を入力してください。", { exact: true })).toHaveCount(0);
  await expect(page.getByLabel("業務・技術分野の大分類ID")).toHaveValue("");
});

test("自然文だけでも一度の検索で条件生成から結果へ進む", async ({ page }) => {
  await page.goto("/validation.html");
  await page.getByLabel("自然文の課題").fill("ソフトウェアの引き継ぎを改善したい");
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
  await page.getByText("根拠を見る").first().click();
  await expect(page.locator("blockquote cite").first()).toBeVisible();
});


test("リランク失敗を正解なしと区別し、未評価候補を推薦しない", async ({ page }) => {
  await page.route("**/api/v2/search", route => route.fulfill({ json: {
    runId: "failed-run", rerankingStatus: "not_performed:inputlimit", assessments: [],
    primary: { candidates: [{ entityId: "unverified", entityType: "person", title: "未評価の人物", routes: [], units: [] }], rerankingStatus: "not_performed:inputlimit" }
  } }));
  await page.goto("/validation.html");
  await page.getByLabel("自然文の課題").fill("運用改善を相談したい");
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  await expect(page.getByText("候補の評価を完了できませんでした", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "未評価の人物" })).toHaveCount(0);
  await expect(page.getByText("条件に合う候補はありません。", { exact: true })).toHaveCount(0);
});

test("事例の指定を検索APIへ送り、人物を混ぜずに取得する", async ({ page }) => {
  await page.goto("/validation.html");
  await page.getByLabel("自然文の課題").fill("引き継ぎ改善の事例を探したい");
  await page.getByLabel("検索対象", { exact: true }).selectOption("knowledge");
  const request = page.waitForRequest(r => r.url().endsWith("/api/v2/search"));
  await page.getByRole("button", { name: "相談候補を探す" }).click();
  expect((await request).postDataJSON().target).toBe("knowledge");
  await expect(page.getByText("根拠に基づく再順位付け済み", { exact: true })).toBeVisible();
  await expect(page.locator("article.candidate")).not.toHaveCount(0);
  await expect(page.locator("article.candidate .kind").filter({ hasText: "相談候補" })).toHaveCount(0);
});

for (const types of [["knowledge", "person", "knowledge", "person"], ["person"], ["knowledge"], []]) {
  test(`検索結果を種別別に表示する: ${types.join(",") || "空"}`, async ({ page }) => {
    const candidates = types.map((entityType, i) => ({ entityId: `group-${i}`, entityType, title: `結果${i}`, routes: [],
      units: [{ summary: "架空の原文", sourceRefs: [], confirmationState: "fixture" }] }));
    await page.route("**/api/v2/search", route => route.fulfill({ json: { runId: "grouped-run", rerankingStatus: candidates.length ? "succeeded" : "no_candidates",
      primary: { candidates, rerankingStatus: candidates.length ? "succeeded" : "no_candidates", matchedUnits: candidates.length, beforeLimit: candidates.length }, assessments: [] } }));
    await page.goto("/validation.html");
    await page.getByLabel("自然文の課題").fill("経験と事例を探したい");
    await page.getByRole("button", { name: "相談候補を探す" }).click();
    for (const [type, name] of [["person", "相談候補"], ["knowledge", "参考事例"]]) {
      const group = page.getByRole("region", { name, exact: true });
      await expect(group).toBeVisible();
      await expect(group.locator("article h3")).toHaveText(candidates.filter(c => c.entityType === type).map(c => c.title));
      if (!types.includes(type)) await expect(group.getByText(`${name}はありません。`, { exact: true })).toBeVisible();
    }
  });
}

for (const code of ["CLASSIFICATION_OUT_OF_SCOPE", "CLASSIFICATION_MANUAL_REQUIRED", "AI_SEARCH_CONFIGURATION_INVALID"]) {
  test(`分類対象外と障害を分け、前の条件を再送しない: ${code}`, async ({ page }) => {
    const bodies: Record<string, unknown>[] = [];
    await page.route("**/api/v2/search", route => {
      bodies.push(route.request().postDataJSON());
      return route.fulfill({ status: 422, json: { code } });
    });
    await page.goto("/validation.html");
    await page.getByLabel("自然文の課題").fill("以前の課題");
    await page.getByLabel("業務・技術分野の大分類ID").fill("software");
    await page.getByLabel("自然文の課題").fill("対象分野が不明の新しい課題");
    for (let i = 0; i < 2; i++) {
      await page.getByRole("button", { name: "相談候補を探す" }).click();
      if (code === "CLASSIFICATION_OUT_OF_SCOPE") {
        await expect(page.getByText("自動検索対象外", { exact: false })).toBeVisible();
        await expect(page.getByText("検索は実行していません。", { exact: false })).toBeVisible();
        await expect(page.getByRole("region", { name: "相談候補と参考事例", exact: true }).getByRole("alert")).toHaveCount(0);
      } else await expect(page.getByRole("region", { name: "相談候補と参考事例", exact: true }).getByRole("alert")).toContainText(code === "AI_SEARCH_CONFIGURATION_INVALID" ? "運用者による設定確認" : "自動分類を利用できません");
      await expect(page.getByRole("button", { name: "相談候補を探す" })).toBeEnabled();
    }
    expect(bodies).toHaveLength(2);
    expect(bodies.every(body => !("conditions" in body))).toBe(true);
    await expect(page.getByText("条件に合う候補はありません。", { exact: true })).toHaveCount(0);
    await expect(page.getByLabel("業務・技術分野の大分類ID")).toHaveValue("");
    await page.getByLabel("業務・技術分野の大分類ID").fill("operations");
    await page.getByRole("button", { name: "相談候補を探す" }).click();
    await expect(page.getByRole("button", { name: "相談候補を探す" })).toBeEnabled();
    expect(bodies[2]).toHaveProperty("conditions.categories", [{ axis: "domain", categoryId: "operations" }]);
  });
}
