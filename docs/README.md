# docs

Building OS OSS の詳細ドキュメントです。README は起動と概要、この
ディレクトリは技術選定、システム構成、アーキテクチャ、運用手順、
移行設計を扱います。

> ℹ️ 本プロダクトは **東京大学 グリーン ICT プロジェクト**の研究成果物の派生物です。**現状有姿（AS IS）で提供され、利用にかかわる一切について開発者・関係者・派生元は責任を負いません（無保証）。** 詳細は [ルート README の「免責事項」](../README.md#免責事項-disclaimer) を参照してください。

## まずここから（新規ユーザー向け）

- 🚀 **[Getting Started（オンボーディング）](getting-started.md)** — 起動 → API/Web → テレメトリ投入 → 読取/制御 までの一筆書き
- 🏗️ **[リソース管理ガイド](resource-management.md)** — ツインへの設備インポート・更新・削除・複数ビル対応・ロール別表示制御
- 🔑 **[Keycloak ユーザー・認証管理](keycloak-user-management.md)** — ユーザー作成・ロール付与・トークン取得・管理 UI の使い方
- 🔌 **[コネクタ・ワーカー拡張ガイド](connector-development-guide.md)** — 新しいプロトコルコネクタを追加する step-by-step チュートリアル
- 📡 **[クライアントアプリケーション開発ガイド](api-client-guide.md)** — REST API 認証・テレメトリ読み取り・制御・SDK 生成
- 🔗 **[Gateway Integration（ゲートウェイ接続モデル）](gateway-integration.md)** — ingress/egress、`(gateway_id, point_id)` 契約、point list 同期、mTLS
- 📊 **[Evaluation Summary（評価結果と妥当性）](evaluation-summary.md)** — E2E 実測でアーキテクチャ/性能の妥当性を解説
- 🏛️ **[System Architecture（全体像）](system-architecture.md)** — 構成・データ/制御フロー・セキュリティ・デプロイ
- 🚢 **[Production Deployment（本番デプロイ構成）](oss-production-deployment.md)** — Kubernetes 配置・ネットワーク境界・mTLS・スケール
- ⏱️ **[SLA / 鮮度モデル](oss-sla-freshness.md)** — latest 即時 / range は flush 遅延 / tail-merge の効果

## 推奨読書順

全体像を短時間で把握する場合は、次の順で読むのが最短です。

1. [Getting Started（オンボーディング）](getting-started.md)
2. [システムアーキテクチャ](system-architecture.md)
3. [リソース管理ガイド](resource-management.md) ← **設備データのインポート・更新・削除・アクセス制御**
4. [Keycloak ユーザー・認証管理](keycloak-user-management.md) ← **ユーザー管理・認証の実践**
5. [Gateway Integration（ゲートウェイ接続）](gateway-integration.md)
6. [テレメトリ仕様](telemetry-specification.md)
7. [Hot/Warm/Cold 階層](oss-tier-architecture.md) / [Warm/Cold Parquet レイク](oss-warm-parquet-lake.md)（既定テレメトリストア）
8. [クライアントアプリケーション開発ガイド](api-client-guide.md) ← **外部クライアント向け API ガイド**
9. [コネクタ・ワーカー拡張ガイド](connector-development-guide.md) ← **新規コネクタ開発**
10. [NATS JetStream 設計](oss-nats-design.md)
11. [OxiGraph / SPARQL マッピング](oss-sparql-mapping.md) / [標準語彙マッピング](standard-mapping.md)
12. [Keycloak 権限マッピング](keycloak-permission-mapping.md)
13. [デバイス接続設計（任意 MQTT + AMQP northbound）](oss-hono-design.md)
14. [Evaluation Summary（評価結果と妥当性）](evaluation-summary.md)

> 補足: TimescaleDB は #216 で既定から外れ **opt-in**（`WARM_STORE=timescale`）です。既定のテレメトリ
> ストアは Parquet レイク（[oss-warm-parquet-lake.md](oss-warm-parquet-lake.md)）です。

## 網羅性評価

| 領域 | 状態 | 主なドキュメント | 補足 |
|---|---|---|---|
| 全体アーキテクチャ | Covered | [system-architecture.md](system-architecture.md) | OSS runtime topology、data flow、security、deployment |
| 技術スタック | Covered | [oss-tech-stack-analysis.md](oss-tech-stack-analysis.md), [oss-feature-comparison.md](oss-feature-comparison.md) | Azure 置換理由と現在の parity |
| メッセージング | Covered | [oss-nats-design.md](oss-nats-design.md) | subject、stream、deduplication、replay、control flow |
| テレメトリ契約 | Covered | [telemetry-specification.md](telemetry-specification.md), [oss-timescaledb-schema.md](oss-timescaledb-schema.md) | 正規化 JSON contract と TimescaleDB mapping |
| デジタルツイン | Covered | [oss-sparql-mapping.md](oss-sparql-mapping.md), [resource-management.md](resource-management.md) | ADT 由来の graph traversal を RDF/SPARQL に対応。インポート・削除・アクセス制御の実践ガイド付き |
| 認証・認可 | Covered | [keycloak-permission-mapping.md](keycloak-permission-mapping.md), [keycloak-admin-provisioning.md](keycloak-admin-provisioning.md) | realm/client/role mapping と admin provisioning |
| IoT ingress / device migration | Covered | [oss-hono-design.md](oss-hono-design.md), [hono-device-test-plan.md](hono-device-test-plan.md), [edge-device-mqtt-cutover.md](edge-device-mqtt-cutover.md) | Hono/EMQX model と cutover plan |
| Frontend deployment | Covered | [nextjs-k8s-rollout.md](nextjs-k8s-rollout.md) | K8s/Ingress/Keycloak rollout と rollback |
| E2E performance / quality | Covered | [e2e-performance-quality-test-plan.md](e2e-performance-quality-test-plan.md), [e2e-performance-report-template.md](e2e-performance-report-template.md) | 入力、蓄積、API 取得、UI 表示までの性能・品質評価とレポート雛形 |
| GitOps / registry operations | Covered | [argocd-gitops-guide.md](argocd-gitops-guide.md), [harbor-cutover.md](harbor-cutover.md) | Argo CD と Harbor runbook |
| API reference | Generated | [schema/swagger.yaml](schema/swagger.yaml) | endpoint 変更時に API Server から再生成 |
| Observability | Partial | [system-architecture.md](system-architecture.md), [oss-feature-comparison.md](oss-feature-comparison.md) | 構成要素は記載済み。alert/tracing runbook は今後拡張余地あり |

## ドキュメント索引

### Architecture And Design

- [システムアーキテクチャ](system-architecture.md) — OSS 版の主要構成、データフロー、制御フロー、セキュリティ、デプロイ構成
- [テレメトリ仕様](telemetry-specification.md) — Connector が生成する `ValidMessageJson` と TimescaleDB へのマッピング
- [NATS JetStream 設計](oss-nats-design.md) — subject、stream、consumer、dedup、request-reply、replay 方針
- [TimescaleDB スキーマ設計](oss-timescaledb-schema.md) — hypertable、圧縮、保持、cold export 方針
- [OxiGraph / SPARQL マッピング](oss-sparql-mapping.md) — Azure Digital Twins 由来の階層クエリを RDF/SPARQL へ移植する設計
- [Hono + EMQX 設計](oss-hono-design.md) — tenant/device/credential、Hono to NATS bridge、cutover/rollback 方針

### Resource Management

- [リソース管理ガイド](resource-management.md) — **設備データ（ビル/フロア/スペース/機器/ポイント）のインポート・更新・削除・複数ビル対応・ロール別表示制御** ← 新規
- [OxiGraph / SPARQL マッピング](oss-sparql-mapping.md) — Azure Digital Twins 由来の階層クエリを RDF/SPARQL へ移植する設計
- [ゲートウェイ Point List 同期](oss-gateway-pointlist-sync.md) — ETag/push 通知によるゲートウェイへのポイントリスト配信
- [標準語彙マッピング](standard-mapping.md) — SBCO / bos: 語彙と Brick / REC / IFC / DTDL の対応

### Authentication And Frontend

- [Keycloak ユーザー・認証管理](keycloak-user-management.md) — **ユーザー作成・ロール付与・トークン取得・管理 UI の実践ガイド** ← 新規
- [Keycloak 権限マッピング](keycloak-permission-mapping.md) — Azure AD 由来の role/scope/permission を Keycloak に移植する対応表
- [Keycloak 管理プロビジョニング](keycloak-admin-provisioning.md) — realm/client/role/group/user の管理手順
- [クライアントアプリケーション開発ガイド](api-client-guide.md) — **REST API 認証・テレメトリ・制御・SDK 生成の入門** ← 新規
- [Next.js Kubernetes ロールアウト](nextjs-k8s-rollout.md) — web-client の K8s 配信、OIDC 環境変数、rollback 手順

### Connector And Worker Development

- [コネクタ・ワーカー拡張ガイド](connector-development-guide.md) — **新しいプロトコルコネクタを追加する step-by-step チュートリアル** ← 新規
- [テレメトリ仕様](telemetry-specification.md) — Connector が生成する `ValidMessageJson` と契約フィールド
- [NATS JetStream 設計](oss-nats-design.md) — subject、stream、consumer、dedup、request-reply、replay 方針
- [Gateway Integration](gateway-integration.md) — gRPC GatewayIngress（推奨取り込み経路）・point list 同期・mTLS

### Migration And Operations

- [本番デプロイ構成](oss-production-deployment.md) — Kubernetes 配置図、コンポーネント責務、ネットワーク境界/ポート、mTLS、スケール/可用性
- [ゲートウェイ セキュリティ運用](oss-gateway-security-ops.md) — 証明書発行/ローテーション/失効、`gateway_id`↔証明書束縛、信頼境界、enforce 段階導入
- [SLA / 鮮度モデル](oss-sla-freshness.md) — Hot/Warm/Cold の層別鮮度、tail-merge の有無で変わる挙動、鮮度 KPI
- [OSS 技術スタック分析](oss-tech-stack-analysis.md) — Azure マネージドサービスを OSS へ置換する技術選定と難易度評価
- [OSS 移行計画](oss-migration-plan.md) — 段階的移行、検証、ロールバック、リスク管理
- [Azure vs OSS 機能比較](oss-feature-comparison.md) — レイヤー別の OSS パリティ、残作業、HITL 項目
- [Hono デバイステスト計画](hono-device-test-plan.md) — device onboarding と MQTT 接続検証
- [E2E 性能評価・品質チェックテスト計画](e2e-performance-quality-test-plan.md) — 入力から蓄積、API 取得、UI 表示までの性能・品質評価計画
- [E2E 性能評価レポートテンプレート](e2e-performance-report-template.md) — 自動生成レポートの構成と記入項目
- [Edge device MQTT cutover](edge-device-mqtt-cutover.md) — IoT Hub から Hono/EMQX への移行手順
- [Harbor cutover](harbor-cutover.md) — ACR から Harbor/GHCR への切替手順
- [Argo CD GitOps guide](argocd-gitops-guide.md) — GitOps 運用、同期、rollback、image update

### Generated Or Visual Artifacts

- [OpenAPI schema](schema/swagger.yaml) — API Server から生成する OpenAPI 3.0 仕様
- `cicd.png` — CI/CD パイプライン図
- `git-hub-flow.png` — GitHub flow 図

## Swagger 定義

API エンドポイントの詳細は `schema/swagger.yaml` を参照してください。

生成方法:

```bash
# API Server が起動している状態で実行
cd Tools
./generate_swagger.bash
```
