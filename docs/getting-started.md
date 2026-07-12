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
- .NET SDK 8.0.126 以上の 8.0.x（API Server / ConnectorWorker をホストで動かす場合。`DotNet/global.json` と一致が必要）
- Node.js 22+（Web Client をホストで動かす場合）
- Yarn 1.22+（または Corepack 経由で有効化。Web Client をホストで動かす場合）
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
| ConnectorWorker | 取り込みワーカー（+ 任意 gRPC ingress） | 8081(health), 5051(ingress)※ |
| GatewayBridge | 制御 egress（gRPC bidi） | 5052 |

> ※ gRPC ingress（GatewayIngress）は `GRPC_INGRESS_PORT` を設定したときだけ listen します（OSS 既定は
> 未設定＝health のみ）。ゲートウェイから gRPC で送る場合は
> `GRPC_INGRESS_PORT=5051 docker compose -f docker-compose.oss.yaml up -d building-os.connector-worker`。
> ポート全一覧と起動オプション（MQTT/TimescaleDB プロファイル等）は
> [ルート README](../README.md#ローカルポート一覧) を参照。

> 📊 **可観測性（Prometheus / Grafana / Loki / Tempo / otel-collector）は既定オフ**です（コスト最適化、A-7）。
> API/ConnectorWorker は `PROMETHEUS_URL` / `OTEL_EXPORTER_OTLP_ENDPOINT` が未到達でも no-op で動き続けるため必須ではありません。使う場合は起動時に `--profile observability` を付けてください:
> ```bash
> docker compose -f docker-compose.oss.yaml --profile observability up -d
> ```
> Prometheus(9090) / Grafana(3010) / Loki(3100) / Tempo(3200) / otel-collector / postgres-exporter が追加で立ち上がります。

---

## 3. API Server と Web Client を起動

### A) Docker だけで起動する（推奨・最短）

`docker compose -f docker-compose.oss.yaml up -d` で API Server と ConnectorWorker は既に起動済みです。
この手順では Web Client だけ追加で起動します。

```bash
docker compose -f docker-compose.oss.yaml --profile webclient up -d
# API: http://localhost:5000
# Swagger UI: http://localhost:5000/swagger
# Web: http://localhost:3000
```

### B) ホストで直接起動する（開発ループ向け・任意）

compose 版の API/ConnectorWorker と同時起動するとポート競合するため、先に停止します。

```bash
docker compose -f docker-compose.oss.yaml stop building-os.api building-os.connector-worker

# API Server（ローカル Docker サービスに接続）
cd DotNet/BuildingOS.ApiServer
dotnet run --launch-profile WithLocal
# → REST/gRPC: http://localhost:5000   Swagger UI: http://localhost:5000/swagger

# Web Client（別ターミナル）
cd web-client
yarn install && yarn dev
# → http://localhost:3000
```

> `DotNet/global.json` は SDK `8.0.126` + `rollForward: latestPatch` を指定しています。
> そのため `dotnet run` は 8.0.x SDK が必要で、10.x SDK のみでは起動できません。

ローカル開発では API Server を `DISABLE_AUTH=true`（compose の API も同様）で動かせるため、
Keycloak トークンなしで叩けます(本番は OIDC/JWT 必須)。**`DISABLE_AUTH` は API Server 自身の
チェックのみに効き、Web Client(`http://localhost:3000`)は引き続き実際の Keycloak ログイン
画面にリダイレクトします。** 既定の dev realm(`oss-stack/keycloak/realm.json`)には
`admin`/`admin`(全操作可)と `testoperator`/`testpass`(読取 + 制御)の2アカウントが
自動投入済みです — 詳細は [docs/keycloak-user-management.md](keycloak-user-management.md)。
**ラボ/CI 専用の既定資格情報です — 本番前に必ず変更してください。**

---

## 4. デジタルツインに設備を入れる

読み取り・制御は twin に登録された point を起点に解決されます（未知の point は 404）。

### 起動時シード（ローカル開発向け）

環境変数 `OXIGRAPH_SEED_TTL_PATH` に Turtle ファイルを指定すると起動のたびにデフォルトグラフを
全置換します。サンプルツインは `OxiGraphSeedHostedService` が自動投入します（OSS compose 既定 —
`fixtures/e2e/twin.ttl`、1 building / 8 point、`gateway_id=GW-SOS-001` を読み込みます。起動後は
`/resources` で確認できます）。

別の Turtle に差し替える場合は、`./fixtures` が bind mount されているコンテナ内パス
（`/fixtures/` 配下）を `OXIGRAPH_SEED_TTL_PATH` に指定してください。起動時シードを無効化し
`/admin/twin` 経由の手動投入のみにしたい場合は、`OXIGRAPH_SEED_TTL_PATH=` と空文字を渡します。

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

既にホスト上で MQTT ブローカーが動いている場合（例: `localhost:11883`）は、
ConnectorWorker をそのブローカーへ向けます（コンテナからホストへ接続するため `host.docker.internal` を使用）:

```bash
MQTT_HOST=host.docker.internal MQTT_PORT=11883 MQTT_USERNAME=devices MQTT_PASSWORD=buildingos-devices \
  docker compose -f docker-compose.oss.yaml up -d --force-recreate --no-deps building-os.connector-worker
```

最小スモーク（1件 publish）例:

```bash
MQTT_HOST=localhost MQTT_PORT=11883 MQTT_USERNAME=devices MQTT_PASSWORD=buildingos-devices \
  TELEMETRY_INTERVAL=1 DEVICE_ID=device-001 TENANT_ID=default POINT_ID=PT001 \
  python Tools/development-edge-device/mqtt_edge_device.py
```

> `MqttIngressWorker` は `telemetry/{tenant}/{deviceId}` を受信し、JSON payload のみ受理します。
> PowerShell でファイル経由 publish する場合、UTF-8 BOM 付き JSON は `non-JSON payload` と判定されることがあるため、BOM なし UTF-8 を推奨します。

ゲートウェイがどう繋がるか（ingress/egress・point list 同期・mTLS）は
[gateway-integration.md](gateway-integration.md) を必読。

同一マシンで `nexus-gateway` と Building OS を共存させる場合は、`nexus-gateway` 側で
`docker-compose.live-bos.yml` オーバーレイを併用します。

```bash
cd nexus-gateway
docker compose -f docker-compose.yml -f docker-compose.live-bos.yml up --build
```

- `mock-bos` を無効化し、gateway を `host.docker.internal:5051/5052` に接続します。
- ポート競合時は `nexus-gateway/.env` で `GATEWAY_HOST_PORT` / `ADMIN_UI_HOST_PORT` /
  `NATS_HOST_PORT` などを上書きしてください。

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

UI で確認する場合:

- テレメトリ表示: `http://localhost:3000/resources` から点を選択し、`/points/{pointId}` で最新値・履歴を確認
- 管理 UI: `http://localhost:3000/admin` はユーザ/グループ/権限・twin 管理用（テレメトリ時系列の主画面ではない）

---

## 7. 動作確認・トラブルシュート

| 症状 | 確認 |
|---|---|
| API が DB に繋がらない | `--no-deps` での個別 recreate はネットワークから外れることがある → `--force-recreate`（deps 込み）で再生成 |
| gRPC ingest が `Unimplemented`/接続不可 | connector に `GRPC_INGRESS_PORT` 未設定（health のみ）。設定して recreate |
| 制御が常に成功扱いで 503 にならない | OSS 既定は `ENABLE_SIM_CONTROL=true`（シミュレート制御）。実 egress は gateway binding を `bacnet-sim` にし GatewayBridge 経由 |
| BOS 側が `AlreadyExists: gateway <gateway_id> already connected` を返す | 同じ `gateway_id` の egress セッションが残留。重複起動を停止し、`building-os.gateway-bridge` を再起動してから gateway を再接続 |
| latest/range が空 | point が twin 未登録（404）／flush 前（既定 5 分、テストは `PARQUET_FLUSH_INTERVAL=1`） |
| 各サービスの health | `GET /api/system/status`（API）、`/health/ready`（worker 8081）、MinIO console 9001、Grafana 3010（`--profile observability` 起動時のみ） |

---

## 8. 次に読む

- [gateway-integration.md](gateway-integration.md) — ゲートウェイ接続モデル（ingress/egress、point list 同期、mTLS）
- [system-architecture.md](system-architecture.md) — 全体構成・データ/制御フロー・セキュリティ
- [evaluation-summary.md](evaluation-summary.md) — E2E 評価の結果と、アーキテクチャ/性能の妥当性
- [telemetry-specification.md](telemetry-specification.md) — テレメトリ契約（`ValidMessageJson`）
- [ルート README](../README.md) — 開発ガイド・環境変数・デプロイ（Compose/Kubernetes）
