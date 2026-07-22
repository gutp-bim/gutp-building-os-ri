# Building OS E2E Performance & Quality Tests

End-to-end performance tests and data quality validation for the Building OS OSS stack.

## Prerequisites

- [uv](https://docs.astral.sh/uv/) — Python package manager (`pip install uv` or `curl -LsSf https://astral.sh/uv/install.sh | sh`)
- Python 3.11+
- k6 (https://k6.io/docs/get-started/installation/)
- Node.js 22+ with npm or yarn
- Playwright CLI (`npx playwright install`)
- OSS stack running via `docker compose -f docker-compose.oss.yaml up -d`
- Docker (for schema apply step in `smoke.sh`)

## One-time setup

**Python venv** — `smoke.sh` creates it automatically, but you can also do it manually:

```bash
cd Tools/e2e-performance
uv venv .venv
uv pip install -r requirements.txt --python .venv/bin/python
```

**TimescaleDB schema** — applied automatically by `smoke.sh` on each run (idempotent).
To apply manually:

```bash
docker exec building-os.postgres psql -U buildingos -d buildingos \
  -f /dev/stdin < oss-stack/postgres/init.sql
```

**Playwright browser dependencies:**

```bash
cd Tools/e2e-performance/playwright
npm install
npx playwright install chromium
```

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `MQTT_HOST` | `localhost` | Mosquitto host |
| `MQTT_PORT` | `1883` | Mosquitto port |
| `NATS_URL` | `nats://localhost:4222` | NATS server URL |
| `TIMESCALE_DSN` | `postgresql://buildingos:buildingos@localhost:5433/buildingos` | TimescaleDB DSN |
| `API_BASE_URL` | `http://localhost:5000` | API Server base URL |

For the API Server, set `TIMESCALE_CONNECTION_STRING` to the same DSN and `DISABLE_AUTH=true`
to skip Keycloak during local E2E testing.

## Ingest pipeline

The smoke evaluation uses the production ingestion boundary:
**Mosquitto → ConnectorWorker (`MqttIngressWorker` + `MqttConnectorWorker`) → NATS JetStream**.
For the Timescale profile, `telemetry_consumer.py` persists the validated stream.

Start the stack with MQTT ingress and the Timescale path enabled before running `smoke.sh`:

```bash
MQTT_HOST=building-os.mosquitto WARM_STORE=timescale \
  docker compose -f docker-compose.oss.yaml --profile mqtt --profile timescale up -d
```

## Running Tests

### S17 — Multi-building 2k–50k Point sweep

Deterministically distributes each scale across buildings and gateways, then measures Point List,
gRPC ingress accepted/rejected counts, Parquet loss, and lake visibility time. The first failed stage
is returned as a 1-based exit code. See the consolidated results in
[`docs/performance-evaluation-report.md`](../../docs/performance-evaluation-report.md).

```bash
PARQUET_FLUSH_INTERVAL=1 docker compose -f docker-compose.oss.yaml up -d --build
Tools/e2e-performance/.venv/bin/python \
  Tools/e2e-performance/s17_multibuilding_scale_sweep.py \
  --run-id "$(date -u +%Y%m%dT%H%M%SZ)-s17" --continue-on-failure
```

### S1 — Smoke E2E (quick CI check)

Verifies ConnectorWorker MQTT ingress, starts the optional Timescale consumer, runs
the load generator (small/baseline/120s), waits for writes, then validates data quality.

```bash
# From repository root
bash Tools/e2e-performance/smoke.sh

# With explicit run ID
bash Tools/e2e-performance/smoke.sh 20240101T120000Z-smoke-manual
```

### S2 — Baseline Throughput

```bash
# Quick CI mode (small scale, 5 min)
QUICK=true bash Tools/e2e-performance/s2_baseline.sh

# Full mode (medium scale, 1 hour) — requires NATS + TimescaleDB + Mosquitto running
bash Tools/e2e-performance/s2_baseline.sh
```

### S3 — Burst And Backpressure

```bash
# Quick CI mode (small scale, burst 2min + recovery 2min)
QUICK=true bash Tools/e2e-performance/s3_burst.sh

# Full mode (medium scale, burst 15min + recovery 15min)
bash Tools/e2e-performance/s3_burst.sh
```

### S4 — Data Size And Schema Quality

```bash
# Quick CI mode (small scale, 2 phases × 2min)
QUICK=true bash Tools/e2e-performance/s4_quality.sh

# Full mode (small scale, 2 phases × 30min)
bash Tools/e2e-performance/s4_quality.sh
```

All S2/S3/S4 scripts require `building-os.nats`, `building-os.postgres`, and `building-os.mosquitto` containers:

```bash
docker compose -f docker-compose.oss.yaml up -d \
  building-os.nats building-os.postgres building-os.pgbouncer building-os.mosquitto
```

### S7 — Resilience And Replay

```bash
# Quick CI mode
QUICK=true bash Tools/e2e-performance/s7_resilience.sh

# Full mode
bash Tools/e2e-performance/s7_resilience.sh
```

Tests:
- **Test A**: NATS JetStream durable consumer replay (deliver_all from position 0)
- **Test B**: TimescaleDB duplicate insert behavior (documents ON CONFLICT DO NOTHING without unique index)
- **Test C**: Bridge restart recovery — Phase 2 (post-restart) loss rate target ≤ 1%

### S5 — API Read Path Performance (k6)

```bash
k6 run Tools/e2e-performance/k6/s5_api_read.js

# Override defaults
BASE_URL=http://localhost:5000 VUS=20 DURATION=5m \
  k6 run Tools/e2e-performance/k6/s5_api_read.js
```

### S6 — Point Control E2E (k6)

```bash
CONTROL_POINT_ID=your-point-id \
  k6 run Tools/e2e-performance/k6/s6_point_control.js
```

### S9 — Warm Parquet Lake KPI (k6, #219)

Compares the warm/cold/aggregate/multi-point read p95 between `WARM_STORE=timescale` and `parquet`.
Run once per mode (same load) and diff; see [docs/oss-warm-parquet-kpi.md](../../docs/oss-warm-parquet-kpi.md).

```bash
BASE_URL=http://localhost:5000 MODE=parquet VUS=10 DURATION=10m \
  k6 run Tools/e2e-performance/k6/s9_warm_kpi.js
# Lake storage / object-count KPI:
bash Tools/e2e-performance/measure_lake_storage.sh
```

### S8 — UI Journey (Playwright)

```bash
# Automated: starts Keycloak if needed, web-client, admin-console, runs tests
bash Tools/e2e-performance/s8_ui.sh

# Manual (if servers are already running)
SKIP_START_SERVERS=true bash Tools/e2e-performance/s8_ui.sh

# Or run Playwright directly
cd Tools/e2e-performance/playwright
BASE_URL=http://localhost:3000 \
ADMIN_CONSOLE_URL=http://localhost:3001 \
KEYCLOAK_URL=http://localhost:8080 \
  npm test
```

Environment variables for S8:

| Variable | Default | Description |
|---|---|---|
| `BASE_URL` | `http://localhost:3000` | Web client URL |
| `ADMIN_CONSOLE_URL` | `http://localhost:3001` | Admin console URL |
| `KEYCLOAK_URL` | `http://localhost:8080` | Keycloak server |
| `KEYCLOAK_REALM` | `building-os` | Keycloak realm |
| `TEST_USER` | `admin` | Login username |
| `TEST_PASSWORD` | `admin` | Login password |

### Manual load generator and quality checker

```bash
# Generate load
python Tools/e2e-performance/device_load_generator.py \
  --scale medium --profile burst --duration 300 --run-id my-run-001

# Check quality
python Tools/e2e-performance/quality_checker.py \
  --run-id my-run-001 --expected 2500
```

## Scale and Profile Reference

| Scale | Devices | Points |
|---|---|---|
| small | 10 | 100 |
| medium | 250 | 2500 |
| large | 1000 | 10000 |
| stress | 5000 | 50000 |

| Profile | Interval | Points/msg |
|---|---|---|
| baseline | 60s | 5 |
| burst | 10s | 5 |
| wide | 60s | 75 |
| mixed | 30s | 25 |

## Results Directory Structure

```
Tools/e2e-performance/results/
└── <test_run_id>/
    ├── load-generator-result.json   # sent/error counts, timing
    └── quality-check-result.json   # DB/API counts, loss_rate, passed flag

Tools/e2e-performance/results/
├── playwright-report/               # Playwright HTML report
└── playwright-results.xml          # JUnit XML for CI
```

## Quality Check Pass Criteria

- Loss rate <= 1% (DB rows vs expected count)
- Duplicate rate <= 0.1%
- Schema invalid count = 0
- DB row count > 0
