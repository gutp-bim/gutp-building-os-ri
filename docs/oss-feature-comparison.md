# Azure vs OSS Feature Comparison Table

Building OS の Azure マネージドサービスと OSS 代替の機能比較。
Phase 0–6 の実装を通じて達成したパリティと残作業を示す。

## Infrastructure Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Container Registry | Azure Container Registry | Harbor v2.11 | ✅ | PR #52 (#8) |
| Container Images CI | ACR-based `az acr build` | GHCR + Harbor dual-push GH Actions | ✅ | PR #52 (#8) |
| Object Storage | Azure Blob Storage | MinIO (S3-compatible) | ✅ | PR — (#9) |
| IaC Tool | Azure Bicep | OpenTofu (Terraform OSS fork) | ✅ | PR #56 (#29) |
| IaC State Backend | Azure Blob Storage (tfstate) | MinIO S3 backend (SSE-S3) | ✅ | PR #56 (#29) |
| GitOps CD | GitHub Actions helm deploy | Argo CD + argocd-image-update | ✅ | PR #57 (#31) |

## Data Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Hot/Warm Store | CosmosDB (NoSQL, 50ms) | TimescaleDB PostgreSQL 16 | ✅ | PR #— (#19, #20) |
| Cold Store | Azure Blob Storage (Parquet) | MinIO + Parquet (Snappy) | ✅ | PR #— (#9) |
| ETL Migration | Manual/Fabric notebooks | Python async ETL + checkpoint | ✅ | PR #51 (#22) |
| Analytics Engine | Microsoft Fabric Lakehouse (Spark) | DuckDB (embedded) + Trino (distributed) | ✅ | PR #55 (#28) |
| Analytics BI | Power BI | Apache Superset 4.0 | ✅ | PR #55 (#28) |
| Dual Write | — | Cosmos + TimescaleDB dual-write + drift monitor | ✅ | PR #— (#21) |
| Point Control Audit | CosmosDB `PointControlContainer` | PostgreSQL JSONB audit table | Planned | #24 |

## Messaging / IoT Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| IoT Device Connectivity | Azure IoT Hub (AMQP/MQTT) | Eclipse Hono (MQTT adapter) | ✅ | PR #54 (#26, #27) |
| Message Bus | Azure Event Hub | NATS JetStream | ✅ | PR #— (#14, #16) |
| Connector Workers | Azure Functions (Isolated) | K8s Deployment (dotnet-isolated) | ✅ | PR #56 (#16) |
| Timer Connectors | Azure Functions Timer Trigger | K8s CronJob | ✅ | #18 |
| Point Control | Azure Functions + CosmosDB Change Feed | NATS request-reply | ✅ | #17 |
| Device Simulator | custom IoT Hub SDK | `mqtt_edge_device.py` + `dual_edge_device.py` | ✅ | PR #54 (#26) |
| Hono→NATS Ingress | — (direct EventHub) | ConnectorWorker `AmqpIngressWorker`（Hono AMQP 1.0 Northbound、`HONO_AMQP_HOST` で有効化。旧 `hono-nats-bridge` Python サービスは廃止） | ✅ | PR #54 (#27) |

## Digital Twin / Knowledge Graph Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Graph Store | Azure Digital Twins (DTDL) | OxiGraph (SPARQL 1.1, RDF) | ✅ | PR #53 (#23) |
| Query Language | ADT Query Language (proprietary) | SPARQL 1.1 (standard) | ✅ | PR #53 (#23) |
| Graph Schema | DTDL (JSON-LD) | RDF ontology (SBCO `sbco:` namespace; `bos:` extension) | ✅ | PR #53 (#23) |
| Hierarchy Resolver | ADT hierarchy traversal | SPARQL explicit multi-hop BGP over `sbco:hasPart` | ✅ | PR #53 (#23) |
| Control Schema Resolver | ADT-based | OxiGraph SPARQL + BACnet fallback | ✅ | PR #53 (#23) |
| DTDL Loader | ADT Import API | `DtdlRdfLoader` SPARQL INSERT DATA | ✅ | PR #53 (#23) |

## Authentication / Authorization Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Identity Provider | Azure AD (Microsoft Entra ID) | Keycloak | HITL sign-off pending | #10 |
| Backend Auth | Microsoft.Identity.Web (JWT validation) | ASP.NET JwtBearer + OIDC | ✅ | #11 |
| Frontend Auth | MSAL.js (Azure AD) | `oidc-client-ts` + Keycloak | ✅ | #12 |
| Service-to-Service | Azure Managed Identity | Keycloak service account + OIDC | HITL sign-off pending | #10, #11 |
| Argo CD SSO | — | Keycloak OIDC (configured) | ✅ placeholder | PR #57 |

## Observability Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Metrics | Azure Monitor / App Insights | Prometheus (kube-prometheus-stack) | ✅ | PR #56 (#29) |
| Dashboards | Azure Monitor Workbooks | Grafana | ✅ | PR #56 (#29) |
| Traces | App Insights (distributed) | Tempo (via OpenTelemetry OTLP) | Planned | #7 |
| Logs | Log Analytics Workspace | Loki | Planned | #7 |
| Alerts | Action Groups (email) | Alertmanager (email_configs) | ✅ | PR #56 (#29) |
| Alert Rules | Azure Monitor Alert Rules | PrometheusRule CRDs | ✅ | PR #56 (#29) |
| OpenTelemetry SDK | App Insights SDK | OpenTelemetry .NET SDK (OTLP) | Planned | #7 |

## CI/CD Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Image Build/Push | GitHub Actions → ACR | GitHub Actions → GHCR + Harbor | ✅ | PR #52 (#8) |
| IaC Deploy | `az deployment group create` (Bicep) | `tofu apply` (OpenTofu) | ✅ | PR #56 (#29) |
| App Deploy | Azure CLI / `helm upgrade` direct | Argo CD GitOps auto-sync | ✅ | PR #57 (#31) |
| Image Tag Bump | — | `argocd-image-update.yml` | ✅ | PR #57 (#31) |

## Kubernetes / Platform Layer

| Feature | Azure (Original) | OSS Replacement | Parity | PR / Issue |
|---|---|---|---|---|
| Compute | Azure App Service (B1), Azure Functions (Y1) | K8s Deployment, K8s StatefulSet | ✅ | PR #56 (#29) |
| Web Client Hosting | Azure Static Web Apps | K8s Deployment + Nginx Ingress | ✅ | PR #56 (#29) |
| K8s Cluster | AKS (optional) | k3s / kubeadm (self-hosted) | ✅ infra | #29 |
| Helm Chart Management | — (direct azure deploy) | Per-component Helm charts | ✅ | PR #56 (#29) |

## Summary

| Category | Total Features | Azure | OSS Parity ✅ | Planned | HITL Needed |
|---|---|---|---|---|---|
| Infrastructure | 7 | 7 | 7 | 0 | 0 |
| Data Layer | 8 | 8 | 7 | 1 | 0 |
| Messaging / IoT | 8 | 8 | 8 | 0 | 0 |
| Digital Twin | 6 | 6 | 6 | 0 | 0 |
| Auth / Authz | 5 | 5 | 3✓ + 2 pending sign-off | 0 | 1 |
| Observability | 7 | 7 | 5 | 2 | 0 |
| CI/CD | 4 | 4 | 4 | 0 | 0 |
| K8s / Platform | 4 | 4 | 4 | 0 | 0 |
| **Total** | **49** | **49** | **44 + 2 pending sign-off** | **3** | **1** |

### Legend

- ✅ = Feature implemented and tested
- Planned = AFK implementation ready to start (no design decision needed)
- HITL = Human-in-the-loop decision required before implementation

### Remaining HITL Items

1. **#10** Keycloak Realm design — PR sign-off pending
2. **#14** NATS JetStream Subject/Stream/Schema design — PR sign-off pending
3. **#25** Eclipse Hono + EMQX design and provisioning mechanism — PR sign-off pending

### Remaining AFK Items

- #7 OpenTelemetry (Tempo + Loki)
- #24 Point Control audit store (PostgreSQL JSONB)
