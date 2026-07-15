import { expect, test, type Page } from "@playwright/test";
import { loginAs } from "./support/auth";
import { fulfillJson, isoSecondsAgo } from "./support/mock";

// Point detail: latest value + freshness badge (#158) driven by a mocked
// `/telemetries/query` (latest + warm both route through the same endpoint).
test.beforeEach(async ({ context }) => {
  await loginAs(context, "admin");
});

async function mockPoint(page: Page, opts: { datetime: string; value: number }) {
  await page.route("**/point-details/**", (route) =>
    fulfillJson(route, {
      point: { dtId: "p1", id: "SOS-PT-001", name: "室温", unit: "degC" },
    }),
  );
  // latest (latest=true) and warm (start/end) both hit /telemetries/query.
  await page.route("**/telemetries/query*", (route) =>
    fulfillJson(route, [{ datetime: opts.datetime, value: opts.value }]),
  );
}

test("shows the latest value and a fresh badge for a recent sample", async ({ page }) => {
  await mockPoint(page, { datetime: isoSecondsAgo(10), value: 22.5 });
  await page.goto("/points/SOS-PT-001");

  await expect(page.getByTestId("freshness-fresh")).toBeVisible();
  await expect(page.getByText("室温")).toBeVisible();
});

test("shows a stale badge for an old sample", async ({ page }) => {
  await mockPoint(page, { datetime: isoSecondsAgo(100_000), value: 22.5 });
  await page.goto("/points/SOS-PT-001");

  await expect(page.getByTestId("freshness-stale")).toBeVisible();
});

test("surfaces an error when point detail fails to load", async ({ page }) => {
  // Operator error feedback: a failed fetch must say so, not render blank.
  await page.route("**/point-details/**", (route) =>
    route.fulfill({ status: 500, contentType: "application/json", body: "{}" }),
  );
  await page.goto("/points/SOS-PT-001");

  await expect(page.getByText("ポイント情報の取得に失敗しました。")).toBeVisible();
});
