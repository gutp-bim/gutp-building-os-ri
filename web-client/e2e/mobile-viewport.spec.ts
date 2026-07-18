import { expect, test, type Page } from "@playwright/test";
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

// Responsive-shell regression (#199): below `md` the sidebar must collapse into an off-canvas
// drawer, and the primary operator flow (home → 要対応 → point 詳細 → チャート) must complete on a
// tablet/phone viewport without the page scrolling horizontally. Runs against a route-mocked API
// (no backend), like the other e2e specs.
test.use({ viewport: { width: 375, height: 812 } });

const POINTS = [
  { dtId: "pt-1", id: "pt-1", name: "室温" },
  { dtId: "pt-2", id: "pt-2", name: "CO2" },
  { dtId: "pt-3", id: "pt-3", name: "電力" },
];

async function mockHomeTwin(page: Page): Promise<void> {
  await mockBuildings(page);
  await mockFloors(page);
  await mockSpaces(page);
  await mockDevices(page);
  await mockPoints(page, POINTS);
  await mockLatestTelemetry(page, {
    "pt-1": isoSecondsAgo(10), // fresh
    "pt-2": isoSecondsAgo(900), // stale
    // pt-3 omitted → missing (sorted first in the attention list)
  });
}

/** The document must not scroll horizontally (allow a 1px sub-pixel rounding tolerance). */
async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - window.innerWidth,
  );
  expect(overflow, "horizontal overflow (px)").toBeLessThanOrEqual(1);
}

test("collapses the sidebar into an off-canvas drawer below md", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockHomeTwin(page);
  await page.goto("/home");

  // The static desktop column is hidden; the hamburger is the only nav entry point.
  await expect(page.getByTestId("sidebar-desktop")).toBeHidden();
  const toggle = page.getByTestId("sidebar-toggle");
  await expect(toggle).toBeVisible();
  await expect(page.getByTestId("sidebar-drawer")).toHaveCount(0);

  // Opening the drawer reveals the modal nav; Escape restores the collapsed state.
  await toggle.click();
  await expect(page.getByTestId("sidebar-drawer")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByTestId("sidebar-drawer")).toHaveCount(0);
});

test("closes the drawer after following a nav link", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockHomeTwin(page);
  await page.goto("/home");

  await page.getByTestId("sidebar-toggle").click();
  const drawer = page.getByTestId("sidebar-drawer");
  await expect(drawer).toBeVisible();

  // Following any nav link navigates and auto-closes the drawer (app-shell closes it on route change).
  await drawer.getByRole("link", { name: "リソース" }).click();
  await expect(page).toHaveURL(/\/resources/);
  await expect(page.getByTestId("sidebar-drawer")).toHaveCount(0);
});

test("completes home → 要対応 → point 詳細 without horizontal scroll", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockHomeTwin(page);
  await page.goto("/home");

  // Home renders the attention list and does not overflow horizontally on a phone width.
  await expect(page.getByTestId("home-attention-list")).toBeVisible();
  await expectNoHorizontalScroll(page);

  // Point detail for the first attention row is mocked (latest + warm both hit /telemetries/query).
  await page.route("**/point-details/**", (route) =>
    fulfillJson(route, {
      point: { dtId: "pt-3", id: "pt-3", name: "電力", unit: "kW" },
    }),
  );
  await page.route("**/telemetries/query*", (route) =>
    fulfillJson(route, [{ datetime: isoSecondsAgo(10), value: 12.3 }]),
  );

  // The chart-heavy point-detail route cold-compiles in the dev server, so allow it extra time to
  // navigate + render before the horizontal-scroll assertion.
  await page.getByTestId("home-attention-link").first().click();
  await expect(page).toHaveURL(/\/points\/pt-3/, { timeout: 30_000 });
  await expect(page.getByTestId("point-detail")).toBeVisible({
    timeout: 30_000,
  });
  await expectNoHorizontalScroll(page);
});
