import { expect, test, type Page } from "@playwright/test";
import { loginAs } from "./support/auth";
import { fulfillJson } from "./support/mock";

// Admin-flow E2E scenarios (#159): Twin RDF import (preview → validate → apply) and the registered-
// gateway view. Route-mocked REST, no backend — consistent with the other e2e specs.

const VALID_TTL =
  "@prefix sbco: <https://www.sbco.or.jp/ont/> .\nsbco:b1 a sbco:Building .";

async function mockTwinPreview(
  page: Page,
  body: unknown,
  status = 200,
): Promise<void> {
  await page.route("**/api/admin/twin/import/preview", (route) =>
    fulfillJson(route, body, status),
  );
}

test.describe("Twin RDF import", () => {
  test.beforeEach(async ({ context }) => {
    await loginAs(context, "admin");
  });

  test("previews a valid TTL, then applies it", async ({ page }) => {
    await mockTwinPreview(page, {
      tripleCount: 42,
      gatewayCount: 1,
      collisions: [],
      valid: true,
    });
    await page.route("**/api/admin/twin/import/apply", (route) =>
      fulfillJson(route, {
        tripleCount: 42,
        gatewayCount: 1,
        collisions: [],
        valid: true,
      }),
    );

    await page.goto("/admin/twin");
    await page.getByTestId("ttl-input").fill(VALID_TTL);

    // Apply is disabled until a clean preview lands.
    await expect(page.getByTestId("apply-button")).toBeDisabled();

    await page.getByTestId("preview-button").click();
    await expect(page.getByTestId("preview-result")).toContainText("検証 OK");
    await expect(page.getByTestId("preview-result")).toContainText(
      "1 ゲートウェイ",
    );
    await expect(page.getByTestId("apply-button")).toBeEnabled();

    // Append mode (default) applies without the destructive-replace confirm dialog.
    await page.getByTestId("apply-button").click();
    await expect(page.getByTestId("import-notice")).toBeVisible();
  });

  test("blocks apply when the preview reports a gateway_id collision", async ({
    page,
  }) => {
    await mockTwinPreview(page, {
      tripleCount: 30,
      gatewayCount: 2,
      collisions: [{ gatewayId: "GW-DUP-001", buildingCount: 2 }],
      valid: true,
    });

    await page.goto("/admin/twin");
    await page.getByTestId("ttl-input").fill(VALID_TTL);
    await page.getByTestId("preview-button").click();

    const result = page.getByTestId("preview-result");
    await expect(result).toContainText("gateway_id 重複 1 件");
    await expect(result).toContainText("GW-DUP-001");
    // A collision makes the twin invalid to apply — the button stays disabled.
    await expect(page.getByTestId("apply-button")).toBeDisabled();
  });

  test("surfaces a preview error instead of failing silently", async ({
    page,
  }) => {
    await mockTwinPreview(page, { error: "invalid turtle" }, 400);

    await page.goto("/admin/twin");
    await page.getByTestId("ttl-input").fill("not valid turtle");
    await page.getByTestId("preview-button").click();

    await expect(page.getByTestId("import-error")).toBeVisible();
    await expect(page.getByTestId("apply-button")).toBeDisabled();
  });
});

test.describe("Registered gateways", () => {
  test.beforeEach(async ({ context }) => {
    await loginAs(context, "admin");
  });

  test("lists gateways with binding, point count, last-seen and revision", async ({
    page,
  }) => {
    await page.route("**/api/admin/gateways*", (route) =>
      fulfillJson(route, [
        {
          gatewayId: "GW-SOS-001",
          bindingType: "bacnet-sim",
          settings: {},
          pointCount: 8,
          revision: "sha256:abcdef1234567890",
          certTrustAnchor: "",
          lastTelemetryAt: new Date(Date.now() - 90_000).toISOString(),
        },
        {
          gatewayId: "GW-002",
          bindingType: "hono",
          settings: {},
          pointCount: 12,
          revision: "sha256:0011223344aabb",
          certTrustAnchor: "",
          lastTelemetryAt: null, // never reported → 受信なし
        },
      ]),
    );

    await page.goto("/admin/gateways");

    const row1 = page.getByTestId("gw-row-GW-SOS-001");
    await expect(row1).toContainText("BACnet Sim");
    await expect(row1).toContainText("8");
    await expect(page.getByTestId("gw-last-seen-GW-SOS-001")).toContainText(
      "分前",
    );
    // A gateway that has never reported shows 受信なし, not a fake timestamp (#181 Phase 2).
    await expect(page.getByTestId("gw-last-seen-GW-002")).toHaveText(
      "受信なし",
    );
    await expect(page.getByTestId("resync-GW-SOS-001")).toBeVisible();
  });

  test("shows a success toast (not an inline notice) after a resync (#162)", async ({
    page,
  }) => {
    await page.route("**/api/admin/gateways", (route) =>
      fulfillJson(route, [
        {
          gatewayId: "GW-SOS-001",
          bindingType: "bacnet-sim",
          settings: {},
          pointCount: 8,
          revision: "sha256:abcdef1234567890",
          certTrustAnchor: "",
          lastTelemetryAt: new Date(Date.now() - 90_000).toISOString(),
        },
      ]),
    );
    await page.route("**/resync-pointlist", (route) =>
      fulfillJson(route, { revision: "sha256:99aabbccddeeff00" }, 202),
    );

    await page.goto("/admin/gateways");
    await page.getByTestId("resync-GW-SOS-001").click();

    // Transient success is a toast (auto-dismissing), per the notification policy — not a persistent
    // inline notice.
    await expect(page.getByTestId("toast-success")).toContainText(
      "再同期を通知しました",
    );
  });

  test("shows the empty state when no gateway is registered", async ({
    page,
  }) => {
    await page.route("**/api/admin/gateways*", (route) =>
      fulfillJson(route, []),
    );
    await page.goto("/admin/gateways");
    await expect(page.getByTestId("gw-empty")).toBeVisible();
  });
});
