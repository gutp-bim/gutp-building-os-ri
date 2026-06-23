# E2 — Ingest E2E latency / 鮮度

## 目的
gateway 生成時刻から Building OS が validated telemetry として扱えるまでを低遅延で行えること、
Parquet 末尾鮮度（分単位）は Hot 層で補完される設計であることを示す。

## 計測指標
- **ingest E2E latency p50/p95/p99**: gateway 生成 ts → `building-os.validated.telemetry` 反映。
- parquet write freshness p95: event time → Parquet で queryable まで（`parquet_writer.freshness_lag` histogram, Prometheus）。
- flush duration（`parquet_writer.flush_*`）。

## 手順
1. 負荷生成 frame に生成 ts を埋め込む（point_id + ts）。
2. validated telemetry 購読側で受信 ts を記録 → 差分が ingest E2E。
3. Prometheus から `parquet_writer.freshness_lag` p95 を取得。
4. medium/large で計測（stress/burst は E1 と同 run で副次取得可）。

## 合否（kpi-thresholds.yaml: E2_ingest_latency）
- ingest E2E p95 < 2000ms。
- parquet freshness p95 ≤ `PARQUET_FLUSH_INTERVAL` + 60s（runner が flush 間隔から動的算出）。

## 既存資産・ギャップ
- 既存: `s2_baseline.sh`（throughput）, Prometheus メトリクス（#213/#216）。
- **実装済**: `Tools/e2e-performance/s11_ingest_latency.{py,sh}`（下記）。

## 実装メモ（2026-06-15, parquet 既定・ローカル）

### ハーネス `s11_ingest_latency.{py,sh}`
gRPC `GatewayIngress` の frame `timestamp` に生成 ts を埋め、ingress がそれを validated entity の
`datetime` にそのまま転記することを利用。core-NATS subscriber（JetStream publish をライブ受信）で
`building-os.validated.telemetry` を購読し、`recv − datetime` を ingest E2E latency とする。frame は
一意 `building` タグを付け、購読側はそのタグの行だけ集計（E5 の ingress/seed 配線を再利用、proto は実行時
コンパイル）。Parquet freshness は Prometheus の
`building_os_parquet_writer_freshness_lag_seconds` histogram（#213）から取得。

### 計測の注意点
- freshness は **flush が起きないと記録されない**。ハーネスは connector-worker を `PARQUET_FLUSH_INTERVAL=1`
  （分）で起動し、計測後 **≥2 flush サイクル**待ってから取得（1 サンプルだと `rate()`/`histogram_quantile`
  が NaN になるため）。p95 が NaN の場合は `sum/count`（平均ラグ）にフォールバック。
- 合否閾値は動的: freshness p95 ≤ `PARQUET_FLUSH_INTERVAL`(分)×60 + 60s。

### 実測（parquet, 600 frames @ 20/s, flush 1min, ローカル）

| 指標 | 実測 | 閾値 | 判定 |
|---|--:|--:|---|
| ingest_e2e p95 | **2.6 ms** | < 2000 ms | ✅ |
| parquet_freshness_p95 | 17.4 s | ≤ 120 s (1min+60s) | ✅ |

`accepted == received == 600`（欠落なし）。gRPC ingress は validated へ直送（per-protocol connector hop
なし）のため E2E は ms オーダー。実行: `bash Tools/e2e-performance/s11_ingest_latency.sh` または
`bash e2e/runner/run-axis.sh E2 --out <dir>`。
