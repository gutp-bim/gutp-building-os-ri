import { expect, test } from "@playwright/test";
import { loginAs } from "./support/auth";

test.beforeEach(async ({ context, page }) => {
  await loginAs(context, "admin");
  await page.route("http://localhost:5000/devices/**", async (route) => {
    await route.fulfill({
      json: {
        id: "device:1",
        dtId: "urn:dev:1",
        name: "AHU-1",
        owner: "owner",
        site: "site",
        supplier: "supplier",
        gatewayId: "GW-SOS-001",
        deviceType: "airHandlingUnit",
      },
    });
  });
  await page.route("http://localhost:5000/points?**", async (route) => {
    await route.fulfill({
      json: [
        {
          id: "point:1",
          name: "室温",
          specification: "Temperature",
          type: "float",
          writable: false,
          targetArea: "Room 1",
        },
      ],
    });
  });
});

test("device detail renders metadata and supports keyboard point navigation", async ({ page }) => {
  await page.goto("/devices/urn%3Adev%3A1");

  await expect(page.getByRole("heading", { name: "AHU-1" })).toBeVisible();
  const pointLink = page.getByRole("link", { name: "室温" });
  await expect(pointLink).toHaveAttribute("href", "/points/point%3A1");

  await pointLink.focus();
  await expect(pointLink).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/points\/point%3A1$/);
});
