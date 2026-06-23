# Microsoft 非依存（OSS）技術スタック分析

Building OS を **Microsoft / Azure コンポーネントに依存しない構成** で構築する場合の現状分析と OSS 技術スタック候補をまとめます。

> 本ドキュメントは技術選定の検討資料です。実際の移行はスコープ確定後に段階的に行う前提です。

---

## 1. エグゼクティブサマリー

- 現状の Building OS は **データ収集・保存・分析・認証・監視・配信のほぼ全レイヤーが Azure マネージドサービスに依存** しています（IoT Hub / Event Hub / CosmosDB / Digital Twins / Blob Storage / Functions / Static Web Apps / Application Insights / Azure AD / Microsoft Fabric / ACR / Bicep）。
- OSS への置換は **技術的にはすべてのレイヤーで実現可能** です。ただしロックインの強さに濃淡があり、移行難易度は均一ではありません。
- **重要な前提**: 「Microsoft 非依存」には 2 つのスコープがあります（後述 §3）。本書は **推奨スコープ = 「Azure マネージドサービスを OSS に置換し、.NET ランタイムは維持」** を主軸に、完全脱却版も併記します。
- **移行が容易な領域（早期に着手すべき）**: 可観測性（OpenTelemetry を既に部分採用）、オブジェクトストレージ、コンテナレジストリ、フロントエンドホスティング。
- **移行が困難な中核**: CosmosDB のテレメトリ移行、IoT Hub のデバイス接続・プロビジョニング。一方、当初「高難易度」とした **Change Feed と Azure Digital Twins は、NATS 化 / OxiGraph (SPARQL) 化により「中」へ低下**（コード調査で実態が単純なグラフ走査・コマンドキューと判明したため。§4.4 / §4.5 参照）。

---

## 2. 現状の Microsoft / Azure 依存マップ

| レイヤー | 現状の Azure / MS コンポーネント | 主な利用箇所 | ロックイン強度 |
|----------|----------------------------------|--------------|----------------|
| IoT 接続 | **Azure IoT Hub**（S1, 4 パーティション）、`Microsoft.Azure.Devices` | エッジデバイス → IoT Hub → Connector | 高 |
| イベントストリーミング | **Azure Event Hub**（IoT Hub 互換エンドポイント）、`Azure.Messaging.EventHubs` | Connector の `EventHubTrigger` | 中（→ NATS で Change Feed も統合） |
| サーバーレス実行 | **Azure Functions**（dotnet-isolated, Consumption）、`Microsoft.Azure.Functions.Worker.*` | 9 Connector + ColdDataConsumer | 中 |
| Hot/Warm DB | **Azure CosmosDB**（`Microsoft.Azure.Cosmos`）+ **Change Feed** | テレメトリ保存、制御コマンド（`CosmosDBTrigger`） | テレメトリ=高 / Change Feed=中（NATS 化） |
| デジタルツイン | **Azure Digital Twins**（DTDL, `Azure.DigitalTwins.Core`） | 建物階層・制御スキーマ解決 | 中（実態はグラフ走査。OxiGraph スクラッチ） |
| コールド/分析 | **Blob Storage** + **Microsoft Fabric (Lakehouse)** + **MySQL Flexible Server** | ColdDataConsumer → Blob → Fabric | 中 |
| 認証・認可 | **Azure AD** + **MSAL**（`@azure/msal-*`, `Microsoft.Identity.Web`, `Microsoft.Graph`）+ **Managed Identity** | フロント/API 認証、ワークロード ID | 中 |
| 可観測性 | **Application Insights** + **Azure Monitor** + **Log Analytics**（一部 **OpenTelemetry** 併用） | API/Functions 監視 | 低〜中 |
| フロント配信 | **Azure Static Web Apps**（web-client / admin-console） | Next.js ホスティング | 低 |
| コンテナレジストリ | **Azure Container Registry**（Basic） | API Server Docker イメージ | 低 |
| IaC | **Azure Bicep**（14 モジュール） | 全インフラ定義 | 高（要再記述） |
| CI/CD | **GitHub Actions** + `azure/login` / SWA deploy / ACR / `functions-action` | デプロイパイプライン | 中 |
| アプリ基盤 | **.NET 8 / ASP.NET Core / EF Core**（いずれも MIT・.NET Foundation の OSS） | バックエンド全体 | 低（OSS だが要スコープ判断） |

> 補足: 外部 API（Daikin OpenAPI）連携は既に **AWS Cognito + SigV4** を利用しており、認証は完全な Azure 専有ではありません。

---

## 3. 移行スコープの定義（最重要の論点）

「Microsoft 非依存」には次の 2 段階があり、推奨と難易度が大きく変わります。意思決定が必要です。

### スコープ A（推奨）: Azure マネージドサービスのみ OSS 置換
- 置換対象: IoT Hub / Event Hub / CosmosDB / Digital Twins / Blob / Functions ランタイム / SWA / App Insights / Azure AD / Fabric / ACR / Bicep。
- **維持**: .NET 8 / ASP.NET Core / EF Core（これらは MIT ライセンスの OSS で Linux 上で完全動作。Azure ロックインではない）。
- 利点: 既存のドメインロジック・コネクタ実装の大半を再利用でき、リスク・コストが最小。

### スコープ B（完全脱却）: Microsoft 製コードを一切排除
- スコープ A に加え、.NET ランタイムも撤廃 → バックエンドを **Go / Java (Spring Boot / Quarkus) / Node.js (NestJS) / Rust / Python (FastAPI)** で全面再実装。
- GitHub も Microsoft 所有のため、リポジトリ/CI を Forgejo・GitLab 等へ移行。
- 利点: 名実ともに MS 非依存。欠点: **実質フルリライト** となりコスト・期間・リグレッションリスクが極大。

> **推奨**: まず **スコープ A** を実行し、Azure ロックインを解消する。スコープ B（言語移行）は ROI が低く、必要性が明確になってから個別判断とする。以降の候補は両スコープに対応できるよう記載します。

---

## 4. レイヤー別 OSS 技術スタック候補

各レイヤーで「第 1 候補」と「代替候補」、選定理由、移行難易度（低 / 中 / 高）を示します。

### 4.1 IoT デバイス接続・プロビジョニング（← IoT Hub）

- **第 1 候補**: 任意の前段に **Eclipse Hono**（AMQP Northbound）+ MQTT ブローカは **Mosquitto / VerneMQ**
  - Hono はデバイス接続・テレメトリ・コマンド&コントロール・テナント/デバイスレジストリを提供し、IoT Hub の機能スコープに最も近い。
  - **ブローカ選定（EMQX 不採用）**: EMQX は現行 **Business Source License 1.1（BSL）** で、第三者向け hosted/embedded 利用に
    制限があり、OSS 外部展開可能な基盤という本プロジェクトの前提とは相容れない。よって **VerneMQ（Apache 2.0）/ Mosquitto（EPL/EDL）** を採用する。
  - **Hono の更新頻度（リスク）**: Eclipse Hono のプロジェクト状態は Mature だが、公式の最新リリースは **2023-11 の 2.4.1 / 2.3.2** と
    更新頻度が低い。2026 年時点で新規の中核コンポーネントには据えず、**必要時のみ前段に置く**扱いとし、正本取り込み経路は gRPC GatewayIngress（#181）とする。
- **代替候補**: **ThingsBoard CE**（接続〜可視化までオールインワン）。※ EMQX 単体は上記ライセンス理由で不採用。
- **選定理由**: IoT Hub は「デバイス ID 管理 + プロトコルゲートウェイ + Kafka/Event Hub 互換出力」。Hono が同等の責務を OSS で代替できる。
- **移行難易度**: **高**（デバイス接続文字列/プロビジョニングの再設計、`Tools/development-edge-device` と実機エッジの接続先変更）

### 4.2 イベントストリーミング / メッセージング（← Event Hub ＋ Change Feed を統合）

- **第 1 候補**: **NATS JetStream**
  - **規模の根拠**: 本ワークロードは IoT Hub S1 = 40 万 msg/日 ≈ 5 msg/秒程度と小規模。Kafka は ZooKeeper/KRaft・JVM・パーティション設計を伴い運用が重く過剰。NATS は単一 Go バイナリ、3 ノード Raft で HA、運用が圧倒的に軽い。
  - Subject 階層 `telemetry.<building>.<floor>.<device>.<point>` がテレメトリ階層に自然対応し、ワイルドカード購読でパーティション設計が不要。
  - JetStream で永続化・Durable Consumer（コンシューマグループ相当）・リプレイ・メッセージ重複排除（`Nats-Msg-Id`）を提供。
  - **request-reply 内蔵**により、制御コマンド（PointControl）を CosmosDB Change Feed なしで実装可能（§4.4 参照）。Event Hub と Change Feed の 2 依存を 1 つに統合できる。
- **代替候補**: **Apache Pulsar**、**RabbitMQ Streams**、（将来的に大規模ログ分析・Kafka Connect エコシステムが必要になった場合のみ）**Apache Kafka**（Strimzi Operator）
- **トレードオフ**: Kafka Connect / Debezium 相当のコネクタ群は小さい。コールドデータ書き出しは JetStream コンシューマで Parquet 出力すれば足り、本件規模では問題にならない。
- **移行難易度**: **中**（接続文字列 → NATS、`EventHubTrigger` を JetStream コンシューマへ。Change Feed 廃止も同時に実現でき、正味の依存はむしろ減る）

### 4.3 サーバーレス/コネクタ実行基盤（← Azure Functions）

- **第 1 候補**: コネクタを **コンテナ化した .NET Worker Service** として実装し、NATS JetStream の Durable Consumer + Kubernetes **CronJob**（`TimerTrigger` 相当）で実行。オートスケールは **KEDA**（CNCF・ベンダ中立。NATS JetStream スケーラ対応）。
- **代替候補**: **Knative**、**OpenFaaS**、**Apache OpenWhisk**（FaaS モデルを維持したい場合）
- **選定理由**: Functions のトリガ/バインディングは「NATS 購読 + 出力先書き込み」に分解可能。**ドメインロジックは流用でき、薄いインフラ層のみ書き換え**。
- **移行難易度**: **中**（`EventHubTrigger` / `CosmosDBOutput` / `CosmosDBTrigger` / `TimerTrigger` を明示的コードへ）

### 4.4 Hot/Warm データストア（← CosmosDB + Change Feed）

テレメトリ（時系列）と制御コマンド（ドキュメント + 変更検知）で要件が異なるため分割設計を推奨。

- **テレメトリ（時系列）**
  - 第 1 候補: **TimescaleDB**（PostgreSQL 拡張。SQL 資産・運用知見を活かせる）または **Apache IoTDB**（IoT 時系列特化）
    - **ライセンス留意**: TimescaleDB は **`tsl/` ディレクトリ外は Apache 2.0、`tsl/` 配下は Timescale License（TSL）** の二層構成。
      圧縮 / columnstore 等の一部機能は TSL に含まれるため、**Apache-only で成立する構成か**（使用イメージ・機能・拡張）を ADR に明記すること。
      なお #216 以降 **既定の warm tier は Parquet レイク**で、TimescaleDB は opt-in（`WARM_STORE=timescale`）に降格しており、テレメトリ経路の必須依存ではない。
  - 代替: **InfluxDB OSS**、**QuestDB**、**ClickHouse**（高スループット分析寄り）
- **制御コマンド（ドキュメント）**
  - 第 1 候補: **PostgreSQL（JSONB）**（時系列と DB 統合でき運用が単純）
  - 代替: **MongoDB CE**（*SSPL ライセンスに留意*）、**FerretDB**（Postgres 上の Mongo 互換 API）
- **Change Feed（`PointControlConnector` の変更検知）**
  - **第 1 候補（推奨設計）**: Change Feed を廃止し、制御コマンドを **NATS JetStream のコマンド用 Stream へ publish**。Connector は Durable Consumer で受信 → デバイス制御実行 → 結果を publish / 書き戻し。`leases` コンテナや「処理済みチェック」は JetStream のオフセット管理＋メッセージ重複排除で代替。
  - **コード確認に基づく評価**: 現状の `PointControlConnector` は「DB に書く → `CosmosDBTrigger`(Change Feed) で拾う → 結果を書き戻す」回りくどい実装。NATS の request-reply / Durable Consumer の方がむしろ単純で、CosmosDB 由来の高難易度要因が 1 つ消える。
  - 代替: PostgreSQL を維持する場合は **Debezium**（CDC）で論理レプリケーションを購読
- **移行難易度**: テレメトリ移行は **高**（データモデル/パーティションキー設計、データ移行）。ただし **Change Feed 依存は NATS 化で「高 → 中」へ低下**

### 4.5 デジタルツイン / 建物階層モデル（← Azure Digital Twins）

- **第 1 候補（推奨）**: **OxiGraph を中核としたスクラッチ実装**
  - **コード確認に基づく根拠**: ADT は実質「グラフ ＋ DTDL モデルレジストリ ＋ クエリ言語」としてのみ利用されている。`DigitalTwinHierarchyResolver` は `MATCH (Building)-[:hasPart]->(Floor)-[:hasPart]->(Space)<-[:locatedIn]-(Device)-[:hasPoint]->(Point)` 相当のグラフ走査、`ControlSchemaResolver` は `IS_OF_MODEL` ＋プロパティ一致のノード検索。**DTDL は JSON-LD（W3C RDF 標準）ベース**であるため、RDF/SPARQL モデルへ概念的に直結し、上記グラフ走査は SPARQL のプロパティパス（`?b :hasPart/:hasPart ?s . ?d :locatedIn ?s . ?d :hasPoint ?p`）でほぼ 1:1 に表現可能。
  - **ライブ値は ADT に存在しない**（CosmosDB 側 →時系列 DB）。グラフ DB はトポロジ＋メタデータ＋制御スキーマのみ保持＝**低書き込み・読み取り中心**（既存実装も 5 分キャッシュ）。HA 要件が緩く単一インスタンス＋バックアップで実用上十分。
  - 既存の抽象（`IResourceHierarchyResolver` / `IControlSchemaResolver` / `IDigitalTwinDatabase`）と生成済み DTDL エンティティ/バリデータ（`Defines/Entities/Dtdl.*`）を再利用でき、**置換はインフラアダプタ 3 本の書き換えに限定**。
  - **Eclipse Ditto は非推奨**: Ditto の接続・メッセージング・ポリシー・検索サブシステムは NATS・Keycloak・API・グラフ DB で充足するため機能が重複・過剰。モデル層も生成済みバリデータで自前管理しており Ditto のモデル管理と二重になる。
- **グラフ DB 候補**:
  - **OxiGraph**（Rust 製、SPARQL 1.1、Apache 2.0 / MIT）: DTDL=JSON-LD と RDF が直結し変換コストが最小。単一バイナリ・組み込み運用が可能で、HA 要件が緩い読み取り中心ワークロードに最適。HTTP/SPARQL Protocol で .NET から `HttpClient` または `dotNetRDF` 経由でアクセス。寛容ライセンスで配布制約なし。**留意**: 公式 README に「heavy development」「SPARQL query evaluation has not been optimized yet」とあり、性能・バックアップ/リストア・SPARQL 互換性・.NET 接続は Phase 0 で検証必須（公式 .NET SDK なし）。読み中心・中小規模 Twin に限定採用とし、大規模化時は PostgreSQL+AGE 等へ切替可能な抽象を維持。
  - **PostgreSQL + Apache AGE**（openCypher, Apache 2.0）: 時系列/制御で既に必要な PostgreSQL に同居でき構成要素が最小化できる。Cypher 移植性を重視する場合の代替。要 AGE の対応 Postgres バージョン確認。
  - **Neo4j Community**（Cypher, GPLv3）: ADT の `MATCH` クエリがほぼ 1:1 で移植できるが GPLv3 のため配布形態に注意。CE はクラスタ不可（単一ノード＋バックアップ）。
  - 代替: **Memgraph**（要ライセンス確認）、大規模化時のみ **JanusGraph** / **NebulaGraph**
- **スクラッチで作る範囲**: ① グラフスキーマ（RDF クラス Building/Floor/Space/Device/Point、述語 hasPart/locatedIn/hasPoint）、② 既存 DTDL/JSON を RDF として取り込むローダー（JSON-LD は OxiGraph に直接ロード可能、生成済みバリデータ流用）、③ 3 アダプタの SPARQL 再実装。Ditto の接続/メッセージング/ポリシー/検索は作らない。
- **移行難易度**: **中**（**高 → 中** に低下。ドメイン非変更、アダプタ＋モデル取込のみ）

### 4.6 コールドデータ / 分析基盤（← Blob Storage + Microsoft Fabric + MySQL）

- **オブジェクトストレージ**: **MinIO**（S3 互換。`ColdDataConsumer` の出力先を差し替え）
- **分析レイク**: **Apache Iceberg** または **Delta Lake (OSS)** + Parquet on MinIO、クエリエンジン **Trino** または **Apache Spark**。可視化は **Apache Superset** / **Grafana**
  - 軽量構成なら **ClickHouse** 単体、または **DuckDB** によるアドホック分析
- **RDB（MySQL Flexible Server）**: 共有 **PostgreSQL/TimescaleDB**（4.4 と統合）へ移行済み。EF Core を継続利用し、ユーザー・グループ・認可テーブルを単一インスタンスに集約。グリーンフィールド OSS のためデータ移行は行わない（本番 MySQL データなし）
- **移行難易度**: **中〜高**（出力先 SDK 差し替えは容易だが、Fabric 分析資産の移植が中核）

### 4.7 認証・認可（← Azure AD / MSAL / Microsoft Graph / Managed Identity）

- **IdP**: **Keycloak**（OIDC / OAuth2 のデファクト OSS。Azure AD の標準的代替）
  - 代替: **Authentik**、**ZITADEL**、**Ory（Hydra/Kratos）**
- **フロントエンド**: `@azure/msal-browser` / `@azure/msal-react` → **oidc-client-ts** + **react-oidc-context**、または **Auth.js (NextAuth)**。`middleware.ts` の MSAL クッキー判定を OIDC セッション判定へ
- **バックエンド**: `Microsoft.Identity.Web` → 標準 JWT Bearer 検証（OIDC discovery 経由）。フレームワーク非依存にするなら言語別 OIDC ライブラリ
- **ユーザ/グループ管理（Microsoft Graph）**: Keycloak Admin API / SCIM
- **ワークロード ID（Managed Identity）**: Keycloak client credentials、または **SPIFFE/SPIRE**
- **移行難易度**: **中**（IdP 構築、トークンスコープ設計、フロント/ミドルウェア書き換え。フロント 2 アプリ分）

### 4.8 可観測性（← Application Insights / Azure Monitor / Log Analytics）

- **第 1 候補**: **OpenTelemetry**（API Server で既に部分採用済み）→ OTLP で送出し
  - メトリクス: **Prometheus**、可視化: **Grafana**
  - ログ: **Loki**
  - 分散トレース: **Tempo** または **Jaeger**
- **選定理由**: `Azure.Monitor.OpenTelemetry.Exporter` を **OTLP Exporter に差し替えるだけ** でコード変更が最小。`host.json` の App Insights 設定を撤去。
- **移行難易度**: **低〜中** — **最も移行容易な「勝ち筋」。早期着手推奨**

### 4.9 フロントエンドホスティング（← Azure Static Web Apps）

- **第 1 候補**: コンテナ化した Next.js を Kubernetes 上で実行し、**Traefik** / **Nginx** / **Caddy** で配信（静的エクスポート可能なら Nginx 静的配信）
- **SWA 組込み認証**: §4.7 の Keycloak へ統合
- **移行難易度**: **低**（ビルド成果物の配信先変更が中心）

### 4.10 コンテナレジストリ（← Azure Container Registry）

- **第 1 候補**: **Harbor**（CNCF 卒業プロジェクト。脆弱性スキャン/署名対応）
- **代替候補**: **Forgejo / Gitea / GitLab** 内蔵レジストリ、**Distribution（Docker Registry）**
- **移行難易度**: **低**

### 4.11 IaC（← Azure Bicep）

- **第 1 候補**: **OpenTofu**（Terraform の完全 OSS フォーク）+ **Helm** / **Kustomize**。クラスタは **k3s** / **kubeadm** + **Ansible** でプロビジョニング
- **代替候補**: **Pulumi**（言語ネイティブ IaC）
- **移行難易度**: **高**（全テンプレート再記述。ただし対象が OSS スタックへ変わるため実質再設計）

### 4.12 CI/CD（← GitHub Actions + Azure デプロイ）

- スコープ A: GitHub Actions を維持し、Azure 固有ステップ（`azure/login` / SWA deploy / ACR / `functions-action`）を **kubectl / Helm / Argo CD** に置換
- スコープ B: リポジトリ/CI を **Forgejo Actions** / **GitLab CI** / **Woodpecker CI** / **Drone** / **Jenkins** / **Tekton + Argo CD**（GitOps）へ移行
- **移行難易度**: **中**（パイプライン書き換え。リポジトリ移行はスコープ B のみ）

### 4.13 アプリケーションランタイム（.NET の扱い）

- スコープ A: **.NET 8 / ASP.NET Core / EF Core を維持**（MIT・OSS・Linux 完全対応）。最小リスク・最大コード再利用。
- スコープ B: **Go / Java (Spring Boot・Quarkus) / Node.js (NestJS) / Rust / Python (FastAPI)** へ全面再実装。ROI が低く非推奨。
- **移行難易度**: スコープ A=**低** / スコープ B=**極高**

---

## 5. 推奨ターゲットスタック（スコープ A）

| レイヤー | 現状 | 推奨 OSS | 代替 |
|----------|------|----------|------|
| IoT 接続 | Azure IoT Hub | **Eclipse Hono（任意・前段）+ Mosquitto/VerneMQ**（EMQX は BSL のため不採用） | ThingsBoard CE |
| イベント/メッセージング | Event Hub ＋ Change Feed | **NATS JetStream**（両者を統合） | Pulsar / RabbitMQ /（大規模時）Kafka |
| 実行基盤 | Azure Functions | **.NET Worker on K8s + KEDA** | Knative / OpenFaaS |
| 時系列 DB | CosmosDB | **TimescaleDB** / Apache IoTDB | InfluxDB / ClickHouse |
| ドキュメント/制御 | CosmosDB + Change Feed | **PostgreSQL(JSONB) + NATS**（Change Feed 廃止） | Debezium CDC / FerretDB |
| デジタルツイン | Azure Digital Twins | **OxiGraph（組み込み運用、SPARQL）** | PostgreSQL+AGE / Neo4j CE |
| オブジェクトストレージ | Blob Storage | **MinIO** | — |
| 分析基盤 | Microsoft Fabric | **Iceberg/Delta + Trino + Superset** | ClickHouse / DuckDB |
| RDB | MySQL Flexible | **PostgreSQL** | MySQL セルフホスト |
| 認証 | Azure AD / MSAL | **Keycloak + oidc-client-ts** | Authentik / ZITADEL / Ory |
| 可観測性 | App Insights | **OpenTelemetry + Prometheus/Grafana/Loki/Tempo** | Jaeger |
| フロント配信 | Static Web Apps | **Next.js on K8s + Traefik/Nginx** | Caddy |
| レジストリ | ACR | **Harbor** | Gitea/GitLab Registry |
| IaC | Bicep | **OpenTofu + Helm/Kustomize** | Pulumi |
| CI/CD | GitHub Actions (Azure) | **GitHub Actions + Argo CD** | Forgejo/GitLab CI |
| アプリ基盤 | .NET 8 | **.NET 8 維持** | （スコープ B 時のみ言語移行） |

**基盤**: 上記は **Kubernetes**（オンプレは k3s/kubeadm、または非 MS クラウドの managed K8s）上での運用を前提。

---

## 6. 段階的移行アプローチ（推奨順）

ロックインが弱く独立性の高いものから着手し、リスクを逓減します。

1. **フェーズ 1 — クイックウィン（低リスク・独立性高）**
   - 可観測性: Azure Monitor Exporter → OTLP + Grafana スタック（コード変更最小）
   - コンテナレジストリ: ACR → Harbor
   - オブジェクトストレージ: Blob → MinIO（`ColdDataConsumer` の出力先差し替え）
2. **フェーズ 2 — 認証・配信基盤**
   - Keycloak 構築、フロント/ API を OIDC へ移行、Next.js を K8s 配信へ
3. **フェーズ 3 — メッセージング・実行基盤**
   - Event Hub → NATS JetStream、Functions → コンテナ化 .NET Worker + KEDA
   - **同フェーズで Change Feed を廃止**し、`PointControlConnector` を NATS コマンドストリーム＋Durable Consumer に置換（`leases` コンテナ撤廃）
4. **フェーズ 4 — 中核データ層**
   - CosmosDB テレメトリ → TimescaleDB/PostgreSQL、データ移行（最難関）
   - Azure Digital Twins → OxiGraph：RDF オントロジ定義＋既存生成バリデータでの JSON-LD モデル取込＋`DigitalTwinHierarchyResolver`/`ControlSchemaResolver`/`IDigitalTwinDatabase` の SPARQL 再実装
5. **フェーズ 5 — IoT 接続・分析基盤**
   - IoT Hub → Hono/EMQX、エッジ接続先切替、Fabric → Iceberg/Trino/Superset
6. **フェーズ 6 — IaC / CI/CD 整理**
   - Bicep → OpenTofu/Helm、デプロイパイプライン再構成

---

## 7. リスクと留意点

- **ライセンス**: MongoDB CE（SSPL）、Grafana（AGPL）、Neo4j CE（GPLv3）、Memgraph（要確認）は商用配布形態によって制約あり。第 1 候補は寛容ライセンス（Apache 2.0 / MIT）の OxiGraph / PostgreSQL+AGE / FerretDB / Prometheus 等から優先選択。
- **NATS の適用範囲**: 超大規模ログ分析や Kafka Connect 系エコシステムが必須の場合は不向きだが、本件の建物 IoT 規模（≈5 msg/秒）では大幅な余裕があり問題なし。将来そうした要件が出た場合のみ Kafka 併用を検討。
- **グラフ DB の HA**: OxiGraph は組み込み運用（単一プロセス）。グラフは読み取り中心・低書き込み（5 分キャッシュ済み）のため定期スナップショット＋必要時ホットスタンバイで実用上許容。PostgreSQL+AGE を選べば Postgres の HA に一本化できる。
- **スクラッチ実装の責任範囲**: DTDL モデル取込・グラフスキーマ・整合性検証は自前実装となる。ただし生成済みバリデータと既存抽象を再利用するため範囲は限定的。
- **マネージド機能の喪失**: 自動スケール・バックアップ・SLA・パッチ適用を **自前運用** する必要がある（SRE コスト増）。Kubernetes Operator（NATS, CloudNativePG, MinIO Operator 等）で緩和。
- **データ移行**: CosmosDB テレメトリのスキーマ・パーティション設計差異により、単純コピーではなく ETL が必要。
- **制御系の再設計**: イベント駆動の制御系（`PointControlConnector`）は NATS JetStream のコマンドストリーム＋Durable Consumer 化により、現状の Change Feed 方式より単純化される（依存削減・難易度低下）。
- **GitHub の扱い**: GitHub は Microsoft 所有。スコープ B では CI/リポジトリ移行が追加発生。スコープ A では実務上問題になりにくい。
- **.NET の位置づけ**: .NET / ASP.NET Core / EF Core は Azure ロックインではない OSS。「Azure 排除」目的なら維持が合理的で、これを撤廃すると ROI が大きく低下する点を意思決定者と合意すべき。

---

## 付録: 確認が必要な意思決定事項

1. スコープは **A（.NET 維持・Azure のみ置換）** か **B（言語含む完全脱却）** か。
2. 運用基盤は **オンプレ K8s** か **非 MS クラウドの managed K8s** か。
3. ライセンス制約（AGPL/SSPL/GPLv3）の許容範囲（第 1 候補 OxiGraph は Apache 2.0 / MIT のため影響は限定的だが、代替候補選定や Grafana 等の他コンポーネントには影響）。
4. GitHub を継続利用してよいか（スコープ B 判定に直結）。

### 決定済みの方針（本検討で確定）

- **メッセージング = NATS JetStream**（Kafka は運用過剰のため不採用。Event Hub と Change Feed を統合）。
- **デジタルツイン = OxiGraph 中核のスクラッチ**（Eclipse Ditto は機能重複のため不採用）。DTDL=JSON-LD と RDF/SPARQL の概念的直結、寛容ライセンス、単一バイナリで組み込み運用可能な点を評価。代替は Cypher 移植性重視時の PostgreSQL+AGE。
- **時系列 DB = TimescaleDB（PostgreSQL 拡張）**。Warm（Hypertable、3 ヶ月）→ Cold（MinIO 上 Parquet）を `ITelemetryDatabase` 抽象で透過化（移行計画 §7 参照）。
