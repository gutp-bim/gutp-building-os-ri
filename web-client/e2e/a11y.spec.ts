import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { loginAs } from "./support/auth";
import { mockBuildings } from "./support/mock";

// Automated WCAG check (E9 axe axis). Fails the build on critical/serious
// violations — the tiers that block real operator tasks.
test("resources page has no critical or serious a11y violations", async ({ page, context }) => {
  await loginAs(context, "admin");
  await mockBuildings(page);
  await page.goto("/resources");
  await expect(page.getByRole("button", { name: "E2E Demo Building" }).first()).toBeVisible();

  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa"])
    .analyze();

  const blocking = results.violations.filter(
    (v) => v.impact === "critical" || v.impact === "serious",
  );
  expect(blocking, JSON.stringify(blocking.map((v) => ({ id: v.id, impact: v.impact })), null, 2)).toEqual([]);
});
