import { test, type Page } from "@playwright/test";
import { loginAs } from "./support/auth";
import {
  fulfillJson,
  isoSecondsAgo,
  mockBuildings,
  mockDevices,
  mockFloors,
  mockLatestTelemetry,
  mockPoints,
  mockSpaces,
} from "./support/mock";

// Capture the README screenshots (#156) from the real app driven against a route-mocked API — no
// backend required. Run explicitly:  npx playwright test e2e/capture-readme-screenshots.spec.ts
// PNGs land in ../docs/screenshots/. Kept in-repo so the images can be regenerated on UI changes.
test.use({ viewport: { width: 1280, height: 860 } });

const SHOTS = "../docs/screenshots";

/** Hide the Next.js dev-mode overlay button so it doesn't appear in the README captures. */
async function hideDevOverlay(page: Page): Promise<void> {
  await page.addStyleTag({
    content:
      "nextjs-portal, [data-nextjs-toast], #__next-build-watcher { display: none !important; }",
  });
}

const HOME_POINTS = [
  { dtId: "pt-1", id: "pt-1", name: "室温" },
  { dtId: "pt-2", id: "pt-2", name: "CO2 濃度" },
  { dtId: "pt-3", id: "pt-3", name: "消費電力" },
];

test("capture: operator home", async ({ context, page }) => {
  await loginAs(context, "operator");
  await mockBuildings(page);
  await mockFloors(page);
  await mockSpaces(page);
  await mockDevices(page);
  await mockPoints(page, HOME_POINTS);
  await mockLatestTelemetry(page, {
    "pt-1": isoSecondsAgo(12), // fresh
    "pt-2": isoSecondsAgo(1200), // stale
    // pt-3 omitted → missing
  });
  await page.goto("/home");
  await page.getByTestId("home-attention-list").waitFor();
  await hideDevOverlay(page);
  await page.screenshot({ path: `${SHOTS}/operator-home.png` });
});

test("capture: resource explorer", async ({ context, page }) => {
  await loginAs(context, "admin");
  await mockBuildings(page);
  await mockFloors(page);
  // Detail pane fetches resource metadata; stub it so the page has no failed request.
  await page.route("**/metadata**", (route) =>
    fulfillJson(route, { identifiers: {}, customTags: {} }),
  );
  await page.goto("/resources");
  // Single root building auto-expands and loads its floors (#135).
  await page.getByText("1F オフィス").waitFor();
  await hideDevOverlay(page);
  await page.screenshot({ path: `${SHOTS}/resource-explorer.png` });
});

async function mockPointDetail(page: Page): Promise<void> {
  await page.route("**/point-details/**", (route) =>
    fulfillJson(route, {
      point: { dtId: "SOS-PT-001", id: "SOS-PT-001", name: "室温", unit: "℃" },
    }),
  );
  // latest (latest=true) + warm history both hit /telemetries/query; return a day of hourly samples
  // so the chart draws a realistic curve.
  await page.route("**/telemetries/query*", (route) => {
    const now = Date.now();
    const series = Array.from({ length: 24 }, (_, i) => ({
      datetime: new Date(now - (23 - i) * 3600_000).toISOString(),
      value: Math.round((22 + Math.sin(i / 3) * 2.5) * 10) / 10,
    }));
    return fulfillJson(route, series);
  });
}

test("capture: point detail", async ({ context, page }) => {
  await loginAs(context, "operator");
  await mockPointDetail(page);
  await page.goto("/points/SOS-PT-001");
  await page.getByTestId("point-detail").waitFor();
  await hideDevOverlay(page);
  // Let the chart animation settle.
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/point-detail.png`, fullPage: true });
});
