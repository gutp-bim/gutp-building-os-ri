#!/usr/bin/env bash
# TDD health-check: all OSS stack services must be reachable.
# Run BEFORE docker-compose.oss.yaml exists → RED.
# Run AFTER  docker-compose.oss.yaml + make local-up-oss → GREEN.

set -euo pipefail

PASS=0
FAIL=0
SKIP=0
ERRORS=()
SKIPPED=()

check() {
  local name="$1"
  local cmd="$2"
  if eval "$cmd" > /dev/null 2>&1; then
    echo "  [PASS] $name"
    PASS=$((PASS+1))
  else
    echo "  [FAIL] $name"
    FAIL=$((FAIL+1))
    ERRORS+=("$name")
  fi
}

# True if a container with this exact name is currently running. Used to gate
# checks for services that live behind a compose profile (observability/mqtt)
# and are not part of the default `make local-up-oss` stack — so a healthy
# default stack does not fail this script for services it never started.
container_running() {
  docker ps --filter "name=^${1}\$" --filter "status=running" --format '{{.Names}}' | grep -qx "$1"
}

skip() {
  local name="$1"
  local reason="$2"
  echo "  [SKIP] $name ($reason)"
  SKIP=$((SKIP+1))
  SKIPPED+=("$name — $reason")
}

wait_for() {
  local name="$1"
  local url="$2"
  local max="${3:-60}"
  local i=0
  while ! curl -sf "$url" > /dev/null 2>&1; do
    if (( i >= max )); then return 1; fi
    sleep 2; ((i+=2))
  done
}

echo "=== OSS Stack Health Check ==="
echo

# ── NATS JetStream ──────────────────────────────────────────────────────────
echo "[NATS JetStream]"
check "management HTTP (8222)"  "curl -sf http://localhost:8222/varz"
check "JetStream enabled"       "curl -sf http://localhost:8222/jsz | grep -q '\"config\"'"
check "client port (4222) open" "nc -z localhost 4222"

# ── PostgreSQL ────────────────────────────────────────────────────────────────
# `building-os.postgres` is always plain postgres:16 (#216/#234 dropped
# TimescaleDB from the default stack) — WARM_STORE=timescale brings your own
# external TimescaleDB via TIMESCALE_CONNECTION_STRING, it is never this
# container, so there is no in-stack TimescaleDB extension to check here.
echo
echo "[PostgreSQL]"
check "TCP port 5433"        "nc -z localhost 5433"
check "pg_isready"           "docker exec building-os.postgres pg_isready -U buildingos -d buildingos"

# ── OxiGraph ─────────────────────────────────────────────────────────────────
echo
echo "[OxiGraph]"
check "HTTP server (7878)" "curl -sf http://localhost:7878/"
check "SPARQL endpoint"    "curl -sf -X POST http://localhost:7878/query \
  -H 'Content-Type: application/sparql-query' \
  -d 'SELECT * WHERE { ?s ?p ?o } LIMIT 1' \
  -H 'Accept: application/sparql-results+json'"

# ── MinIO ─────────────────────────────────────────────────────────────────────
echo
echo "[MinIO]"
check "health endpoint (9000)" "curl -sf http://localhost:9000/minio/health/live"
check "console port (9001)"    "nc -z localhost 9001"

# ── Keycloak ──────────────────────────────────────────────────────────────────
echo
echo "[Keycloak]"
check "health endpoint (8180/management)" "curl -sf http://localhost:8180/health/ready | grep -qi 'UP\|healthy'"
check "realms endpoint (master)"          "curl -sf http://localhost:8080/realms/master | grep -q realm"

# ── Prometheus / Grafana / Loki / Tempo (--profile observability) ─────────────
# Not part of the default `make local-up-oss` stack (CLAUDE.md A-7, cost
# optimization) — only check these when the profile was actually started.
echo
echo "[Prometheus]"
if container_running "building-os.prometheus"; then
  check "healthy (9090)"   "curl -sf http://localhost:9090/-/healthy"
  check "ready  (9090)"    "curl -sf http://localhost:9090/-/ready"
else
  skip "Prometheus checks" "building-os.prometheus not running — needs --profile observability"
fi

echo
echo "[Grafana]"
if container_running "building-os.grafana"; then
  check "health (3010)"    "curl -sf http://localhost:3010/api/health"
else
  skip "Grafana health check" "building-os.grafana not running — needs --profile observability"
fi

echo
echo "[Loki]"
if container_running "building-os.loki"; then
  check "ready (3100)"     "curl -sf http://localhost:3100/ready"
else
  skip "Loki ready check" "building-os.loki not running — needs --profile observability"
fi

echo
echo "[Tempo]"
if container_running "building-os.tempo"; then
  check "ready (3200)"         "curl -sf http://localhost:3200/ready"
  check "OTLP gRPC port (4317)" "nc -z localhost 4317"
  check "OTLP HTTP port (4318)" "nc -z localhost 4318"
else
  skip "Tempo checks" "building-os.tempo not running — needs --profile observability"
fi

# ── Mosquitto (MQTT, --profile mqtt) ───────────────────────────────────────────
# Not part of the default stack either (#25) — Scenario A (Mosquitto) is opt-in.
echo
echo "[Mosquitto MQTT]"
if container_running "building-os.mosquitto"; then
  check "MQTT port (1883)"  "nc -z localhost 1883"
else
  skip "Mosquitto MQTT port check" "building-os.mosquitto not running — needs --profile mqtt"
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo
echo "========================================"
echo "  PASS: $PASS   FAIL: $FAIL   SKIP: $SKIP"
if (( SKIP > 0 )); then
  echo "  Skipped (profile not started):"
  for s in "${SKIPPED[@]}"; do echo "    - $s"; done
fi
if (( FAIL > 0 )); then
  echo "  Failed checks:"
  for e in "${ERRORS[@]}"; do echo "    - $e"; done
  echo "========================================"
  exit 1
fi
echo "  All checks passed!"
echo "========================================"
