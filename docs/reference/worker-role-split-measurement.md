# WORKER_ROLE 分割の前後計測（#400 / #401 の一次計測）

計測日: 2026-08-29
対象: `feat/worker-role-capability-runtime`（#400 = ConnectorWorker の `WORKER_ROLE` 導入）
ハーネス: `Tools/e2e-performance/s11_ingest_latency.sh`（E2）/ `s15_ingest_throughput.sh`（E1）+ 制御 API 直接検証
環境: 単一ホスト（macOS / Docker Desktop, 8 GiB）、OSS 既定スタック（NATS / MinIO / OxiGraph / PostgreSQL / Keycloak）

## 1. 結論

- **`WORKER_ROLE=all`（既定）は変更前と同等**。ingest E2E p95 は 2.9 ms → 3.1 ms、loss / duplicate /
  validation error はいずれも 0。加えてユニットテストの回帰ガードが、登録される
  ServiceDescriptor 集合と hosted service の起動順序が変更前と**同一**であることを検証している。
- **分割構成（ingest / lake / control の 3 プロセス）は 3 経路とも実動作を確認した**。
  取り込み・Parquet 書き込み・compaction・制御実行のいずれも、担当ロールだけが実行している。
- **単一ホストの最大スループットは改善しない**。分割構成の飽和スループットは 491〜804 frames/s と
  ばらつき、all 構成（679〜778 frames/s）と範囲が重なる。run 間ノイズが差より大きい。
- **メモリは約 3 倍になる**（55.7 MiB → 合計 164.6 MiB）。.NET ランタイムが 3 つに増えるため。
- したがって本計測が示したのは「**分割しても壊れない**」ことであって、分割の効果ではない。
  効果（compaction 中の tail latency 安定性、ingest だけの水平スケール）の検証には #401 の
  混在負荷ベンチマークが必要で、それにはクライアントとサーバが CPU を奪い合わないホストが要る。

## 2. 性能計測

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

### リソース使用量（アイドル時）

| 構成 | プロセス | メモリ |
|---|---|---|
| all-in-one | `connector-worker` | 55.7 MiB |
| 分割 | `connector-worker`(ingest) 53.9 + `-lake` 70.2 + `-control` 40.3 | **164.6 MiB** |

分割は**メモリを約 3 倍消費する**。ランタイム 3 つ分の固定コストで、負荷とは独立に効く。
小規模構成で分割する理由がないことの、これが最も直接的な根拠。

## 3. 分割構成の機能検証

性能とは別に、「分割した各ロールが本当にその仕事をしているか」を経路ごとに確認した。

### 制御経路 — 差分検証で所有を証明

制御は 202 を返して結果は非同期なので、`GET /points/{id}/control-audit` の行で実行を判定した。

| 手順 | control コンテナ | 監査行の結果 |
|---|---|---|
| A | 起動中 | **success**（730 ms で完了） |
| B | 停止 | **pending のまま**（ingest ワーカーは処理しない） |
| C | 再起動 | success（B の命令も遅れて実行された） |

B が pending で止まることが重要で、これは制御が**本当に control ロールへ移った**ことの証明になる
（ingest ロールが肩代わりしていない）。

> **副次的な発見**: B で滞留した命令は破棄されず、C の再起動時に **27 秒遅れて実行された**。
> in-process 制御経路（`building-os.control.request`）は JetStream の durable consumer なので、
> ワーカー停止中の命令は失われず後で届く。all-in-one でも同じ挙動であり本変更由来ではないが、
> 「制御ロールだけを落とす」運用が可能になったことで**遅延実行の窓が観測しやすくなった**。
> 設備へ古い値が書かれうるという意味で、role 分割を本番採用する際は考慮が要る（#401 か別 Issue）。

### Parquet lake 経路 — compaction とロールアップ

`LAKE_COMPACTION_INTERVAL=1` で settle 済みの時間帯（UTC 11時台）を compaction させた。

- lake ロールが **8 パーティションすべてを compaction**（例: 60,001 行 / 3 parts → `compact-*.parquet` 1 個）
- `agg_hourly/agg-*.parquet` のロールアップも生成
- **ingest / control ロールの compaction ログは 0 行** — 担当外の仕事をしていない

これで「compaction が ingest と同居しない」という分割の主目的が、構成として成立していることは確認できた
（ただし ingest 負荷と同時に走らせた際の tail latency への影響は未測定 = #401 の本体）。

## 4. 追試のしかた

```bash
# BEFORE 相当（all-in-one）
docker compose -f docker-compose.oss.yaml up -d
GRPC_INGRESS_PORT=5051 bash Tools/e2e-performance/s11_ingest_latency.sh   results/BEFORE-E2
GRPC_INGRESS_PORT=5051 RATE=2000 DURATION=30 \
  bash Tools/e2e-performance/s15_ingest_throughput.sh results/BEFORE-E1-sat

# 分割構成（ingest + lake + control）
PARQUET_FLUSH_INTERVAL=1 LAKE_COMPACTION_INTERVAL=1 docker compose \
  -f docker-compose.oss.yaml -f docker-compose.roles.yaml up -d
WORKER_ROLE=ingest GRPC_INGRESS_PORT=5051 RATE=2000 DURATION=30 \
  bash Tools/e2e-performance/s15_ingest_throughput.sh results/AFTER-ingest-E1-sat

# 制御経路（DISABLE_AUTH=true で API を起動しておく）
curl -X POST localhost:5000/points/<writable-point>/control -H 'Content-Type: application/json' -d '{"value":22.5}'
curl -s localhost:5000/points/<writable-point>/control-audit | jq '.[0].status'   # success なら実行された
```

ハーネスは `--build` を付けずに connector-worker を再作成するため、コードを変えたら
`docker compose -f docker-compose.oss.yaml build building-os.connector-worker` を挟まないと
**古いバイナリを測ってしまう**。role が効いているかは起動ログで確認できる:

```
docker logs building-os.connector-worker | grep "Connector role"
# Connector role: ingest — gRPC ingress: enabled port=5051, ... MQTT: disabled (role=ingest) ...
```

`LAKE_COMPACTION_SETTLE_MINUTES` は **0 を渡しても既定の 30 分のまま**（実装が `> 0` のときだけ
上書きする）。compaction をすぐ観測したいときは、settle 済みの過去の時間帯にデータがある状態で
`LAKE_COMPACTION_INTERVAL=1` にして待つ。

## 5. 残課題（#401）

分割の**是非**を決める材料は、まだ揃っていない。#401 で必要なのは:

- compaction 実行中 / 非実行中で分けた ingest p95 / p99 の比較（本計測では両者を同時に走らせていない）
- telemetry 負荷あり / なしでの control RTT
- CPU・メモリ・GC pause・JetStream consumer lag の系列記録
- 複数コアに余裕のあるホスト（本計測はクライアントとサーバが同一ホストで CPU を奪い合っている）
