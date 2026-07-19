# Gateway の真の接続状態と PointList 同期状態を NATS KV ハートビートで集約する

- **Status**: Accepted — **Phase 1（connected/disconnected）実装済み**。PointList 同期状態（Phase 2b,
  オプション A: `EgressUp.Status` proto 追加）は nexus-gateway 側の追従が要るため未実装（後続）。
- **関連**: #230（本 ADR の対象）、#181 Phase 2①（last-seen、実装済み / #222）、#186（gateway offline 503）、
  #224（PointList 同期 / ETag）、ADR-0003（GatewayBridge ステートレス egress）

## Context

運用者が「Gateway 状態」に期待するのは **今つながっているか（connected / disconnected）** と
**配信中の PointList が最新か（同期状態）** である。現状はどちらも一級には持っていない。

- **connected/disconnected は per-replica のインメモリのみ**。egress ストリームの生死は GatewayBridge の
  `GatewayConnectionRegistry`（`ConcurrentDictionary<string, GatewayConnection>`, プロセスローカル）にしかない。
  出典: `DotNet/BuildingOS.GatewayBridge/Infrastructure/GatewayConnectionRegistry.cs:45`、同ファイル L32-42 の
  「no persistent state … on pod restart the registry is rebuilt」。GatewayBridge は**ステートレス水平スケール**
  前提（per-gateway NATS ルーティング、ADR-0003）なので、複数レプリカのどこに生きたストリームが載っているかを
  集約する共有状態が無い。
- **last-seen は connected の代替にならない**。#181 Phase 2①（#222）は当該ゲートウェイ配下ポイントの最新テレメトリ
  時刻の最大値を `GatewayAdminView.LastTelemetryAt` として導出・表示するが、これは「最終受信時刻」であり、
  受信が止まっただけで切断とは限らない。`GatewaysController.cs` 自身がコメントで明記している:
  「True connected/disconnected requires cross-replica egress state (a NATS-KV heartbeat) and is a follow-up
  (option ②)」（`DotNet/BuildingOS.ApiServer/Controllers/GatewaysController.cs` L149-151, L193-198）。
  UI 側も `web-client/src/components/home/gateway-status-panel.tsx` L53-60 で「真の接続状態ではない」と disclaim
  している。
- **PointList 同期状態は判定不能**。サーバは権威 ETag（`"sha256:…"`, 順序非依存のコンテンツハッシュ）を
  `PointListEtag.Compute` で計算できる（`DotNet/BuildingOS.ApiServer/GatewayProvisioning/GatewayPointListSerializer.cs:53-62`）。
  だが **ゲートウェイが今どの ETag を適用済みかをサーバは記録していない**。ETag の往復は
  `GET /gateways/{id}/pointlist` の条件付き GET（`If-None-Match`→304 / `?since={etag}`）という**リクエスト単位の
  一過性**のみで、「gateway X は現在 ETag Y を適用中」を保持する場所が無い（`GatewayProvisioningController.cs`
  L69-95）。`gateway_egress.proto` の `EgressUp`（gateway→bridge）にも状態報告メッセージは無く、gateway が上りに
  送るのは `Hello`（接続時）と `ControlResult`（コマンド毎）だけ（`proto/gateway_egress.proto` L18-23）。

本 ADR は #230 の残スコープ（Phase 2②: 真の connected/disconnected + PointList 同期状態）の**設計方針**を、
既存資産（NATS KV の先例、egress ストリームの register/unregister フック、`GatewayAdminView` の読み取り面）に
接続する形で確定する。実装は本 ADR 承認後に別 PR で行う。

## Considered Options

### 接続状態の集約先

1. **NATS JetStream KV ハートビート（推奨）** — 各 GatewayBridge レプリカが per-gateway の接続エントリを共有 KV に
   書き、TTL で切断を検知する。読み取り面（`GatewaysController`）が KV を引いて `connected` を返す。既存の
   `NatsKvLatestStore`（`DotNet/BuildingOS.Shared/Infrastructure/Oss/NatsKvLatestStore.cs`, bucket `telemetry-latest`,
   `NATS.Net` の `INatsKVStore`）と同じ注入・遅延 `CreateStoreAsync` パターンを踏襲でき、追加インフラ不要
   （NATS は既にコア依存）。TTL 失効でクラッシュ検知が自動。
2. **分散レジストリ（Redis / consistent-hash で gateway→replica 固定）** — ADR-0003 が明確に不採用とした方向。
   LB スティッキー性や外部ストアの追加運用が要り、ステートレス水平スケールの前提を崩す。不採用。
3. **Prometheus メトリクスから逆算** — 各レプリカが `gateway.egress.connected{gatewayId}` を出し、UI が Prometheus に
   問い合わせる。だが Prometheus は既定スタックで**オプトイン**（`--profile observability`）であり、UI の基本機能を
   任意の観測基盤に依存させられない（`SYSTEM_STATUS_HEALTH_TARGETS` から prometheus を外した #127 と同じ判断）。不採用。

→ **オプション 1（NATS KV ハートビート）を採用**。

### PointList 同期状態の取得経路

A. **`EgressUp` に gateway 状態報告を追加（推奨）** — `proto/gateway_egress.proto` の `EgressUp` oneof に第3ケース
   `Status status = 3`（`applied_revision` = 適用済み ETag、任意で `heartbeat_at`）を追加。GatewayBridge の
   egress 読み取りループ（現状 `Result` 以外を無視: `GatewayEgressService.RunAsync` L88
   `if (up.MCase != EgressUp.MOneofCase.Result) continue;`）で受け、接続 KV エントリに `appliedRevision` として
   書き込む。読み取り面が twin 側 ETag（`PointListEtag.Compute`）と突き合わせて `pointlistSynced` を導出。
   接続の生死と同期状態を**同じハートビート経路**で運べるのが利点（gateway 側の実装は nexus-gateway 側の対応が要る）。
B. **条件付き GET の `If-None-Match` をサーバ側で記録** — `GatewayProvisioningController.GetPointList` が受け取る
   `If-None-Match` を per-gateway に KV へ記録し、`appliedRevision` とみなす。proto 変更・gateway 側変更が不要な反面、
   「適用済み」ではなく「最後に revalidate を投げた時の手持ち ETag」であり、304 を受けた後に実際に差分適用したかは
   保証しない。また ingress/pointlist 経路（ApiServer）と egress heartbeat（GatewayBridge）で書き手が分かれる。
   軽量な暫定として **Phase 2b で選択可**。
C. **やらない（connected のみ）** — 同期状態は将来。Phase を切って connected を先に出す。

→ **A を最終形**、ただし**段階導入**（後述）。gateway 側（nexus-gateway）の proto 追従が要るため、connected を
   先に出し（gateway 変更不要）、同期状態は proto 追加後に載せる。

## Decision（設計）

### 1. 接続ハートビート KV

- **新バケット** `gateway-connection`（`NatsKvLatestStore` と同じ生成パターン、ただし **`NatsKVConfig.MaxAge` = TTL を設定**。
  既存の `telemetry-latest` は `History=1` のみで TTL 未設定なので、ここは新設プロパティ）。
- **キー** = サニタイズ済み `gatewayId`（既存 `SanitizeKey` と同じ `[^a-zA-Z0-9_.\-]→_`）。
- **値**（JSON）= `{ replicaId, connectedAt, lastHeartbeatAt, appliedRevision? }`。`replicaId` は
  `OTEL_SERVICE_NAME` + ホスト/ポッド名相当（供給元は実装時に確定）。
- **書き込みフック**（GatewayBridge、`GatewayEgressService.RunAsync`）:
  - **接続時**: `registry.Register(gatewayId)` 直後（L41）に KV `PutAsync`。
  - **ハートビート**: ストリーム生存中、`HeartbeatInterval`（既定 **10s** 目安）毎に `lastHeartbeatAt` を更新
    （down 方向の gRPC write は `SemaphoreSlim` 直列化済みだが、KV 書き込みは gRPC ストリームとは独立なので
    競合しない）。
  - **切断時**: `finally` の `registry.Unregister(connection)`（L98）と同じ場所で KV `DeleteAsync`（**エポック保護**:
    自分が最新書き手のときだけ消す＝supersede で新接続が奪った後の遅延切断は消さない。`Unregister` の
    KeyValuePair 一致削除と同じ思想）。
- **TTL**: `MaxAge` = `HeartbeatInterval × 3`（既定 **30s** 目安）。レプリカがクラッシュして `Delete` に到達できなくても、
  TTL 失効で `connected=false` に落ちる。**この TTL がクラッシュ時のバックストップ**。
- **読み取り**（ApiServer、`GatewaysController.BuildViewAsync`）: KV エントリを引き、存在かつ未失効なら `connected=true`。
  `GatewayAdminView`（`GatewaysController.cs` L199-206）に **`bool Connected`** と（Phase 2b で）
  **`bool? PointlistSynced`** を追加。既存の `LastTelemetryAt` は**併記**（connected は egress 制御面の生死、
  last-seen は ingress テレメトリの最終受信で、意味が異なるため UI で区別して出す）。

### 2. PointList 同期状態（Phase 2b, オプション A）

- `EgressUp` に `Status status = 3`（`applied_revision`）を追加 → GatewayBridge が KV の `appliedRevision` を更新。
- 読み取り面が `PointListEtag.Compute(_twin.ListGatewayPointList(id))`（＝既存 `Revision` 計算, `GatewaysController.cs`
  L128 相当）と `appliedRevision` を比較し、一致で `pointlistSynced=true`、不一致で `false`、未報告で `null`（不明）。

### 3. UI

- `web-client/src/lib/admin/gateways.ts` の `GatewayAdminView` に `connected: boolean` /
  `pointlistSynced: boolean | null` を追加、`connectedLabel` 等の表示ヘルパを純関数で追加（既存 `lastSeenLabel`/
  `shortRevision` と同じ場所）。
- 表示面 = `components/home/gateway-status-panel.tsx`（オペレーターホーム）と `components/admin/gateways-page-client.tsx`
  （`/admin/gateways`）。現状の「真の接続状態ではない」注記（panel L53-60）は connected 列の追加に合わせて更新。

## Implementation notes (Phase 1)

- Store: `NatsKvGatewayConnectionStore` (`BuildingOS.Shared/Infrastructure/Oss/`), bucket
  `gateway-connection`, `NatsKVConfig.MaxAge` TTL. Best-effort — every method swallows/logs and never
  throws. `IGatewayConnectionStatusStore` is the shared seam (writer = GatewayBridge, reader = ApiServer).
- Writer: `GatewayEgressService` marks connected at register, runs `HeartbeatLoopAsync` every
  `GATEWAY_HEARTBEAT_INTERVAL_SEC` (default 10), and clears the entry at teardown — epoch-guarded twice
  (skip if `connection.IsSuperseded`; the store also compares `replicaId` before deleting). TTL =
  `GATEWAY_HEARTBEAT_TTL_SEC` (default 30 = `NatsKvGatewayConnectionStore.DefaultTtlSeconds`).
- Reader: `GatewaysController.BuildViewAsync` → `GatewayAdminView.Connected` (bool). UI: `connected` on
  the admin façade + a badge in the operator-home panel and `/admin/gateways` table.
- Verified offline: unit (bridge wiring, controller read) + Testcontainers (`NatsKvGatewayConnectionStoreTest`:
  round-trip, epoch guard, TTL expiry — runs in the weekly `integration-tests` workflow, not runnable
  without Docker here).
- **Cross-replica flap (known Phase-1 limitation):** if a gateway moves to another replica, the old
  replica's teardown is skipped by the `replicaId` compare, so the new owner's next heartbeat (≤ interval)
  keeps the entry — worst case a ≤ interval window before the entry reflects the new replica. Acceptable
  for observability; revisit if it matters.

## Consequences

- **クラッシュ含む切断が TTL で自律検知**され、レプリカ増減・ローリング更新に強い（ADR-0003 のステートレス性を維持
  したまま「収容の集約ビュー」だけを KV に外出しする）。分散レジストリ（重い共有状態）は依然不要。
- **意味の分離を UI に持ち込む必要**: `connected`（egress 制御ストリームの生死）と `lastTelemetryAt`
  （ingress テレメトリの最終受信）は別物。**ingress 専用ゲートウェイ**（egress binding を持たず制御しない構成）は
  egress ストリームを張らないため、この heartbeat では `connected=false` になる。ここは last-seen が補完する
  ——両方を並べて出し、「接続（制御）」と「受信（テレメトリ）」を別ラベルにする。**この非対称は要合意事項**
  （下記 Open Questions）。
- **gateway 側の対応が前提（同期状態のみ）**: connected は gateway 変更不要（bridge が接続を観測するだけ）。
  一方 `pointlistSynced` の最終形（オプション A）は nexus-gateway の proto 追従が要るため、**Phase を分ける**。
- **KV 書き込み頻度**: gateway 数 × (1/HeartbeatInterval)。数百ゲートウェイ規模でも 10s 間隔なら NATS KV の負荷は軽微
  （`telemetry-latest` はポイント毎に毎フレーム書いており、桁が違う）。ハートビート間隔・TTL は env で調整可能にする。
- **読み取りの追加コスト**: `BuildViewAsync` に KV `Get` が1回増える（gateway 毎）。`LastTelemetryAt` の 500-fan-out に
  比べれば無視できる。

## Open Questions（実装前に確定）

1. **connected の定義合意**: 「egress 制御ストリームの生死」を connected とするか、ingress last-seen も OR して
   「いずれかで生きていれば connected」とするか。→ 本 ADR は **egress ストリーム = connected、last-seen は別軸で併記**を
   推奨するが、運用者の期待と要すり合わせ。
2. **HeartbeatInterval / TTL の既定値**と env 名（`GATEWAY_HEARTBEAT_INTERVAL_SEC` / 派生 TTL 案）。
3. **オプション A vs B の採否**（proto 追加を待つか、`If-None-Match` 記録の暫定 B を先に出すか）。
4. **replicaId の供給元**（ポッド名 / OTEL リソース属性）。
5. **メトリクス**: `gateway.connection.state{gatewayId,state}` 等の SLO 化（ADR-0003 の未決 (c) と合流）。

## 参照

- ADR-0003（`docs/adr/0003-gateway-bridge-stateless-egress.md`）— ステートレス egress の成立条件と「分散レジストリは将来課題」
- `docs/oss-egress-gateway-bridge-plan.md`, `docs/oss-gateway-pointlist-sync.md`
- コード: `NatsKvLatestStore.cs`（KV 先例）, `GatewayConnectionRegistry.cs` / `GatewayEgressService.cs`（フック点）,
  `GatewaysController.cs`（読み取り面 + #230 を予告する既存コメント）, `GatewayPointListSerializer.cs`（ETag）,
  `proto/gateway_egress.proto`（`EgressUp` 拡張点）
