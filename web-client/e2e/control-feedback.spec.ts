import { expect, test, type Page } from "@playwright/test";
import { loginAs } from "./support/auth";
import { fulfillJson, isoSecondsAgo } from "./support/mock";

// Control-feedback E2E (#162 + closes the #159 "permission-denied / gateway-offline on control" gap):
// the control POST's 403 and 503 must be explained distinctly, not collapsed into a generic failure.
// Route-mocked REST only — the 403/503 land on `POST /points/{id}/control` *before* any gRPC stream
// opens, so no gRPC mocking is needed.

/** A BACnet-controllable (boolean) point so the "制御信号を送信" button renders. */
async function mockControllablePoint(page: Page): Promise<void> {
  await page.route("**/point-details/**", (route) =>
    fulfillJson(route, {
      point: {
        dtId: "p1",
        id: "SOS-PT-001",
        name: "給気ファン",
        unit: "",
        objectTypeBacnet: "binaryOutput",
        instanceNoBacnet: 1,
        deviceIdBacnet: "1",
      },
      controlSchema: { dataType: "boolean" },
    }),
  );
  // latest value so the page renders with a freshness badge.
  await page.route("**/telemetries/query*", (route) =>
    fulfillJson(route, [{ datetime: isoSecondsAgo(10), value: 1 }]),
  );
  // audit history endpoint (not the focus here) → empty.
  await page.route("**/points/*/control-audit*", (route) =>
    fulfillJson(route, []),
  );
}

async function submitControl(page: Page): Promise<void> {
  await page.getByRole("button", { name: "制御信号を送信" }).click();
  await page.getByRole("button", { name: "送信" }).click();
}

test.describe("control feedback", () => {
  test("explains a permission-denied (403) control attempt", async ({
    page,
    context,
  }) => {
    // A viewer can still see the control button (the client does not RBAC-gate it); the API rejects
    // the POST with 403 — the UI must say *which* permission is missing, not just "failed".
    await loginAs(context, "viewer");
    await mockControllablePoint(page);
    await page.route(
      "**/points/*/control",
      (route) =>
        route.request().method() === "POST"
          ? fulfillJson(route, { title: "Forbidden" }, 403)
          : route.fallback(),
      { times: 1 },
    );

    await page.goto("/points/SOS-PT-001");
    await submitControl(page);

    const bar = page.getByTestId("control-status-permission_denied");
    await expect(bar).toBeVisible();
    await expect(bar).toContainText("point:SOS-PT-001:write");
  });

  test("explains a gateway-offline (503) control attempt", async ({
    page,
    context,
  }) => {
    await loginAs(context, "operator");
    await mockControllablePoint(page);
    await page.route(
      "**/points/*/control",
      (route) =>
        route.request().method() === "POST"
          ? fulfillJson(route, { title: "gateway offline" }, 503)
          : route.fallback(),
      { times: 1 },
    );

    await page.goto("/points/SOS-PT-001");
    await submitControl(page);

    const bar = page.getByTestId("control-status-gateway_offline");
    await expect(bar).toBeVisible();
    await expect(bar).toContainText("ゲートウェイ");
  });
});
