import { expect, type Page } from "@playwright/test";

export const DEMO_ADMIN_USER = process.env.E2E_DEMO_USER ?? "admin";
export const DEMO_ADMIN_PASSWORD = process.env.E2E_DEMO_PASSWORD ?? "admin";
export const DEMO_POINT_ID = process.env.E2E_DEMO_POINT_ID ?? "SOS-PT-004";

export async function loginWithKeycloak(page: Page): Promise<void> {
  await page.goto("/sign-in");
  await page.getByRole("button", { name: "Keycloakでサインイン" }).click();

  await page.getByRole("textbox", { name: /username|ユーザー名/i }).fill(DEMO_ADMIN_USER);
  await page.getByRole("textbox", { name: /password|パスワード/i }).fill(DEMO_ADMIN_PASSWORD);
  await page.getByRole("button", { name: /sign in|ログイン/i }).click();

  await expect(page).toHaveURL(/\/buildings|\/resources|\/points\//, { timeout: 30_000 });
  await page.evaluate(() => {
    localStorage.setItem("buildingos.onboarding.completed.v1", "1");
  });
}

export async function openDemoPoint(page: Page, pointId = DEMO_POINT_ID): Promise<void> {
  await page.goto(`/points/${encodeURIComponent(pointId)}`);
  await expect(page.getByText(pointId)).toBeVisible({ timeout: 30_000 });
}
