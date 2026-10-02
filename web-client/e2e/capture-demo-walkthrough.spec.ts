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
test.setTimeout(420_000); // a fresh stack needs ~3 min before the evaluator raises its first events

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

// The app shell scrolls inside its own container, so Playwright's `fullPage` cannot see below the
// viewport. For the screens that are taller than one viewport, grow the viewport instead, wait for the
// late-arriving panels (the 24 h coverage bar), and put it back.
async function tallShot(page: Page, name: string): Promise<void> {
  const original = page.viewportSize() ?? { width: 1280, height: 860 };
  await page.setViewportSize({ width: original.width, height: 1500 });
  await page.waitForTimeout(2_000);
  await shot(page, name);
  await page.setViewportSize(original);
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
  await tallShot(page, "3-point-missing");

  // 4. Point 詳細 — stale の Point（interval × multiplier の判定根拠）
  await page.goto("/health?freshness=stale");
  await page.getByTestId("health-table").waitFor();
  await shot(page, "4-health-stale");
  await page.getByTestId("health-table").locator("tbody a").first().click();
  await page.getByTestId("point-health-panel").waitFor({ timeout: 30_000 });
  await tallShot(page, "5-point-stale");

  // 5. /resources — タグ検索（critical）
  await page.goto("/resources");
  await page.getByTestId("tag-input").fill("critical");
  await page.getByTestId("tag-input").press("Enter");
  await expect(page.getByTestId("tag-chip-critical")).toBeVisible();
  await page.waitForTimeout(1_500);
  await shot(page, "6-resources-tag");

  // 6b. /resources — Point の facet（機器・計測・単位・Gateway と、鮮度・アラーム）(#454)
  await page.goto("/resources");
  await page.getByLabel("種別で絞り込み").selectOption("point");
  await expect(page.getByTestId("facet-panel")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId("facet-health-freshness")).toBeVisible({
    timeout: 60_000,
  });
  await page.getByTestId("facet-freshness-stale").check();
  await expect(
    page.getByTestId("resource-search-box").locator("ul li button").first(),
  ).toBeVisible({ timeout: 60_000 });
  await page.waitForTimeout(1_500);
  await shot(page, "10-resources-facets");

  // 6c. /health?view=events — 永続ヘルスイベント（#455）。評価器が連続スキャンで条件を確認してから
  // 発生させるので、最初の行が出るまで待つ。
  await page.goto("/health?view=events");
  await expect(page.getByTestId("events-table")).toBeVisible({ timeout: 240_000 });
  // gateway 切断は配下の欠測を 1 件に集約したイベントになる。1 件確認応答して「確認済み」の行も写す。
  const gatewayRow = page
    .locator("tr", { has: page.getByTestId("event-kind").filter({ hasText: "Gateway 切断" }) })
    .first();
  await expect(gatewayRow).toBeVisible({ timeout: 60_000 });
  // Re-runs find it already acknowledged (the first acknowledger stays), which is the state we want to show.
  const ackButton = gatewayRow.getByTestId("event-ack-button");
  if (await ackButton.count()) await ackButton.click();
  await expect(gatewayRow.getByTestId("event-ack")).not.toHaveText("確認する");
  await page.waitForTimeout(500);
  await tallShot(page, "8-health-events");

  // 6d. 値異常のイベントがある Point の詳細 — 「この Point の直近イベント」
  await page.goto("/health?view=events&kind=alarm");
  await page.getByTestId("events-table").waitFor({ timeout: 60_000 });
  await page.getByTestId("events-table").locator("tbody a").first().click();
  await page.getByTestId("point-health-events").waitFor({ timeout: 30_000 });
  await expect(page.getByTestId("point-health-events")).toContainText("値異常");
  await tallShot(page, "9-point-events");

  // 6. /platform/status — 流量
  await page.goto("/platform/status");
  await page.waitForLoadState("networkidle");
  await tallShot(page, "7-platform-status");
});
