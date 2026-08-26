# NATS JetStream Design

This document is the HITL review artifact for replacing Event Hub and CosmosDB
Change Feed with NATS JetStream.

## Subject Contract

| Subject | Direction | Transport | Purpose |
|---|---|---|---|
| `building-os.raw.*` | inbound | JetStream | Raw device telemetry per connector/protocol (MQTT / Hono connectors) |
| `building-os.validated.telemetry` | internal | JetStream | Normalized telemetry; fanned out to the Hot KV (`telemetry-latest`) + the Parquet lake writer (default), or the TimescaleDB cold-export path when `WARM_STORE=timescale` |
| `building-os.control.request` | API → worker | JetStream | Generic point control requests (in-process Hono/Kandt handlers) |
| `building-os.control.request.gw.{gatewayId}` | API → bridge | **core NATS request** | Per-gateway egress command to the GatewayBridge replica holding that gateway's stream (#181/#154). Sent as a NATS *request*, so an offline gateway surfaces as no-responders → API returns **503** (#186) |
| `building-os.control.result.{controlId}` | worker/bridge → API | core NATS | Control result keyed by control ID, consumed by `WaitForResult` |
| `building-os.pointlist.updated.gw.{gatewayId}` | seed → bridge | **core NATS** | Point-list-changed push; the bridge forwards `EgressDown{PointListUpdate}` down the egress stream (#224) |
| `building-os.dlq.*` | bridge → ops | JetStream | Dead-letter messages (e.g. Hono bridge) |

> **JetStream vs core NATS.** Persisted, replayable flows (raw / validated / generic control / dlq)
> are JetStream streams (below). The **per-gateway egress** (`control.request.gw.*`) and the
> **point-list push** (`pointlist.updated.gw.*`) use **core NATS** pub/sub — ephemeral, delivered only
> to the replica currently subscribed for that gateway. This is what lets GatewayBridge stay stateless
> and makes the offline-gateway 503 work. They share the `control.*` / `pointlist.*` namespace but are
> **not** persisted on a stream.

> **Hot value store.** The latest value per point lives in a NATS **KV bucket** `telemetry-latest`
> (`History=1`), not a JetStream stream — see `docs/architecture/oss-tier-architecture.md`.

The implementation uses the `building-os.*` prefix already present in
ConnectorWorker, API Server, GatewayBridge, and the Hono bridge.

## Streams and Consumers

| Stream | Stream subjects | Retention | Consumers |
|---|---|---|---|
| `BUILDING_OS_RAW` | `building-os.raw.>` | 7 days or size cap | Connector workers |
| `BUILDING_OS_VALIDATED` | `building-os.validated.>` | **24h** (`MaxAge`, `Limits`/`Discard=Old`; optional `PARQUET_STREAM_MAX_BYTES` cap) | Parquet lake writer (sole JetStream consumer) |
| `BUILDING_OS_CONTROL` | `building-os.control.request` （**リテラル。ワイルドカードにしないこと** — 下記） | 7 days | Point control worker |
| `BUILDING_OS_DLQ` | `building-os.dlq.>` | 30 days | Operations replay tooling |

> **`BUILDING_OS_CONTROL` をワイルドカードで束ねてはいけない（#381）。** `building-os.control.` 配下には
> JetStream に載せる subject と、**core NATS の request/reply として使う subject** が同居している:
>
> | subject | 用途 | JetStream |
> |---|---|---|
> | `building-os.control.request` | 汎用制御要求（Hono/Kandt の in-process ハンドラ。`pointcontrolworker` durable の `FilterSubject`） | **載せる** |
> | `building-os.control.request.gw.{gatewayId}` | per-gateway egress。GatewayBridge が **core** `nats.SubscribeAsync` で購読 | 載せない |
> | `building-os.control.result.{controlId}` | `WaitForResult` の応答。core NATS | 載せない |
>
> ストリームを `building-os.control.>` で束ねると、JetStream が per-gateway subject にも interest を
> 登録し、**NATS request に PubAck を返してしまう**。`NatsPointControlCommandPublisher` は
> `NatsNoRespondersException` でゲートウェイの offline を検知して 503 を即返す設計（#186）なので、
> この応答があると **no-responders が永久に発生せず 503 の分岐が到達不能**になる。呼び出し側は
> 結果タイムアウトまで待たされ、`point_control_audit` の行は `pending` のまま終端しない。
>
> なお `building-os.control.request` は `building-os.control.request.gw.X` に**マッチしない**
> （NATS のリテラル subject はトークン単位の完全一致）。
>
> **既存デプロイの移行:** ストリームは get-or-create で、既存ストリームの subject は自動更新
> **されない**。既に `building-os.control.>` で作成済みの環境では、手動で絞る必要がある:
>
> ```bash
> nats stream info BUILDING_OS_CONTROL --json | jq '.config.subjects'   # 現状確認
> nats stream edit BUILDING_OS_CONTROL --subjects 'building-os.control.request'
> ```
>
> `pointcontrolworker` consumer の `FilterSubject` は `building-os.control.request` 完全一致なので、
> この変更で消費側は影響を受けない。絞る前に保存済みの per-gateway / result メッセージは、
> retention で自然に消える。

> **VALIDATED is a buffer, not the store.** The Parquet lake (MinIO) is the long-term telemetry store;
> the stream only needs to outlive the un-acked window (flush interval + AckWait). `MaxAge` is therefore
> **24h** (`ParquetLakeWriterWorker.StreamMaxAge`, applied via `ValidatedStreamLimits`), not 30d — long
> enough to replay after a writer crash, short enough that NATS holds ≈ one day of telemetry. The
> **Hot KV** (`telemetry-latest`) is written **inline at publish** (`NatsKvPublisher` decorator + gRPC
> ingress bus), not by a stream consumer, so the Parquet writer is the only durable JetStream consumer
> of this stream. (`WorkQueue`/delete-on-ack was rejected: it forbids a second overlapping consumer and
> drops the crash-replay window — the short `MaxAge` gives the same footprint without that fragility.)

> **The 24h buffer doubles as the warm "tail" (#220).** Because validated telemetry sits in the stream
> until it ages out, warm queries whose end time is within `PARQUET_TAIL_LOOKBACK_SEC` (default 900s) of
> now read the still-unflushed JetStream tail **in parallel with** the lake scan and merge the two
> (`TailMergedTelemetryStore`, dedup by id; degrades to lake-only on tail-fetch error). This closes the
> ≤ flush-interval gap where the newest readings are not yet in Parquet, so "latest data" is queryable
> immediately without waiting for a flush. The lookback can be raised up to the stream `MaxAge` (24h) to
> serve more of the recent window straight from NATS. **Worker memory** is bounded by
> `PARQUET_FLUSH_MAX_ROWS` (the accumulator flushes the instant it is reached; default 50k rows ≈ 25 MB);
> the stream's on-disk size is separately capped by `PARQUET_STREAM_MAX_BYTES`.

`BUILDING_OS_RAW` / `BUILDING_OS_VALIDATED` / `BUILDING_OS_DLQ` は `>`（多トークン）ワイルドカードで
束ねる。これらの名前空間には JetStream に載せる subject しか存在せず、ネストした subject も
まとめて捕捉したいため。消費側は per-consumer の `FilterSubject` で自分の subject に絞る
（例 `building-os.raw.bacnet`）。

**`BUILDING_OS_CONTROL` だけは例外でリテラル指定**（上記）。`control.` 名前空間には JetStream に
載せる subject と core NATS の request/reply が同居しているので、ワイルドカードで束ねると後者にも
interest が付き、#186 の no-responders 検知を壊す（#381）。

> **Caveat — per-gateway egress must not be persisted.** `building-os.control.request.gw.*` と
> `building-os.pointlist.updated.gw.*` は構文上 `control.>` / `pointlist.>` の下に入るが、
> **core NATS**（request-reply / pub-sub）で publish/consume されるものであり、
> `BUILDING_OS_CONTROL` に retain されて**はならない**。これらの経路の信頼性は
> ack / no-responders の request（egress）と ETag 再検証（point-list）から来るもので、
> stream replay からではない。
>
> **この段落はかつて「retain されていない」と断言していたが、実装はそうなっていなかった**（#381）。
> ストリームが `building-os.control.>` で束ねられていたため per-gateway コマンドは実際に
> ストリームへ書き込まれており（`seq` が進む）、その副作用として JetStream が NATS request に
> PubAck を返し、offline 検知を無効化していた。**不変条件は上記のリテラル subject 指定によって
> 強制される**（`NatsStreamTopologyTest` が per-gateway / result subject を捕捉しないことを
> 表明している）。文書の主張だけでは守られない、という前例として残しておく。

Durable consumer names use `{component}-{purpose}`, for example
`pointcontrolworker` and `connectorworker-bacnet`. Ack policy is explicit ack
with bounded retry; poison messages go to DLQ where the bridge supports it.

### Implementation note

`BuildingOS.Shared.Infrastructure.Messaging.NatsStreamTopology.Resolve` is the
single source of truth for the subject → (stream, stream-subjects) mapping in
this table. Every host resolves the same full subject set for a given stream,
so whichever worker starts first creates the stream with all of its subjects;
later workers reuse it. This removes the previous startup-order-dependent
behaviour where only the first worker's subject was registered.

## Deduplication

Control requests set `Nats-Msg-Id` to the control ID. Telemetry publishers should
use a deterministic ID when the source protocol provides one; otherwise use
`{connector}:{deviceId}:{pointId}:{observedAt}`. Consumers remain idempotent at
the database layer.

## Schema Registry

Schemas remain source-controlled under `BuildingOS.Shared/Defines/Schemas/`.
Generated C# entities under `BuildingOS.Shared/Defines/Entities/` remain
generated artifacts and must not be edited manually.

## Request-Reply Sequence

**In-process handlers (Hono / Kandt)** — JetStream:

```text
API Server  -> publish building-os.control.request (Nats-Msg-Id=<controlId>)
NatsPointControlWorker -> re-resolve gateway binding -> execute handler
                       -> publish building-os.control.result.<controlId>
PointController -> WaitForResult on result subject -> respond
```

**Per-gateway egress (BacnetSim / external BOWS)** — core NATS request (#181/#186):

```text
API Server  -> NATS *request* building-os.control.request.gw.<gatewayId>
GatewayBridge replica (holding the gateway stream)
            -> ack the request, forward EgressDown{ControlCommand} down the gRPC stream
            -> gateway returns ControlResult up -> publish building-os.control.result.<controlId>
API Server  -> WaitForResult
# No subscriber (gateway offline) => no-responders => PointController returns 503 immediately.
```

This removes CosmosDB Change Feed and `leases` container coupling from the OSS
control path.

## Migration

The NATS path is the OSS default (Event Hub / CosmosDB removed). Historical cutover steps:

1. ~~Shadow publish raw telemetry to NATS while keeping current readers stable.~~ ✅
2. ~~Compare writes through the parity harness.~~ ✅
3. ~~Move ConnectorWorker consumers to NATS durable consumers.~~ ✅
4. ~~Switch point control to NATS request/result.~~ ✅ (+ per-gateway egress, #181/#186)
5. DLQ + replay tooling retained for ops.

## Decisions needed (HITL sign-off)

Reviewer to confirm / decide and tick the boxes, then close #14.
**Signed off 2026-06-14 (interactive HITL review):** all six boxes ticked; the two numeric decisions
(VALIDATED retention, control latency) were applied to code, the rest confirm as-built behavior.

- [x] **Retention/size caps** per JetStream stream fit expected traffic: RAW `7d or size cap`,
      VALIDATED **`24h`** (`MaxAge`, as-built default; the Parquet lake is the long-term store, the stream
      is only a crash-replay buffer — optional `PARQUET_STREAM_MAX_BYTES` cap), CONTROL `7d`, DLQ `30d`.
      _Signed off 2026-06-14: VALIDATED set to 24h (was 30d in doc); in-memory accumulator peak ≈ 50k
      rows ≈ 25 MB at 10k rows/min; `WorkQueue`/delete-on-ack rejected (forbids a 2nd consumer, drops
      replay window)._
- [x] **Dedup keys**: control = `Nats-Msg-Id`=controlId; telemetry deterministic id or
      `{connector}:{deviceId}:{pointId}:{observedAt}` — acceptable for idempotency. _(as-built, OK)_
- [x] **Hot KV** `telemetry-latest` `History=1` / TTL policy is acceptable (latest-only, no history;
      history lives in the Parquet lake). _(as-built, OK)_
- [x] **Ephemeral egress/push acceptable**: per-gateway egress + point-list push are core NATS (no
      replay); offline → 503 (egress) and ETag revalidation (push) are sufficient reliability. _(as-built, OK)_
- [x] **Replay/RTO** behavior for raw (7d) / validated (24h) streams acceptable to operations. _(as-built, OK)_
- [x] **Control latency** budget: online `WaitForResult` timeout **10s** (`CONTROL_RESULT_TIMEOUT_SEC`,
      default 10), offline → immediate 503 (#186). _Signed off 2026-06-14: reduced from 30s to 10s and
      made env-overridable._

### (legacy checklist, retained)

Architecture reviewers must confirm:

- Subject names and stream retention fit expected traffic.
- Deduplication keys satisfy telemetry and control idempotency.
- Replay behavior is acceptable for operations.
- Control latency is compatible with gRPC streaming expectations.

