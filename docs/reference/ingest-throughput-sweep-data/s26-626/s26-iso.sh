#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")/s26lib.sh"
A=$S/cw; PPS="--points-per-stream 50"; export FRESH_NATS=1
natscpu() { $PY - <<'PY'
import requests, time
v=[requests.get("http://localhost:8222/varz",timeout=3).json()["cpu"] for _ in range(10) if not time.sleep(2)]
print("nats cpu over 20 s:", v)
PY
}
echo "== E0 idle: 1 replica, then 4 replicas, no load"
ingest_set $A 1; sleep 20; natscpu; $PY "$(dirname "${BASH_SOURCE[0]}")/percore.py" 10
ingest_set $A 4; sleep 20; natscpu; $PY "$(dirname "${BASH_SOURCE[0]}")/percore.py" 10
echo "== E1 4 replicas running, load only on replica 1 (24 streams, 20k/s)"
INGRESS=localhost:15051; PIDS=${PIDS%%,*}; run_s26 e1-ing4-load1 24 20000 $PPS
echo "== E2 4 replicas, load on all, per-core CPU during load"
ingest_set $A 4; ( for i in $(seq 1 40); do echo "$(date -u +%T) $($PY "$(dirname "${BASH_SOURCE[0]}")/percore.py" 8)"; done > $S/e2-percore.txt ) & PC=$!; run_s26 e2-ing4-loadall 24 20000 $PPS; kill $PC 2>/dev/null
echo "== E3 1 replica control, per-core CPU during load"
ingest_set $A 1; ( for i in $(seq 1 40); do echo "$(date -u +%T) $($PY "$(dirname "${BASH_SOURCE[0]}")/percore.py" 8)"; done > $S/e3-percore.txt ) & PC=$!; run_s26 e3-ing1 24 20000 $PPS; kill $PC 2>/dev/null
workers_down; echo "== iso done"
