# WORKER_ROLE 分割の前後性能計測（#400 / #401 の一次計測）

計測日: 2026-08-29
対象: `feat/worker-role-capability-runtime`（#400 = ConnectorWorker の `WORKER_ROLE` 導入）
ハーネス: `Tools/e2e-performance/s11_ingest_latency.sh`（E2）/ `s15_ingest_throughput.sh`（E1）
環境: 単一ホスト（macOS / Docker Desktop）、OSS 既定スタック（NATS / MinIO / OxiGraph / PostgreSQL / Keycloak）

## 1. 結論

- **`WORKER_ROLE=all`（既定）は変更前と同等**。ingest E2E p95 は 2.9 ms → 3.1 ms、loss / duplicate /
  validation error はいずれも 0 のまま。既存デプロイへの回帰は観測されなかった。
- **role 分割構成（ingest / lake / control の 3 プロセス）も機能的に正常**。テレメトリは lake ロールの
  ParquetLakeWriter が欠落なく書き込み（loss 0）、ingest p95 は 3.2 ms で all と同等。
- **単一ホストの最大スループットは改善しない**。分割構成の飽和スループットは 491〜804 frames/s と
  ばらつき、all 構成（679〜778 frames/s）と範囲が重なる。run 間ノイズが差より大きく、
  **この環境では分割による throughput 上の優劣は測定できない**。
- したがって本計測は「分割しても壊れない」ことの確認であって、**分割の効果を示すものではない**。
  効果（compaction 中の tail latency 安定性、ingest だけの水平スケール）の検証は #401 の
  混在負荷ベンチマークが必要で、それには複数コアに余裕のあるホストが要る。

## 2. 計測結果

### E2 — ingest E2E latency（600 frames @ 20/s、50 points）

| 構成 | p50 | p95 | p99 | loss |
|---|---|---|---|---|
| BEFORE（変更前バイナリ・all-in-one） | 1.9 ms | **2.9 ms** | 4.1 ms | 0 |
| AFTER `WORKER_ROLE` 未設定（= all） | 2.0 ms | **3.1 ms** | 3.6 ms | 0 |
| AFTER 分割（ingest + lake + control） | 2.0 ms | **3.2 ms** | 5.7 ms | 0 |

しきい値は p95 < 2,000 ms。3 構成とも桁違いに余裕があり、差は run 間ノイズの範囲。

> `parquet_freshness_p95_s` は 3 構成とも SKIP。Prometheus（`--profile observability`）を
> 起動していないため取得できず、BEFORE / AFTER で条件を揃える方を優先した。

### E1 — 飽和スループット（目標 2,000 frames/s を 30 秒、60,000 frames）

| 構成 | run 1 | run 2 | run 3 | loss / dup / invalid |
|---|---|---|---|---|
| BEFORE（変更前バイナリ・all-in-one） | 803 f/s | — | — | 0 / 0 / 0 |
| AFTER `WORKER_ROLE=all` | 778 f/s | 679 f/s | 745 f/s | 0 / 0 / 0 |
| AFTER 分割（ingest + lake + control） | 633 f/s | 491 f/s | 804 f/s | 0 / 0 / 0 |

all と分割を交互に実行して機材のドリフトが一方に偏らないようにした。それでも分割構成の
run 間分散（491〜804）が構成間の差を上回る。全 run で `sustained_throughput_ratio` は 1.0、
lake の行数は accepted と一致（分割構成でも lake ロールが取りこぼしていない）。

負荷生成側が目標 2,000 f/s に届いていない（達成 500〜800 f/s）点に注意。この数値は
**サーバ上限ではなく単一ホストの総容量**を測っている。分割は総 CPU コストを減らさず、
.NET ランタイムを 3 つ動かす分だけ増やすので、単一ホストで速くならないのは想定どおり。

## 3. 追試のしかた

```bash
# BEFORE 相当（all-in-one）
docker compose -f docker-compose.oss.yaml up -d
GRPC_INGRESS_PORT=5051 bash Tools/e2e-performance/s11_ingest_latency.sh   results/BEFORE-E2
GRPC_INGRESS_PORT=5051 RATE=2000 DURATION=30 \
  bash Tools/e2e-performance/s15_ingest_throughput.sh results/BEFORE-E1-sat

# 分割構成（ingest + lake + control）
PARQUET_FLUSH_INTERVAL=1 docker compose \
  -f docker-compose.oss.yaml -f docker-compose.roles.yaml up -d \
  building-os.connector-worker building-os.connector-worker-lake building-os.connector-worker-control
WORKER_ROLE=ingest GRPC_INGRESS_PORT=5051 RATE=2000 DURATION=30 \
  bash Tools/e2e-performance/s15_ingest_throughput.sh results/AFTER-ingest-E1-sat
```

ハーネスは `--build` を付けずに connector-worker を再作成するため、コードを変えたら
`docker compose -f docker-compose.oss.yaml build building-os.connector-worker` を挟まないと
**古いバイナリを測ってしまう**。role が効いているかは起動ログで確認できる:

```
docker logs building-os.connector-worker | grep "Connector role"
# Connector role: ingest — gRPC ingress: enabled port=5051, ... MQTT: disabled (role=ingest) ...
```

## 4. 残課題（#401）

この計測は単一負荷（ingest のみ）であり、分割の是非を決める材料にはならない。#401 で必要なのは:

- compaction 実行中 / 非実行中で分けた ingest p95 / p99 の比較
- telemetry 負荷あり / なしでの control RTT
- CPU・メモリ・GC pause・JetStream consumer lag の系列記録
- 複数コアに余裕のあるホスト（本計測のようにクライアントとサーバが同一ホストで CPU を奪い合わない環境）
