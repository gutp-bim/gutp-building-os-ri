#!/usr/bin/env bash
# Re-run the #626 cells that aborted on the slowed twin (probe DEADLINE_EXCEEDED), with a fresh
# OxiGraph per replica set, then the NATS-core diagnostics and R-4.
source "$(dirname "${BASH_SOURCE[0]}")/s26lib.sh"
A=$S/cw; B=$S/cw-base; PPS="--points-per-stream 50"; export FRESH_NATS=1
echo "== rerun start $(date -u +%FT%TZ)"
ingest_set $A 4; run_s26 r1hi-after-ing4 48 40000 $PPS
ingest_set $B 2; run_s26 r1-base-ing2-48 48 20000 $PPS
ingest_set $B 4; run_s26 r1-base-ing4-48 48 20000 $PPS
ingest_set $A 4 GRPC_INGRESS_PUBLISH_WINDOW=32; run_s26 d1-ing4-window32 24 20000 $PPS
ingest_set $A 4 HOT_STORE_COALESCE_MS=1000; run_s26 d2-ing4-coalesce1000 24 20000 $PPS
unset FRESH_NATS
nats4() { pkill -f "[n]ats-server -js -sd $S/nats-s26"; sleep 1; rm -rf $S/nats-s26; mkdir -p $S/nats-s26
  setsid nohup taskset -c 0-1,14-15 $HOME/go/bin/nats-server -js -sd $S/nats-s26 -p 4222 -m 8222 > $S/nats-s26.log 2>&1 < /dev/null &
  for i in $(seq 1 20); do curl -sf -m 1 http://localhost:8222/varz >/dev/null && break; sleep 0.5; done; echo "nats on 4 cores"; }
nats4; ingest_set $A 1; run_s26 d3-ing1-nats4core 24 20000 $PPS --loadgen-cpus 12-13
nats4; ingest_set $A 4; run_s26 d3-ing4-nats4core 24 20000 $PPS --loadgen-cpus 12-13
nats4; ingest_set $A 2; run_s26 d3-ing2-nats4core-40k 48 40000 $PPS --loadgen-cpus 12-13
nats4; ingest_set $A 4; run_s26 d3-ing4-nats4core-40k 48 40000 $PPS --loadgen-cpus 12-13
# R-3: sync_interval always, and a 3-node cluster with the validated stream at R=3
workers_down; pkill -f "[n]ats-server -js -sd $S/nats-s26"; sleep 1; rm -rf $S/nats-s26; mkdir -p $S/nats-s26
printf 'port: 4222\nhttp_port: 8222\njetstream { store_dir: "%s/nats-s26", sync_interval: always }\n' $S > $S/nats-sync.conf
setsid nohup taskset -c 0-1 $HOME/go/bin/nats-server -c $S/nats-sync.conf > $S/nats-sync.log 2>&1 < /dev/null &
sleep 2; ingest_set $A 2; run_s26 r3-sync-always 24 20000 $PPS
workers_down; pkill -f "[n]ats-server -c $S/nats-"; sleep 1
for i in 0 1 2; do
  rm -rf $S/nats-c$i; mkdir -p $S/nats-c$i
  printf 'server_name: n%s\nport: %s\nhttp_port: %s\njetstream { store_dir: "%s/nats-c%s" }\ncluster { name: s26, port: %s, routes: [nats://localhost:6222, nats://localhost:6223, nats://localhost:6224] }\n' \
    $i $((4222+i)) $((8222+i)) $S $i $((6222+i)) > $S/nats-c$i.conf
  setsid nohup taskset -c 0-1 $HOME/go/bin/nats-server -c $S/nats-c$i.conf > $S/nats-c$i.log 2>&1 < /dev/null &
done
sleep 6
$PY - <<'PY'
import asyncio, nats
from nats.js.api import StreamConfig, StorageType
async def main():
    nc = await nats.connect("nats://localhost:4222"); js = nc.jetstream()
    for _ in range(30):
        try:
            await js.add_stream(StreamConfig(name="BUILDING_OS_VALIDATED", subjects=["building-os.validated.>"],
                                             storage=StorageType.FILE, num_replicas=3, max_age=24*3600)); break
        except Exception as e:
            print("retry", e); await asyncio.sleep(1)
    print("stream", (await js.stream_info("BUILDING_OS_VALIDATED")).config.num_replicas, "replicas"); await nc.close()
asyncio.run(main())
PY
ingest_set $A 2; run_s26 r3-cluster-r3 24 20000 $PPS
workers_down; pkill -f "[n]ats-server -c $S/nats-"; sleep 1
# R-4 endurance: 2 ingest replicas, 14,000/s, 1 hour
export FRESH_NATS=1
ingest_set $A 2; run_s26 r4-ing2-14k-1h 24 14000 $PPS --duration 3600 --drain-timeout 900
workers_down
echo "== rerun done $(date -u +%FT%TZ)"
