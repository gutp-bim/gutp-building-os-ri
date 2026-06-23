# システムアーキテクチャ

Building OS OSS はスマートビル運用向けのセルフホスト型 IoT
プラットフォームです。設備データを収集し、プロトコル別 payload を
正規化し、時系列データと建物トポロジを保存し、REST / gRPC API と
Next.js UI から利用できるようにします。

## 構成概要

```text
IoT devices / edge gateways
  -> MQTT broker / Eclipse Hono / optional IoT Hub bridge
  -> NATS JetStream raw subjects
  -> ConnectorWorker protocol normalizers
  -> NATS JetStream validated telemetry subject
  -> TimescaleDB telemetry writer
  -> API Server
  -> Web Client (含 (admin) ワークスペース)
```

主要サービス:

| 領域 | OSS コンポーネント | 役割 |
|---|---|---|
| Message bus | NATS JetStream | Raw telemetry、正規化済み telemetry、point-control request/result |
| Time-series store | TimescaleDB on PostgreSQL | Hot/warm telemetry のクエリストア |
| Digital twin graph | OxiGraph | RDF/SPARQL による building / floor / space / device / point トポロジ |
| Object storage | MinIO | S3 互換 cold storage と export artifact |
| Identity provider | Keycloak | OIDC issuer、JWT 検証元、realm/client/role 管理 |
| MQTT ingress | Mosquitto / Eclipse Hono | MQTT device ingress と IoT Hub 型デバイスからの移行経路 |
| Observability | OpenTelemetry, Prometheus, Grafana, Loki, Tempo | Metrics、dashboard、logs、traces |
| Deployment | Docker Compose, Helm, Argo CD, OpenTofu | ローカルスタック、Kubernetes リリース、GitOps、IaC |

Azure SDK は原則として再導入しません。例外として
`BuildingOS.ConnectorWorker` 内の `Microsoft.Azure.Devices` だけは意図的に
残しています。既存 BACnet エッジゲートウェイが IoT Hub direct method を
受ける構成から段階移行するためのブリッジ用途です。

## ランタイムコンポーネント

### API Server

`DotNet/BuildingOS.ApiServer` は ASP.NET Core の API 境界です。REST
endpoint、gRPC service、Keycloak JWT 検証、telemetry / graph read API、
point-control の入口を提供します。

認証は標準の JWT bearer middleware で検証します。ローカル開発では
`DISABLE_AUTH=true` で認証を無効化できますが、本番では Keycloak issued token
を使います。

### ConnectorWorker

`DotNet/BuildingOS.ConnectorWorker` は長時間稼働する .NET worker host です。
各 worker はプロトコル別の `building-os.raw.*` subject を購読し、raw payload
を JSON Schema で検証し、`valid-message.json` 形式へ変換して
`building-os.validated.telemetry` へ publish します。

現行 subject:

| Raw subject | Worker | 入力 |
|---|---|---|
| `building-os.raw.hvac` | `HvacConnectorWorker` | HVAC JSON |
| `building-os.raw.bacnet` | `BacnetConnectorWorker` | BACnet point values |
| `building-os.raw.environmental` | `EnvironmentalConnectorWorker` | 環境センサー |
| `building-os.raw.electric` | `ElectricConnectorWorker` | 電力メーター |
| `building-os.raw.behavior` | `BehaviorConnectorWorker` | 行動センサー |
| `building-os.control.request` | `NatsPointControlWorker` | 機器制御コマンド |

### Frontend Apps

`web-client` がメイン dashboard で、users / groups / permissions 管理は
その `(admin)` ワークスペース（`/admin` 配下）です（旧 `admin-console` アプリを統合）。
Next.js アプリケーションで Keycloak OIDC を使います。Frontend の API 呼び出しは
生成済み client（Aspida/Zodios/gRPC）または `(admin)` の認証付き bespoke fetch
（`src/lib/admin/`）を経由します。

### Data Services

TimescaleDB は正規化済み時系列データを保存します。OxiGraph は建物トポロジと
制御 schema metadata を RDF triple として保存します。MinIO は cold data と
export artifact の S3 互換 object storage です。

## データフロー

1. Device は MQTT/Hono または既存 bridge 経路で protocol-specific payload を
   publish します。
2. Ingress layer は raw message を `building-os.raw.*` へ publish します。
3. Connector worker は raw message を
   `DotNet/BuildingOS.Shared/Defines/Schemas/` の schema で parse / validate
   します。
4. 有効な payload は `ValidMessageJson` へ変換され、
   `building-os.validated.telemetry` へ publish されます。
5. Telemetry writer が TimescaleDB へ保存します。
6. API query は TimescaleDB の telemetry と OxiGraph の topology metadata を
   組み合わせます。
7. Web apps は Keycloak token 付きで API Server を呼び出します。

## 機器制御フロー

```text
Web Client
  -> API Server / gRPC point-control service
  -> building-os.control.request
  -> NatsPointControlWorker
  -> protocol handler
  -> building-os.control.result.<controlId>
  -> gRPC stream back to client
```

Protocol handler には、Kandt ゲートウェイ（Azure IoT Hub direct-method bridge 経由・
下流は BACnet）への制御と、Eclipse Hono AMQP Northbound 経由の制御があります。

## デプロイ構成

ローカル開発では `docker-compose.oss.yaml` が NATS、TimescaleDB、OxiGraph、
MinIO、Keycloak、Prometheus、Grafana、Loki、Tempo、Mosquitto を起動します。
Mosquitto は Scenario A (OSS オンプレ MQTT ingress) の MQTT ブローカーとして
OSS スタックの正式コンポーネントです。ローカルデバイスシミュレーターを追加する
場合は `make local-up-dev`（`docker-compose.dev.yaml`）を使ってください。

Kubernetes deployment は `kubernetes/helm/` 配下の Helm chart を使います。
`argocd/` の manifest は GitOps deployment を提供します。`opentofu/` は
infrastructure state と platform dependency の provisioning を扱います。

## セキュリティモデル

Keycloak が identity provider です。Realm と client は frontend apps 用の
browser client と backend automation 用の service client を定義します。API
Server は JWT claim を `AuthorizationContext` へ変換し、admin status、role、
permission strings を保持します。

Permission string の形式:

```text
{resourceType}:{resourceId}:{actions}
```

詳細な対応は
[`keycloak-permission-mapping.md`](keycloak-permission-mapping.md) と
[`keycloak-admin-provisioning.md`](keycloak-admin-provisioning.md) に記載しています。

## 関連ドキュメント

- [テレメトリ仕様](telemetry-specification.md)
- [NATS JetStream 設計](oss-nats-design.md)
- [TimescaleDB スキーマ](oss-timescaledb-schema.md)
- [SPARQL マッピング](oss-sparql-mapping.md)
- [Hono + EMQX 設計](oss-hono-design.md)
- [Next.js Kubernetes ロールアウト](nextjs-k8s-rollout.md)
- [Azure vs OSS 機能比較](oss-feature-comparison.md)
