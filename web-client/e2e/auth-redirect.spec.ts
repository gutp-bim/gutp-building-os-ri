import { expect, test } from "@playwright/test";
import { loginAs } from "./support/auth";
import { BUILDING, mockBuildings } from "./support/mock";

// Auth gate (middleware): no token → protected routes bounce to /sign-in;
// with a token → they render.
test("unauthenticated access to a protected route redirects to sign-in", async ({ page }) => {
  await page.goto("/resources");
  await expect(page).toHaveURL(/\/sign-in/);
});

test("authenticated access to a protected route is allowed", async ({ page, context }) => {
  await loginAs(context, "admin");
  await mockBuildings(page);
  await page.goto("/resources");
  await expect(page).not.toHaveURL(/\/sign-in/);
  await expect(page.getByRole("button", { name: BUILDING.name }).first()).toBeVisible();
});
