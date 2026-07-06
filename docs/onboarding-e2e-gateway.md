# GUTP スマートビルディング基盤 オンボーディングガイド

nexus-gateway / bacnet-sim-gateway / opcua-sim-gateway / Building OS (OSS) を、
**物理設備なしで** ローカルに立ち上げ、テレメトリ取り込みから点制御までを
E2E（エンドツーエンド）で通すための実践ガイドです。

対象読者は開発者。上から順に実行すれば、**シミュレータ → ゲートウェイ → Building OS →
API/ダッシュボード → 制御** までの一筆書きを再現できます。

> ⚠️ 本スタックはすべて研究・開発用途の OSS です。既定の認証情報・`SecurityPolicy=None`・
> `DISABLE_AUTH=true` などは **ローカル/CI 専用**。実機・本番（設備制御を含む）での利用は
> 利用者の責任で十分に検証のうえ行ってください。

> 📁 **このガイドの配置と相対リンクについて:** 本ドキュメントは Building OS リポジトリの
> `docs/` に置いていますが、内容は 4 リポジトリ（`gutp-building-os-oss-public` /
> `nexus-gateway` / `bacnet-sim-gateway` / `opcua-sim-gateway`）を横断します。文中の
> `nexus-gateway/...` や `gutp-building-os-oss-public/...` などの相対リンクは、
> [Step 0](#step-0--github-からダウンロードリポジトリ取得) で 4 リポジトリを**同じ親ディレクトリ
> `gutp/` に並べた**構成を前提としています。Building OS 単体の内部リンク（`docs/...`）は
> このリポジトリ内で解決できます。

---

## 目次

1. [全体像](#1-全体像)
2. [登場コンポーネントと役割](#2-登場コンポーネントと役割)
3. [Step 0 — GitHub からダウンロード（リポジトリ取得）](#step-0--github-からダウンロードリポジトリ取得)
4. [前提ツール（実行環境の整備）](#4-前提ツール実行環境の整備)
5. [Step A — Building OS を起動する](#step-a--building-os-を起動する)
6. [Step B — nexus-gateway を起動する（単体スモーク）](#step-b--nexus-gateway-を起動する単体スモーク)
7. [Step C — シミュレータを接続する（BACnet / OPC-UA）](#step-c--シミュレータを接続するbacnet--opc-ua)
8. [Step D — nexus-gateway を Building OS につなぐ](#step-d--nexus-gateway-を-building-os-につなぐ)
9. [Step E — ユーザ・ロール・トークン（認証）](#step-e--ユーザロールトークン認証)
10. [Step F — 証明書と mTLS（本番寄り）](#step-f--証明書と-mtls本番寄り)
11. [E2E チェックリスト](#e2e-チェックリスト)
12. [トラブルシューティング](#トラブルシューティング)
13. [ポート早見表](#ポート早見表)
14. [ビジュアル資料（技術レポート HTML）](#ビジュアル資料技術レポート-html)
15. [参照ドキュメント](#参照ドキュメント)

---

## 1. 全体像

```mermaid
flowchart TB
    subgraph field["フィールド側（シミュレータ = 物理機器の代替）"]
        BBC["bacnet-sim-gateway<br/>(bbc-sim)<br/>BACnet/IP 仮想 B-BC"]
        OPC["opcua-sim-gateway<br/>(opcua-sim)<br/>OPC UA 仮想サーバ"]
    end

    subgraph nexus["nexus-gateway（エッジ統合ゲートウェイ）"]
        CONN["Connectors<br/>bacnet / opcua / mqtt"]
        NORM["Normalizer<br/>local_id → point_id"]
        SF["Store-and-Forward<br/>(SQLite リングバッファ)"]
        UP["Ingress Uplink / Egress Agent"]
        CONN -->|"evt.&lt;proto&gt; (NATS JetStream)"| NORM --> SF --> UP
    end

    subgraph bos["Building OS（System of Record）"]
        ING["ConnectorWorker<br/>GatewayIngress :5051"]
        BR["GatewayBridge<br/>GatewayEgress :5052"]
        BUS["NATS JetStream<br/>validated.telemetry"]
        HOT["Hot: NATS KV（最新値）"]
        LAKE["Warm/Cold: MinIO Parquet レイク"]
        TWIN["デジタルツイン<br/>OxiGraph / SPARQL"]
        API["API Server<br/>REST + gRPC :5000"]
        WEB["Web Client (Next.js) :3000"]
        ING --> BUS --> HOT
        BUS --> LAKE
        API --> HOT & LAKE & TWIN
        WEB --> API
    end

    BBC -->|"BACnet/IP"| CONN
    OPC -->|"opc.tcp"| CONN
    UP -->|"gRPC TelemetryFrame (mTLS at edge)"| ING
    BR -->|"gRPC ControlCommand"| UP

    style field fill:#1f2937,color:#fff
    style nexus fill:#0e3a5f,color:#fff
    style bos fill:#14532d,color:#fff
```

**契約の要（最重要）:** ワイヤ（線）に載る同一性は **`(gateway_id, point_id)`** だけです。
建物・機器・名称・単位といった静的メタデータは毎フレーム送らず、Building OS が
デジタルツインから `point_id` を鍵に補完します。ネイティブアドレス
（BACnet の `analogInput,1001` や OPC-UA の `ns=2;s=PT001`）を `point_id` に解決するのは
**nexus-gateway の Normalizer** の責務です。

**データの流れ（上り = テレメトリ）:**
シミュレータが値を公開 → nexus-gateway のコネクタが読取 → Normalizer が `point_id` を付与 →
Store-and-Forward → gRPC で Building OS の `GatewayIngress` へ → NATS 検証済みバス →
Hot KV（最新値）と Parquet レイク（履歴）へ。

**制御の流れ（下り = コマンド）:**
API `POST /points/{id}/control` → NATS per-gateway subject → GatewayBridge(`GatewayEgress`) →
nexus-gateway の Egress Agent → コネクタが `WriteProperty`（BACnet）/ `Write`（OPC-UA）を実行。

---

## 2. 登場コンポーネントと役割

| コンポーネント | リポジトリ | 役割 | 実装言語 |
|---|---|---|---|
| **Building OS (OSS)** | `gutp-building-os-oss-public` | System of Record。テレメトリ蓄積・API・ダッシュボード・デジタルツイン・制御ルーティング | .NET 8 / Next.js 15 |
| **nexus-gateway** | `nexus-gateway` | エッジ統合ゲートウェイ。プロトコル差異の吸収と `(gateway_id, point_id)` への正規化、Building OS への上り/下り | Go（コネクタは Go/Python/Java）|
| **bacnet-sim-gateway** | `bacnet-sim-gateway` | SBCO 点リストから標準準拠の **仮想 BACnet B-BC** を生成し BACnet/IP で公開 | Python 3.12 |
| **opcua-sim-gateway** | `opcua-sim-gateway` | SBCO 点リストから **仮想 OPC UA サーバ**（アドレス空間）を生成し opc.tcp で公開 | Python 3.12 |

> **用語:** **SBCO**（スマートビルディング共創機構）が点リスト（Point List）のスキーマを定義。
> **GUTP**（グリーン東大 ICT プロジェクト）がプロジェクト全体。**Point List** は
> 「論理点 `point_id` ⇔ プロトコルネイティブアドレス」の対応表で、正本は Building OS の
> デジタルツインにあります。

**責務分担の原則:** Building OS が「登録簿（デジタルツイン）」と「蓄積・API・制御指令元」を持ち、
nexus-gateway は「接続と翻訳」だけを担います。シミュレータは物理設備の代替で、
**1 インスタンス = 1 デバイス**（1 B-BC / 1 OPC UA サーバ）が不変条件です。

---

## Step 0 — GitHub からダウンロード（リポジトリ取得）

まず 4 つのリポジトリを取得します。**唯一の必須ツールは `git`**（未インストールなら
[git-scm.com](https://git-scm.com/) から。macOS は `xcode-select --install`、Debian/Ubuntu は
`sudo apt-get install -y git`、Windows は Git for Windows）。

| リポジトリ（ローカル配置名） | GitHub | 内容 |
|---|---|---|
| `gutp-building-os-oss-public` | [gutp-bim/gutp-building-os-ri](https://github.com/gutp-bim/gutp-building-os-ri) | ビル OS の参考実装（OSS） |
| `nexus-gateway` | [gutp-bim/nexus-gateway](https://github.com/gutp-bim/nexus-gateway) | ビル OS 用の汎用ゲートウェイ（参考実装） |
| `bacnet-sim-gateway` | [takashikasuya/bacnet-sim-gateway](https://github.com/takashikasuya/bacnet-sim-gateway) | BACnet/IP 仮想 B-BC シミュレータ |
| `opcua-sim-gateway` | [takashikasuya/opcua-sim-gateway](https://github.com/takashikasuya/opcua-sim-gateway) | OPC UA 仮想サーバ シミュレータ |

> **重要 — 配置ルール:** nexus-gateway の統合 Compose は、姉妹シミュレータを **相対パス `../`**
> で参照します（`../bacnet-sim-gateway`, `../opcua-sim-gateway`）。**必ず 4 つを同じ親ディレクトリに
> 並べて** clone してください。並んでいないと Step C のシミュレータビルドが失敗します。

```bash
# 任意の作業用親ディレクトリを作って、その中に 4 つ並べる
mkdir -p ~/gutp && cd ~/gutp

git clone https://github.com/gutp-bim/gutp-building-os-ri   gutp-building-os-oss-public
git clone https://github.com/gutp-bim/nexus-gateway
git clone https://github.com/takashikasuya/bacnet-sim-gateway
git clone https://github.com/takashikasuya/opcua-sim-gateway
```

取得後の配置（この形になっていることを確認）:

```
gutp/                          # 親ディレクトリ（名前は任意）
├── gutp-building-os-oss-public/
├── nexus-gateway/
├── bacnet-sim-gateway/
└── opcua-sim-gateway/
```

```bash
ls -1        # 4 ディレクトリが並んでいれば OK
```

Windows (PowerShell) の場合:

```powershell
New-Item -ItemType Directory -Force ~/gutp | Out-Null; Set-Location ~/gutp
git clone https://github.com/gutp-bim/gutp-building-os-ri   gutp-building-os-oss-public
git clone https://github.com/gutp-bim/nexus-gateway
git clone https://github.com/takashikasuya/bacnet-sim-gateway
git clone https://github.com/takashikasuya/opcua-sim-gateway
Get-ChildItem -Directory | Select-Object Name    # 4 ディレクトリが並んでいれば OK
```

### 補足

- **プライベートリポジトリ / 認証が必要な場合:** SSH 鍵を登録済みなら
  `git clone git@github.com:takashikasuya/<repo>.git`、HTTPS なら
  [Personal Access Token](https://github.com/settings/tokens) を使います。
- **特定ブランチ/タグを使う場合:** `git clone -b <branch> <url>`
  （nexus-gateway は `v0.1.0` public preview がベースライン）。
- **git を使わず ZIP で取得する場合:** 各リポジトリの GitHub ページ →「Code」→
  「Download ZIP」。展開後のフォルダ名が上記の並びになるよう、
  `-main` などのサフィックスを外してリネームしてください
  （特に `gutp-building-os-ri` → `gutp-building-os-oss-public` に合わせる）。
- **更新するとき:** 各ディレクトリで `git pull` を実行します。

> 本ガイドが置かれている `gutp/` 直下は、既にこの配置になっています。
> 既に clone 済みなら Step 0 はスキップして構いません。

---

## 4. 前提ツール（実行環境の整備）

取得したら、共通で必要なものを入れます。

| ツール | バージョン | 使う場面 |
|---|---|---|
| **Docker + Docker Compose** | 最近のもの | 全スタックの起動（最優先） |
| **Go** | ≥ 1.25 | nexus-gateway をホストで直接ビルド/実行する場合 |
| **.NET SDK** | 8.0+ | Building OS の API Server / ConnectorWorker をホストで動かす場合 |
| **Node.js** | 22+ | Building OS の Web Client、nexus-gateway の Admin UI |
| **uv**（Python パッケージ管理） | 最新 | シミュレータ 2 種（Python）をネイティブ実行する場合 |
| **Python** | 3.12+ | 同上 |
| `curl` + `jq` | 任意 | API の疎通確認 |
| Buf CLI | 任意 | Building OS の proto → TS コード生成 |
| **GNU Make** | 任意 | Makefile ショートカット用。**Windows では未同梱**（[後述](#windows-で-make-が使えない場合)の生 `docker compose` で代替可） |

**Docker だけあれば** 各リポジトリの Compose スタックは起動できます。ホスト実行
（`go run` / `dotnet run` / `uv run`）は開発ループを速くしたいとき用です。

> 💡 **`make` は必須ではありません。** Building OS の Makefile ターゲットは薄い `docker compose`
> ラッパーです。Windows のように `make` が無い環境では、[Windows で make が使えない場合](#windows-で-make-が使えない場合)
> の対応表どおり `docker compose` を直接叩けば同じことができます。

### Raspberry Pi / Debian 系でシミュレータをネイティブ実行する場合

opcua-sim は `cryptography`/`cffi` のビルドにヘッダが必要です。`uv sync` の前に導入してください。

```bash
sudo apt-get update
sudo apt-get install -y build-essential libffi-dev libssl-dev
# 未導入だと `fatal error: ffi.h: No such file or directory` で失敗します
```

---

## Step A — Building OS を起動する

> ℹ️ **これは一気通貫の E2E（Step D で合流）向けです。** ゲートウェイの単体スモークだけが目的なら、
> Step A は飛ばして [Step B](#step-b--nexus-gateway-を起動する単体スモーク) から始められます
> （ゲートウェイはローカル Point List + 同梱 mock Building OS で動くため）。

フル E2E では Building OS が System of Record なので **最初に立ち上げます**。

### A-1. OSS スタックを起動

```bash
cd gutp-building-os-oss-public

make local-up-oss
# 中身は: docker compose -f docker-compose.oss.yaml up -d

make wait-oss-stack       # 全サービス healthy まで待機（最大 120 秒）
```

これで既定構成（`WARM_STORE=parquet`）の以下が起動します。**可観測系（Prometheus / Grafana /
Loki / Tempo）は既定では起動しません**（`--profile observability` の opt-in。[A-1b](#a-1b-起動オプション最小構成プロファイル) 参照）。

| サービス | 役割 | ポート | 既定起動 |
|---|---|---|---|
| NATS JetStream | コアメッセージバス | 4222 / 8222 | ✅ |
| PostgreSQL 16 | ユーザ/グループ/権限 + 制御監査 | 5433 | ✅ |
| pgBouncer / pgBouncer-session | 接続プール（アプリ / EF Core マイグレーション） | 6432 / 6433 | ✅ |
| OxiGraph | デジタルツイン（SPARQL） | 7878 | ✅ |
| MinIO | Parquet レイク（S3 互換） | 9000 / 9001(console) | ✅ |
| Keycloak | 認証（OIDC/JWT） | 8080 | ✅ |
| ConnectorWorker | 取り込みワーカー（+ 任意 gRPC ingress） | 8081(health) / 5051(ingress)※ | ✅ |
| GatewayBridge | 制御 egress（gRPC bidi） | 5052 | ✅ |
| Prometheus / Grafana / Loki / Tempo / otel-collector / postgres-exporter | 可観測性 | 9090 / 3010 / 3100 / 3200 / 4317-4318 | ⛔ `--profile observability` |

> ※ gRPC ingress（`GatewayIngress`）は `GRPC_INGRESS_PORT` を設定したときだけ listen します
> （OSS 既定は未設定 = health のみ）。Step D で有効化します。

### A-1b. 起動オプション（最小構成・プロファイル）

用途に応じて Compose 構成を選べます。まず押さえるべき要点:

- **OSS ベーススタック（`docker-compose.oss.yaml` のプロファイル無し起動）は、そのまま
  「可観測性を除いた最小構成」です。** 可観測系・MQTT・TimescaleDB・LLM アシスタントは
  すべて Compose の `profiles:` の裏に隠れており、フラグを付けない限り起動しません。
  **データ保存（MinIO Parquet レイク + PostgreSQL）とポイントリスト取込（OxiGraph）は
  ベース側に含まれます。**
- `docker-compose.minimal.yaml` は **NATS + PostgreSQL + pgBouncer のみ**で、
  **MinIO も OxiGraph も Keycloak も含みません**。したがって **Parquet への履歴蓄積も
  twin へのポイントリスト取込もできません**。データ保存・point list を伴う検証には向きません。

| 構成 / ファイル | 含まれるもの | データ保存 | Point List 取込 | 用途 | 起動コマンド |
|---|---|---|---|---|---|
| **可観測性を除いた最小構成**（推奨）`docker-compose.oss.yaml`（プロファイル無し） | NATS / PostgreSQL / pgBouncer / OxiGraph / MinIO / Keycloak / API / ConnectorWorker / GatewayBridge（可観測系なし） | ✅ MinIO Parquet + PostgreSQL | ✅ OxiGraph | 標準の軽量起動。テレメトリ蓄積・twin 取込まで可能 | `docker compose -f docker-compose.oss.yaml up -d` |
| **OSS + Web Client** `docker-compose.oss.yaml` `--profile webclient` | 上記 + Next.js Web Client（`building-os.web` :3000） | ✅ | ✅ | UI もコンテナで一括起動したいとき（`yarn dev` 不要） | `docker compose -f docker-compose.oss.yaml --profile webclient up -d --build` |
| **OSS + 可観測性** `docker-compose.oss.yaml` `--profile observability` | 上記 + Prometheus / Grafana / Loki / Tempo / otel-collector | ✅ | ✅ | ダッシュボード/トレースまで見たいとき | `docker compose -f docker-compose.oss.yaml --profile observability up -d` |
| **超最小構成** `docker-compose.minimal.yaml` | NATS + PostgreSQL + pgBouncer のみ（**MinIO / OxiGraph / Keycloak なし**） | ⛔ | ⛔ | メッセージング/DB だけの PoC | `docker compose -f docker-compose.minimal.yaml up -d` |
| **デバイスシミュレータ追加** `docker-compose.dev.yaml` | OSS 起動済み前提で仮想エッジデバイス（MQTT）を追加 | — | — | MQTT でテレメトリを流したいとき | `docker compose -f docker-compose.oss.yaml --profile mqtt up -d` → `docker compose -f docker-compose.dev.yaml up -d` |
| **レガシー** `docker-compose.yaml` | Azure 互換補助・Redis 等 | — | — | 後方互換 | `docker compose up -d` |

> **まとめ:** 「可観測性は要らないが、データ保存とポイントリスト取込はしたい」＝
> **`docker compose -f docker-compose.oss.yaml up -d`（プロファイル無し）** が答えです。
> `minimal.yaml` は要件を満たしません（twin / レイクが無い）。

#### Windows で make が使えない場合

Windows には `make` が同梱されていません。Makefile のターゲットは単なる `docker compose`
ラッパーなので、**PowerShell から生コマンドを直接叩けば同じ結果**になります。

| make ターゲット | Windows (PowerShell) で叩く生コマンド |
|---|---|
| `make local-up-oss` | `docker compose -f docker-compose.oss.yaml up -d` |
| `make local-down-oss` | `docker compose -f docker-compose.oss.yaml down` |
| `make local-up-minimal` | `docker compose -f docker-compose.minimal.yaml up -d` |
| `make local-down-minimal` | `docker compose -f docker-compose.minimal.yaml down` |
| `make local-up-dev` | `$env:MQTT_HOST="building-os.mosquitto"; docker compose -f docker-compose.oss.yaml --profile mqtt up -d; docker compose -f docker-compose.dev.yaml up -d` |
| `make wait-oss-stack` | （下記の `docker compose ps` で代替） |

状態確認（`wait-oss-stack` の代替）:

```powershell
docker compose -f docker-compose.oss.yaml ps
curl http://localhost:5000/api/system/status   # API 経由の疎通
```

> ⚠️ `make wait-oss-stack` / `make mvp-test` などは内部で `bash` / `timeout` / `python3` を使う
> Unix 前提のターゲットです。Windows の素の `make` を入れても動かないため、**PowerShell から
> `docker compose` を直接叩くのが確実**です。どうしても `make` を使いたい場合のみ
> `winget install GnuWin32.Make`（または `choco install make` / `scoop install make`）で導入できますが、
> 上記 `bash` 依存ターゲットは WSL などが別途必要になります。

追加プロファイル（`docker-compose.oss.yaml` に対して有効化）:

```bash
# 可観測性（Prometheus / Grafana / Loki / Tempo）を足す（A-7）
docker compose -f docker-compose.oss.yaml --profile observability up -d

# MQTT ブローカ（Mosquitto）を足す（#25）
MQTT_HOST=building-os.mosquitto docker compose -f docker-compose.oss.yaml --profile mqtt up -d

# Warm 層を TimescaleDB にする（既定は Parquet。opt-in）
WARM_STORE=timescale TIMESCALE_CONNECTION_STRING=<conn> \
  docker compose -f docker-compose.oss.yaml --profile timescale up -d
```

Windows (PowerShell) で環境変数を前置きする場合は `$env:` で設定してから実行します:

```powershell
$env:MQTT_HOST="building-os.mosquitto"
docker compose -f docker-compose.oss.yaml --profile mqtt up -d
```

### A-2. API Server と Web Client を起動

**API Server と Web Client は両方とも Docker（compose）に含められます。**

- **API Server** は OSS ベーススタックに既に含まれており（`building-os.api`、ポート 5000）、
  `docker compose ... up -d` で自動起動します。
- **Web Client** は最小構成を保つため既定では起動せず、**`--profile webclient` のオプトイン**で
  compose 起動できます（`building-os.web`、ポート 3000）。

```bash
# Web Client も含めて Docker で一括起動（ビルドを伴う）
docker compose -f docker-compose.oss.yaml --profile webclient up -d --build
# → API: http://localhost:5000 / Swagger: http://localhost:5000/swagger
# → Web: http://localhost:3000
```

> ℹ️ **`NEXT_PUBLIC_*` はブラウザバンドルへ *ビルド時* に焼き込まれます。** compose は build args
> として `NEXT_PUBLIC_API_BASE_URL`（既定 `http://localhost:5000`）等を渡すため、値を変えたら
> `--build` で再ビルドが必要です。ブラウザはホスト側で動くので API はホストマップの
> `localhost:5000` を指し、SSR/route handler は `API_BASE_URL=http://building-os.api:8080`
> でコンテナ内 DNS を使います。カスタマイズは [.env.example](../.env.example) の
> `NEXT_PUBLIC_*` を参照。

ホストで直接動かして開発ループを速くしたい場合は、compose ではなく次を使います（compose 版と
どちらか一方で十分）:

```bash
# API Server（別ターミナル）— ローカル Docker サービスに接続
cd DotNet/BuildingOS.ApiServer
dotnet run --launch-profile WithLocal
# → REST/gRPC: http://localhost:5000   Swagger UI: http://localhost:5000/swagger

# Web Client（別ターミナル）
cd web-client
yarn install && yarn dev
# → http://localhost:3000
```

`WithLocal` プロファイルは `DISABLE_AUTH=true` なので、**ローカルでは Keycloak トークンなし**で
API を叩けます（認証を試すときは `WithLocalAuth`。詳細は [Step E](#step-e--ユーザロールトークン認証)）。

### A-3. デジタルツインに設備（Point List の正本）を入れる

> ⭐ **この Step D と共通で使える完成済み fixture を用意しています:**
> [`../fixtures/e2e/`](../fixtures/e2e/README.md)。`twin.ttl`（Building OS twin 正本 = 8 point /
> `GW-SOS-001` / `bldg-e2e`）をそのまま `/admin/twin` に replace アップロードするか
> `OXIGRAPH_SEED_TTL_PATH` で投入し、nexus-gateway 側は同ディレクトリの `pointlist.csv` を
> `PROVISIONING_FILE` に渡せば、両者が同じ `(gateway_id, point_id)` を指した状態になります。
> 以下は最小例（自作する場合の参考）。

読み取り・制御は **twin に登録された point** を起点に解決されます（未登録は 404）。
Web Client の `/admin/twin`（`http://localhost:3000/admin/twin`）から Turtle を
アップロードするのが簡単です。最小例:

```turtle
@prefix sbco: <https://www.sbco.or.jp/ont/> .

<https://example.com/bldg/bldg-1> a sbco:Building ;
    sbco:name "デモビル" ; sbco:building "bldg-1" .

<https://example.com/point/pt-001> a sbco:PointExt ;
    sbco:name "室温" ; sbco:unit "degC" ;
    sbco:localId "PT-001" ; sbco:gatewayId "GW-DEMO" ;
    sbco:building "bldg-1" .   # 必須: ingress でビルを解決するために使用
```

起動時シードで自動投入する場合は、環境変数 `OXIGRAPH_SEED_TTL_PATH` に Turtle ファイルを
指定すると、起動のたびにデフォルトグラフを全置換します（`OxiGraphSeedHostedService`）。

> **gateway_id 制約:** 1 つの `gateway_id` は 1 つのビルにのみ所属できます
> （複数ビルにまたがると起動停止 or 409）。E2E では `GW-SOS-001` のような ID を使います。

### A-4. 起動確認

```bash
curl http://localhost:5000/api/system/status      # API 全体
curl 'http://localhost:5000/telemetries/query?pointId=demo-pt-001&latest=true'  # 最新値（空でOK）
# MinIO console http://localhost:9001（可観測性 profile 起動時は Grafana http://localhost:3010 も）
```

---

## Step B — nexus-gateway を起動する（単体スモーク）

> 💡 **Step A（実 Building OS）は不要です。** ゲートウェイを個別に試験する（単体スモーク）場合は、
> **ローカルのポイントリストだけ**で起動できます。nexus-gateway のフルスタックには
> **mock Building OS が同梱**されており、Point List は同梱の `fixtures/point_list.json` を使うため、
> Building OS を先に立ち上げる必要はありません。実 Building OS に繋ぐのは
> [Step D](#step-d--nexus-gateway-を-building-os-につなぐ) からで十分です。

まずゲートウェイ単体が動くことを確認します。ローカル Point List の指定は、フラグ/環境変数で
切り替えます（[README の設定表](nexus-gateway/README.md)参照）。

| 用途 | フラグ / 環境変数 | 既定 |
|---|---|---|
| bootstrap 用の Point List ファイル | `--point-list` / `POINT_LIST_FILE` | `fixtures/point_list.json` |
| ファイル/CSV 由来の Point List（dev/E2E） | `--provisioning-file` / `PROVISIONING_FILE` | – |
| 同期先の Building OS provisioning API | `--provisioning-url` / `PROVISIONING_URL` | –（未指定なら上のファイルを使用） |

### B-1. フルスタック（NATS + mock Building OS + gateway + Keycloak + Admin UI）

```bash
cd nexus-gateway
docker compose up --build
docker compose ps        # 全サービスが ~60 秒で healthy になる
```

| エンドポイント | URL | 備考 |
|---|---|---|
| Admin UI | http://localhost:13000 | Keycloak realm `nexus-gateway`。`operator`/`operator`, `viewer`/`viewer` |
| Gateway Admin API | http://localhost:18080 | `/health`, `/metrics`, `/connectors` |
| Keycloak | http://localhost:18090 | Admin: `admin`/`admin` |
| mock Building OS (gRPC) | `localhost:15051` | `GatewayIngressService` スタブ（dev） |
| NATS | `localhost:14222` | クライアント。監視 `:18222` |

### B-2. 稼働確認（認証不要のエンドポイント）

```bash
curl -s http://localhost:18080/health | jq      # uptime / mem / disk / コネクタ生存性
curl -s http://localhost:18080/metrics          # Prometheus 形式（gateway_* / normalizer_*）
```

### B-3. 機器なしの最速ループ（in-process sim コネクタ）

Docker も NATS も不要。Go コードを速く回すとき用:

```bash
go run ./cmd/gateway --dev-sim --dev-sim-interval 5s
curl -s http://localhost:8080/telemetry | jq    # sim→JetStream→Normalizer→S&F を端から端まで観察
```

> `--dev-sim` は本番非対応の合成コネクタです（ADR-0001）。`--admin-jwks-url` 未指定時は
> Admin API が **認証無効**（dev 専用、警告ログ）になり、トークンなしで `/devices` などを叩けます。

---

## Step C — シミュレータを接続する（BACnet / OPC-UA）

nexus-gateway の統合 Compose overlay（`docker-compose.integration.yml`）が、
**姉妹シミュレータ + 対応コネクタ**を、共有 Point List
（`fixtures/integration/point_list.json`）1 つから駆動します。8 つの論理点
`PT001..PT008` を **両シミュレータが** プロトコルネイティブアドレスでモデル化しています。

| point_id | BACnet (bbc-sim) | OPC-UA (opcua-sim) | writable |
|---|---|---|---|
| PT001 | `analogInput,1001` | `ns=2;s=PT001` | no |
| PT004 | `binaryOutput,2001` | `ns=2;s=PT004` | **yes** |
| PT006 | `analogValue,1002` | `ns=2;s=PT006` | **yes** |
| PT007 | `multiStateValue,3001` | `ns=2;s=PT007` | **yes** |
| … | （PT002/003/005/008 も同様に定義） | | |

プロファイルで 2 プロトコルを分離し、各 `point_id` の供給元を 1 つに保ちます。

```bash
cd nexus-gateway

# OPC-UA E2E（plain TCP:4840、CI フレンドリ）— ../opcua-sim-gateway をビルド
docker compose -f docker-compose.yml -f docker-compose.integration.yml --profile opcua up

# BACnet E2E（Who-Is/I-Am ブロードキャストのため host networking 必須）— ../bacnet-sim-gateway をビルド
docker compose -f docker-compose.yml -f docker-compose.integration.yml --profile bacnet up
```

> 統合 overlay は gateway を `PROVISIONING_FILE=/fixtures/integration/point_list.csv` /
> `DEV_SIM=false` に上書きし、`KEYCLOAK_JWKS_URL` を空（認証なし）にします。

### リモート/実機に向ける場合（シミュレータをビルドしない）

`*-remote` プロファイルとエンドポイント環境変数を使います。

```bash
OPCUA_ENDPOINT=opc.tcp://192.0.2.10:4840 \
  docker compose -f docker-compose.yml -f docker-compose.integration.yml --profile opcua-remote up -d

BACNET_ADDRESS=192.0.2.10 \
  docker compose -f docker-compose.yml -f docker-compose.integration.yml --profile bacnet-remote up -d
```

### シミュレータ単体を触ってみる（任意）

各シミュレータは独立でも動きます。まず OPC-UA:

```bash
cd opcua-sim-gateway
uv sync
uv run opcua-sim generate-yaml examples/pointlists/sample_pointlist.csv -o config/simulator.yaml
uv run opcua-sim run                              # opc.tcp://0.0.0.0:4840
uv run opcua-sim browse opc.tcp://localhost:4840  # 別端末からブラウズ
```

BACnet:

```bash
cd bacnet-sim-gateway
uv sync
uv run bbc-sim generate-yaml --input tests/fixtures/sample_pointlist.csv --output config/simulator.yaml
uv run bbc-sim run --config config/simulator.yaml   # BACnet/IP 北向き公開
# 別端末で疎通確認
uv run bbc-sim whois
uv run bbc-sim list-objects
# Admin UI（localhost 限定・認証なし）
uv run bbc-sim run -c config/simulator.yaml --rest-port 8080 --ui   # → http://127.0.0.1:8080/ui/
```

> シミュレータの Admin UI は **`127.0.0.1` バインド・認証なし（MVP）**。信頼できない
> ネットワークに晒さないこと。opcua-sim の既定 `SecurityPolicy=None`・匿名アクセスも同様に
> ローカル/CI 専用です。

### C-1. 共有 fixture で一気通貫（`fixtures/e2e/` を opcua-sim に渡す）

上の単体例は opcua-sim 同梱の **サンプル**（`examples/pointlists/sample_pointlist.csv` /
`tests/fixtures/sample_pointlist.csv` = `GW001` / `PT001..PT008` / AHU のデモデータ）を使います。
これは opcua-sim のスキーマ確認用で、**Building OS の twin 正本とは別データセット**です。
一気通貫（Building OS ⇔ 接続GW ⇔ opcua-sim）を同一データで通すには、
[`../fixtures/e2e/pointlist.csv`](../fixtures/e2e/README.md)（`GW-SOS-001` / `SOS-PT-001..008` /
`bldg-e2e`）を **そのまま** `generate-yaml` に渡します。

```bash
cd opcua-sim-gateway
uv sync
# ↓ 同梱サンプルではなく、Building OS twin 正本と同一データセットの共有 fixture を使う
uv run opcua-sim generate-yaml ../gutp-building-os-ri/fixtures/e2e/pointlist.csv -o config/simulator.yaml
uv run opcua-sim run                              # opc.tcp://0.0.0.0:4840
uv run opcua-sim browse opc.tcp://localhost:4840
```

**`local_id` ＝ OPC-UA `node_id` の契約（最重要）:** opcua-sim は各点の node_id を
`ns=2;s=<point_id>`（`VENDOR_NS=2`、`docs/specs/node-id-numbering.md` §2）で採番します。
共有 fixture の `point_id` は `SOS-PT-001..008` なので、生成される node_id は
`ns=2;s=SOS-PT-001..008` です。この値を Building OS twin 側の `sbco:localId`
（＝汎用のデバイス側識別子。BACnet なら ObjectID、OPC-UA なら nodeId）に一致させてあり、
`twin.ttl` / `pointlist.json` / `pointlist.csv` / opcua-sim 生成 node_id の **4 者で全 8 点が一致**
します（`fixtures/e2e/README.md` に検証手順あり）。

| point_id | opcua-sim `node_id` = twin `sbco:localId` | DataType | writable |
|---|---|---|---|
| SOS-PT-001（室温） | `ns=2;s=SOS-PT-001` | Double | no |
| SOS-PT-004（照明 On/Off） | `ns=2;s=SOS-PT-004` | Boolean（Off/On） | **yes** |
| SOS-PT-006（設定温度） | `ns=2;s=SOS-PT-006` | Double | **yes** |
| SOS-PT-007（ファン速度） | `ns=2;s=SOS-PT-007` | Int32（Off/Low/Medium/High） | **yes** |

> **共有 CSV の互換ポイント:** opcua-sim は多状態を `states` 列（`|` 区切り）から読み、
> SBCO 標準の `labels`（`&&` 区切り）は読みません。また `point_specification` からは多状態を
> 導出できず Command→binary になるため、Fan Speed（4 状態）は `point_type=multistate` を明示
> しています。共有 fixture はこの両方を満たすよう作ってあるので、**追加加工なしにドロップイン**
> で `generate-yaml` に通ります。

**よくある 2 つの疑問への回答:**

- **Q. Building OS に `twin.ttl` を取り込めば opcua-sim にも同期される？** → いいえ。opcua-sim は
  Building OS クライアントを持たない純粋な OPC UA **サーバ**シミュレータで、CSV からアドレス空間を
  生成して北向き公開するだけです。したがって **同じ `pointlist.csv` を opcua-sim にも別途渡す**
  必要があります（正本 twin と同一データセットなので内容は一致）。twin から自動追従するのは
  point-list-sync 対応の**接続ゲートウェイ**（nexus-gateway、`GET /gateways/GW-SOS-001/pointlist`
  の ETag ポーリング／NATS プッシュ）側です。
- **Q. opcua-sim 同梱サンプルと内容同期している？** → いいえ（同梱サンプルは別デモデータ）。ただし
  共有 fixture は **スキーマ互換のドロップイン置換**で、opcua-sim の実ジェネレータで
  `ns=2;s=SOS-PT-00X` を生成することを検証済みです。一気通貫では**サンプルではなく共有
  `pointlist.csv`** を使ってください。

---

## Step D — nexus-gateway を Building OS につなぐ

mock Building OS の代わりに、Step A で起動した **実 Building OS** に向けます。

### D-1. Building OS 側で gRPC ingress を有効化

```bash
cd gutp-building-os-oss-public
GRPC_INGRESS_PORT=5051 docker compose -f docker-compose.oss.yaml up -d --force-recreate \
  --no-deps building-os.connector-worker
```

Windows (PowerShell):

```powershell
Set-Location gutp-building-os-oss-public
$env:GRPC_INGRESS_PORT="5051"
docker compose -f docker-compose.oss.yaml up -d --force-recreate --no-deps building-os.connector-worker
```

- ingress（テレメトリ）: `building-os.connector-worker` の `GatewayIngress` = **:5051**
- egress（制御）: `building-os.gateway-bridge` の `GatewayEgress` = **:5052**（常時待受）

### D-2. gateway を Building OS に向けて起動

```bash
cd nexus-gateway
GATEWAY_ID=GW-SOS-001 \
BOS_ADDR=localhost:5051 \
BOS_INSECURE=true \
PROVISIONING_FILE=/path/to/mvp-pointlist.csv \
go run ./cmd/gateway
```

- `GATEWAY_ID` は twin に登録した `sbco:gatewayId` と一致させます（例 `GW-SOS-001`）。
- `BOS_INSECURE=true`（平文 h2c）は **dev/CI 専用**。本番は [Step F](#step-f--証明書と-mtls本番寄り) の mTLS に置き換えます。
- Point List は Building OS の twin が正本。`PROVISIONING_URL=https://.../provisioning` を
  与えれば twin から同期（ETag / `If-None-Match`→304 / `?since=` 差分）します。

### D-3. 上り（テレメトリ）を確認

Building OS の Hot KV に最新値が反映されることを確認します。

```bash
# ingress の受理挙動（既知 point は Accepted、未知 point_id / 他 gateway 所有は skip）
curl 'http://localhost:5000/telemetries/query?pointId=SOS-PT-001&latest=true'
```

- 受理: 既知 `point_id` かつ当該 `gateway_id` が所有 → 永続化（`StreamAck.accepted` に計上）。
- 拒否（skip + メータ）: 未知 `point_id` / 所有不一致 / publish 不成立。

### D-4. 下り（制御）を確認

```bash
# writable な点に制御指令（202 + controlId、結果は非同期）
curl -X POST 'http://localhost:5000/points/SOS-PT-004/control' \
  -H 'Content-Type: application/json' -d '{"value": 1}'
```

制御が **実 egress**（GatewayBridge → nexus-gateway → コネクタ → シミュレータの WriteProperty）を
通るには、対象 gateway の binding を `bacnet-sim` にします（OSS 既定は
`ENABLE_SIM_CONTROL=true` のシミュレート制御で、常に成功扱いになる点に注意）。

```bash
# API 側の環境変数（対象 gateway を実 egress に）
GatewayConnectionTypes__Map__GW-SOS-001=bacnet-sim
```

購読者（gateway）が未接続なら per-gateway NATS request が no-responders となり、
API は結果を待たず **503**（オフライン即時失敗）を返します。

### D-5. E2E テストで自動検証（任意・推奨）

nexus-gateway 側の Go E2E がそのまま検証に使えます（env 未設定なら自動 skip）。

```bash
cd nexus-gateway
# #44 — 取り込み + API 読み戻し（M4/M5）
E2E_BOS_INGRESS_URL=localhost:5051 E2E_BOS_API_URL=http://localhost:5000 \
  go test ./integration/... -run TestE2E_BosIngestAPI -v -timeout 60s

# #45 — 制御ゲート（writable→202 / 非writable→403）
E2E_BOS_API_URL=http://localhost:5000 \
  go test ./integration/... -run TestE2E_BosControlGate -v -timeout 30s

# #45 — egress ディスパッチ（gateway-bridge:5052 + binding=bacnet-sim が必要）
E2E_BOS_EGRESS_ADDR=localhost:5052 E2E_BOS_API_URL=http://localhost:5000 \
  go test ./integration/... -run TestE2E_BosEgressDispatch -v -timeout 60s
```

シミュレータ ↔ コネクタ間だけを見たい場合は Layer 2（`E2E_NATS_URL=nats://localhost:14222`）を
使います（`TestE2E_OpcUATelemetry` が自動 MVP ゲート）。詳細は
[`nexus-gateway/docs/e2e-test-overview.md`](nexus-gateway/docs/e2e-test-overview.md)。

---

## Step E — ユーザ・ロール・トークン（認証）

**2 つの独立した認証**があります。混同しないことが重要です。

| 認証の対象 | 仕組み | 使う場所 |
|---|---|---|
| 人間の運用者（Admin UI / Admin API） | **Keycloak / OIDC**（Bearer JWT, `realm_access.roles`） | Building OS `/admin`、nexus-gateway Admin API |
| ゲートウェイ ↔ Building OS のマシン認証 | **mTLS**（Keycloak は不関与） | ingress/egress の gRPC → [Step F](#step-f--証明書と-mtls本番寄り) |

### E-1. Building OS のユーザ管理

- ローカル開発は API を `DISABLE_AUTH=true`（`WithLocal`）で回避できます。
- 認証を有効化するには API を `--launch-profile WithLocalAuth` で起動し、Keycloak（:8080）を使います。
- ユーザ・ロール・権限は Web Client の `/admin` ワークスペース
  （`http://localhost:3000/admin`）に統合されています（別アプリ不要）。
- トークン取得・ロール付与の手順は
  [`gutp-building-os-oss-public/docs/keycloak-user-management.md`](keycloak-user-management.md)、
  権限モデルは [`docs/keycloak-permission-mapping.md`](keycloak-permission-mapping.md)。

### E-2. nexus-gateway のトークン取得（dev）

Admin API の主要エンドポイントはロール保護（operator / viewer）です。

```bash
TOKEN=$(curl -s http://localhost:18090/realms/nexus-gateway/protocol/openid-connect/token \
  -d grant_type=password \
  -d client_id=admin-ui -d client_secret=admin-ui-secret \
  -d username=operator -d password=operator | jq -r .access_token)

# 例: コネクタ一覧（要トークン）
curl -s http://localhost:18080/connectors -H "Authorization: Bearer $TOKEN" | jq
# ライフサイクル操作（operator ロール）: start | stop | restart | rollback
curl -s -X POST http://localhost:18080/connectors/<id>/restart -H "Authorization: Bearer $TOKEN" -i
```

dev 資格情報: `operator`/`operator`（フル操作）、`viewer`/`viewer`（読み取り専用）。
**ラボ以外へのデプロイ前に必ず変更**してください（`nexus-gateway/SECURITY.md`）。

### E-3. 本番の IdP 設定

本番では bundled Keycloak を使わず、**Building OS の Keycloak（または組織共通 IdP）**に
gateway と Admin UI の両方を向けます。realm には最低 2 つのロール
（`gateway-operator` / `gateway-viewer`）が必要です。

```env
# Gateway
KEYCLOAK_JWKS_URL=https://auth.example.com/realms/building-os/protocol/openid-connect/certs
KEYCLOAK_ISSUER=https://auth.example.com/realms/building-os
KEYCLOAK_AUDIENCE=nexus-gateway-admin-api   # "account" より専用 audience を推奨

# Admin UI
KEYCLOAK_ID=nexus-gateway-admin-ui
KEYCLOAK_SECRET=<production-secret>
NEXTAUTH_URL=https://gateway-admin.example.com
NEXTAUTH_SECRET=<random-secret>
ADMIN_API_URL=https://gateway-admin-api.example.com
```

`nexus-gateway/docker-compose.external-keycloak.yml` が統合/本番向けの ready-made override です。

---

## Step F — 証明書と mTLS（本番寄り）

**ゲートウェイ ↔ Building OS の gRPC は、Building OS の Traefik エッジで mTLS 終端**します
（ADR-0007）。gateway の `gateway_id` を **クライアント証明書の CN/SAN** に束縛し、
エッジが証明書由来の信頼ヘッダ `X-Gateway-Id` を注入、Building OS がフレームの
`gateway_id` と一致するかを検証します。

### F-1. nexus-gateway 側（mTLS で Building OS に接続）

`--bos-insecure` を外し、CA + クライアント証明書/鍵を渡します。

```bash
GATEWAY_ID=GW-SOS-001 \
BOS_ADDR=bos.example.com:443 \
BOS_CA_FILE=/etc/nexus/tls/ca.pem \
BOS_CERT_FILE=/etc/nexus/tls/gateway.crt \   # CN/SAN が GATEWAY_ID を表す
BOS_KEY_FILE=/etc/nexus/tls/gateway.key \
BOS_SERVER_NAME=bos.example.com \            # 任意: SNI/検証名の上書き
PROVISIONING_URL=https://bos.example.com/provisioning \
go run ./cmd/gateway
```

- `--bos-cert`/`--bos-key` を省くと **サーバ認証のみ TLS**（CA 検証だけ）、付けると **mTLS**。
- gateway 自身は `X-Gateway-Id` を送りません（Traefik エッジが証明書から供給）。
- 詳細: `nexus-gateway/SECURITY.md` と ADR-0007。

### F-2. Building OS 側（エッジ mTLS の配線）

- north-south gRPC ingress（Traefik）と cert-manager による mTLS 発行は
  [`gutp-building-os-oss-public/docs/oss-gateway-bridge-infra.md`](oss-gateway-bridge-infra.md)。
- 証明書の発行/ローテーション/失効、`gateway_id` 束縛の enforce 段階導入は
  [`docs/oss-gateway-security-ops.md`](oss-gateway-security-ops.md)。
- なりすまし注入防止: `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true` で、mTLS 検証済み
  gateway id とフレームの `gateway_id` の不一致を拒否（既定 OFF、mTLS 配線のある本番で ON）。

### F-3. シミュレータ側の証明書（該当時）

- opcua-sim は `Basic256Sha256 + SignAndEncrypt`・自己署名証明書・ユーザ/パスワード認証を
  実装。プロファイルは [`opcua-sim-gateway/docs/specs/security-profiles.md`](opcua-sim-gateway/docs/specs/security-profiles.md)。
- bacnet-sim の下り制御コネクタ（BOWS）の mTLS 証明書は環境変数
  `BOWS_EGRESS_TLS_CA/CERT/KEY` から注入（既定なし）。

---

## E2E チェックリスト

一気通貫の疎通を、上から順に確認します。

- [ ] **環境**: `docker --version` / `go version` / `dotnet --version` / `node -v` / `uv --version`
- [ ] **配置**: `nexus-gateway` と `../bacnet-sim-gateway` / `../opcua-sim-gateway` が並んでいる
- [ ] **Building OS**: `make wait-oss-stack`（Windows は `docker compose ... ps`）が成功、`/api/system/status` が OK
- [ ] **twin**: `/admin/twin` に Point List 投入、`/resources` で階層が見える
- [ ] **gateway 単体**: `curl :18080/health` が healthy
- [ ] **シミュレータ**: `--profile opcua`（または `bacnet`）で sim + connector が起動
- [ ] **ingress 有効化**: Building OS を `GRPC_INGRESS_PORT=5051` で recreate
- [ ] **上り**: gateway を `GATEWAY_ID/BOS_ADDR` で起動 → `/telemetries/query?...&latest=true` に値
- [ ] **下り**: `POST /points/{writable}/control` → 202、非 writable → 403
- [ ] **認証**: Keycloak トークンで `/connectors` が叩ける（operator）
- [ ] **E2E テスト**: `TestE2E_BosIngestAPI` / `TestE2E_BosControlGate` が pass

---

## トラブルシューティング

| 症状 | 想定原因 / 対処 |
|---|---|
| `make` が見つからない（Windows） | Windows に make は同梱されない。[Windows で make が使えない場合](#windows-で-make-が使えない場合)の生 `docker compose` で代替 |
| gRPC ingest が `Unimplemented` / 接続不可 | ConnectorWorker に `GRPC_INGRESS_PORT` 未設定（health のみ）。設定して `--force-recreate` |
| API が DB に繋がらない | `--no-deps` の個別 recreate でネットワークから外れることがある → deps 込みで `--force-recreate` |
| latest / range が空 | point が twin 未登録（404）、または flush 前（既定 5 分。テストは `PARQUET_FLUSH_INTERVAL=1`） |
| 制御が常に成功扱いで 503 にならない | OSS 既定 `ENABLE_SIM_CONTROL=true`（シミュレート制御）。実 egress は binding を `bacnet-sim` に |
| Grafana (3010) に繋がらない | 可観測系は既定で起動しない。`--profile observability` を付けて起動しているか確認 |
| `/connectors` 等で `401 Unauthorized` | トークン未設定/期限切れ。Keycloak トークンは短命 → 再取得 |
| `POST` アクションで `403 Forbidden` | トークンが `viewer`。`operator` で取得 |
| gateway がコネクタを管理できない | コンテナに host Docker socket（`/var/run/docker.sock`）マウントが必要 |
| `/telemetry` の `buffer_depth` が増え続ける | Building OS への上りが断。フレームが S&F バッファに滞留（Building OS 再起動時など想定内） |
| opcua-sim ビルドで `ffi.h: No such file` | `build-essential libffi-dev libssl-dev` を先に導入 |
| BACnet で機器が見つからない | BACnet/IP は UDP ブロードキャスト。`network_mode: host`（統合 Compose では設定済み）が必要 |

---

## ポート早見表

| ポート | サービス | 所属 | 既定起動 |
|---|---|---|---|
| 4222 / 8222 | NATS（client / monitor） | Building OS | ✅ |
| 5433 | PostgreSQL 16 | Building OS | ✅ |
| 6432 / 6433 | pgBouncer（アプリ / セッション） | Building OS | ✅ |
| 7878 | OxiGraph（SPARQL） | Building OS | ✅ |
| 9000 / 9001 | MinIO（S3 / console） | Building OS | ✅ |
| 8080 | Keycloak | Building OS | ✅ |
| 9090 / 3010 | Prometheus / Grafana | Building OS | ⛔ `--profile observability` |
| 3100 / 3200 | Loki / Tempo | Building OS | ⛔ `--profile observability` |
| **5000** | **API Server**（REST/gRPC, Swagger） | Building OS | ✅ |
| **3000** | **Web Client**（`/admin`, `/resources`, `/admin/twin`） | Building OS | `--profile webclient`（またはホスト `yarn dev`） |
| **5051** | **GatewayIngress**（gRPC ingress） | Building OS | `GRPC_INGRESS_PORT` 設定時 |
| **5052** | **GatewayEgress**（gRPC egress） | Building OS | ✅ |
| 8081 | ConnectorWorker health | Building OS | ✅ |
| 13000 | Admin UI | nexus-gateway | – |
| 18080 | Gateway Admin API | nexus-gateway | – |
| 18090 | Keycloak（dev） | nexus-gateway | – |
| 14222 / 18222 | NATS（client / monitor） | nexus-gateway | – |
| 15051 | mock Building OS（gRPC スタブ） | nexus-gateway | – |
| 4840 | OPC UA（opc.tcp） | opcua-sim | – |
| 47808 | BACnet/IP（UDP） | bacnet-sim | – |

---

## ビジュアル資料（技術レポート HTML）

各リポジトリの `docs/` に、SVG アーキテクチャ図・データフロー図・UI スクリーンショットを
含む **ビジュアルな技術レポート**があります。全体像を俯瞰するのに最適です
（ブラウザで開いてください）。

| ファイル | 内容 |
|---|---|
| [`nexus-gateway/docs/nexus-gateway-report.html`](nexus-gateway/docs/nexus-gateway-report.html) | 背景・概要・**SVG アーキテクチャ図**・データフロー/コネクタ図・技術スタック・性能・品質・**セキュリティモデル**・セットアップ |
| [`gutp-building-os-oss-public/docs/repository-review.html`](repository-review.html) | 概要・アーキテクチャ・データモデル & Point List 取込・データフロー・**UI スナップショット**・管理ツール UI・SBCO 概念比較・**nexus-gateway E2E 稼働記録(2026-06-20)**・実験環境仕様 |
| [`bacnet-sim-gateway/docs/architecture-review.html`](bacnet-sim-gateway/docs/architecture-review.html) | Executive Summary・System Context・**Architecture & Pipeline 図**・主要コンポーネント・データ/制御フロー・ADR・不変条件・**管理 UI スクリーンショット** |

補助的なシーケンス図（Markdown）も参照:
[`bacnet-sim-gateway/docs/specs/communication-sequences.md`](bacnet-sim-gateway/docs/specs/communication-sequences.md)。

---

## 参照ドキュメント

**Building OS**

- [`docs/getting-started.md`](getting-started.md) — オンボーディング（起動→API/Web→投入→読取/制御）
- [`docs/gateway-integration.md`](gateway-integration.md) — ゲートウェイ接続モデル（ingress/egress・point list 同期・mTLS）
- [`docs/keycloak-user-management.md`](keycloak-user-management.md) — ユーザ管理・ロール・トークン
- [`docs/system-architecture.md`](system-architecture.md) — 全体構成
- [`docs/evaluation-summary.md`](evaluation-summary.md) — E2E 評価結果

**nexus-gateway**

- [`docs/getting-started.ja.md`](nexus-gateway/docs/getting-started.ja.md) — 10 分ハンズオン
- [`docs/connector-spec.md`](nexus-gateway/docs/connector-spec.md) — コネクタ契約（NATS topic・Common Event・書込コマンド・カタログ）
- [`docs/e2e-test-overview.md`](nexus-gateway/docs/e2e-test-overview.md) — テスト全体像（Layer 1〜3）
- [`fixtures/integration/README.md`](nexus-gateway/fixtures/integration/README.md) — 共有 Point List と統合トポロジ
- [`SECURITY.md`](nexus-gateway/SECURITY.md) / `docs/adr/0001..0007` — セキュリティと設計判断

**シミュレータ**

- [`bacnet-sim-gateway/README.md`](bacnet-sim-gateway/README.md) — bbc-sim（CLI・モード・エクスポート・BOWS）
- [`opcua-sim-gateway/README.md`](opcua-sim-gateway/README.md) — opcua-sim（CLI・セキュリティ・OWS）

---

*このガイドは各リポジトリの README / docs（2026 年時点）を横断的に統合したものです。
最新の正は各リポジトリのドキュメントを参照してください。*
