#!/usr/bin/env bash
# Decisive check for the 4-replica slowdown: same cells with the JetStream store on tmpfs.
source "$(dirname "${BASH_SOURCE[0]}")/s26lib.sh"
A=$S/cw; PPS="--points-per-stream 50"
while pgrep -f "[r]eplica_scale" >/dev/null; do sleep 5; done
nats_shm() { pkill -f "[n]ats-server -js -sd $S/nats-s26"; pkill -f "[n]ats-server -js -sd /dev/shm/nats-s26"; sleep 1; rm -rf /dev/shm/nats-s26; mkdir -p /dev/shm/nats-s26
  setsid nohup taskset -c 0-1 $HOME/go/bin/nats-server -js -sd /dev/shm/nats-s26 -p 4222 -m 8222 > $S/nats-shm.log 2>&1 < /dev/null &
  for i in $(seq 1 20); do curl -sf -m 1 http://localhost:8222/varz >/dev/null && break; sleep 0.5; done; echo "nats store on tmpfs"; }
nats_shm; ingest_set $A 1; run_s26 shmhi-ing1-40k 48 40000 $PPS
nats_shm; ingest_set $A 2; run_s26 shmhi-ing2-60k 48 60000 $PPS
nats_shm; ingest_set $A 4; run_s26 shmhi-ing4-80k 48 80000 $PPS
workers_down; pkill -f "[n]ats-server -js -sd /dev/shm/nats-s26"; rm -rf /dev/shm/nats-s26; echo "== shm done"
