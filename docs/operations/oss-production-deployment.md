# 本番デプロイ構成 — Building OS OSS

外部利用者・運用者向けに、Building OS を **Kubernetes 本番構成**でどう配置するかを 1 枚で示します。
ローカル起動は [getting-started.md](../guides/getting-started.md)、ゲートウェイ接続の契約は
[gateway-integration.md](../guides/gateway-integration.md)、設計詳細は [system-architecture.md](../architecture/system-architecture.md) を参照。

> ⚠️ 現状有姿（AS IS）・無保証。mTLS / ingress 疎通 / 証明書発行などライブ要素の on-cluster 検証は
> HITL（人手）です（[oss-gateway-bridge-infra.md](oss-gateway-bridge-infra.md)）。本書は配置と責務の地図であり、
> リソースサイジングの確定値は [#297](https://github.com/takashikasuya/gutp-building-os-oss/issues/297)/
> [#298](https://github.com/takashikasuya/gutp-building-os-oss/issues/298) を前提とします。

---

## 1. 全体構成図

```
        ビル現場 (edge)                       ║                Kubernetes クラスタ
                                              ║
 ┌─────────────────────────┐                 ║   ┌──────────────── Ingress (Traefik) ─────────────────┐
 │ Gateway / BOWS          │   gRPC + mTLS   ║   │ TLSOption: RequireAndVerifyClientCert (client CA)  │
 │  (nexus-gateway 等)     │ ───────────────────► │ passTLSClientCert → X-Gateway-Id (cert SAN/CN)     │
 │  BACnet→point_id 解決   │                 ║   └───┬───────────────────────────────┬────────────────┘
 └─────────────────────────┘                 ║       │ h2c (telemetry)               │ h2c (control)
                                             ║       ▼                               ▼
                                             ║  ┌─────────────────────┐     ┌─────────────────────┐
                                             ║  │ ConnectorWorker      │     │ GatewayBridge        │
                                             ║  │  GatewayIngress :5051│     │  GatewayEgress :5052 │
                                             ║  │  (point_id 正準/enrich)    │  (per-gateway 制御)  │
                                             ║  └──────────┬──────────┘     └──────────┬──────────┘
                                             ║             │ validated.telemetry        │ control.request.gw.{id}
                                             ║             ▼                            ▼
                                             ║      ┌──────────────────── NATS JetStream ───────────────────┐
                                             ║      │  BUILDING_OS_VALIDATED stream / per-gateway subjects   │
                                             ║      └───┬───────────────────────┬───────────────────────────┘
                                             ║          │ Hot KV (latest)       │ ParquetLakeWriter (flush)
                                             ║          ▼                       ▼
                                             ║   ┌─────────────┐        ┌────────────────────┐
                                             ║   │ NATS KV     │        │ MinIO Parquet Lake │  ← Cold/Warm 統一
                                             ║   │ telemetry-  │        │ (S3 互換, hive 区画)│     + ILM 保持
                                             ║   │ latest      │        └─────────┬──────────┘
                                             ║   └──────┬──────┘                  │
                                             ║          │   ┌───────────────── Query Router ─────────────────┐
        ブラウザ (operator) ────HTTPS────────────────────► │ ApiServer (REST + gRPC)  GET /telemetries/query │
                                             ║          └──►│  Hot / Warm(tail-merge) / Cold / 集計 自動選択   │
                                             ║              └───┬───────────────┬───────────────┬────────────┘
                                             ║                  │ OxiGraph      │ PostgreSQL    │ Keycloak (OIDC)
                                             ║                  ▼ (twin/SPARQL) ▼ (user/audit)  ▼
                                             ║          ┌──────────────┐ ┌─────────────┐ ┌──────────────┐
                                             ║          │ OxiGraph     │ │ PostgreSQL16│ │ Keycloak     │
                                             ║          └──────────────┘ └─────────────┘ └──────────────┘
                                             ║
                  Web Client (Next.js)  ◄───HTTPS───  Ingress (Traefik)
                                             ║
        Observability: 各コンポ → OTLP → otel-collector → Prometheus / Grafana / Loki / Tempo
```

---

## 2. コンポーネントと責務

| レイヤ | コンポーネント | Helm template | 主な責務 / ポート |
|---|---|---|---|
| Edge | Gateway / BOWS（別リポ nexus-gateway） | — | BACnet 等を `point_id` にローカル解決し gRPC でストリーム |
| Ingress | **Traefik**（IngressRoute, h2c, mTLS 終端） | `templates/ingress.yaml` | mTLS 終端・client cert 検証・`X-Gateway-Id` 注入・gRPC L7 LB |
| Ingest | **ConnectorWorker** `GatewayIngress` | `templates/connector-worker.yaml` | テレメトリ取り込み（:5051, twin enrich）→ `validated.telemetry` |
| Control | **GatewayBridge** `GatewayEgress` | `templates/gateway-bridge.yaml` | per-gateway 制御 egress（:5052, bidi）。既定 `enabled: false` |
| Bus | **NATS JetStream** | （依存 chart / 外部） | コアメッセージバス・per-gateway subject・dedup/replay |
| Hot | **NATS KV** `telemetry-latest` | （NATS 内） | 最新値（即時） |
| Warm/Cold | **MinIO** Parquet レイク | （依存 chart / 外部 S3） | 履歴の統一レイク（hive 区画 + ILM 保持） |
| API | **ApiServer**（REST + gRPC, Query Router） | `templates/api-server.yaml` | `/telemetries/query` の tier 自動選択・制御 API・認可 |
| Twin | **OxiGraph**（SPARQL） | （依存 chart / 外部） | 建物→…→point 階層・point list の正本 |
| RDB | **PostgreSQL 16** | （依存 chart / 外部） | user/group/point_control_audit/system_config |
| Auth | **Keycloak**（OIDC/JWT） | （依存 chart / 外部） | 認証（ユーザ）。ゲートウェイは mTLS マシン認証 |
| UI | **Web Client**（Next.js） | `templates/web-client.yaml` | ダッシュボード + `(admin)`/`(platform)` |
| Observability | otel-collector → Prometheus/Grafana/Loki/Tempo | `observability/` | traces+metrics+logs（OTLP） |

> Helm: モノリシックチャート `kubernetes/helm/building-os`（`values.yaml` / `values-dev` / `values-prod` /
> `values-minimal`）に api-server / connector-worker / gateway-bridge / web-client / ingress が含まれます。
> GatewayBridge は単体チャート `kubernetes/helm/gateway-bridge` もあり、いずれも既定 `enabled: false`（インフラ
> レビュー後に opt-in）。GitOps は `argocd/` 配下。

---

## 3. ネットワーク境界とポート

| 区間 | プロトコル | ポート | 認証 |
|---|---|---|---|
| Gateway → Ingress（telemetry） | gRPC over TLS | 443（ingress）→ h2c 5051（内部） | **mTLS**（client CA）→ `X-Gateway-Id` |
| Gateway → Ingress（control） | gRPC over TLS（bidi） | 443（ingress）→ h2c 5052（内部） | **mTLS**（client CA） |
| ブラウザ → Ingress（API/Web） | HTTPS | 443 | Keycloak OIDC/JWT |
| 内部（ApiServer↔NATS/MinIO/OxiGraph/PG/Keycloak） | クラスタ内 | 各サービス DNS | クラスタネットワークポリシー |

**信頼境界**: `GRPC_INGRESS_PORT`(5051) / GatewayBridge(5052) を**外部に直公開しない**。必ず mTLS 終端 ingress 経由とし、
`X-Gateway-Id` などの信頼ヘッダは**非信頼経路で必ず除去**すること。ingress でのみ証明書 subject から注入します
（[gateway-integration.md §4](../guides/gateway-integration.md)、ingress テレメトリ経路の gateway_id 束縛強化は
[#296](https://github.com/takashikasuya/gutp-building-os-oss/issues/296)）。

---

## 4. mTLS / 証明書（cert-manager）

- ingress サーバ証明書: cert-manager `Certificate` → `serverSecretName`。
- クライアント認証: Traefik `TLSOption` `clientAuth.clientAuthType: RequireAndVerifyClientCert` + client CA を
  `clientCaSecretName`。当該 CA 署名のゲートウェイ証明書のみ接続可。
- `passTLSClientCert` で検証済み証明書 subject（SAN/CN）を `X-Gateway-Id` として転送 → アプリが `gateway_id` と突合。
  - **テレメトリ ingress 束縛（#296）**: ConnectorWorker `GatewayIngress` で `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true`
    を設定すると、フレームの `gateway_id` が信頼ヘッダと一致しない送信を拒否（なりすまし注入防止）。本番では ON 推奨。
    ヘッダ名は `GRPC_INGRESS_GATEWAY_ID_HEADER`（既定 `X-Gateway-Id`）。
  - pointlist 同期 API（#224）は同じ信頼ヘッダで `header == path` を要求。両者で同一の信頼境界を共有する。
- ゲートウェイ証明書の**発行・ローテーション・失効**手順は [oss-gateway-security-ops.md](oss-gateway-security-ops.md) を参照。
- 詳細・on-cluster 検証チェックリスト: [oss-gateway-bridge-infra.md](oss-gateway-bridge-infra.md)。

---

## 5. スケール / 可用性

- **ConnectorWorker GatewayIngress / ApiServer はステートレス** → 水平スケール可。
- **GatewayBridge もステートレス**: ゲートウェイのストリームは任意レプリカに載り、制御は per-gateway NATS subject で
  当該ストリーム保持レプリカに必ず届く（LB のスティッキー不要、[oss-gateway-bridge-infra.md](oss-gateway-bridge-infra.md)）。
- **耐障害性**: テレメトリは store-and-forward（NATS JetStream の at-least-once + ParquetLakeWriter durable consumer）。
  ConnectorWorker 停止→再起動でも復旧後 publish で欠落 0（E8 実測 RTO ~4.5s / loss 0、[evaluation-summary.md](../reference/evaluation-summary.md)）。
- **制御**は deadline-bounded な request-reply。オフライン GW は即時 503（#186）で、復旧後の stale 実行なし（E6 stale-replay 0）。
- 自動スケール（KEDA）・GitOps（ArgoCD）の on-cluster 検証は HITL（[#116](https://github.com/takashikasuya/gutp-building-os-oss/issues/116) / [#117](https://github.com/takashikasuya/gutp-building-os-oss/issues/117)）。

---

## 6. リソースサイジング（暫定）

確定値は本番スケール実測（[#297](https://github.com/takashikasuya/gutp-building-os-oss/issues/297)）後に
サイジング表（[#298](https://github.com/takashikasuya/gutp-building-os-oss/issues/298)）へ反映します。現時点の方針:

- **ConnectorWorker / ApiServer**: スループットに応じてレプリカ数で水平スケール（ステートレス）。
- **NATS JetStream**: `BUILDING_OS_VALIDATED` の MaxBytes/MaxAge（24h）でストリーム容量を上限管理（`PARQUET_STREAM_MAX_BYTES`）。
- **MinIO**: Parquet + ILM 保持（`LAKE_RETENTION_DAYS`）で容量を制御。bytes/row は TimescaleDB 比 ~0.02（E7）。
- **flush/compaction**: 鮮度と小ファイル数のトレードオフ（[oss-sla-freshness.md](oss-sla-freshness.md)）。

---

## 7. 関連

- [getting-started.md](../guides/getting-started.md) — ローカル起動
- [gateway-integration.md](../guides/gateway-integration.md) — ゲートウェイ接続・point list 同期・mTLS
- [oss-gateway-bridge-infra.md](oss-gateway-bridge-infra.md) — Traefik/mTLS/cert-manager/ArgoCD 配線（HITL）
- [oss-sla-freshness.md](oss-sla-freshness.md) — 層別の鮮度モデル
- [oss-tier-architecture.md](../architecture/oss-tier-architecture.md) — Hot/Warm/Cold + Query Router
- [system-architecture.md](../architecture/system-architecture.md) — 全体設計
- [argocd-gitops-guide.md](argocd-gitops-guide.md) — GitOps 運用
