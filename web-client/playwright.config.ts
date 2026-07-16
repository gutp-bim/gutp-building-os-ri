import { defineConfig, devices } from "@playwright/test";

/**
 * Playwright E2E (#159). Specs live in `web-client/e2e/` — deliberately OUTSIDE
 * `src/` so Vitest's `src/**` include does not pick them up (they use different
 * runners/globals).
 *
 * Two run modes:
 *  - Default (this repo's primary gate): route-mock UI flows that need NO backend.
 *    The Next.js dev server is started by `webServer` below; all API/telemetry
 *    calls are stubbed per-test via `page.route()`. Chromium is the preinstalled
 *    browser (chromium-1194, matches @playwright/test 1.61).
 *  - Full-stack (CI `workflow_dispatch`, against `make demo`): set
 *    `E2E_BASE_URL=http://web.localhost:3000` and `E2E_NO_SERVER=1` so Playwright
 *    drives the already-running demo stack instead of spawning `yarn dev`.
 */
const BASE_URL = process.env.E2E_BASE_URL ?? "http://localhost:3000";
const CHROMIUM_HOST_RESOLVER_RULES =
  process.env.E2E_CHROMIUM_HOST_RESOLVER_RULES?.trim();

// Fixed OIDC identity used to (a) build the dev server's public env and (b) derive
// the oidc-client-ts localStorage key when injecting a logged-in storageState.
export const E2E_KEYCLOAK_AUTHORITY =
  process.env.NEXT_PUBLIC_KEYCLOAK_AUTHORITY ?? "http://localhost:8080/realms/building-os";
export const E2E_KEYCLOAK_CLIENT_ID =
  process.env.NEXT_PUBLIC_KEYCLOAK_CLIENT_ID ?? "web-client";
export const E2E_API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5000";

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: BASE_URL,
    trace: "on-first-retry",
  },
  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"],
        // CI installs the version-matched browser via `npx playwright install chromium`
        // (leave unset). In environments with a preinstalled browser whose revision
        // differs from this @playwright/test pin, point at it via this env var.
        launchOptions: {
          ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE
            ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE }
            : {}),
          ...(CHROMIUM_HOST_RESOLVER_RULES
            ? {
                args: [`--host-resolver-rules=${CHROMIUM_HOST_RESOLVER_RULES}`],
              }
            : {}),
        },
      },
    },
  ],
  webServer:
    process.env.E2E_NO_SERVER || process.env.E2E_BASE_URL
      ? undefined
      : {
          command: "yarn dev",
          url: BASE_URL,
          reuseExistingServer: !process.env.CI,
          timeout: 180_000,
          env: {
            NEXT_PUBLIC_KEYCLOAK_AUTHORITY: E2E_KEYCLOAK_AUTHORITY,
            NEXT_PUBLIC_KEYCLOAK_CLIENT_ID: E2E_KEYCLOAK_CLIENT_ID,
            NEXT_PUBLIC_API_BASE_URL: E2E_API_BASE_URL,
          },
        },
});
