# Helpers for the #626 provisional runs on one 16-core host, core-pinned:
#   NATS 0-1 · OxiGraph+RustFS+lake worker 2-3 · ingest replicas 4-11 (2 cores each) · loadgen 12-15
#
# Usage: S26_WORKDIR=<scratch dir> bash s26-matrix.sh   (any of the s26-*.sh runners)
#   $S26_WORKDIR holds the NATS store, logs and results, plus the builds the runners expect:
#     cw/       `dotnet publish -c Release` of the ConnectorWorker under test (After)
#     cw-base/  the same for the baseline commit
#     s22venv/  a venv with Tools/e2e-performance/requirements.txt (or set PY)
#   The repository is found relative to this file.
: "${S26_WORKDIR:?set S26_WORKDIR to a scratch directory}"
S=$S26_WORKDIR
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)
PY=${PY:-$S/s22venv/bin/python}
NATS_CONF=${NATS_CONF:-}

infra_up() {
  docker rm -f s26-rustfs s26-oxigraph >/dev/null 2>&1
  docker run -d --name s26-rustfs --cpuset-cpus 2-3 -p 9010:9000 -e RUSTFS_VOLUMES=/data -e RUSTFS_ADDRESS=0.0.0.0:9000 \
    -e RUSTFS_ACCESS_KEY=buildingos -e RUSTFS_SECRET_KEY=buildingos123 rustfs/rustfs:1.0.0 >/dev/null
  docker run -d --name s26-oxigraph --cpuset-cpus 2-3 -p 7878:7878 ghcr.io/oxigraph/oxigraph:0.5.0 serve --location /data --bind 0.0.0.0:7878 >/dev/null
  for i in $(seq 1 30); do curl -sf -m 2 http://localhost:9010/health >/dev/null && curl -s -m 2 -o /dev/null 'http://localhost:7878/query?query=ASK%7B%7D' && break; sleep 1; done
  echo "infra up"
}
oxigraph_fresh() {  # a twin that saw many insert/delete cycles got slow to scan (#626 run 2); start clean
  docker rm -f s26-oxigraph >/dev/null 2>&1
  docker run -d --name s26-oxigraph --cpuset-cpus 2-3 -p 7878:7878 ghcr.io/oxigraph/oxigraph:0.5.0 serve --location /data --bind 0.0.0.0:7878 >/dev/null
  for i in $(seq 1 30); do curl -s -m 2 -o /dev/null 'http://localhost:7878/query?query=ASK%7B%7D' && break; sleep 0.5; done
}
nats_up() {  # $1 = optional extra nats-server args
  pkill -f "[n]ats-server -js -sd $S/nats-s26" ; sleep 1; rm -rf $S/nats-s26; mkdir -p $S/nats-s26
  setsid nohup taskset -c 0-1 $HOME/go/bin/nats-server -js -sd $S/nats-s26 -p 4222 -m 8222 $1 > $S/nats-s26.log 2>&1 < /dev/null &
  for i in $(seq 1 20); do curl -sf -m 1 http://localhost:8222/varz >/dev/null && break; sleep 0.5; done
  echo "nats up $1"
}
workers_down() { pkill -f "[B]uildingOS.ConnectorWorker.dll"; sleep 3; }
# start_worker <build dir> <role> <index> <cpus> [ENV=VAL ...]   (index 0 = lake/all health 18080)
start_worker() {
  local build=$1 role=$2 idx=$3 cpus=$4; shift 4
  local grpc=$((15050 + idx)) health=$((18080 + idx))
  (cd $S && env -i "HOME=$HOME" "PATH=$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 WORKER_ROLE=$role HEALTH_PORT=$health \
     GRPC_INGRESS_PORT=$grpc PARQUET_FLUSH_INTERVAL=1 NATS_URL=nats://localhost:4222 \
     MINIO_ENDPOINT=http://localhost:9010 MINIO_ACCESS_KEY=buildingos MINIO_SECRET_KEY=buildingos123 \
     OXIGRAPH_ENDPOINT=http://localhost:7878 WARM_STORE=parquet ENABLE_SIM_CONTROL=true "$@" \
     setsid nohup taskset -c $cpus dotnet $build/BuildingOS.ConnectorWorker.dll > $S/worker-$role-$idx.log 2>&1 < /dev/null &)
  for i in $(seq 1 40); do curl -s -m 1 http://localhost:$health/health/ready | grep -qE "Healthy|Degraded" && break; sleep 0.5; done
}
pid_of() { pgrep -f "taskset -c $1 dotnet" >/dev/null; ps -eo pid,args | awk -v c="$1" '$0 ~ "dotnet .*BuildingOS.ConnectorWorker.dll" {print $1}' ; }
# ingest_set <build> <n> [ENV=VAL ...]  → starts lake (cpus 2-3) + n ingest replicas; sets INGRESS and PIDS
ingest_set() {
  [ -n "$FRESH_NATS" ] && nats_up
  oxigraph_fresh
  local build=$1 n=$2; shift 2
  workers_down
  start_worker $build lake 0 2-3
  local addrs=() pids=()
  for k in $(seq 1 $n); do
    local c0=$((4 + 2*(k-1))); start_worker $build ingest $k $c0-$((c0+1)) "$@"
  done
  sleep 2
  for k in $(seq 1 $n); do
    addrs+=("localhost:$((15050+k))")
    pids+=("$(ss -ltnpH "sport = :$((15050+k))" | grep -oE 'pid=[0-9]+' | head -1 | cut -d= -f2)")
  done
  INGRESS=$(IFS=,; echo "${addrs[*]}"); PIDS=$(IFS=,; echo "${pids[*]}")
  echo "lake + $n ingest: ingress=$INGRESS pids=$PIDS"
}
run_s26() {  # run_s26 <label> <streams> <rate> [extra args]
  local label=$1 streams=$2 rate=$3; shift 3
  (cd $REPO/Tools/e2e-performance && $PY s26_replica_scale.py --out $S/results26c/$label --label $label --ingress $INGRESS \
     --worker-pid $PIDS --streams $streams --rate $rate --duration 60 --loadgen-cpus 12-15 --minio-endpoint localhost:9010 "$@" \
     > $S/s26-$label.log 2>&1)
  grep -E "^\[s26\]   ->" $S/s26-$label.log | sed "s/^/[$label] /"
}
