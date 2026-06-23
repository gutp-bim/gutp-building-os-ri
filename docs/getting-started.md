# Getting Started — Building OS OSS オンボーディング

このドキュメントは **初めて Building OS OSS を触る人**向けに、ローカルでの起動から「テレメトリが
入って・読めて・制御できる」までを一筆書きで案内します。アーキテクチャ全体は
[system-architecture.md](system-architecture.md)、ゲートウェイ接続は
[gateway-integration.md](gateway-integration.md)、性能・妥当性は
[evaluation-summary.md](evaluation-summary.md) を参照してください。

> ⚠️ 本ソフトウェアは現状有姿（AS IS）・無保証です。実機・本番（設備制御を含む）での利用は利用者の
> 責任で十分に検証のうえ行ってください（[免責事項](../README.md#免責事項-disclaimer)）。

---

## 0. これは何か（30 秒）

スマートビル向けのオープンソース IoT プラットフォームです。ビル設備（HVAC・電力計・環境センサ等）の
**テレメトリを収集 → 検証 → Parquet レイク（MinIO）に蓄積 → REST/gRPC API + Next.js ダッシュボードで
可視化**し、**点（point）単位の制御**を行います。設備の階層（建物→フロア→空間→機器→点）は
デジタルツイン（OxiGraph / SPARQL）で管理します。

データの流れ:

```
ゲートウェイ / デバイス
   │  (gRPC GatewayIngress  ／  MQTT  ／  AMQP)
   ▼
NATS JetStream  building-os.validated.telemetry
   │
   ├─▶ Hot KV（最新値・near-realtime）            ← latest API
   └─▶ ParquetLakeWriter → MinIO Parquet レイク   ← warm/cold/集計 API
                              │
                              ▼
   API Server (REST + gRPC) ──▶ Web Client (Next.js)
```

詳細は [oss-tier-architecture.md](oss-tier-architecture.md)（Hot/Warm/Cold 階層）と
[oss-warm-parquet-lake.md](oss-warm-parquet-lake.md)（既定の Parquet レイク）。

---

## 1. 前提

- Docker Desktop（ローカル開発の全サービスを compose で起動）
- .NET SDK 8.0+（API Server / ConnectorWorker をホストで動かす場合）
- Node.js 22+（Web Client を動かす場合）
- 任意: `uv`（Python）+ k6（E2E 性能ハーネスを動かす場合）

---

## 2. OSS スタックを起動

```bash
docker compose -f docker-compose.oss.yaml up -d
```

これで次が立ち上がります（既定構成・WARM_STORE=parquet）:

| サービス | 役割 | ローカルポート |
|---|---|---|
| NATS JetStream | コアメッセージバス | 4222 / 8222 |
| PostgreSQL 16 | ユーザ/グループ/監査（テレメトリは Parquet） | 5433 |
| OxiGraph | デジタルツイン（SPARQL） | 7878 |
| MinIO | Parquet レイク（S3 互換） | 9000 / 9001(console) |
| Keycloak | 認証（OIDC/JWT） | 8080 |
| Prometheus / Grafana / Loki / Tempo | 可観測性 | 9090 / 3010 / 3100 / 3200 |
| ConnectorWorker | 取り込みワーカー（+ 任意 gRPC ingress） | 8081(health), 5051(ingress)※ |
| GatewayBridge | 制御 egress（gRPC bidi） | 5052 |

> ※ gRPC ingress（GatewayIngress）は `GRPC_INGRESS_PORT` を設定したときだけ listen します（OSS 既定は
> 未設定＝health のみ）。ゲートウェイから gRPC で送る場合は
> `GRPC_INGRESS_PORT=5051 docker compose -f docker-compose.oss.yaml up -d building-os.connector-worker`。
> ポート全一覧と起動オプション（MQTT/TimescaleDB プロファイル等）は
> [ルート README](../README.md#ローカルポート一覧) を参照。

---

## 3. API Server と Web Client を起動

```bash
# API Server（ローカル Docker サービスに接続）
cd DotNet/BuildingOS.ApiServer
dotnet run --launch-profile WithLocal
# → REST/gRPC: http://localhost:5000   Swagger UI: http://localhost:5000/swagger

# Web Client（別ターミナル）
cd web-client
yarn install && yarn dev
# → http://localhost:3000
```

ローカル開発では API Server を `DISABLE_AUTH=true`（compose の API も同様）で動かせるため、
Keycloak トークンなしで叩けます（本番は OIDC/JWT 必須）。

---

## 4. デジタルツインに設備を入れる

読み取り・制御は twin に登録された point を起点に解決されます（未知の point は 404）。

### 起動時シード（ローカル開発向け）

環境変数 `OXIGRAPH_SEED_TTL_PATH` に Turtle ファイルを指定すると起動のたびにデフォルトグラフを
全置換します。サンプルツインは `OxiGraphSeedHostedService` が自動投入します（OSS compose 既定）。

### 管理 UI からのインポート（推奨）

`/admin/twin`（`http://localhost:3000/admin/twin`）から Turtle ファイルをアップロードできます。

| モード | 動作 |
|--------|------|
| `append`（省略時） | 既存データに追記。同一トリプルは無視 |
| `replace` | 全データを削除して新しい TTL で置き換え |

プレビューでトリプル件数・ゲートウェイ数・エラーを確認してから適用できます。

### Turtle の最小構成例

```turtle
@prefix sbco: <https://www.sbco.or.jp/ont/> .

<https://example.com/bldg/bldg-1> a sbco:Building ;
    sbco:name "デモビル" ; sbco:building "bldg-1" .

<https://example.com/point/pt-001> a sbco:PointExt ;
    sbco:name "室温" ; sbco:unit "degC" ;
    sbco:localId "PT-001" ; sbco:gatewayId "GW-DEMO" ;
    sbco:building "bldg-1" .   # 必須: ingress でビルを解決するために使用
```

> **gateway_id 制約:** 1 つの `gateway_id` は 1 つのビルにのみ所属できます。
> 複数ビルにまたがると起動停止（シード時）または 409（API 時）になります。

詳細（重複の扱い・複数ビル・ロール別表示・削除方法）は
**[resource-management.md](resource-management.md)** を参照してください。

リソース階層は Web Client の `/resources`（エクスプローラ）で確認できます。

---

## 5. テレメトリを流す（最小例）

ゲートウェイ正本経路は **gRPC GatewayIngress**（point-id 共有契約, #181）です。最小の送信例は
E2E ハーネスの送信部が参考になります（proto を実行時コンパイル）:

```bash
# gRPC ingress を有効化して持続負荷を流し、Parquet レイクへの取り込み品質を実測
GRPC_INGRESS_PORT=5051 PARQUET_FLUSH_INTERVAL=1 \
  docker compose -f docker-compose.oss.yaml up -d --force-recreate --no-deps building-os.connector-worker
bash Tools/e2e-performance/s15_ingest_throughput.sh   # 約 200 frames/s を投入 → quality_checker で検証
```

MQTT で試したい場合は Mosquitto プロファイルとデバイスシミュレータを使います:

```bash
MQTT_HOST=building-os.mosquitto docker compose -f docker-compose.oss.yaml --profile mqtt up -d
# Tools/development-edge-device/ のシミュレータで telemetry/# にパブリッシュ
```

ゲートウェイがどう繋がるか（ingress/egress・point list 同期・mTLS）は
[gateway-integration.md](gateway-integration.md) を必読。

---

## 6. 読む・制御する

```bash
# 最新値（Hot KV、cold 時はレイク fallback）
curl 'http://localhost:5000/telemetries/query?pointId=demo-pt-001&latest=true'

# 期間レンジ（tier 自動選択: warm/cold/集計）
curl 'http://localhost:5000/telemetries/query?pointId=demo-pt-001&start=2026-06-15T00:00:00Z&end=2026-06-16T00:00:00Z&granularity=Hour'

# 点の制御（202 + controlId、結果は building-os.control.result.{controlId} に非同期）
curl -X POST 'http://localhost:5000/points/demo-pt-001/control' \
  -H 'Content-Type: application/json' -d '{"value": 1}'
```

統一読み取りエンドポイント `GET /telemetries/query` が tier（latest / warm / cold / 集計）を自動選択します。

---

## 7. 動作確認・トラブルシュート

| 症状 | 確認 |
|---|---|
| API が DB に繋がらない | `--no-deps` での個別 recreate はネットワークから外れることがある → `--force-recreate`（deps 込み）で再生成 |
| gRPC ingest が `Unimplemented`/接続不可 | connector に `GRPC_INGRESS_PORT` 未設定（health のみ）。設定して recreate |
| 制御が常に成功扱いで 503 にならない | OSS 既定は `ENABLE_SIM_CONTROL=true`（シミュレート制御）。実 egress は gateway binding を `bacnet-sim` にし GatewayBridge 経由 |
| latest/range が空 | point が twin 未登録（404）／flush 前（既定 5 分、テストは `PARQUET_FLUSH_INTERVAL=1`） |
| 各サービスの health | `GET /api/system/status`（API）、`/health/ready`（worker 8081）、MinIO console 9001、Grafana 3010 |

---

## 8. 次に読む

- [gateway-integration.md](gateway-integration.md) — ゲートウェイ接続モデル（ingress/egress、point list 同期、mTLS）
- [system-architecture.md](system-architecture.md) — 全体構成・データ/制御フロー・セキュリティ
- [evaluation-summary.md](evaluation-summary.md) — E2E 評価の結果と、アーキテクチャ/性能の妥当性
- [telemetry-specification.md](telemetry-specification.md) — テレメトリ契約（`ValidMessageJson`）
- [ルート README](../README.md) — 開発ガイド・環境変数・デプロイ（Compose/Kubernetes）
