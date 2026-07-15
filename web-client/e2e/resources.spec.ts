import { expect, test } from "@playwright/test";
import { loginAs } from "./support/auth";
import { BUILDING, FLOOR, mockBuildings, mockFloors } from "./support/mock";

// Resource explorer navigation with a route-mocked API (no backend).
test.beforeEach(async ({ context }) => {
  await loginAs(context, "admin");
});

test("resources tree renders a building from a mocked API", async ({ page }) => {
  await mockBuildings(page);
  await page.goto("/resources");
  await expect(
    page.getByRole("button", { name: BUILDING.name }).first(),
  ).toBeVisible();
});

test("a single building auto-expands and loads its floors", async ({ page }) => {
  await mockBuildings(page);
  await mockFloors(page);
  await page.goto("/resources");

  // With exactly one root building the tree auto-expands it (#135), loading floors.
  await expect(page.getByRole("button", { name: BUILDING.name }).first()).toBeVisible();
  await expect(page.getByText(FLOOR.name)).toBeVisible();
});
