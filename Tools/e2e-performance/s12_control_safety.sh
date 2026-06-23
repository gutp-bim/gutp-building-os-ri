#!/usr/bin/env bash
# E6 — Control path 安全性（残シナリオ, #244）. Runs s12_control_safety.py against the running stack:
# not-writable rejection / typed-failure classification / stale-replay = 0.
# (offline→503 is covered by backend unit tests + #186 — not reproducible on the local stack, so it is
#  not measured here; see the harness docstring.)
#
# Usage: bash s12_control_safety.sh [OUT_DIR]    (N default 30; BASE_URL/OXIGRAPH_URL/NATS_URL override)
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E6-safety-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

N="${N:-30}"
PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

"$PYTHON_VENV" "$PERF/s12_control_safety.py" \
  --out "$OUT" --n "$N" \
  --base-url "${BASE_URL:-http://localhost:5000}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --nats "${NATS_URL:-nats://localhost:4222}"
rc=$?
echo "[s12] E6 safety done → $OUT (rc=$rc)"
exit $rc
