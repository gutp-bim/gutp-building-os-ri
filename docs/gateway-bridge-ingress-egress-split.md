# 設計メモ: GatewayBridge の ingress / egress 分割

## 背景・目的

デプロイ粒度の合理化検証で、`BuildingOS.GatewayBridge` が**性質の異なる2つの責務を1プロセスに同居**させていることが論点になった。
「bridge は ConnectorWorker と同種の取込機能なので、まとめられるのでは」という指摘に対し、
**ingress（取込・データプレーン）を ConnectorWorker 系に寄せ、egress（制御・コントロールプレーン）を専用サービスとして残す**
分割案を整理する。

> 関連: 非推奨 `hono-nats-bridge` の撤去（別PR）。Hono northbound 取込は ConnectorWorker の
> `MqttIngressWorker` / `AmqpIngressWorker` に一本化済み。本メモはその延長で「gRPC 取込」も
> ConnectorWorker 系に寄せられるかを扱う。

## 現状（実装の事実）

`GatewayBridge`（`Microsoft.NET.Sdk.Web`、Kestrel h2c gRPC :8080）は2つの gRPC サービスを同一プロセスでホストする。

| 責務 | サービス | インフラ | 状態 | サブジェクト | スケール軸 | 外部公開 |
|------|---------|---------|------|------------|-----------|---------|
| **egress（制御・下り）** | `GatewayEgressService`（bidi `Connect`） | `GatewayConnectionRegistry` ＋ `NatsEgressCommandBus`（core NATS） | **ステートフル**（per-gateway 常設 bidi ＋ 接続レジストリ） | sub `building-os.control.request.gw.{gatewayId}` / pub `building-os.control.result.{controlId}` | 同時GW接続数（bidi を pod に固定） | **あり**（Traefik IngressRoute `websecure` ＋ `TLSOption clientAuth: RequireAndVerifyClientCert`＝BOWS mTLS） |
| **ingress（取込・上り）** | `GatewayIngressService`（client-stream `StreamTelemetry`） | `NatsIngressTelemetryBus`（JetStream） | **ステートレス**（バッファ/レジストリなし、フレーム毎に即 publish） | pub `building-os.raw.bacnet`（`BUILDING_OS_RAW` stream 共有） | フレーム流量（水平分散可） | あり（BOWS からの telemetry push） |

実装コメントが既に分離前提を明示している:
- `GatewayIngressService`: *"The service keeps no state … so Ingress pods scale horizontally — a separate scale unit from Egress."*
- `Program.cs`: *"Ingress bridge (separate scale unit; shares the BUILDING_OS_RAW stream)."*

つまり **ingress は ConnectorWorker の `MqttIngressWorker` / `AmqpIngressWorker` と機能的に同型**
（外部プロトコル終端 → `building-os.raw.*` へ publish）。一方 egress は外部 mTLS・常設接続・接続レジストリを持つ
コントロールプレーンで、取込とはスケール/セキュリティ/ライフサイクルの軸が異なる。

## 分割方針

```
                 ┌──────────────── 現状: gateway-bridge (1プロセス) ────────────────┐
                 │  GatewayEgress (制御)  +  GatewayIngress (取込)                    │
                 └────────────────────────────────────────────────────────────────┘
                                            │ 分割
        ┌───────────────────────────────────┴───────────────────────────────────┐
        ▼                                                                         ▼
  gateway-egress（専用サービス・据え置き）                          GatewayIngress を ConnectorWorker 系へ
  - GatewayEgressService + Registry + EgressCommandBus              - 外部プロトコル終端 → building-os.raw.bacnet
  - 外部 mTLS / IngressRoute / HPA(接続数)                          - 取込ワーカー群と同居（MQTT/AMQP/gRPC）
  - ステートフル制御プレーン                                        - ステートレス
```

### egress 側（変更小）
`GatewayEgressService` ＋ `GatewayConnectionRegistry` ＋ `NatsEgressCommandBus` を**専用サービスとして存置**。
現行の Helm（`gatewayBridge`: replicas/autoscaling/ingress mTLS/cert-manager）をそのまま引き継ぐ。
イメージ/チャートを将来的に `gateway-egress` へ改名するかは任意（互換のため当面 `gateway-bridge` 名を維持してよい）。

### ingress 側（移設先の選択 ＝ 本メモの主要論点）

**Option A（推奨）: ConnectorWorker に取り込む**
`GatewayIngressService` を ConnectorWorker のホストする gRPC エンドポイントとして移設し、
`Mqtt/AmqpIngressWorker` と並ぶ「gRPC 取込ワーカー」にする。

- 利点: 全テレメトリ取込（MQTT/AMQP/gRPC）が ConnectorWorker に集約され**アーキテクチャが一貫**。
  ご指摘の「取込は ConnectorWorker の役割」を実現。gateway-egress は純粋なコントロールプレーンに縮退。
- コスト/論点: ConnectorWorker が **Kestrel h2c gRPC リスナー（`Microsoft.NET.Sdk.Worker` → `Microsoft.NET.Sdk.Web` 化 or 明示的 WebHost 併設）** を持つ必要。
  さらに **BOWS からの外部 telemetry push を受けるため ConnectorWorker に外部 gRPC 公開面（IngressRoute/mTLS）が増える**
  ＝内部専用だった ConnectorWorker のセキュリティ面が広がる。これが本案最大の判断ポイント。
- 緩和: 取込用 gRPC は専用ポート＋専用 IngressRoute に分離。ConnectorWorker の HPA は NATS 取込負荷主体のままで、
  ステートレス gRPC 取込は水平分散に追従しやすい。

**Option B: 専用 `gateway-ingress` サービスに分離**
`GatewayIngressService` ＋ `NatsIngressTelemetryBus` を独立デプロイにする。

- 利点: ingress/egress の隔離が最もクリーン。各々独立スケール。ConnectorWorker の公開面を広げない。
- コスト: gateway 側のデプロイ単位が **1 → 2 に増える**（粒度削減にはならない）。「ConnectorWorker に寄せる」という
  当初意図とも逆行。

### 比較

| 観点 | A: ConnectorWorker 同居 | B: 専用 gateway-ingress |
|------|------------------------|------------------------|
| デプロイ単位（gateway 関連） | egress 1（ingress は connector に吸収） | egress 1 ＋ ingress 1＝**2** |
| アーキテクチャ一貫性（取込集約） | ◎ | △ |
| ConnectorWorker の外部公開面 | 増える（要 mTLS 分離） | 不変 |
| 実装変更量 | 中（ConnectorWorker の WebHost 化 ＋ proto/DI 移設） | 小（ファイル移動＋chart 複製） |
| スケール独立性 | ○（ステートレスなので可） | ◎ |

**推奨は Option A**。当初の合理化意図（取込を ConnectorWorker に集約）に合致し、egress を純粋なコントロールプレーンに
分離できる。ただし **「ConnectorWorker に外部 gRPC 公開面を持たせてよいか」** が採否の分岐点であり、ここは要判断（下記 Open Decision）。

## 決定（確定 2026-06-11）

**Option A を採用**。ingress（`GatewayIngress`）を ConnectorWorker へ移設し、`GatewayBridge` を egress 専用の
コントロールプレーンに縮退する。下記 Open Decision（ConnectorWorker に外部 gRPC 公開面を持たせてよいか）は
**「許容（ただし opt-in）」で確定**。gRPC 取込は `GRPC_INGRESS_PORT` が設定されたときだけ有効化し、未設定時は
ConnectorWorker は Kestrel を持たない generic host として起動する＝外部公開面は「設定」ではなく「構造」で閉じる。
gRPC 取込は取込の**正本**（architecture-review #173 と整合）なので、本番/Helm では既定で有効、OSS/local/CI は
セーフデフォルトで無効。

> 本 PR のスコープは「.NET コード移設 + テスト + proto 分割 + docs」。Helm/IaC（外部公開面・mTLS）は別 PR。
> gRPC ingest のマルチプロトコル化・validated 直送（passthrough）は egress 拡張性と並ぶ**次フェーズ**のバックログ。

## Option A の移行手順（実装どおり）

1. **proto 分割**: `gateway_bridge.proto` を `gateway_egress.proto`（`GatewayEgress` 系メッセージ、
   `csharp_namespace = BuildingOS.GatewayBridge.Protos`）と `gateway_ingress.proto`（`GatewayIngress` /
   `TelemetryFrame` / `StreamAck`、`csharp_namespace = BuildingOS.ConnectorWorker.Protos`）に分割。
   proto の `package gatewaybridge` は両ファイルで**維持**（gRPC サービスパス
   `/gatewaybridge.GatewayIngress/StreamTelemetry` の wire 互換のため）。各 csproj は必要な側のみ生成。
2. **ConnectorWorker を WebHost 化（host builder 分岐）**: `Microsoft.NET.Sdk.Web` 化し、`GRPC_INGRESS_PORT` 設定時のみ
   `WebApplication`（Kestrel h2c + `MapGrpcService<GatewayIngressService>()`）、未設定時は現状の
   `Host.CreateApplicationBuilder`（generic host、Kestrel 不在）。DI 登録は両者が実装する
   `IHostApplicationBuilder` への共有関数 `ConfigureCommon` に括り出し、サービスグラフを一致させる。
   既存の `BackgroundService` 群はそのまま共存。
3. **コード移設**: `GatewayIngressService` / `NatsIngressTelemetryBus` / `IIngressTelemetryBus` /
   `BacnetTelemetryMapper` を `ConnectorWorker/Connectors/` へ移動（namespace `BuildingOS.ConnectorWorker.Connectors`）。
   `NatsIngressTelemetryBus` は raw 経路（`building-os.raw.bacnet`）への直接 publish を**維持**し、
   `INatsPublisher`（= validated 用の KV publisher デコレーター）には**寄せない**。`MapGrpcService<GatewayIngressService>()` を登録。
4. **gateway-egress の縮退**: `GatewayBridge` から ingress 関連を削除し、egress のみのサービスに。
   `Program.cs` の `MapGrpcService<GatewayIngressService>()` と ingress DI を除去。
5. **Helm/IaC**:
   - `gateway-bridge` chart は egress 専用に（ingress 用ポート/ルート除去、mTLS は据え置き）。
   - ConnectorWorker chart に gRPC 取込ポート＋（必要なら）専用 IngressRoute/mTLS を追加。
   - `connector-worker` の env に取込 gRPC 有効化フラグ（例: `GRPC_INGRESS_PORT`）を追加。
6. **テスト**: 既存の GatewayIngress 単体テスト（`RunAsync` は transport 非依存）を ConnectorWorker テストへ移設。
   egress 側のテストはそのまま。

## 影響範囲（ファイル）

- 移設: `DotNet/BuildingOS.GatewayBridge/Services/GatewayIngressService.cs`,
  `Infrastructure/{IIngressTelemetryBus,NatsIngressTelemetryBus}.cs`, `Mapping/BacnetTelemetryMapper.cs`
  → `DotNet/BuildingOS.ConnectorWorker/`
- 残置（egress）: `Services/GatewayEgressService.cs`, `Infrastructure/{GatewayConnectionRegistry,IEgressCommandBus,NatsEgressCommandBus}.cs`,
  `Mapping/ControlCommandMapper.cs`
- csproj: `BuildingOS.ConnectorWorker.csproj`（Web SDK 化 ＋ `gateway_ingress.proto`/Grpc.AspNetCore 追加 ＋
  `InternalsVisibleTo BuildingOS.Shared.Test`）、`BuildingOS.GatewayBridge.csproj`（`gateway_egress.proto` へ、
  未使用の JetStream パッケージ除去）
- テスト移設: `GatewayIngressServiceTest` / `BacnetTelemetryMapperTest` ＋ fake（`FakeStreamReader<T>`）を
  `BuildingOS.Shared.Test/Infrastructure/ConnectorWorker/` へ。egress テストは GatewayBridge.Test に据え置き
- Helm: `kubernetes/helm/{gateway-bridge,connector-worker}/`、`building-os` umbrella の該当 values（**別 PR**）
- proto: `proto/gateway_bridge.proto` → `gateway_egress.proto` / `gateway_ingress.proto` に分割

## Open Decision（解決済み 2026-06-11）

**ConnectorWorker に外部 gRPC 取込（BOWS telemetry push）を受ける公開面を持たせてよいか → 許容（opt-in）で決定。**
- **Option A** を採用（取込集約・egress 純化）。
- 公開面の拡大は `GRPC_INGRESS_PORT` の opt-in に閉じ込め、未設定時は generic host で listener を一切持たない。
- 外部公開面の保護（専用ポート分離 / IngressRoute / mTLS）は Helm/IaC PR（別 PR）で対応。

## 非対象（本 PR）

- ~~gRPC ingest 契約の一般化~~ → **#181 で対応済み**（別 PR）。正本は **point-id ベース**：
  BuildingOS と GW は point list を共有するため、GW がプロトコル固有アドレス（BACnet device/object/
  instance、ベンダー unit id 等）を `point_id` にローカル解決し、`TelemetryFrame` は
  `gateway_id + point_id + value + timestamp(+optional attributes)` のみを運ぶ。サーバは `point_id` で
  ツインから静的メタdata（building/device/name）を補完（`IPointMetadataCache` でローカルキャッシュ）し、
  `building-os.validated.telemetry` へ直送（raw.{protocol} ホップ・プロトコル別コネクタ不要）。未知 `point_id`・
  gateway 所有権不一致のフレームは skip。`gateway_id` の全体一意性はシードインポート時に検証（複数棟で衝突→起動失敗）。
  MQTT/Hono コネクタは point list 非共有デバイス用に存続。
- egress 側の挙動変更（接続レジストリ・mTLS・keepalive はそのまま）。
- ConnectorWorker の既存取込ワーカーのロジック変更。
- Helm/IaC（外部公開面・mTLS）→ 別 PR。
