import { expect, test } from "@playwright/test";
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
      },
    ]),
  );
  await page.goto("/home");

  await expect(page.getByTestId("home-gateway-panel")).toBeVisible();
  await expect(page.getByTestId("home-gateway-row")).toContainText("GW-SOS-001");
});
