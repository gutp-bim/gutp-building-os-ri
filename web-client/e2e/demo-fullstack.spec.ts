import { expect, test } from "@playwright/test";
import { loginWithKeycloak, openDemoPoint } from "./support/demo";

test.describe("demo full-stack UI E2E @demo", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test("operator can sign in and see a live demo point sample", async ({ page }) => {
    await loginWithKeycloak(page);
    await openDemoPoint(page);

    await expect(page.getByTestId("freshness-fresh")).toBeVisible({ timeout: 60_000 });
    await expect(page.getByRole("button", { name: "制御信号を送信" })).toBeVisible();
  });

  test("operator can execute demo control and see the success result", async ({ page }) => {
    await loginWithKeycloak(page);
    await openDemoPoint(page);

    await page.getByRole("button", { name: "制御信号を送信" }).click();
    await page.getByLabel("ON").check();
    await page.getByRole("button", { name: "送信" }).click();

    await expect(page.getByText("制御が正常に完了しました")).toBeVisible({ timeout: 30_000 });
  });
});
