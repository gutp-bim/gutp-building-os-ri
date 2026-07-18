import { expect, test } from "@playwright/test";
import { loginAs } from "./support/auth";
import {
  fulfillJson,
  isoSecondsAgo,
  mockBuildings,
  mockDevices,
  mockFloors,
  mockLatestTelemetry,
  mockLatestTelemetryFailure,
  mockPoints,
  mockSpaces,
} from "./support/mock";

// Operator home (#158) with a route-mocked API (no backend). Three points exercise the full
// freshness spectrum: fresh (10s ago), stale (900s ago > 300s threshold), missing (no data).
const POINTS = [
  { dtId: "pt-1", id: "pt-1", name: "室温" },
  { dtId: "pt-2", id: "pt-2", name: "CO2" },
  { dtId: "pt-3", id: "pt-3", name: "電力" },
];

async function mockTwin(page: import("@playwright/test").Page): Promise<void> {
  await mockBuildings(page);
  await mockFloors(page);
  await mockSpaces(page);
  await mockDevices(page);
  await mockPoints(page, POINTS);
  await mockLatestTelemetry(page, {
    "pt-1": isoSecondsAgo(10), // fresh
    "pt-2": isoSecondsAgo(900), // stale
    // pt-3 omitted → missing
  });
}

test("operator home shows freshness counts and worst-first attention list", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockTwin(page);
  await page.goto("/home");

  // Summary: 1 fresh, 1 stale, 1 missing.
  await expect(page.getByTestId("summary-fresh")).toContainText("1");
  await expect(page.getByTestId("summary-stale")).toContainText("1");
  await expect(page.getByTestId("summary-missing")).toContainText("1");

  // Attention list keeps only stale + missing, missing sorted first.
  const rows = page.getByTestId("home-attention-row");
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText("電力"); // missing first
  await expect(rows.nth(1)).toContainText("CO2"); // then stale

  // Each row links to the point detail and shows its space / device context (#179).
  const firstLink = page.getByTestId("home-attention-link").first();
  await expect(firstLink).toHaveAttribute("href", "/points/pt-3");
  await expect(firstLink).toContainText("執務室"); // space name
  await expect(firstLink).toContainText("AHU-1"); // device name
});

test("shows an error, not false 欠測, when the freshness batch fails", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockBuildings(page);
  await mockFloors(page);
  await mockSpaces(page);
  await mockDevices(page);
  await mockPoints(page, POINTS);
  // The batch-latest endpoint is down; the home must surface it rather than showing every point as
  // missing (#182 review point 1).
  await mockLatestTelemetryFailure(page, 503);
  await page.goto("/home");

  await expect(page.getByTestId("home-error")).toBeVisible();
  await expect(page.getByTestId("home-attention-row")).toHaveCount(0);
});

test("classifies a floor larger than the batch cap by chunking client-side", async ({
  context,
  page,
}) => {
  // 501 points > the 500-id server cap: the client must split into <=500 chunks instead of tripping
  // the server's 400 and misclassifying the whole floor as missing (#182 review point 2).
  const manyPoints = Array.from({ length: 501 }, (_, i) => ({
    dtId: `bulk-${i}`,
    id: `bulk-${i}`,
    name: `点${i}`,
  }));
  const allFresh = Object.fromEntries(
    manyPoints.map((p) => [p.id, isoSecondsAgo(10)]),
  );

  await loginAs(context, "operator");
  await mockBuildings(page);
  await mockFloors(page);
  await mockSpaces(page);
  await mockDevices(page);
  await mockPoints(page, manyPoints);
  await mockLatestTelemetry(page, allFresh);
  await page.goto("/home");

  await expect(page.getByTestId("summary-fresh")).toContainText("501");
  await expect(page.getByTestId("summary-missing")).toContainText("0");
});

test("gateway panel is hidden for operators", async ({ context, page }) => {
  await loginAs(context, "operator");
  await mockTwin(page);
  await page.goto("/home");

  await expect(page.getByTestId("home-attention-list")).toBeVisible();
  await expect(page.getByTestId("home-gateway-panel")).toHaveCount(0);
});

test("gateway panel is shown for admins", async ({ context, page }) => {
  await loginAs(context, "admin");
  await mockTwin(page);
  await page.route("**/api/admin/gateways*", (route) =>
    fulfillJson(route, [
      {
        gatewayId: "GW-SOS-001",
        bindingType: "bacnet-sim",
        settings: {},
        pointCount: 8,
        revision: "sha256:abcdef1234567890",
        certTrustAnchor: "",
        lastTelemetryAt: null,
      },
    ]),
  );
  await page.goto("/home");

  await expect(page.getByTestId("home-gateway-panel")).toBeVisible();
  await expect(page.getByTestId("home-gateway-row")).toContainText(
    "GW-SOS-001",
  );
});
