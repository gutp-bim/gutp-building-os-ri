import { expect, test, type Page } from "@playwright/test";
import { loginWithKeycloak } from "./support/demo";

// Capture the 5-minute demo walkthrough screenshots (#458, docs/guides/demo-walkthrough.md) from a
// LIVE `make demo` stack (DEMO_SCENARIO=degraded). Not part of any suite — run explicitly:
//
//   E2E_CAPTURE_WALKTHROUGH=1 E2E_NO_SERVER=1 E2E_BASE_URL=http://localhost:3000 \
//     npx playwright test e2e/capture-demo-walkthrough.spec.ts
//
// PNGs land in ../docs/screenshots/demo-walkthrough-*.png.
test.skip(
  process.env.E2E_CAPTURE_WALKTHROUGH !== "1",
  "captures from a live make demo stack; set E2E_CAPTURE_WALKTHROUGH=1",
);
test.use({ viewport: { width: 1280, height: 860 } });
test.setTimeout(120_000);

const SHOTS = "../docs/screenshots";

async function hideDevOverlay(page: Page): Promise<void> {
  await page.addStyleTag({
    content:
      "nextjs-portal, [data-nextjs-toast], #__next-build-watcher { display: none !important; }",
  });
}

async function shot(page: Page, name: string, fullPage = false): Promise<void> {
  await hideDevOverlay(page);
  await page.screenshot({
    path: `${SHOTS}/demo-walkthrough-${name}.png`,
    fullPage,
  });
}

test("capture: demo walkthrough", async ({ page }) => {
  await loginWithKeycloak(page);

  // 1. /home — the demo building, all floors
  await page.goto("/home");
  await page
    .getByTestId("home-building-select")
    .selectOption({ label: "東京デモビル" });
  await page
    .getByTestId("home-floor-select")
    .selectOption({ label: "すべてのフロア（建物全体）" });
  await expect(
    page.getByTestId("summary-missing").locator("div").first(),
  ).not.toHaveText("0", {
    timeout: 60_000,
  });
  await page.getByTestId("home-attention-list").waitFor();
  await shot(page, "1-home");

  // 2. 欠測カード → /health?freshness=missing
  await page.getByTestId("summary-missing").click();
  await expect(page).toHaveURL(/\/health\?freshness=missing/);
  await page.getByTestId("health-table").waitFor();
  await expect(
    page.getByTestId("health-gateway-disconnected").first(),
  ).toBeVisible({
    timeout: 30_000,
  });
  await shot(page, "2-health-missing");

  // 3. Point 詳細 — gateway 断で欠測の Point
  await page.goto("/points/DEMO-RF-WS01-OAT");
  await page.getByTestId("point-health-panel").waitFor({ timeout: 30_000 });
  // 欠測理由と gateway はサーバ側の行を別に引くので、その到着を待つ（#457）。
  await expect(page.getByTestId("health-missing-reason")).toHaveText(
    "ゲートウェイ切断",
    { timeout: 30_000 },
  );
  await shot(page, "3-point-missing", true);

  // 4. Point 詳細 — stale の Point（interval × multiplier の判定根拠）
  await page.goto("/health?freshness=stale");
  await page.getByTestId("health-table").waitFor();
  await shot(page, "4-health-stale");
  await page.getByTestId("health-table").locator("tbody a").first().click();
  await page.getByTestId("point-health-panel").waitFor({ timeout: 30_000 });
  await shot(page, "5-point-stale", true);

  // 5. /resources — タグ検索（critical）
  await page.goto("/resources");
  await page.getByTestId("tag-input").fill("critical");
  await page.getByTestId("tag-input").press("Enter");
  await expect(page.getByTestId("tag-chip-critical")).toBeVisible();
  await page.waitForTimeout(1_500);
  await shot(page, "6-resources-tag");

  // 6. /platform/status — 流量
  await page.goto("/platform/status");
  await page.waitForLoadState("networkidle");
  await shot(page, "7-platform-status", true);
});
