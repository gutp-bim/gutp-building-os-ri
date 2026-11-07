#!/usr/bin/env bash
# #626 provisional matrix on one core-pinned 16-core host (see s26lib.sh for the core map).
source /tmp/claude-1000/-home-takashi-projects-gutp-bos-ri-main/80e8998f-8e72-4e50-91de-83d1e729f3ef/scratchpad/s26lib.sh
A=$S/cw; B=$S/cw-base; PPS="--points-per-stream 50"; export FRESH_NATS=1
echo "== start $(date -u +%FT%TZ)"
nats_up
# R-1 replica linearity: after (defaults), baseline, after with the pre-#625 knobs
for n in 1 2 4; do ingest_set $A $n; run_s26 r1-after-ing$n 12,24,48 20000 $PPS; done
for n in 1 2 4; do ingest_set $A $n; run_s26 r1hi-after-ing$n 48 40000 $PPS; done
for n in 1 2 4; do ingest_set $B $n; run_s26 r1-base-ing$n 12,24,48 20000 $PPS; done
for n in 1 2 4; do ingest_set $A $n GRPC_INGRESS_PUBLISH_WINDOW=1 HOT_STORE_COALESCE_MS=0; run_s26 r1-afterlegacy-ing$n 24 20000 $PPS; done
# R-2 role split: one `all` process on 4 cores vs ingest (2 cores) + lake (2 cores)
for build in A B; do
  dir=${!build}
  workers_down; nats_up; start_worker $dir all 1 2-5; INGRESS=localhost:15051
  PIDS=$(ss -ltnpH "sport = :15051" | grep -oE 'pid=[0-9]+' | head -1 | cut -d= -f2); echo "all: pids=$PIDS"
  run_s26 r2-$build-all 12 10000 $PPS
  ingest_set $dir 1; run_s26 r2-$build-split 12 10000 $PPS
done
unset FRESH_NATS
# R-3 NATS settings (after build, 2 ingest replicas, 24 streams, saturating rate)
workers_down
printf 'port: 4222\nhttp_port: 8222\njetstream { store_dir: "%s/nats-s26", sync_interval: always }\n' $S > $S/nats-sync.conf
pkill -f "[n]ats-server -js -sd $S/nats-s26"; pkill -f "[n]ats-server -c $S/nats-"; sleep 1; rm -rf $S/nats-s26; mkdir -p $S/nats-s26
setsid nohup taskset -c 0-1 $HOME/go/bin/nats-server -c $S/nats-sync.conf > $S/nats-sync.log 2>&1 < /dev/null &
sleep 2; ingest_set $A 2; run_s26 r3-sync-always 24 20000 $PPS
workers_down; pkill -f "[n]ats-server -c $S/nats-"; sleep 1
# 3-node cluster, validated stream pre-created with R=3 (the code keeps the replica count)
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
echo "== done $(date -u +%FT%TZ)"
