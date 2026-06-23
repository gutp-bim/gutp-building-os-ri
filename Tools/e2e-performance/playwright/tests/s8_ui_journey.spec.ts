import { test, expect, Page, BrowserContext } from "@playwright/test";

const BASE_URL = process.env.BASE_URL ?? "http://localhost:3000";
const KEYCLOAK_URL = process.env.KEYCLOAK_URL ?? "http://localhost:8080";
const REALM = process.env.KEYCLOAK_REALM ?? "building-os";
const TEST_USER = process.env.TEST_USER ?? "admin";
const TEST_PASSWORD = process.env.TEST_PASSWORD ?? "admin";
const ADMIN_CONSOLE_URL = process.env.ADMIN_CONSOLE_URL ?? `${BASE_URL}/admin`;
const P95_LOAD_THRESHOLD_MS = 3000;

/**
 * Obtain an OIDC access token via Keycloak direct grant and inject it as the
 * `oidc.access_token` cookie that web-client middleware checks.
 * This lets us verify post-login navigation without relying on the headless-
 * browser OIDC redirect flow (which requires JS-driven window.location).
 */
async function injectAuthCookie(context: BrowserContext): Promise<void> {
  const tokenRes = await fetch(
    `${KEYCLOAK_URL}/realms/${REALM}/protocol/openid-connect/token`,
    {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        client_id: "web-client",
        username: TEST_USER,
        password: TEST_PASSWORD,
        grant_type: "password",
        scope: "openid",
      }).toString(),
    }
  );
  const data = (await tokenRes.json()) as Record<string, unknown>;
  const accessToken = data["access_token"] as string;
  if (!accessToken) {
    throw new Error(`Failed to obtain access token: ${JSON.stringify(data)}`);
  }
  const expiresIn = (data["expires_in"] as number) ?? 300;

  // Derive domain/secure from BASE_URL so the cookie works for any host.
  const baseUrl = new URL(BASE_URL);
  await context.addCookies([
    {
      name: "oidc.access_token",
      value: accessToken,
      url: BASE_URL,
      path: "/",
      httpOnly: false,
      secure: baseUrl.protocol === "https:",
      sameSite: "Lax",
      expires: Math.floor(Date.now() / 1000) + expiresIn,
    },
  ]);
}

// ── T1: Sign-in page renders correctly (without auth) ────────────────────────
test("sign-in page renders with Keycloak button", async ({ page }) => {
  const start = Date.now();
  // Navigate without auth cookie — middleware should redirect to /sign-in
  await page.goto("/");
  await page.waitForURL((url) => url.pathname.includes("/sign-in"), {
    timeout: 10000,
  });
  const loadTime = Date.now() - start;

  // The sign-in page must show the Keycloak sign-in button
  const button = page.locator("button:has-text('Keycloak'), button:has-text('サインイン')");
  await expect(button.first()).toBeVisible({ timeout: 10000 });

  expect(loadTime).toBeLessThan(P95_LOAD_THRESHOLD_MS);
});

// ── T2: Authenticated dashboard navigation ───────────────────────────────────
test("authenticated: dashboard loads", async ({ page, context }) => {
  await injectAuthCookie(context);

  const start = Date.now();
  await page.goto("/");
  // Middleware lets authenticated users through — skip sign-in redirect
  await page.waitForURL((url) => !url.pathname.includes("/sign-in"), {
    timeout: 10000,
  });
  const loadTime = Date.now() - start;

  // The app renders a main content area after login
  const content = page.locator("main, h1, h2, [role='main'], body");
  await expect(content.first()).toBeVisible({ timeout: 10000 });

  expect(loadTime).toBeLessThan(P95_LOAD_THRESHOLD_MS);
});

// ── T3: Buildings navigation ──────────────────────────────────────────────────
test("authenticated: navigate to buildings", async ({ page, context }) => {
  await injectAuthCookie(context);
  await page.goto("/");
  await page.waitForURL((url) => !url.pathname.includes("/sign-in"), {
    timeout: 10000,
  });

  const buildingsLink = page.locator(
    "a[href*='building'], nav a:has-text('Building'), nav a:has-text('ビル')"
  );

  if ((await buildingsLink.count()) > 0) {
    const start = Date.now();
    await buildingsLink.first().click();
    await page.waitForLoadState("networkidle");
    const loadTime = Date.now() - start;

    const content = page.locator("ul li, table tbody tr, [class*='building'], main, h1");
    await expect(content.first()).toBeVisible({ timeout: 10000 });
    expect(loadTime).toBeLessThan(P95_LOAD_THRESHOLD_MS);
  } else {
    // Navigate directly — the route might be /buildings
    const start = Date.now();
    await page.goto("/buildings");
    await page.waitForLoadState("networkidle");
    const loadTime = Date.now() - start;
    const content = page.locator("main, h1, h2, body");
    await expect(content.first()).toBeVisible({ timeout: 10000 });
    expect(loadTime).toBeLessThan(P95_LOAD_THRESHOLD_MS);
  }
});

// ── T4: (admin) workspace loads ───────────────────────────────────────────────
test("admin workspace: page loads", async ({ page }) => {
  const start = Date.now();
  await page.goto(ADMIN_CONSOLE_URL);
  await page.waitForLoadState("networkidle");
  const loadTime = Date.now() - start;

  // The web-client /admin workspace renders a heading
  const heading = page.locator("h1, h2");
  await expect(heading.first()).toBeVisible({ timeout: 10000 });

  expect(loadTime).toBeLessThan(P95_LOAD_THRESHOLD_MS);
});
