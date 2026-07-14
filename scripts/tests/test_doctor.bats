#!/usr/bin/env bats
# Tests for scripts/doctor.sh (issue #157 — `make doctor`).
# Requires: bats-core (https://bats-core.readthedocs.io/).
# Run: bats scripts/tests/test_doctor.bats
#
# The individual health probes (curl/nc/docker) are mocked on PATH so the check *logic* — dispatch,
# pass/fail tallying, actionable fix hints, exit code, profile skipping, seed-count parsing — is
# verified without a live stack. The endpoints/ports themselves are copied verbatim from the
# stack-proven scripts/test-oss-stack.sh + Makefile `wait-oss-stack`.

SCRIPT="$(git rev-parse --show-toplevel)/scripts/doctor.sh"

setup() {
  export BATS_TMPDIR="${BATS_TMPDIR:-/tmp}/bats-doctor-$$"
  mkdir -p "$BATS_TMPDIR/bin"
  export PATH="$BATS_TMPDIR/bin:$PATH"

  # Mock curl: fails for any URL containing a FAIL_URLS substring; the OxiGraph SPARQL /query
  # endpoint echoes a sparql-results JSON whose count comes from SEED_COUNT (default nonzero).
  cat > "$BATS_TMPDIR/bin/curl" <<'EOF'
#!/usr/bin/env bash
args="$*"
if [[ "$args" == *"/query"* ]]; then
  for f in ${FAIL_URLS:-}; do [[ "$args" == *"$f"* ]] && exit 1; done
  echo "{\"results\":{\"bindings\":[{\"c\":{\"value\":\"${SEED_COUNT:-1234}\"}}]}}"
  exit 0
fi
for f in ${FAIL_URLS:-}; do [[ "$args" == *"$f"* ]] && exit 1; done
exit 0
EOF

  # Mock nc: `nc -z host port` fails when the trailing port is in FAIL_PORTS.
  cat > "$BATS_TMPDIR/bin/nc" <<'EOF'
#!/usr/bin/env bash
port="${@: -1}"
for f in ${FAIL_PORTS:-}; do [[ "$port" == "$f" ]] && exit 1; done
exit 0
EOF

  # Mock docker: `docker ps` lists RUNNING_CONTAINERS (for profile gating); `docker exec` succeeds
  # unless DOCKER_EXEC_FAIL is set; everything else (info/version) succeeds.
  cat > "$BATS_TMPDIR/bin/docker" <<'EOF'
#!/usr/bin/env bash
case "$1" in
  ps)   for c in ${RUNNING_CONTAINERS:-}; do echo "$c"; done; exit 0 ;;
  exec) [[ -n "${DOCKER_EXEC_FAIL:-}" ]] && exit 1; exit 0 ;;
  *)    exit 0 ;;
esac
EOF

  chmod +x "$BATS_TMPDIR/bin/"*
}

teardown() {
  rm -rf "$BATS_TMPDIR"
}

@test "healthy stack: exits 0 and every core check passes" {
  run bash "$SCRIPT"
  [ "$status" -eq 0 ]
  [[ "$output" == *"✓"* ]]
  [[ "$output" != *"✗"* ]]
}

@test "NATS down: exits 1, marks NATS failed, prints an actionable fix hint" {
  FAIL_URLS="8222" run bash "$SCRIPT"
  [ "$status" -eq 1 ]
  [[ "$output" == *"✗"* ]]
  [[ "$output" == *"NATS"* ]]
  # a failed check must carry a remediation line
  [[ "$output" == *"→"* ]]
}

@test "API crash-loop: fix hint points at the api container logs" {
  FAIL_URLS="localhost:5000" run bash "$SCRIPT"
  [ "$status" -eq 1 ]
  [[ "$output" == *"building-os.api"* ]]
}

@test "twin not seeded (triple count 0): fails with an OXIGRAPH_SEED hint" {
  SEED_COUNT=0 run bash "$SCRIPT"
  [ "$status" -eq 1 ]
  [[ "$output" == *"OXIGRAPH_SEED"* ]]
}

@test "twin seeded (nonzero triple count): the seed check passes" {
  SEED_COUNT=4321 run bash "$SCRIPT"
  [ "$status" -eq 0 ]
  # the seed line reports the observed count
  [[ "$output" == *"4321"* ]]
}

@test "profile service not running (Prometheus) is skipped, not failed" {
  # RUNNING_CONTAINERS empty → no profile services running
  run bash "$SCRIPT"
  [ "$status" -eq 0 ]
  [[ "$output" == *"Prometheus"* ]]
  [[ "$output" == *"skip"* ]]
}

@test "docker binary missing: reports the prerequisite as failed with an install hint" {
  rm -f "$BATS_TMPDIR/bin/docker"
  run bash "$SCRIPT"
  [ "$status" -eq 1 ]
  [[ "$output" == *"Docker"* ]]
}

@test "prints a summary line with ok/failed/skip tallies" {
  run bash "$SCRIPT"
  [[ "$output" == *"ok"* ]]
  [[ "$output" == *"skip"* ]]
}
