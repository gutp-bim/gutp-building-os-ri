import { expect, test } from "@playwright/test";
import { loginAs } from "./support/auth";
import {
  BUILDING,
  mockBuildings,
  mockDevices,
  mockFloors,
  mockPoints,
  mockSpaces,
} from "./support/mock";

/** Stub the twin endpoints the operator home loads so `/home` renders without a backend. */
async function mockHomeTwin(page: import("@playwright/test").Page): Promise<void> {
  await mockBuildings(page);
  await mockFloors(page);
  await mockSpaces(page);
  await mockDevices(page);
  await mockPoints(page, []);
}

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

// Post-login landing (#178): the root route sends every authenticated role to the operator home.
for (const role of ["operator", "viewer", "admin"] as const) {
  test(`post-login root redirects ${role} to the operator home`, async ({ page, context }) => {
    await loginAs(context, role);
    await mockHomeTwin(page);
    await page.goto("/");
    await expect(page).toHaveURL(/\/home$/);
    await expect(page.getByTestId("operator-home")).toBeVisible();
  });
}
