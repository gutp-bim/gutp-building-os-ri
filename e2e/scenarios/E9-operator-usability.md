# E9 — Operator Usability (#159)

Measures whether an operator can actually *use* the UI, complementing the backend axes
(E1–E8). Unlike those, E9 needs **no OSS stack** — it runs the web client's Playwright
route-mock harness (`web-client/e2e/`), where login is injected and all API/telemetry
calls are stubbed.

## How it runs

```bash
# via the evaluation runner (docker-free axis)
bash e2e/runner/run-axis.sh E9 --out e2e/results/<run-id>
#   → cd web-client && E9_OUT=<out>/E9.json yarn test:e2e e9-metrics

# or directly
cd web-client && E9_OUT=/tmp/E9.json yarn test:e2e e9-metrics
```

The `e9-metrics` spec measures the metrics below and writes
`{ "axis": "E9_operator_usability", "metrics": {...} }` to `E9_OUT`.
`e2e/runner/gate.py` scores that JSON against `e2e/kpi-thresholds.yaml` (missing metrics
are SKIP, so partial runs still gate). E9 is included in `run-all.sh`'s default axis set.

Browser: CI installs it with `npx playwright install chromium`; a preinstalled browser is
selected with `PLAYWRIGHT_CHROMIUM_EXECUTABLE` (see `web-client/playwright.config.ts`).

## Metrics

| metric | gate | meaning |
|---|---|---|
| `axe_critical_violations` | `== 0` | axe-core WCAG2 A/AA critical violations on `/resources` |
| `axe_serious_violations` | `<= 0` | axe-core WCAG2 A/AA serious violations |
| `time_to_sample_seconds` | `<= 5` | point page load → latest value (freshness badge) visible |
| `login_to_point_clicks` | report | clicks from logged-in landing to a point's value (route-mock deep-link baseline = 0) |
| `keyboard_first_focusable` | report | `Tab` lands on an interactive element (1/0) |

## Scope

Route-mock only. The realistic **navigation click count** and **task-completion timing**
against a real backend (Keycloak login, live telemetry, control round-trip) are part of the
full-stack UI E2E — a separate `make demo`-driven job — and would extend this axis with
real-login variants of the same metrics.
