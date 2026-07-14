#!/usr/bin/env bash
# doctor.sh — self-diagnose a running OSS stack and, on every failure, print an actionable fix hint
# (issue #157). Unlike `make wait-oss-stack` (waits) and `scripts/test-oss-stack.sh` (pass/fail), this
# is aimed at the "it won't come up, now what?" moment: each check that fails tells you the next step.
#
# Endpoints/ports are the same ones scripts/test-oss-stack.sh and Makefile `wait-oss-stack` already
# use against the real stack; the value added here is the remediation hints + a twin-seed count.
#
# Usage: make doctor   (or)   bash scripts/doctor.sh
# Exit code: 0 when no check failed, 1 otherwise (skipped profile services do not count as failures).

set -uo pipefail

OK=0
FAIL=0
SKIP=0
FAILED_NAMES=()

emit_ok() {
  echo "  ✓ $1"
  OK=$((OK + 1))
}

emit_fail() {
  # $1 = check name, $2 = actionable fix hint
  echo "  ✗ $1"
  echo "    → fix: $2"
  FAIL=$((FAIL + 1))
  FAILED_NAMES+=("$1")
}

emit_skip() {
  # $1 = check name, $2 = reason
  echo "  − $1 (skip: $2)"
  SKIP=$((SKIP + 1))
}

# ── low-level probes (mockable) ───────────────────────────────────────────────
probe_http() { curl -sf "$1" >/dev/null 2>&1; }
probe_tcp() { nc -z "$1" "$2" >/dev/null 2>&1; }

container_running() {
  docker ps --filter "name=^${1}\$" --filter "status=running" --format '{{.Names}}' 2>/dev/null |
    grep -qx "$1"
}

# check_http NAME URL FIX
check_http() {
  if probe_http "$2"; then emit_ok "$1"; else emit_fail "$1" "$3"; fi
}

# check_tcp NAME HOST PORT FIX
check_tcp() {
  if probe_tcp "$2" "$3"; then emit_ok "$1"; else emit_fail "$1" "$4"; fi
}

COMPOSE="docker compose -f docker-compose.oss.yaml"

# Count triples in the OxiGraph default graph; echoes the count (0 on any error).
twin_triple_count() {
  local body
  body=$(curl -sf -X POST http://localhost:7878/query \
    -H 'Content-Type: application/sparql-query' \
    -H 'Accept: application/sparql-results+json' \
    -d 'SELECT (COUNT(*) AS ?c) WHERE { ?s ?p ?o }' 2>/dev/null) || {
    echo 0
    return 1
  }
  printf '%s' "$body" | python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
    print(d["results"]["bindings"][0]["c"]["value"])
except Exception:
    print(0)
'
}

main() {
  echo "── Building OS doctor ──"
  echo "(diagnosing a running OSS stack — start it with \`make local-up-oss\` first)"
  echo

  # ── Prerequisites ───────────────────────────────────────────────────────────
  echo "[Prerequisites]"
  local docker_ok=1
  if command -v docker >/dev/null 2>&1; then
    emit_ok "Docker CLI available"
  else
    docker_ok=0
    emit_fail "Docker CLI available" \
      "Docker is not on PATH. Install Docker Desktop / Engine and ensure \`docker\` runs."
  fi
  for tool in curl python3; do
    if command -v "$tool" >/dev/null 2>&1; then
      emit_ok "$tool available"
    else
      emit_fail "$tool available" "Install $tool — the health checks depend on it."
    fi
  done

  # ── NATS JetStream ────────────────────────────────────────────────────────────
  echo
  echo "[NATS JetStream]"
  check_http "NATS management API (:8222)" "http://localhost:8222/varz" \
    "NATS is not answering on :8222. Check \`$COMPOSE logs building-os.nats\` and that port 4222/8222 are free."
  check_tcp "NATS client port (:4222)" localhost 4222 \
    "The client port is closed. If 4222 is taken by another process, stop it or remap the port."

  # ── PostgreSQL ────────────────────────────────────────────────────────────────
  echo
  echo "[PostgreSQL]"
  check_tcp "PostgreSQL port (:5433)" localhost 5433 \
    "Postgres is not listening on :5433. Check \`$COMPOSE logs building-os.postgres\`."
  if [[ "$docker_ok" -eq 1 ]]; then
    if docker exec building-os.postgres pg_isready -U buildingos -d buildingos >/dev/null 2>&1; then
      emit_ok "PostgreSQL pg_isready"
    else
      emit_fail "PostgreSQL pg_isready" \
        "The DB is up but not ready. Wait a few seconds, or inspect \`$COMPOSE logs building-os.postgres\`."
    fi
  else
    emit_skip "PostgreSQL pg_isready" "docker CLI unavailable"
  fi

  # ── OxiGraph (digital twin) ──────────────────────────────────────────────────
  echo
  echo "[OxiGraph / Digital Twin]"
  check_http "OxiGraph HTTP (:7878)" "http://localhost:7878/" \
    "OxiGraph is not answering on :7878. Check \`$COMPOSE logs building-os.oxigraph\`."
  local triples
  triples=$(twin_triple_count)
  if [[ "${triples:-0}" -gt 0 ]]; then
    emit_ok "Digital twin seeded (${triples} triples)"
  else
    emit_fail "Digital twin seeded" \
      "The twin is empty — every /resources & /points screen will look blank. The API seeds it from OXIGRAPH_SEED_TTL_PATH on startup; confirm that env points at a mounted twin.ttl (default /fixtures/e2e/twin.ttl) and re-create the API container."
  fi

  # ── MinIO (Parquet lake) ──────────────────────────────────────────────────────
  echo
  echo "[MinIO]"
  check_http "MinIO health (:9000)" "http://localhost:9000/minio/health/live" \
    "MinIO is not healthy on :9000. Check \`$COMPOSE logs building-os.minio\`. The API fail-fasts without MINIO_ENDPOINT in parquet mode."

  # ── Keycloak ──────────────────────────────────────────────────────────────────
  echo
  echo "[Keycloak]"
  check_http "Keycloak health (:8180)" "http://localhost:8180/health/ready" \
    "Keycloak has not finished starting (it is slow). Wait, then re-run; check \`$COMPOSE logs building-os.keycloak\`."
  check_http "Keycloak master realm (:8080)" "http://localhost:8080/realms/master" \
    "The realm endpoint is unreachable. A JWT issuer mismatch here surfaces later as Web Client login failures."

  # ── API server ────────────────────────────────────────────────────────────────
  echo
  echo "[API server]"
  check_http "API health (:5000)" "http://localhost:5000/health" \
    "The API is not responding. It commonly crash-loops on a bad migration or a missing MINIO_ENDPOINT — check \`$COMPOSE logs building-os.api\`."

  # ── ConnectorWorker ───────────────────────────────────────────────────────────
  echo
  echo "[ConnectorWorker]"
  check_http "ConnectorWorker readiness (:8081)" "http://localhost:8081/health/ready" \
    "Readiness is false = the worker cannot reach NATS. Confirm NATS is healthy above and NATS_URL is correct; check \`$COMPOSE logs building-os.connector-worker\`."

  # ── Profile-gated services (only when actually running) ───────────────────────
  echo
  echo "[Optional profiles]"
  if [[ "$docker_ok" -eq 1 ]] && container_running "building-os.prometheus"; then
    check_http "Prometheus (:9090)" "http://localhost:9090/-/healthy" \
      "Prometheus is running but not healthy — check \`$COMPOSE logs building-os.prometheus\`."
  else
    emit_skip "Prometheus" "not running — needs --profile observability"
  fi
  if [[ "$docker_ok" -eq 1 ]] && container_running "building-os.mosquitto"; then
    check_tcp "Mosquitto MQTT (:1883)" localhost 1883 \
      "Mosquitto is running but :1883 is closed — check \`$COMPOSE logs building-os.mosquitto\`."
  else
    emit_skip "Mosquitto MQTT" "not running — needs --profile mqtt"
  fi

  # ── Summary ────────────────────────────────────────────────────────────────────
  echo
  echo "────────────────────────────────────────"
  echo "Result: ${OK} ok, ${FAIL} failed, ${SKIP} skip"
  if [[ "$FAIL" -gt 0 ]]; then
    echo "Failed: ${FAILED_NAMES[*]}"
    echo "────────────────────────────────────────"
    return 1
  fi
  echo "All core services healthy."
  echo "────────────────────────────────────────"
  return 0
}

# Only auto-run when executed directly, so tests can source the functions in isolation.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  main "$@"
fi
