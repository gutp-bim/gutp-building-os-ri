# UI E2E (Playwright) — #159

Browser-level end-to-end tests for the web client. **Route-mock tier**: every API /
telemetry call is stubbed via `page.route()`, and login is injected, so these tests
need **no backend** (no OSS stack, no Keycloak). They run in CI (`ui-e2e-mock` job,
`workflow_dispatch`) and locally against `yarn dev`.

## Run

```bash
cd web-client
yarn test:e2e                 # starts `yarn dev` and runs non-@demo specs
yarn test:e2e:all             # runs every spec, including @demo
yarn test:e2e:demo            # drives an already-running demo stack
cd ..
make demo-e2e                 # starts the demo stack and runs @demo inside Docker
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

## E9 "Operator Usability" KPI axis (#159)

`e9-metrics.spec.ts` measures a11y (axe), time-to-sample, and keyboard/first-focus and
writes `{ axis: "E9_operator_usability", metrics }` to `E9_OUT` (default `e9-results/E9.json`),
which `e2e/runner/gate.py` scores against `e2e/kpi-thresholds.yaml`. It is wired into the
evaluation runner (`bash e2e/runner/run-axis.sh E9 --out <dir>`; also in `run-all.sh`).
See `e2e/scenarios/E9-operator-usability.md`.

## Full-stack demo tier

The demo tier (`make demo-e2e`, or `yarn test:e2e:demo` against an already-running
stack) uses real Keycloak login, real API calls, live demo telemetry, and the
simulated control binding configured by the demo overlay. It is intended for the
manual `workflow_dispatch` CI path, not push/PR triggers.

## Conventions

- Specs live **outside `src/`** (here) so Vitest's `src/**` include never picks them
  up. Vitest = component/unit; Playwright = browser E2E.
- Match routes to the **real** paths (e.g. `/point-details/` is hyphenated; telemetry
  reads all go through `/telemetries/query`).
