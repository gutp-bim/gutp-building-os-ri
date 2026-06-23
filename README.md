# Building OS — OSS Edition

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](./LICENSE)

スマートビルディング管理のためのオープンソース IoT プラットフォーム。  
gRPC / MQTT / NATS 経由でビル設備（HVAC・電力・環境センサー等）のデータを収集し、  
MinIO 上の Parquet レイク（+ 最新値は NATS KV）にストア、REST + gRPC API と Next.js ダッシュボードで提供します。

> ℹ️ 本プロダクトは **東京大学 グリーン ICT プロジェクト** の研究成果物の派生として作られたものです。  
> **利用にかかわる一切について、開発者・関係者・派生元は一切の責任を負いません（無保証）。** 詳細は [免責事項](#免責事項-disclaimer) を参照してください。

> 🔒 セキュリティ上の問題を発見した場合は [SECURITY.md](./SECURITY.md) に従って非公開で報告してください。

---

## アーキテクチャ

```
IoT Devices / Integration Gateway
   ├─ gRPC GatewayIngress（正本：(gateway_id, point_id) 契約）┐
   ├─ MQTT(Mosquitto) → building-os.raw.mqtt ───────────────┤→ ConnectorWorker
   └─ Hono(AMQP)      → building-os.raw.hono ────────────────┘  （twin メタ付与・正規化）
                                                                │
                              NATS JetStream（コアバス）
                              building-os.validated.telemetry
                                       ├─► NATS KV telemetry-latest    ← Hot：最新1点/point
                                       └─► ParquetLakeWriterWorker ─► MinIO Parquet レイク（Warm/Cold 既定）
                                                                │
                                    API Server（ASP.NET Core REST + gRPC）
                                    ・/telemetries/query（Hot/Warm/Cold 自動選択）
                                    ・/resources/search（横断検索）
                                    ・/gateways/{id}/pointlist（ゲートウェイ Point List 同期）
                                                                │
                                            Web Client（Next.js）
                                  /resources ツリーエクスプローラ + /admin 管理ワークスペース

制御: API → NATS control.request[.gw.{id}] → NatsPointControlWorker / GatewayBridge(GatewayEgress) → 現場
```

**デジタルツイン:** OxiGraph（SPARQL / SBCO）がビル→フロア→スペース→機器→ポイントの階層を管理（point list の正本）。  
**リレーショナル DB:** PostgreSQL 16（ユーザー・グループ・権限 + `point_control_audit`、EF Core）。  
**テレメトリ:** Hot=NATS KV（最新値）/ Warm・Cold=MinIO 上の統合 Parquet レイク（S3 互換）。  
**認証:** Keycloak（OIDC / JWT）。ゲートウェイ provisioning は mTLS マシン認証。  
**可観測性:** OpenTelemetry → Prometheus + Grafana + Loki + Tempo。

> **⚠️ Breaking change（#216 / #234）: 既定の Warm 層が `parquet` になり、TimescaleDB は既定スタックから外して選択式（opt-in）になりました。**
> 既定ではテレメトリ Warm/Cold は MinIO 上の統合 Parquet レイク、`point_control_audit` は PostgreSQL
> （EF Core）を使い、**TimescaleDB は既定では不要**です（compose の DB イメージは `postgres:16`）。TimescaleDB
> Warm 構成を選ぶ場合は `WARM_STORE=timescale`（compose は `--profile timescale` +
> `TIMESCALE_CONNECTION_STRING` を手動指定）。
> 詳細は [docs/oss-warm-parquet-lake.md](docs/oss-warm-parquet-lake.md) / [docs/oss-tier-architecture.md](docs/oss-tier-architecture.md)。

> **ゲートウェイ Point List 同期（#224）**：twin を正本に、`GET /gateways/{id}/pointlist` でゲートウェイが
> native addressing 付き Point List を取得（内容ハッシュ ETag / `If-None-Match`→304 / `?since=` 差分）。
> twin 更新時は GatewayEgress ストリーム経由で push 通知。認証は mTLS 由来の信頼ヘッダ。
> 詳細は [docs/oss-gateway-pointlist-sync.md](docs/oss-gateway-pointlist-sync.md)。

> **Azure IoT Hub について**  
> `ConnectorWorker` は Kandt ゲートウェイへの制御コマンドを Azure IoT Hub ダイレクトメソッド経由で送信するオプションハンドラ（`KandtDeviceControlHandler`、下流は BACnet）を含みます。  
> これは既存設備との後方互換ブリッジであり、新規デプロイメントには不要です。

---

## 技術スタック

| レイヤー | 技術 |
|----------|------|
| Backend / API | .NET 8, ASP.NET Core, REST, gRPC-web |
| Worker | .NET `BackgroundService`, NATS JetStream durable consumers |
| Frontend | Next.js 15, React 19, TypeScript 5, Tailwind CSS 4 |
| 認証 | Keycloak, OIDC, JWT bearer |
| メッセージバス | NATS JetStream |
| テレメトリ | Hot=NATS KV（最新値）/ Warm・Cold=MinIO Parquet レイク（既定。TimescaleDB は opt-in）|
| リレーショナル DB | PostgreSQL 16（ユーザー・グループ・権限 + point_control_audit、EF Core）|
| デジタルツイン | OxiGraph, RDF, SPARQL |
| Blob / コールドデータ | MinIO, S3-compatible, Parquet |
| IoT 接続 | MQTT, Mosquitto, Eclipse Hono（オプション）|
| 可観測性 | OpenTelemetry, Prometheus, Grafana, Loki, Tempo |
| IaC / デリバリー | Docker Compose, Helm, Argo CD, OpenTofu |

---

## 前提条件

| ツール | バージョン | 用途 |
|--------|-----------|------|
| Docker Desktop | 最新 | OSS スタック起動 |
| .NET SDK | 8.0+ | バックエンドビルド |
| Node.js | 22+ | フロントエンドビルド |
| Buf CLI | 最新 | proto → TypeScript コード生成 |

---

## クイックスタート

> 🚀 はじめての方は **[docs/getting-started.md（オンボーディング）](docs/getting-started.md)** が、起動→API/Web→
> テレメトリ投入→読取/制御 までを一筆書きで案内します。ゲートウェイ接続は
> **[docs/gateway-integration.md](docs/gateway-integration.md)**、評価結果は
> **[docs/evaluation-summary.md](docs/evaluation-summary.md)**。

### 1. OSS スタックを起動

```bash
make local-up-oss
# または: docker compose -f docker-compose.oss.yaml up -d
```

すべてのサービスが healthy になるまで待機する場合:

```bash
make wait-oss-stack
```

### 2. API サーバーを起動

```bash
cd DotNet/BuildingOS.ApiServer
dotnet run --launch-profile WithLocal
# → http://localhost:5000
# → http://localhost:5000/swagger  （Swagger UI）
```

`WithLocal` プロファイルは `DISABLE_AUTH=true` を設定しており、  
Keycloak なしでローカル開発できます。  
認証を有効にする場合は `--launch-profile WithLocalAuth` を使用してください。

### 3. Web クライアントを起動

```bash
cd web-client
yarn install
yarn dev
# → http://localhost:3000
```

> ユーザー・権限管理は web-client の `(admin)` ワークスペース（`http://localhost:3000/admin`）に統合済みです（別アプリの起動は不要）。
> リソース閲覧は `/resources`（ツリーエクスプローラ + 横断検索）。

### 4. ConnectorWorker を起動（NATS 稼働後）

```bash
cd DotNet/BuildingOS.ConnectorWorker
dotnet run
```

---

## 開発ガイド

### OSS スタック管理

```bash
make local-up-oss        # 起動
make local-down-oss      # 停止
make wait-oss-stack      # ヘルスチェック待機（最大 120 秒）
make test-oss-stack      # スタック疎通テスト
```

### バックエンド（DotNet/）

```bash
cd DotNet
dotnet restore
dotnet build

# ユニットテスト
dotnet test --filter "FullyQualifiedName!~IntegrationTest"

# 統合テスト（Docker 必須）
dotnet test BuildingOS.IntegrationTest

# EF Core マイグレーション追加
cd BuildingOS.Shared
../../Tools/add-migration-file.bash <MigrationName>
```

### フロントエンド（web-client/）

```bash
cd web-client
yarn install
yarn dev          # Turbopack 開発サーバー
yarn typecheck
yarn lint
yarn build

# proto → TypeScript コード生成（proto/ 変更後）
yarn generate
```

### 管理画面（web-client の `(admin)` ワークスペース）

ユーザー・権限管理は web-client に統合され、`/admin` 配下で提供されます（別アプリ・別ポートは不要）。`web-client` の `yarn dev` で起動し、`http://localhost:3000/admin` でアクセスします。

### コード生成（Tools/）

```bash
cd Tools
./generate-dotnet-entities-from-schema.bash   # JSON Schema → C# エンティティ
./generate_swagger.bash                        # OpenAPI 定義生成（API Server 起動中に実行）
./sync-type.bash                               # Swagger → Aspida 型同期
```

---

## テスト

### ユニットテスト・統合テスト

```bash
cd DotNet

# ユニットテスト（高速）
dotnet test --filter "FullyQualifiedName!~IntegrationTest"

# 統合テスト（Testcontainers、Docker 必須、約 2 分）
dotnet test BuildingOS.IntegrationTest

# 特定テストのみ実行
dotnet test --filter "FullyQualifiedName~<TestName>"
```

> **CI はテスト系ワークフローを手動起動（`workflow_dispatch`）のみ**に制限しています（クレジット節約）。
> push / PR では自動実行されません。**ローカル検証が主ゲート**（`dotnet test` / `yarn test`・`typecheck`・
> `lint` / `yarn build`）。必要時は GitHub Actions タブから手動実行してください。

### E2E パフォーマンス・品質テスト

OSS スタック稼働中に実行します。

```bash
cd Tools/e2e-performance

# 初回のみ: Python 仮想環境と k6 のセットアップ
uv venv .venv
uv pip install -r requirements.txt --python .venv/bin/python
# k6: https://k6.io/docs/get-started/installation/

# Smoke テスト（S1: データパイプライン品質検証）
bash smoke.sh

# S5: API Read Path パフォーマンステスト（API Server 起動中に実行）
k6 run -e BASE_URL=http://localhost:5000 -e DURATION=3m k6/s5_api_read.js
```

詳細は [`Tools/e2e-performance/README.md`](Tools/e2e-performance/README.md) と  
[`docs/e2e-performance-quality-test-plan.md`](docs/e2e-performance-quality-test-plan.md) を参照してください。

**実施済みテストの結果:** [`Tools/e2e-performance/PERFORMANCE_SUMMARY.md`](Tools/e2e-performance/PERFORMANCE_SUMMARY.md)

### 参照アーキテクチャ E2E 定量評価（E1–E8 + KPI ゲート）

評価軸 E1–E8 を `e2e/runner/run-all.sh` で実行し、`gate.py` が [`e2e/kpi-thresholds.yaml`](e2e/kpi-thresholds.yaml) と
突合して pass/fail とヘッドライン指標を出力します。

**最新の実測レポート:** [`e2e/evaluation-report.md`](e2e/evaluation-report.md)（gate **PASS**:
ingest E2E p95 2.7ms / latest p95 51ms / warm 24h p95 101ms / point resolution 1.000）。
計画・各軸の手順は [`e2e/`](e2e/) を参照。

---

## プロジェクト構成

```
DotNet/
├── BuildingOS.ApiServer/        # ASP.NET Core REST + gRPC API
│   ├── Controllers/             # REST（Building, Telemetry, ResourceSearch, GatewayProvisioning, Users, …）
│   ├── GatewayProvisioning/     # Point List 同期（ETag/差分・mTLS 信頼ヘッダ識別）#224
│   ├── Modules/                 # 環境変数（EnvModule）
│   └── Startup/                 # DI 設定
├── BuildingOS.ConnectorWorker/  # NATS コネクタワーカー群 + gRPC GatewayIngress（テレメトリ取り込み正本）
│   ├── Connectors/              # 各プロトコルのワーカー（HVAC, BACnet, MQTT, Hono, …）+ GatewayIngress
│   └── Infrastructure/          # KandtDeviceControlHandler（IoT Hub direct method）
├── BuildingOS.GatewayBridge/    # gRPC ⇄ NATS egress ブリッジ（外部 BOWS 制御プレーン: GatewayEgress）
├── BuildingOS.Shared/           # ドメイン層・インフラ層・共有ライブラリ
│   ├── Defines/Schemas/         # JSON Schema 定義（エンティティの唯一の正）
│   ├── Defines/Entities/        # 自動生成エンティティ — 手動編集不可
│   ├── Domain/                  # ドメインモデル（Authorization, Grouping, …）
│   └── Infrastructure/          # OxiGraph, MinIO（Parquet レイク）, NATS, ControlRouting, Keycloak
├── BuildingOS.IntegrationTest/  # Testcontainers 統合テスト
├── BuildingOS.ApiServer.Test/   # xUnit（ApiServer：検索・gateway provisioning 等）
└── BuildingOS.Shared.Test/      # xUnit ユニットテスト

web-client/                      # Next.js ダッシュボード + (admin) 管理ワークスペース（port 3000）
                                 #   /resources ツリーエクスプローラ + 横断検索、/admin 管理
                                 #   lib/resources・lib/telemetry = API アクセスファサード層
proto/                           # Protobuf 定義（point_control / gateway_ingress / gateway_egress）
oss-stack/                       # Docker Compose 設定ファイル（NATS, Postgres, …）
e2e/                             # 参照アーキテクチャ E2E 評価計画（論文向け、E1–E8 / KPI / runner）
Tools/
├── e2e-performance/             # E2E パフォーマンス・品質テスト（k6, Python）
├── development-edge-device/     # デバイスシミュレータ（MQTT）
├── auth-proxy-server/           # ローカル開発用認証プロキシ
└── workload-test-project/       # 負荷試験
kubernetes/                      # Helm チャート
opentofu/                        # OpenTofu（Terraform）IaC
argocd/                          # Argo CD GitOps マニフェスト
observability/                   # Prometheus / Grafana / Loki / Tempo 設定
docs/                            # アーキテクチャ・設計ドキュメント
```

---

## ローカルポート一覧

OSS スタック（`docker-compose.oss.yaml`）と各コンポーネントが使用するポートです。

| ポート | サービス | URL |
|--------|---------|-----|
| 4222 | NATS（クライアント） | `nats://localhost:4222` |
| 8222 | NATS（モニタリング） | http://localhost:8222 |
| 5433 | PostgreSQL 16 | `localhost:5433` |
| 7878 | OxiGraph（SPARQL） | http://localhost:7878 |
| 9000 | MinIO（S3 API） | http://localhost:9000 |
| 9001 | MinIO（Web Console） | http://localhost:9001 |
| 8080 | Keycloak | http://localhost:8080 |
| 9090 | Prometheus | http://localhost:9090 |
| 3010 | Grafana | http://localhost:3010 |
| 3100 | Loki | — |
| 4317 | Tempo（OTLP gRPC） | — |
| 1883 | Mosquitto（MQTT） | `mqtt://localhost:1883` |
| **5000** | **API Server**（`WithLocal`）| http://localhost:5000 |
| **3000** | **Web Client**（`/admin` に管理ワークスペース）| http://localhost:3000 |

---

## 環境変数リファレンス

### API サーバー

| 変数 | 説明 | デフォルト |
|------|------|-----------|
| `PORT` | HTTP リッスンポート | `8080` |
| `ASPNETCORE_ENVIRONMENT` | 環境名 | — |
| `LOG_LEVEL` | ログレベル（`Information`/`Warning`/`Error`） | — |
| `POSTGRES_CONNECTION_STRING` | PostgreSQL（ユーザー・グループ・権限 + point_control_audit）| — |
| `WARM_STORE` | Warm 層モード。`parquet`（既定）/ `timescale`（opt-in）| `parquet` |
| `MINIO_ENDPOINT` / `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` | Parquet レイク（MinIO/S3）| — |
| `TIMESCALE_CONNECTION_STRING` | TimescaleDB（`WARM_STORE=timescale` 時のみ・opt-in）| — |
| `OXIGRAPH_ENDPOINT` | OxiGraph SPARQL エンドポイント | `http://localhost:7878` |
| `NATS_URL` | NATS 接続 URL | `nats://localhost:4222` |
| `DISABLE_AUTH` | 認証スキップ（ローカル開発用） | `false` |
| `KEYCLOAK_AUTHORITY` | Keycloak issuer URL | — |
| `KEYCLOAK_CLIENT_ID` | JWT audience | — |
| `KEYCLOAK_REALM` | Keycloak realm | — |
| `KEYCLOAK_ADMIN_CLIENT_ID` | Admin API クライアント ID | — |
| `KEYCLOAK_ADMIN_CLIENT_SECRET` | Admin API クライアントシークレット | — |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP エクスポート先 | — |
| `OTEL_SERVICE_NAME` | サービス名（トレース） | `building-os-api` |

### ConnectorWorker

| 変数 | 説明 | 必須 |
|------|------|------|
| `NATS_URL` | NATS 接続 URL | ✅ |
| `IOT_HUB_CONNECTION_STRING` | Azure IoT Hub（Kandt ゲートウェイ制御）| Kandt のみ |
| `IOT_EDGE_MODULE_ID` | IoT Edge モジュール ID | Kandt のみ |

> **セキュリティ:** `docker-compose.oss.yaml` の認証情報はすべて `${VAR:-default}` で外部化されています。  
> デフォルト値は **ローカル開発専用** です。本番環境では Kubernetes Secret または外部シークレットマネージャで注入してください。

---

## コネクタ層の拡張

ConnectorWorker は `building-os.raw.*` subject を購読し、正規化した `ValidTelemetryData` スキーマに変換して `building-os.validated.telemetry` に publish します。

| Subject | ワーカー |
|---------|---------|
| `building-os.raw.hvac` | `HvacConnectorWorker` |
| `building-os.raw.bacnet` | `BacnetConnectorWorker` |
| `building-os.raw.environmental` | `EnvironmentalConnectorWorker` |
| `building-os.raw.electric` | `ElectricConnectorWorker` |
| `building-os.raw.behavior` | `BehaviorConnectorWorker` |
| `building-os.control.request` | `NatsPointControlWorker` |

**新しいコネクタを追加する手順:**

1. `BuildingOS.Shared/Defines/Schemas/` に JSON Schema を作成
2. `./generate-dotnet-entities-from-schema.bash` でエンティティを生成
3. `BuildingOS.ConnectorWorker/Connectors/` に `ConnectorWorkerBase` を継承したクラスを実装
4. `BuildingOS.ConnectorWorker/Program.cs` に `AddHostedService` 登録を追加
5. `BuildingOS.Shared.Test/` にユニットテストを追加

メッセージスキーマの詳細は [`docs/telemetry-specification.md`](docs/telemetry-specification.md)、  
NATS subject / stream 設計は [`docs/oss-nats-design.md`](docs/oss-nats-design.md) を参照してください。

---

## デプロイ構成（Docker Compose / Kubernetes）

### Docker Compose プロファイル

用途別に複数の Compose ファイルを用意しています。ローカル開発・PoC 向けで、**デフォルトの認証情報はローカル専用**です（本番では `.env` / Kubernetes Secret / シークレットマネージャを使用）。

| ファイル | 用途 | 起動コマンド |
|---------|------|------------|
| `docker-compose.oss.yaml` | **OSS フルスタック**（NATS / PostgreSQL 16 / OxiGraph / MinIO / Keycloak / Prometheus / Grafana / Loki / Tempo 等）。MQTT ブローカ（Mosquitto）は任意で `--profile mqtt`（#25） | `make local-up-oss`（= `docker compose -f docker-compose.oss.yaml up -d`） |
| `docker-compose.minimal.yaml` | **最小構成**（NATS + PostgreSQL 16 + pgBouncer）。PoC・軽量開発向け | `make local-up-minimal` |
| `docker-compose.dev.yaml` | OSS スタック起動済みを前提に **仮想エッジデバイス（MQTT シミュレータ）** を追加 | `make local-up-dev` |
| `docker-compose.observability.yml` | **OpenTelemetry Collector + 観測バックエンド**（OTLP 受信） | `docker compose -f docker-compose.observability.yml up -d` |
| `docker-compose.harbor.yaml` | ローカル **Harbor** コンテナレジストリ | `docker compose -f docker-compose.harbor.yaml up -d` |
| `docker-compose.yaml` | レガシー（Azure 互換補助・Redis 等） | `make local-up-azure` |

主な Makefile ターゲット:

```bash
make local-up-oss / local-down-oss          # OSS スタック 起動 / 停止
make local-up-minimal / local-down-minimal  # 最小構成 起動 / 停止
make local-up-dev / local-down-dev          # デバイスシミュレータ 起動 / 停止
make local-up-dual                          # Azure 互換 + OSS を同時起動
make local-down-all                         # すべて停止
make wait-oss-stack                         # ヘルスチェック待機（最大 120 秒）
make test-oss-stack                         # スタック疎通テスト
```

### Kubernetes / Helm / Argo CD

| パス | 内容 |
|------|------|
| `kubernetes/helm/building-os/` | 統合（all-in-one）Helm チャート |
| `kubernetes/helm/{api-server,web-client,connector-worker,gateway-bridge}/` | コンポーネント別 Helm チャート（Argo CD はこちらを参照） |
| `kubernetes/keda/` | KEDA による NATS 滞留ベースのオートスケール |
| `argocd/{apps,values,install,tests}/` | Argo CD GitOps マニフェスト（環境別 Application / values / 構造テスト） |

- GitOps 運用は [`docs/argocd-gitops-guide.md`](docs/argocd-gitops-guide.md)、フロントのローリングデプロイは [`docs/nextjs-k8s-rollout.md`](docs/nextjs-k8s-rollout.md) を参照。
- GatewayBridge の north-south gRPC ingress（Traefik）・cert-manager mTLS は [`docs/oss-gateway-bridge-infra.md`](docs/oss-gateway-bridge-infra.md) を参照。
- 各コンポーネントは既定で安全側（GatewayBridge 等は `enabled: false`）。必要なものを opt-in で有効化します。

> ⚠️ ここに記載の設定値・マニフェストは参考用です。本番環境での適用・運用は利用者の責任で行ってください（[免責事項](#免責事項-disclaimer)）。

## ドキュメント

**はじめに読むドキュメント（利用者向け）:**

| ドキュメント | 内容 |
|-------------|------|
| [`docs/getting-started.md`](docs/getting-started.md) | 🚀 オンボーディング（起動→API/Web→投入→読取/制御） |
| [`docs/keycloak-user-management.md`](docs/keycloak-user-management.md) | 🔑 Keycloak ユーザー管理・ロール付与・トークン取得 |
| [`docs/connector-development-guide.md`](docs/connector-development-guide.md) | 🔧 コネクタ・ワーカー拡張（新プロトコル対応の step-by-step） |
| [`docs/api-client-guide.md`](docs/api-client-guide.md) | 📡 クライアントアプリ開発（REST API・認証・テレメトリ・制御） |
| [`docs/gateway-integration.md`](docs/gateway-integration.md) | 🔌 ゲートウェイ接続モデル（ingress/egress・point list 同期・mTLS） |

**アーキテクチャ・設計・運用:**

| ドキュメント | 内容 |
|-------------|------|
| [`docs/evaluation-summary.md`](docs/evaluation-summary.md) | 📊 E2E 評価結果とアーキテクチャ/性能の妥当性 |
| [`e2e/evaluation-report.md`](e2e/evaluation-report.md) | E2E 実測レポート（生値、E1–E8 gate） |
| [`docs/system-architecture.md`](docs/system-architecture.md) | システム全体のアーキテクチャ詳細 |
| [`docs/telemetry-specification.md`](docs/telemetry-specification.md) | テレメトリメッセージスキーマ仕様 |
| [`docs/oss-nats-design.md`](docs/oss-nats-design.md) | NATS JetStream subject / stream 設計 |
| [`docs/oss-warm-parquet-lake.md`](docs/oss-warm-parquet-lake.md) | Warm/Cold Parquet レイク設計（既定テレメトリストア） |
| [`docs/oss-tier-architecture.md`](docs/oss-tier-architecture.md) | Hot / Warm / Cold 階層アーキテクチャ |
| [`docs/oss-gateway-pointlist-sync.md`](docs/oss-gateway-pointlist-sync.md) | ゲートウェイ Point List 同期 API（ETag/差分/push・mTLS） |
| [`docs/oss-sparql-mapping.md`](docs/oss-sparql-mapping.md) | OxiGraph / SPARQL デジタルツインマッピング |
| [`docs/standard-mapping.md`](docs/standard-mapping.md) | SBCO / `bos:` 語彙 ↔ Brick / REC / IFC / DTDL 対応表 |
| [`docs/keycloak-permission-mapping.md`](docs/keycloak-permission-mapping.md) | Keycloak 権限モデル（トークンクレーム詳細） |
| [`docs/oss-hono-design.md`](docs/oss-hono-design.md) | デバイス接続設計（MQTT + AMQP Northbound） |
| [`docs/nextjs-k8s-rollout.md`](docs/nextjs-k8s-rollout.md) | Next.js Kubernetes ローリングデプロイ |
| [`docs/argocd-gitops-guide.md`](docs/argocd-gitops-guide.md) | Argo CD GitOps 運用ガイド |
| [`docs/e2e-performance-quality-test-plan.md`](docs/e2e-performance-quality-test-plan.md) | E2E パフォーマンス・品質テスト計画 |
| [`e2e/plan.md`](e2e/plan.md) | 参照アーキテクチャ E2E 定量評価計画（E1–E8 / KPI 閾値） |
| [`Tools/e2e-performance/PERFORMANCE_SUMMARY.md`](Tools/e2e-performance/PERFORMANCE_SUMMARY.md) | パフォーマンス・品質テスト 実施結果サマリー |
| [`docs/oss-tech-stack-analysis.md`](docs/oss-tech-stack-analysis.md) | 技術選定の背景と Azure 置換分析 |

---

## 出自・謝辞 (Acknowledgements)

本プロダクト **Building OS — OSS Edition** は、**東京大学 グリーン ICT プロジェクト（UTokyo Green ICT Project）** の研究成果物を基にした派生物です。原研究・関係各位に深く感謝します。

本リポジトリは当該研究成果を OSS スタック向けに再構成・拡張したものであり、東京大学・グリーン ICT プロジェクト・原著者らによって公式に提供・保証・サポートされるものではありません。

## 免責事項 (Disclaimer)

本ソフトウェアおよび本リポジトリに含まれる一切の成果物（コード・設定・Docker/Kubernetes マニフェスト・ドキュメント等）は、**現状有姿（AS IS）** で提供されます。商品性・特定目的適合性・非侵害性を含むいかなる明示または黙示の保証も行いません。

**本ソフトウェアの利用・改変・配布・運用にかかわる一切（直接・間接・付随的・結果的な損害、データ消失、業務中断、機器・設備への影響、第三者からの請求等を含むがこれらに限られない）について、開発者・コントリビューター・派生元（東京大学 グリーン ICT プロジェクトを含む）は一切の責任を負いません。** 利用者は自己の責任において本ソフトウェアを評価・利用するものとします。

実機・本番環境（ビル設備の制御を含む）での利用は、利用者自身による十分な検証・安全確認のうえで行ってください。

## ライセンス

Apache License 2.0. 上記「免責事項」もあわせて適用されます。詳細は [LICENSE](./LICENSE) および [NOTICE](./NOTICE) を参照してください。
