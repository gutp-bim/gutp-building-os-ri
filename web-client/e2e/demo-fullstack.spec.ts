import { expect, test } from "@playwright/test";
import { loginWithKeycloak, openDemoPoint } from "./support/demo";

test.describe("demo full-stack UI E2E @demo", () => {
  test.describe.configure({ mode: "serial" });
  test.setTimeout(90_000);

  test("operator can sign in and see a live demo point sample", async ({
    page,
  }) => {
    await loginWithKeycloak(page);
    await openDemoPoint(page);

    await expect(page.getByTestId("freshness-fresh").first()).toBeVisible({
      timeout: 60_000,
    });
    await expect(
      page.getByRole("button", { name: "制御信号を送信" }),
    ).toBeVisible();
  });

  test("operator can execute demo control and see the success result", async ({
    page,
  }) => {
    await loginWithKeycloak(page);
    await openDemoPoint(page);

    await page.getByRole("button", { name: "制御信号を送信" }).click();
    await page.getByLabel("ON").check();
    await page.getByRole("button", { name: "送信" }).click();

    await expect(page.getByText("制御が正常に完了しました")).toBeVisible({
      timeout: 30_000,
    });
  });
});

// #458: `make demo` seeds the generated demo building (fixtures/demo/) and the feeder runs the
// `degraded` scenario by default — stale / missing / alarm must all be visible on /home from the
// start. Skipped when the stack was brought up with DEMO_SCENARIO=healthy.
test.describe("demo dataset degraded scenario @demo", () => {
  test.skip(
    (process.env.E2E_DEMO_SCENARIO ?? "degraded") !== "degraded",
    "demo stack runs DEMO_SCENARIO=healthy",
  );
  test.setTimeout(90_000);

  test("home shows stale, missing and alarm points for the demo building", async ({
    page,
  }) => {
    await loginWithKeycloak(page);
    await page.goto("/home");

    await page
      .getByTestId("home-building-select")
      .selectOption({ label: "東京デモビル" });
    await page
      .getByTestId("home-floor-select")
      .selectOption({ label: "すべてのフロア（建物全体）" });

    for (const card of ["summary-stale", "summary-missing", "summary-alarm"]) {
      await expect(
        page.getByTestId(card).locator("div").first(),
      ).not.toHaveText("0", {
        timeout: 60_000,
      });
    }

    await page.getByTestId("summary-missing").click();
    await expect(page).toHaveURL(/\/health\?freshness=missing/);
  });
});
