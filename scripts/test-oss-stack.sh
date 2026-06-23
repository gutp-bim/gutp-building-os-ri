#!/usr/bin/env bash
# TDD health-check: all OSS stack services must be reachable.
# Run BEFORE docker-compose.oss.yaml exists → RED.
# Run AFTER  docker-compose.oss.yaml + make local-up-oss → GREEN.

set -euo pipefail

PASS=0
FAIL=0
ERRORS=()

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

# ── PostgreSQL + TimescaleDB ─────────────────────────────────────────────────
echo
echo "[PostgreSQL + TimescaleDB]"
check "TCP port 5433"        "nc -z localhost 5433"
check "pg_isready"           "docker exec building-os.postgres pg_isready -U buildingos -d buildingos"
check "TimescaleDB extension" \
  "docker exec building-os.postgres psql -U buildingos -d buildingos -tAc \
   \"SELECT extname FROM pg_extension WHERE extname='timescaledb'\" | grep -q timescaledb"

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

# ── Prometheus ────────────────────────────────────────────────────────────────
echo
echo "[Prometheus]"
check "healthy (9090)"   "curl -sf http://localhost:9090/-/healthy"
check "ready  (9090)"    "curl -sf http://localhost:9090/-/ready"

# ── Grafana ───────────────────────────────────────────────────────────────────
echo
echo "[Grafana]"
check "health (3010)"    "curl -sf http://localhost:3010/api/health"

# ── Loki ──────────────────────────────────────────────────────────────────────
echo
echo "[Loki]"
check "ready (3100)"     "curl -sf http://localhost:3100/ready"

# ── Tempo ─────────────────────────────────────────────────────────────────────
echo
echo "[Tempo]"
check "ready (3200)"         "curl -sf http://localhost:3200/ready"
check "OTLP gRPC port (4317)" "nc -z localhost 4317"
check "OTLP HTTP port (4318)" "nc -z localhost 4318"

# ── Mosquitto (MQTT) ──────────────────────────────────────────────────────────
echo
echo "[Mosquitto MQTT]"
check "MQTT port (1883)"  "nc -z localhost 1883"

# ── Summary ──────────────────────────────────────────────────────────────────
echo
echo "========================================"
echo "  PASS: $PASS   FAIL: $FAIL"
if (( FAIL > 0 )); then
  echo "  Failed checks:"
  for e in "${ERRORS[@]}"; do echo "    - $e"; done
  echo "========================================"
  exit 1
fi
echo "  All checks passed!"
echo "========================================"
