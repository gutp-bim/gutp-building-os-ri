# UI E2E (Playwright) — #159

Browser-level end-to-end tests for the web client. **Route-mock tier**: every API /
telemetry call is stubbed via `page.route()`, and login is injected, so these tests
need **no backend** (no OSS stack, no Keycloak). They run in CI (`ui-e2e-mock` job,
`workflow_dispatch`) and locally against `yarn dev`.

## Run

```bash
cd web-client
yarn test:e2e                 # starts `yarn dev` and runs the specs
```

CI installs the version-matched browser with `npx playwright install chromium`. If
you have a preinstalled Chromium whose revision differs from the `@playwright/test`
pin, point at it:

```bash
PLAYWRIGHT_CHROMIUM_EXECUTABLE=/path/to/chrome yarn test:e2e
```

## How login works (no Keycloak)

The app never verifies the access-token signature — `middleware.ts` only checks the
`oidc.access_token` cookie exists, and `claims.ts` base64-decodes the payload. So
`support/auth.ts` mints an unsigned JWT and injects both the cookie and the
oidc-client-ts user in `localStorage`, plus suppresses the first-login onboarding
tour. Use `loginAs(context, "admin" | "operator" | "viewer")` in `beforeEach`.

## What's covered here (route-mock)

- resource explorer: tree renders + a single building auto-expands its floors
- point detail: fresh / stale freshness badge, and load-error feedback
- auth gate: unauthenticated → `/sign-in`; authenticated → allowed
- accessibility: axe-core WCAG 2 A/AA on `/resources` (no critical/serious)

## Out of scope (full-stack, follow-up)

Scenarios that need a real backend — actual Keycloak login, control **success**
(gRPC-web streaming result), live telemetry values, `/platform/status` health
fan-out — belong to the full-stack UI E2E driven by `make demo`, a separate
`workflow_dispatch` job. The E9 "Operator Usability" KPI axis
(`e2e/kpi-thresholds.yaml`) is a follow-up that reuses this harness to emit
click-count / time-to-sample / axe metrics.

## Conventions

- Specs live **outside `src/`** (here) so Vitest's `src/**` include never picks them
  up. Vitest = component/unit; Playwright = browser E2E.
- Match routes to the **real** paths (e.g. `/point-details/` is hyphenated; telemetry
  reads all go through `/telemetries/query`).
