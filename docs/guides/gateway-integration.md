# Gateway Integration — ゲートウェイ接続モデル

Building OS は現場プロトコル（BACnet 等）の差異を**ゲートウェイ**で吸収し、Building OS とゲートウェイは
**共有 point list 上の `(gateway_id, point_id)` を契約境界**にします（#181）。本書はゲートウェイがどう
接続・認証・同期するかを、利用者・ゲートウェイ実装者向けに一枚で説明します。深掘りは末尾のリンク先へ。

> 参照実装のゲートウェイは別リポジトリの **nexus-gateway**（BOWS 互換の egress エージェント）です。

---

## 1. 設計の要：point-id 正準契約

- Building OS とゲートウェイは **同じ point list を共有**します。正本は Building OS のデジタルツイン
  （OxiGraph `sbco:PointExt`）。
- ゲートウェイは現場のネイティブアドレス（BACnet device/object/instance、ベンダ単位 ID 等）を
  **ローカルで `point_id` に解決**します。
- したがって線（ワイヤ）に載るのは Building OS が導出できない最小限だけ:
  **`gateway_id`（経路/出自）+ `point_id`（同一性）+ `value` + `timestamp`**。
  建物/機器/名称/単位などの静的メタデータは毎フレーム送らず、Building OS が twin から `point_id` で補完します。
- **所有関係**は twin が持ちます（point の `sbco:gatewayId` + `sbco:EquipmentExt sbco:hasPoint`）。
  `gateway_id` の建物内一意性は seed 取り込み時に検証されます。

この契約により、プロトコル差異の隠蔽・テレメトリ欠落の検知・誤送信の拒否が成立します
（評価軸 E5、[evaluation-summary.md](../reference/evaluation-summary.md)）。

---

## 2. 2 つのストリーム：ingress（テレメトリ）と egress（制御）

ingress と egress は**別サービス・別ストリーム・別スケール単位**です（#178 で分離）。

```
                       ┌──────────────────────── Building OS ────────────────────────┐
 Gateway               │                                                              │
  telemetry  ──gRPC──▶ │ ConnectorWorker : GatewayIngress (client-stream)  :5051      │
                       │      └─ twin 補完 → building-os.validated.telemetry          │
                       │                                                              │
  control    ◀─gRPC──▶ │ GatewayBridge   : GatewayEgress  (bidi stream)    :5052      │
   (BOWS)              │      ▲ NATS per-gateway: building-os.control.request.gw.{id} │
                       └──────────────────────────────────────────────────────────────┘
```

### 2-1. Ingress（テレメトリ取り込み）— `GatewayIngress`
- ホスト: **ConnectorWorker**（`GRPC_INGRESS_PORT`、ローカル例 `:5051`）。
- 契約: `rpc StreamTelemetry(stream TelemetryFrame) returns (StreamAck)`（`proto/gateway_ingress.proto`）。
- 各フレームを twin メタデータで enrich（`point_id` → building/device/name、`IPointMetadataCache`）し、
  `building-os.validated.telemetry` へ **JetStream publish-ack** で直送（raw.{protocol} ホップなし）。
- **拒否（skip + メータ）**: 未知の `point_id` / `gateway_id` が当該 point を所有しない / publish 不成立。
  `StreamAck.accepted` は永続化に成功したフレーム数のみ。
- ステートレス → 水平スケール可。OSS 既定は **未起動**（`GRPC_INGRESS_PORT` 未設定＝health のみ）。

### 2-2. Egress（制御）— `GatewayEgress`
- ホスト: **GatewayBridge**（`GRPC_PORT`、ローカル例 `:5052`）。
- 契約: `rpc Connect(stream EgressUp) returns (stream EgressDown)`（`proto/gateway_egress.proto`）。
  ゲートウェイは `Hello{gateway_id}` で接続 → Building OS が `EgressDown{ControlCommand}` を down 送信 →
  ゲートウェイが `EgressUp{ControlResult}` を返す。
- ルーティング: API `POST /points/{id}/control` → NATS **per-gateway** subject
  `building-os.control.request.gw.{gatewayId}` → 当該 gateway のストリームを持つ Bridge レプリカが down 送信。
- **オフライン即時 503**（#186）: per-gateway は NATS *request* で送られ、生存レプリカが ack。購読者が
  いない（gateway 未接続）と no-responders → API は**結果タイムアウトを待たず 503**。
- `ControlCommand` は point-id 正準（`control_id` + `point_id` + `present_value` + `priority`）。ゲートウェイが
  `point_id` → BACnet object/instance を共有 point list でローカル解決。

> ローカル開発: ingress と egress は**別ポート**（5051 / 5052）です。参照ゲートウェイの E2E は
> `E2E_BOS_EGRESS_ADDR=localhost:5052`。本番は ingress LB が gRPC service path
> （`gatewaybridge.GatewayIngress` / `gatewaybridge.GatewayEgress`）で振り分け、単一アドレスに見せられます。

---

## 3. Point List 同期（twin → gateway）

twin が point list の正本で、ゲートウェイはこれに追従します（#224）。

- `GET /gateways/{gatewayId}/pointlist` — 当該 gateway 所有の点を、ネイティブアドレス
  （`sbco:localId` / `deviceIdBacnet` / `objectTypeBacnet` / `instanceNoBacnet`）・単位・writable・
  制御スキーマ（`bos:*`）・機器グルーピング付きで返す。
- **版管理 = 内容ハッシュ ETag**（`"sha256:..."`, 順序非依存）。`If-None-Match` 一致で **304** → 安価にポーリング。
- **差分**: `?since={etag}`（added/removed/changed）。
- **push**: twin seed が `building-os.pointlist.updated.gw.{id}` を発火 → GatewayBridge が
  `EgressDown{PointListUpdate}` を down 送信 → ゲートウェイが ETag で再検証（push は最適化、ETag ポーリングが
  信頼性のバックストップ）。

---

## 4. 認証と信頼境界

- **ゲートウェイ認証はマシン認証**（ユーザ RBAC ではない）。mTLS 終端 ingress（Traefik/Envoy）が検証済みの
  gateway id を**信頼ヘッダ**（既定 `X-Gateway-Id`、`passTLSClientCert` で証明書 subject から導出）で注入。
- `GET /gateways/{id}/pointlist` は **ヘッダ gateway_id == path gatewayId** を要求（admin JWT は ops バイパス）。
  当該ルートは mTLS ingress 経由のみ到達可とし、信頼ヘッダは非信頼経路で必ず除去すること。
- ingress/egress の gRPC は h2c（平文 HTTP/2）。**TLS/mTLS は ingress（Traefik/Envoy）で終端**する前提。
- **ingress テレメトリの gateway_id 束縛（#296）**: `GatewayIngress` は mTLS が検証した gateway id を
  信頼ヘッダ（既定 `X-Gateway-Id`、証明書 SAN/CN 由来）で受け取り、`GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true`
  のとき各フレームの `gateway_id` と一致しなければ拒否（skip + メータ `identity_mismatch`/`identity_missing`）。
  これにより「有効な証明書を持つ GW が他 GW を名乗る」なりすまし注入を防ぐ。**既定 OFF**（後方互換: フレームの
  `gateway_id` は provenance として扱い、所有権は twin で検証）— mTLS ingress 配線のある本番で ON にする。
- 詳細な信頼境界・Traefik 配線は [oss-gateway-bridge-infra.md](../operations/oss-gateway-bridge-infra.md) と
  [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md)。

---

## 5. ローカルで試す

```bash
# ingress を有効化（テレメトリを gRPC で受ける）
GRPC_INGRESS_PORT=5051 docker compose -f docker-compose.oss.yaml up -d --force-recreate \
  --no-deps building-os.connector-worker

# egress（制御）は GatewayBridge が常時 :5052 で待受。制御を per-gateway egress に流すには、
# 対象 gateway の binding を bacnet-sim にする（API 側）:
#   GatewayConnectionTypes__Map__<gatewayId>=bacnet-sim
```

最小の gRPC 送受信例は E2E ハーネス（`Tools/e2e-performance/s10`〜`s16`、proto を実行時コンパイル）が
そのまま参考になります。取り込み品質・制御安全性・整合性の実測は
[evaluation-summary.md](../reference/evaluation-summary.md)（E1/E5/E6）。

---

## 6. さらに詳しく

- [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md) — point list 同期 API（ETag/差分/push、mTLS）
- [oss-egress-gateway-bridge-plan.md](../project/oss-egress-gateway-bridge-plan.md) — egress 制御プレーン設計
- [gateway-bridge-ingress-egress-split.md](../architecture/gateway-bridge-ingress-egress-split.md) — ingress/egress 分離
- [oss-gateway-bridge-infra.md](../operations/oss-gateway-bridge-infra.md) — Traefik/mTLS インフラ配線
- [oss-gateway-security-ops.md](../operations/oss-gateway-security-ops.md) — 証明書発行/ローテーション/失効・gateway_id 束縛・enforce 段階導入
- [telemetry-specification.md](../architecture/telemetry-specification.md) — テレメトリ契約（`ValidMessageJson`）
- `proto/gateway_ingress.proto` / `proto/gateway_egress.proto` — gRPC 契約の正本
