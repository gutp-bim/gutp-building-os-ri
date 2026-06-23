# OSS 移行計画 (スコープ A: Azure マネージドサービス置換)

`docs/oss-tech-stack-analysis.md` に基づき、Building OS を Azure マネージドサービス非依存（.NET 8 は維持）の OSS スタックへ段階的に移行する計画。

---

## 1. 移行原則

### 1.1 基本方針

1. **Strangler Fig パターン**: 既存システムを稼働させたまま、新 OSS コンポーネントを並行配備して段階的に置換する。一斉切替は行わない。
2. **テストドリブン (TDD)**: 各置換は「現行システムの動作を記録する E2E テスト → OSS 版を Red から Green へ」の順で進める。
3. **機能パリティの担保**: 各レイヤーで「現行 (Azure) と OSS の出力を同一入力で比較するハーネス」を用意し、ドリフトを検出可能にする。
4. **ロールバック可能性**: 各フェーズの切替は環境変数 / Feature Flag で旧→新を切り替えられる構造を維持する。
5. **ローカル先行**: docker-compose で OSS スタック一式が起動できる状態を作ってから本番移行に着手する。
6. **コア再利用**: ドメインロジック (`BuildingOS.Shared/Domain`, `Defines/Entities`) は変更しない。書き換えはインフラアダプタ層 (`Infrastructure/*`) に限定する。

### 1.2 アーキテクチャ層別の置換戦略

| 層 | 戦略 | 理由 |
|---|---|---|
| ドメインロジック | **変更しない** | スコープ A は Azure 排除であり、.NET ドメインコードは資産 |
| インターフェース (`I*Database`, `I*Repository`, `I*Resolver`) | **維持** | 既存抽象に新アダプタを追加するだけで切替可能 |
| Azure SDK 直接呼び出し | **アダプタ化** → OSS SDK へ差替 | `Microsoft.Azure.Cosmos`, `Azure.DigitalTwins.Core`, `Azure.Messaging.EventHubs` 等 |
| Azure Functions トリガ | **コード化** | `EventHubTrigger` → NATS Consumer、`CosmosDBTrigger` → JetStream Stream、`TimerTrigger` → K8s CronJob |
| インフラ定義 (Bicep) | **再記述** | OSS スタック対応の OpenTofu/Helm へ |

---

## 2. 全体ロードマップ

```
Phase 0:  基盤整備（テスト・並行稼働・観測）          ←★最重要・全フェーズの前提
Phase 1:  クイックウィン（可観測性・レジストリ・Blob）  ←独立性が高くリスク低
Phase 2:  認証・フロント配信
Phase 3:  メッセージング・実行基盤（Change Feed廃止含む）
Phase 4:  中核データ層（テレメトリ + Digital Twins）
Phase 5:  IoT接続・分析基盤
Phase 6:  IaC / CI/CD 再構成
```

各フェーズは独立に検証・本番投入可能とし、Phase 0 の基盤上で並行稼働させる。

> **フェーズ番号の規約**: **Phase 0 は準備（基盤整備）フェーズで、移行フェーズには数えない**。
> 実際の移行は **Phase 1–6 の 6 フェーズ**。「Phase 0–6」（計 7 区分）と「移行 6 フェーズ」は同じものを指す
> （= Phase 0〔準備〕 + 移行 6 フェーズ〔1–6〕）。サマリ等で「6 フェーズ」と書く場合は移行フェーズ（1–6）を指す。

---

## 3. Phase 0: 基盤整備（マイグレーション・ハーネス）

**目的**: 以降のすべてのフェーズで「機能パリティを検証可能」「並行稼働可能」「いつでもロールバック可能」とするための共通基盤を整える。

### 3.1 成果物

#### F0-1: ローカル OSS スタック docker-compose
- 既存 `docker-compose.yaml` と並列に `docker-compose.oss.yaml` を新設
- 含めるサービス:
  - NATS JetStream
  - **PostgreSQL 16 + TimescaleDB 拡張**（テレメトリ Warm + 制御コマンド監査 + ユーザ管理を統合）
  - **OxiGraph**（グラフ DB、組み込み的に単一プロセス HTTP サーバ運用）
  - MinIO（S3 互換、Cold データ Parquet 置き場）
  - Keycloak（OIDC IdP）
  - Prometheus / Grafana / Loki / Tempo（観測基盤）
  - Mosquitto または EMQX（MQTT、Phase 5 用に先行用意）
- `make local-up-azure` / `make local-up-oss` / `make local-up-dual`（両方起動）の Makefile

#### F0-2: 現状動作のゴールデンファイルテスト
既存システムの「入出力契約」を凍結する E2E テスト群を整備する。これが OSS 版の合格基準となる。

- **API レスポンス契約**: 全 API エンドポイントの代表ケース（buildings/floors/spaces/devices/points/telemetries/control）の request/response JSON を `tests/golden/api/*.json` に固定
- **テレメトリ取り込み契約**: 各 Connector に対する device-message → 永続化結果のスナップショット（`BuildingOS.Functions.Test/TestData/` を拡張）
- **ADT クエリ契約**: `DigitalTwinHierarchyResolver`/`ControlSchemaResolver` の主要シナリオの入出力スナップショット
- **制御フロー契約**: `PointControlConnector` の Change Feed → Handler → Result 書き戻しのトレース

#### F0-3: 機能パリティ比較ハーネス
- `Tools/parity-harness/` に新設
- 同一入力を 「Azure 版エンドポイント」と「OSS 版エンドポイント」の両方に送り、出力 JSON を diff
- Connector: 同一 device message を Event Hub と NATS の両方へ publish → CosmosDB と PostgreSQL の書き込み結果を比較
- API: 同一クエリを両 API Server に送り、レスポンスを比較
- レポートを HTML/JSON で生成

#### F0-4: アダプタ層の Feature Flag 化
- `IDigitalTwinDatabase` / `ITelemetryDatabase` / `IPointControlRepository` 等のインターフェースを軸に、実装を DI で切替
- 環境変数 `BUILDING_OS_BACKEND=azure|oss|dual` で切替
  - `azure`: 現行（既定）
  - `oss`: OSS 版のみ
  - `dual`: 両方に書き、読みは Azure（シャドウライト検証用）

#### F0-5: 統合テスト基盤（Testcontainers）
- `BuildingOS.IntegrationTest/`（新規プロジェクト）
- Testcontainers .NET で NATS / PostgreSQL / MinIO / Keycloak を起動
- 既存の Moq ベースのユニットテストを補完する位置づけ（ユニットは残す）

#### F0-6: 観測基盤（OpenTelemetry 移行先行）
- Phase 1 と一体化（Phase 1 の F1-1 を Phase 0 で先行着手）
- 理由: 移行中の挙動差を観測できないと比較ができない

### 3.2 完了条件
- [ ] `make local-up-oss` で OSS スタックが全コンテナ Healthy
- [ ] 既存全テストが Azure / OSS 両モード（OSS は no-op アダプタ）で Green
- [ ] パリティハーネスで「Azure==Azure」自己比較が 100% 一致
- [ ] CI で Azure モードと OSS モードの両方が並行実行される

---

## 4. Phase 1: クイックウィン

リスク・独立性が高いものから着手。Phase 0 と並行で進めて良い。

### F1-1: Application Insights → OpenTelemetry + Prometheus/Grafana/Loki/Tempo
- `Azure.Monitor.OpenTelemetry.Exporter` を OTLP Exporter へ差替
- `ApiServer/Startup.cs:125-133`, `Functions/Startup/Startup.cs:30-38` を改修
- `FunctionLoggerHelper` を OTel Context 連携へ
- ダッシュボード: 既存 App Insights クエリ相当を Grafana で再現
- **テスト**: 同一トレース ID で App Insights と Tempo の両方に届くことを検証

### F1-2: ACR → Harbor
- Harbor を docker-compose / K8s に配備
- `build-and-push-api-server.bash` の push 先を可変化
- CI ワークフロー（`main_gutp-build-api-server.yaml`）に Harbor push ステップを追加
- **テスト**: 同一イメージダイジェストが両レジストリに存在することを検証

### F1-3: Blob Storage → MinIO
- `ColdDataConsumer` の出力先抽象化（`IBlobStorage` 導入）
- `Azure.Storage.Blobs` → `AWSSDK.S3` または `Minio.AspNetCore`
- **テスト**: 同一テレメトリバッチを両方に書き、Parquet バイナリの一致を検証

---

## 5. Phase 2: 認証・フロント配信

### F2-1: Keycloak 構築 & OIDC スキーマ設計
- Realm / Client / Role / Scope の Bicep 相当を Keycloak Realm Import JSON で定義
- 既存の Azure AD App Registration の権限モデルを移植
- Workload Identity 相当: Service Account + client credentials

### F2-2: バックエンド認証移行
- `Microsoft.Identity.Web` → 標準 `JwtBearer` + OIDC discovery
- `ApiServer/Startup.cs:33-199` の `ChainedTokenCredential` を Keycloak service account へ
- `Microsoft.Graph` 経由のユーザ管理 → Keycloak Admin API
- 環境変数 `AUTH_PROVIDER=azure-ad|keycloak` で切替

### F2-3: フロントエンド認証移行
- `web-client` と `admin-console` の両方
- `@azure/msal-*` → `oidc-client-ts` + `react-oidc-context`
- `admin-console/src/lib/use-authenticated-api.ts` を OIDC 版に書き換え（インターフェースは維持）
- `middleware.ts` の MSAL クッキー判定を OIDC セッション判定へ

### F2-4: Next.js on K8s 配信
- `web-client` と `admin-console` のコンテナ化
- Helm Chart 作成、Traefik Ingress 設定
- CI に SWA デプロイと並行で Helm デプロイステップ追加
- **テスト**: 同一 URL に対する両配信の応答を比較

---

## 6. Phase 3: メッセージング・実行基盤

**この Phase で Change Feed 依存を完全に廃止する。**

### F3-1: NATS JetStream 設計
- Subject 階層設計:
  - テレメトリ: `telemetry.<connector>.<building>.<floor>.<device>.<point>`
  - 制御: `control.points.<pointId>.request` / `control.points.<pointId>.result`
- Stream / Consumer 設計（永続化、リプレイ、重複排除）
- Schema Registry 方針（JSON Schema 既存資産を流用）

### F3-2: Connector ランタイム抽象化
- `EventHubTrigger` を `IMessageSubscription` インターフェースへ抽象化
- `ArrayConnectorBase` / `ObjectConnectorBase` のトリガ依存を切離
- Azure Functions ホスト = `EventHubTrigger` 実装、Worker Service ホスト = NATS Consumer 実装

### F3-3: 6 Connector の Worker Service 化
- 対象: BacnetDevice / Electric / Hvac / Environmental / BRidge / Behavior
- .NET Worker Service として再パッケージ（ドメインロジックは無変更）
- K8s Deployment + KEDA NATS スケーラ
- **テスト**: パリティハーネスで Event Hub と NATS 両方に同 message → 両 sink 一致

### F3-4: PointControlConnector の Change Feed 廃止
- `CosmosDBTrigger` を NATS request-reply / Durable Consumer に置換
- フロー:
  ```
  旧: API → CosmosDB Write → CosmosDBTrigger → Handler → CosmosDB Update
  新: API → NATS publish(control.request) → Consumer(Handler) → NATS publish(control.result)
  ```
- `leases` コンテナ廃止
- `PointControlGrpcService.WaitForResult` の subscribe 先を NATS へ
- **テスト**: gRPC ストリーミングの応答が両モードで同一であることを検証

### F3-5: Timer 系の K8s CronJob 化
- DaikinOpenApiConnector / DaikinEnergyManagementConnector
- `0 */5 * * * *` cron → K8s CronJob

---

## 7. Phase 4: 中核データ層

### 7.0 データ階層設計（要件: Warm/Cold を等価的に API から出力）

| 階層 | エンジン | ストレージ | 保持期間 | 用途 |
|------|---------|-----------|---------|------|
| **Warm** | TimescaleDB Hypertable（PostgreSQL 拡張）| ローカル SSD | 直近 3 ヶ月 | リアルタイム参照・集計 |
| **Cold** | Parquet ファイル | MinIO 上のオブジェクト | 3 ヶ月超〜永久 | 履歴クエリ・Microsoft Fabric 資産互換 |
| **統一参照** | `ITelemetryDatabase` の抽象化 | — | — | API は階層を意識せず取得 |

設計のキモ:
- TimescaleDB の **Compression Policy + Retention Policy** で 3 ヶ月で Hypertable から除去
- 除去前に **K8s CronJob で Parquet として MinIO へエクスポート**（pg_parquet 拡張 or .NET Worker + Parquet.Net）
- `ITelemetryDatabase` は階層を抽象化:
  - Warm 期間内クエリ → TimescaleDB SQL
  - Cold 期間クエリ → DuckDB 埋め込みで MinIO 上 Parquet を読み（または Parquet.Net で直接読み）
  - 跨ぎクエリ → 両方を取得して `point_id, time` で結合
- 既存 Microsoft Fabric 資産（Parquet ベース）との互換性を維持

採用根拠（ClickHouse との比較結果）:
- 本ワークロードは約 5 msg/秒（年間 80GB 生 / 8GB 圧縮）で TimescaleDB の sweet spot
- Hot クエリ（`ORDER BY ts DESC LIMIT 1`）は B-tree index で素直に高速
- 制御コマンド・ユーザ管理と DB エンジン統合可能（運用コンポーネント削減）
- 既存 .NET / Npgsql / EF Core 資産が流用可能
- UPDATE/DELETE が普通に効くため IoT データ品質管理（外れ値修正等）に対応容易
- ClickHouse は将来流量が 10x 以上に伸びたら追加投入を再検討（Cold Parquet を `s3()` で直接読めるため追加コスト小）

### F4-1: TimescaleDB スキーマ設計と Cold 階層化方針
- Hypertable: `telemetry` パーティション `(point_id, time)`、`chunk_time_interval = 1 day`
- 圧縮ポリシー: 7 日以前のチャンクを圧縮（10x 圧縮見込み）
- Cold 出力ジョブ: 3 ヶ月超のチャンクを月単位で Parquet 化 → MinIO へ
- 保持ポリシー: Parquet 出力確認後に Hypertable から `drop_chunks`
- 既存 CosmosDB ドキュメントスキーマからのマッピング表
- 成果物: `docs/oss-timescaledb-schema.md`, `DotNet/BuildingOS.Shared/Migrations/Timescale/V001__telemetry_hypertable.sql`

### F4-2: `ITelemetryDatabase` の TimescaleDB + MinIO Parquet 実装
- `TelemetryDatabase.cs` の Cosmos SQL を Npgsql + TimescaleDB SQL へ書換
- 階層判定ロジック:
  - リクエストの time range が直近 3 ヶ月以内 → TimescaleDB のみ
  - 3 ヶ月超のみ → MinIO 上 Parquet のみ（DuckDB 埋め込み or Parquet.Net）
  - 跨ぎ → 両方を取得し point_id+time でマージ
- API レスポンス形式は既存と完全互換
- `ColdDataConsumer` の役割は「TimescaleDB → MinIO Parquet エクスポート CronJob」に変更（Parquet 形式は Microsoft Fabric 互換）

### F4-3: テレメトリ二重書込（CosmosDB + TimescaleDB）と差異監視
- `dual` モードで CosmosDB と TimescaleDB に並行書込
- 1 時間ごとに件数/値差異を Prometheus にエクスポート
- 1〜2 週間の並行運用で差異 0 を確認

### F4-4: 過去データ移行（CosmosDB → TimescaleDB + MinIO Parquet）
- ETL ステップ:
  - 直近 3 ヶ月: CosmosDB → COPY → TimescaleDB Hypertable
  - 3 ヶ月超: CosmosDB → Parquet → MinIO へ直接配置（Cold 領域）
- バッチサイズ可変、進捗を Prometheus にエクスポート
- 再開可能性 (checkpoint をローカルファイル/PostgreSQL に保存)
- 整合性検証: 件数・SUM・MIN・MAX を CosmosDB と新ストレージ双方で計算し比較

### F4-5: Digital Twins → OxiGraph（組み込み運用）
- **第一候補**: OxiGraph（Rust 製、Apache 2.0 / MIT、SPARQL 1.1）
- 採用理由:
  - DTDL は JSON-LD（W3C RDF 標準）ベースであり、OxiGraph (SPARQL) と概念的に直結
  - 単一バイナリ・ファイル DB として組み込み運用可能（HA 要件が緩い読み中心 / 5 分キャッシュ済みワークロードに最適）
  - 寛容ライセンス（GPL/AGPL 回避）
- **採用上の留意（Phase 0 で検証必須）**: OxiGraph 公式 README は「heavy development」「SPARQL query evaluation has
  not been optimized yet」と明記している。したがって **読み中心・中小規模の Digital Twin DB** としての採用に限定し、
  Phase 0 で **(1) 想定 twin 規模での SPARQL クエリ性能、(2) バックアップ/リストア手順、(3) 使用する SPARQL 1.1 機能の
  互換性、(4) .NET からの接続安定性（公式 .NET SDK は無く HttpClient/dotNetRDF 経由）** を検証する。大規模・書き込み
  集約・HA 要件が出た場合は代替（PostgreSQL+AGE 等）へ切替可能なよう `IDigitalTwinDatabase` 抽象を維持する。
- 代替: PostgreSQL+AGE（Cypher 移植性重視の場合）
- アクセス方法: OxiGraph HTTP Server（SPARQL Protocol 1.1）+ .NET から `HttpClient` または `dotNetRDF` ライブラリ経由
- ADT クエリ → SPARQL マッピング表（成果物: `docs/oss-sparql-mapping.md`）:
  - `MATCH (Building)-[:hasPart]->(Floor)-[:hasPart]->(Space)<-[:locatedIn]-(Device)-[:hasPoint]->(Point)`
    → `?b :hasPart/:hasPart ?s . ?d :locatedIn ?s . ?d :hasPoint ?p`
  - `IS_OF_MODEL('dtmi:...')` → `?node a :ModelClass` (RDF type assertion)
- 実装範囲:
  - グラフスキーマ（オントロジ）: RDF クラス Building/Floor/Space/Device/Point、述語 hasPart/locatedIn/hasPoint
  - DTDL → RDF 変換ローダー（JSON-LD として直接ロード可能）
  - 3 アダプタの SPARQL 再実装:
    - `DigitalTwinDatabase`
    - `DigitalTwinHierarchyResolver`
    - `ControlSchemaResolver`
- **テスト**: Phase 0 の ADT ゴールデンが OxiGraph 実装でも 100% 一致

### F4-6: 制御コマンドストア（PostgreSQL JSONB）
- Phase 3 で NATS 化済みなので、永続化のみが対象（監査ログ用途）
- PostgreSQL テーブル: `point_control_audit (id uuid, point_id text, request jsonb, result jsonb, created_at timestamptz)`

---

## 8. Phase 5: IoT 接続・分析基盤

### F5-1: Eclipse Hono + EMQX 設計
- テナント / デバイスレジストリの設計（IoT Hub Device Twin 相当）
- プロビジョニング機構（IoT Hub DPS 相当）
- 接続文字列 → MQTT credentials への変換マッピング

### F5-2: エッジデバイスの接続先切替
- `Tools/development-edge-device/` の接続先を可変化
- 実機エッジは段階的にデュアル接続 → 切替

### F5-3: Hono → NATS ブリッジ
- Hono の Northbound から NATS Stream への bridge

### F5-4: 分析基盤（DuckDB / Trino + Superset）
- `ColdDataConsumer` 出力（MinIO 上 Parquet）を読む分析エンジンを整備
- 小〜中規模分析: **DuckDB**（埋め込み or DuckDB UI）で MinIO 上 Parquet を直接クエリ
- 大規模・複数ユーザ: **Trino** クラスタ（Iceberg カタログ任意）+ Superset ダッシュボード
- 既存 Microsoft Fabric Lakehouse のクエリを Trino SQL or DuckDB SQL に移植
- 将来 ClickHouse を追加投入する場合も同じ Parquet を `s3()` で読めるため移行容易

---

## 9. Phase 6: IaC / CI/CD 再構成

### F6-1: OpenTofu モジュール作成
- Bicep 14 モジュールを OpenTofu モジュールへ再記述
- K8s リソースは Helm Chart に分離

### F6-2: GitHub Actions ワークフロー再構成
- `azure/login` 廃止 → `kubectl` / Helm / Argo CD ステップへ
- Static Web Apps デプロイ削除
- Functions Action 削除

### F6-3: Argo CD GitOps 導入（試験環境 1 つに限定）
- **試験環境 1 つのみ**（例: utokyo-eng2）を Argo CD Application として定義
- multi-env (ApplicationSet) は本フェーズではスコープ外（移行完了後に検討）
- ロールバック手順 (git revert → 自動同期)

---

## 10. テスト戦略（全フェーズ共通）

### 10.1 テストピラミッド

```
            E2E (本番相当環境)
         ────────────────────
        統合テスト (Testcontainers)
       ──────────────────────────
      コンポーネントテスト (アダプタ単位)
     ────────────────────────────────
    ユニットテスト (ドメインロジック・既存維持)
```

### 10.2 機能パリティ・テストの実装方針

1. **コントラクト凍結**: Phase 0 で現行システムの I/O を Golden File として固定
2. **シャドウライト**: `dual` モードで両系統に書込、後続検証で差異を計測
3. **シャドウリード**: 読みも両系統で実行し diff（trace 可能 ID で紐付け）
4. **段階的トラフィック切替**: 読みのプライマリを feature flag で 0% → 10% → 50% → 100%
5. **ロールバックドリル**: 各 Phase 完了時に「OSS → Azure へ戻す」演習を実施

### 10.3 受け入れ基準（各フェーズ共通）

- [ ] パリティハーネスで指定期間（最低 1 週間）差異 0
- [ ] ロールバック手順を実行し旧環境で復旧できることを確認
- [ ] レイテンシ / スループット指標が現行 ±10% 以内
- [ ] エラー率が現行と同等以下
- [ ] ドキュメント (runbook) 整備済み

---

## 11. リスク管理

| リスク | 影響 | 対策 |
|---|---|---|
| 並行運用中の二重書込で不整合 | データ汚染 | 書込先を冪等化（`Nats-Msg-Id` / Upsert）、整合性検証ジョブ |
| OSS スタックの運用コスト過大 | SRE 負荷 | Operator (NATS / CloudNativePG / MinIO) を全面採用 |
| データ移行の長時間化 | カットオーバ遅延 | 過去データは段階移行、新規データから優先切替 |
| ライセンス制約（AGPL/SSPL/GPLv3） | 配布リスク | 優先順位: Apache 2.0 / MIT > LGPL > AGPL/GPL。グラフ DB は OxiGraph（Apache 2.0 / MIT）を第一候補 |
| Phase 4 の難度 | スケジュール超過 | Phase 0〜3 で基盤を固めてから着手、Phase 4 はサブフェーズ化 |
| 機能パリティの抜け | 本番障害 | E2E ゴールデンの網羅率を測定指標化 |

---

## 12. 意思決定の記録

合意済み:
1. **スコープ**: スコープ A（.NET 維持・Azure のみ置換）で進める
2. **グラフ DB**: **OxiGraph（組み込み運用）** を第一候補。代替は PostgreSQL+AGE
3. **時系列 DB**: **TimescaleDB（PostgreSQL 拡張）** を第一候補。Warm（Hypertable、3 ヶ月）→ Cold（MinIO 上 Parquet、CronJob でエクスポート）。`ITelemetryDatabase` 抽象化層で Warm/Cold を API 透過に。ClickHouse は将来流量 10x 増時に追加投入を再検討
4. **カットオーバ**: **試験環境 1 つ**（例: utokyo-eng2）に限定。multi-env デプロイは本移行のスコープ外
5. **GitHub 継続利用**: スコープ A では継続

未決（Phase 0 着手前に決定）:
- 運用基盤: オンプレ K8s / 非 MS クラウド managed K8s
- ライセンス許容範囲（AGPL/SSPL/GPLv3 が必要になった場合の判断基準）

---

## 13. バックログ（タスク一覧）

Phase 0 から順に着手。各 Phase 内のタスクは依存関係に従って実施。詳細は TaskList で管理。
