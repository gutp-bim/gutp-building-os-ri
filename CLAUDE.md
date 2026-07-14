# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Building OS is an open-source IoT platform for smart building management. Built with .NET 8.0 backend and Next.js 16 frontend. Collects data from building equipment (HVAC, power meters, environmental sensors) via MQTT / NATS, stores telemetry in a Parquet lake on MinIO, and provides REST + gRPC APIs with a Next.js dashboard.

## Architecture

```
IoT Devices (MQTT / Azure IoT Hub)
        │
        ▼
  Mosquitto / Hono        ← MQTT broker (extensible connector layer)
  Azure IoT Hub           ← optional: for bridging existing building systems
        │
        ▼
      NATS JetStream      ← core message bus
        │
   ┌────┴────┐
   │         │
ConnectorWorker × N       ← per-protocol workers (BACnet, HVAC, …)
   │
   ▼
 NATS (building-os.validated.telemetry)
   │
   ▼
ParquetLakeWriterWorker   ← flushes validated telemetry to Parquet lake (MinIO)
   │
   ▼
MinIO (Parquet lake)      ← telemetry store (warm/cold)
   │
   ▼
API Server (ASP.NET Core) ← REST + gRPC
   │
   ▼
Web Client (Next.js)      ← real-time dashboard + (admin) workspace (user/permission management)
```

**Digital twin:** OxiGraph (SPARQL) manages the building → floor → space → device hierarchy.  
**Blob storage:** MinIO (S3-compatible), cold data exported as Parquet.  
**Authentication:** Keycloak (OIDC / JWT).  
**Observability:** OpenTelemetry → Prometheus + Grafana + Loki + Tempo.

## Build and Run Commands

### Backend (.NET) — from `DotNet/` directory
```bash
dotnet restore
dotnet build
dotnet test --filter "FullyQualifiedName!~IntegrationTest"   # unit tests only
dotnet test BuildingOS.IntegrationTest                        # integration tests (Docker required)
dotnet test --filter "FullyQualifiedName~TestName"            # run a specific test

# API Server
cd BuildingOS.ApiServer
dotnet run --launch-profile WithLocal     # uses local Docker services

# ConnectorWorker
cd BuildingOS.ConnectorWorker
NATS_URL=nats://localhost:4222 dotnet run
```

### Frontend (Next.js) — from `web-client/` directory
```bash
yarn install
yarn dev                    # dev server with Turbopack
yarn build
yarn lint
yarn format:check
yarn format:write
yarn typecheck
yarn generate               # regenerate TypeScript types from proto files via Buf
```

> User/permission management (the former `admin-console` app) now lives in the web-client `(admin)`
> workspace under `/admin` — there is no separate admin app.

### Local Development Services
```bash
docker compose -f docker-compose.oss.yaml up -d   # NATS, PostgreSQL 16, OxiGraph, MinIO, Keycloak, ConnectorWorker, GatewayBridge
docker compose -f docker-compose.oss.yaml down
docker compose up -d   # Redis (legacy helper service)

# Gateway gRPC endpoints (distinct services/ports by the ingress/egress split, #178):
#   ConnectorWorker GatewayIngress (telemetry ingest)  → enable with GRPC_INGRESS_PORT (e.g. 5051)
#   GatewayBridge   GatewayEgress  (control plane)     → host port 5052 (always in the oss stack)
# External BOWS / nexus-gateway egress agent: E2E_BOS_EGRESS_ADDR=localhost:5052 (NOT 5051).

# Observability (Prometheus/Grafana/Loki/Tempo/otel-collector/postgres-exporter) is OPTIONAL and NOT
# in the base stack (A-7, cost optimization). Apps degrade gracefully without it — PROMETHEUS_URL and
# OTEL_EXPORTER_OTLP_ENDPOINT are no-ops when the targets are unreachable. Opt in with:
docker compose -f docker-compose.oss.yaml --profile observability up -d

# MQTT broker is OPTIONAL and NOT in the base stack (#25). Scenario A (Mosquitto) is opt-in:
MQTT_HOST=building-os.mosquitto docker compose -f docker-compose.oss.yaml --profile mqtt up -d

# Warm-tier mode (#216): default is parquet (telemetry → Parquet lake on MinIO; the Python
# telemetry-consumer is gated behind the `timescale` profile and does NOT start by default).
# To run the legacy TimescaleDB warm path instead:
WARM_STORE=timescale docker compose -f docker-compose.oss.yaml --profile timescale up -d
```

> **Breaking change (#216 + #234):** Warm tier defaults to `parquet`; **TimescaleDB is dropped from the
> default stack and kept as a selectable opt-in** (default DB image is `postgres:16`). Telemetry and
> `point_control_audit` use the unified Parquet lake (MinIO) and shared PostgreSQL respectively by
> default. Select the legacy warm store with `WARM_STORE=timescale` (bring your own TimescaleDB + set
> `TIMESCALE_CONNECTION_STRING`). See `docs/oss-warm-parquet-lake.md` and `docs/oss-tier-architecture.md`.

### Code Generation — from `Tools/` directory
```bash
./generate-dotnet-entities-from-schema.bash   # C# entities from JSON Schema
./generate_swagger.bash                        # OpenAPI definition (API Server must be running)
./sync-type.bash                               # sync frontend Aspida types from Swagger
./build-and-push-api-server.bash               # build and push API Server Docker image
./add-migration-file.bash                      # add EF Core migration
```

## Project Structure

```
DotNet/
├── BuildingOS.ApiServer/        # ASP.NET Core REST + gRPC API
│   ├── Controllers/             # API endpoints
│   └── Startup/                 # DI configuration
├── BuildingOS.ConnectorWorker/  # NATS connector worker host (+ optional gRPC GatewayIngress on GRPC_INGRESS_PORT)
│   ├── Connectors/              # per-protocol workers (BACnet, HVAC, …) + GatewayIngress (gRPC telemetry ingest)
│   └── Infrastructure/          # KandtDeviceControlHandler (IoT Hub direct method)
├── BuildingOS.GatewayBridge/    # gRPC ⇄ NATS egress bridge for the external BOWS connector (control plane)
│   ├── Services/                # GatewayEgress (bidi, per-gateway)
│   ├── Infrastructure/          # core-NATS egress bus / connection registry
│   └── Mapping/                 # ControlCommand mapper
├── BuildingOS.Shared/           # domain layer, infrastructure, shared libraries
│   ├── Defines/Schemas/         # JSON Schema definitions (source of truth for entities)
│   ├── Defines/Entities/        # auto-generated entity classes — do NOT edit manually
│   ├── Domain/                  # domain entities and business logic
│   ├── Infrastructure/          # repositories, OxiGraph, MinIO, NATS, Keycloak
│   └── Migrations/              # EF Core migrations (PostgreSQL, user/group/authorization tables)
├── BuildingOS.IntegrationTest/  # Testcontainers integration tests
└── BuildingOS.Shared.Test/      # xUnit unit tests

web-client/src/
├── app/                         # Next.js App Router
│   ├── (auth)/                  # auth pages (sign-in)
│   └── (protected)/             # routes requiring authentication
├── components/                  # shared React components
├── lib/
│   ├── auth/                    # Keycloak OIDC config
│   ├── infra/aspida-client/     # auto-generated Aspida types — do NOT edit manually
│   ├── infra/zod-api-client/    # Zodios client with runtime Zod validation
│   ├── infra/grpc-client/       # gRPC-web client for device control streaming
│   └── gen/                     # auto-generated gRPC TypeScript from proto files
├── middleware.ts                # auth middleware
└── app/(protected)/(admin)/     # (admin) workspace — user/group/permission management
                                 #   (formerly the standalone admin-console app)

proto/
├── greet.proto                  # Greeter service + server-streaming notifications
├── point_control.proto          # PointControlService for device control streaming
├── gateway_egress.proto         # GatewayEgress (bidi) — BOWS control plane (GatewayBridge)
└── gateway_ingress.proto        # GatewayIngress (client-stream telemetry) — hosted in ConnectorWorker

Tools/
├── auth-proxy-server/           # Python proxy to bypass Keycloak in local dev
├── development-edge-device/     # IoT device simulator (MQTT / IoT Hub)
└── workload-test-project/       # load testing

kubernetes/                      # Helm charts
opentofu/                        # OpenTofu (Terraform) IaC
argocd/                          # ArgoCD GitOps manifests
observability/                   # Prometheus / Grafana / Loki / Tempo config
```

## Key Development Patterns

### .NET Coding Conventions
```csharp
// Always pass CancellationToken in async methods
public async Task<IActionResult> GetAsync(CancellationToken ct)
{
    var data = await _service.GetDataAsync(ct).ConfigureAwait(false);
    return Ok(data);
}

// Structured logging — never use string interpolation
_logger.LogInformation("Processing device {DeviceId} with {PointCount} points", deviceId, pointCount);

// AsNoTracking() for read-only queries
var entities = await _context.Buildings.AsNoTracking().ToListAsync(ct);
```

### Adding New Connectors

Connectors are long-running `BackgroundService` workers that subscribe to a NATS `building-os.raw.*` subject, normalise the payload, and republish to `building-os.validated.telemetry`.

1. Create JSON Schema in `BuildingOS.Shared/Defines/Schemas/`
2. Run `./generate-dotnet-entities-from-schema.bash` to generate entity classes
3. Implement the worker in `BuildingOS.ConnectorWorker/Connectors/` extending `ConnectorWorkerBase`
4. Register it in `BuildingOS.ConnectorWorker/Program.cs`
5. Add unit tests in `BuildingOS.Shared.Test/`

NATS subject → worker mapping:

| Subject | Worker |
|---------|--------|
| `building-os.raw.hvac` | HvacConnectorWorker |
| `building-os.raw.bacnet` | BacnetConnectorWorker |
| `building-os.raw.environmental` | EnvironmentalConnectorWorker |
| `building-os.raw.electric` | ElectricConnectorWorker |
| `building-os.raw.behavior` | BehaviorConnectorWorker |
| `building-os.raw.mqtt` | MqttConnectorWorker (Mosquitto 経由デバイス — Scenario A) |
| `building-os.raw.hono` | HonoConnectorWorker (Hono AMQP Northbound 経由デバイス — Scenario B) |
| `building-os.control.request` | NatsPointControlWorker (in-process handlers: Hono / Kandt) |
| `building-os.control.request.gw.{gatewayId}` | GatewayBridge (per-gateway egress; BacnetSim/BOWS) |

gRPC ingress (telemetry ingest 正本): the **GatewayIngress** gRPC service is hosted by ConnectorWorker
(enabled only when `GRPC_INGRESS_PORT` is set; otherwise no listener). The contract is **point-id
based** — BuildingOS and the gateway share the point list, so the gateway resolves protocol-native
addressing to a `point_id` locally and a `TelemetryFrame` carries only `gateway_id` + `point_id` +
`value` + `timestamp` (+ optional `attributes`). The service enriches static metadata
(building/device/name) from the digital twin by `point_id` via `IPointMetadataCache` (a process-local
TTL cache over OxiGraph, so no per-frame graph query) and publishes straight to
`building-os.validated.telemetry` (no raw.{protocol} hop, no per-protocol connector for this path).
A frame with an unknown `point_id`, or whose `gateway_id` does not own that point in the twin, is
skipped (logged + metered). `gateway_id` global uniqueness is enforced at seed import
(`OxiGraphSeedHostedService` throws if a gateway_id spans multiple buildings). The egress half
(`GatewayEgress`) lives in GatewayBridge. (The MQTT/Hono connectors are unchanged — they serve devices
that do **not** share the point list and still resolve localId→pointId via `IPointIdFactory`.)

**Gateway point-list sync (#224):** the twin is the **source of truth** for the shared point list, and
gateways follow it via `GET /gateways/{gatewayId}/pointlist` (`GatewayProvisioningController`). It returns
the gateway-owned points with native addressing (`sbco:localId` / `deviceIdBacnet` / `objectTypeBacnet` /
`instanceNoBacnet`), unit, writable, control schema (`bos:*`) and device grouping, via
`IDigitalTwinDatabase.ListGatewayPointList`. Versioning is a **content-hash ETag** (`"sha256:..."`,
order-independent) with `If-None-Match`→`304` so gateways poll cheaply and refetch only on change. Auth is
**machine auth, not user RBAC**: the mTLS-terminating ingress injects the verified gateway id as a trusted
header (`IGatewayIdentityResolver`, default `X-Gateway-Id`); the endpoint requires it to match the path
(admin JWT bypasses for ops). The route has no `[AuthorizeFilter]`. Trust boundary + Traefik
`passTLSClientCert` wiring: `docs/oss-gateway-pointlist-sync.md`. Incremental diff is `?since={etag}`
(snapshot-cached, falls back to full). Push: the twin seed signals each gateway via
`building-os.pointlist.updated.gw.{id}` (`IPointListUpdatePublisher`) → GatewayBridge forwards
`EgressDown{PointListUpdate}` down the egress stream (`gateway_egress.proto` `EgressDown` is now a
oneof); the gateway revalidates via ETag (push is an optimization, ETag polling is the reliability
backstop).

> **Onboarding a new gateway (#127):** the steps above (twin seed, control binding, ingress/egress
> ports, point-list polling) are consolidated into one checklist:
> `docs/gateway-onboarding-checklist.md`.

### Device Control Flow

`ControlTypeResolver` resolves the egress ControlType + Body from the point's gateway **binding type**
(`IGatewayConnectionRegistry`, config-driven), replacing the old hard-coded Hono. Two transports:

1. **In-process handlers** — API Server → NATS (`building-os.control.request`) → NatsPointControlWorker → handler.
   The worker does **not** trust the wire-supplied `Type`: it re-resolves the gateway's
   `GatewayConnection` (`{ BindingType, Settings }`) from `IGatewayConnectionRegistry` by `GatewayId`,
   dispatches to the handler matching `BindingType`, and passes the resolved connection in
   (`ExecuteControlAsync(info, connection, ct)`). Adapters read **no env directly** — per-gateway
   host/credentials come from `connection.Settings`, so two same-binding gateways can target different
   hosts (#154 Phase 2).
   - **KandtDeviceControlHandler** — Azure IoT Hub direct method to the Kandt gateway (`Microsoft.Azure.Devices`); the gateway speaks BACnet downstream
   - **HonoDeviceControlHandler** — Eclipse Hono AMQP Northbound (`/command/{tenant}/{localId}`)
2. **GatewayBridge (BacnetSim / external BOWS)** — API Server → NATS per-gateway subject
   (`building-os.control.request.gw.{gatewayId}`) → GatewayBridge `GatewayEgress` bidi stream → BOWS →
   BACnet WriteProperty. The `ControlCommand` is **point-id canonical** (#181): it carries
   `control_id` + `point_id` + `present_value` + `priority`; the gateway resolves `point_id` → BACnet
   object/instance from the shared point list (the BACnet identity fields were removed from the proto).
   Results return on `building-os.control.result.{controlId}` → existing `WaitForResult`. GatewayBridge
   is a stateless/horizontally scalable egress control plane (per-gateway NATS routing). **Offline
   detection (#186):** the per-gateway command is sent as a NATS *request* — the bridge replica acks
   after forwarding it down the stream, so an offline gateway (no subscriber) surfaces as NATS
   no-responders and `PointController.Control` returns **503** immediately (metric
   `control.requests{result=gateway_offline}`) instead of letting the client wait out the result
   timeout. An ack timeout (replica present but slow) is treated as delivered; the result timeout
   remains the backstop. Telemetry
   **ingress** (`GatewayIngress`, client-stream) is hosted by ConnectorWorker, not GatewayBridge (gated
   on `GRPC_INGRESS_PORT`). See
   `docs/oss-egress-gateway-bridge-plan.md`, `docs/gateway-bridge-ingress-egress-split.md` and
   `DotNet/BuildingOS.GatewayBridge/README.md`.

### Frontend API Integration (web-client)

| Client | When to use |
|--------|-------------|
| Aspida client (`src/lib/infra/aspida-client/`) | standard REST endpoints (type-safe, generated from Swagger) |
| Zodios client (`src/lib/infra/zod-api-client/`) | REST with runtime Zod validation |
| gRPC client via `useControlExecution()` (`src/lib/infra/grpc-client/`) | device control streaming (point_control) |
| bespoke authenticated fetch (`src/lib/admin/`) | admin endpoints (`/api/Users`, `/api/Groups`, `/api/Permissions`) not yet in the Aspida schema |
| resource/telemetry façade (`src/lib/resources/`, `src/lib/telemetry/`) | resource hierarchy + telemetry reads — **UI calls these, not aspida directly** |

The `(admin)` workspace uses the bespoke fetch helpers in `src/lib/admin/http.ts` (Keycloak bearer token from the `oidc.access_token` cookie). Adding the admin endpoints to Swagger and generating their Aspida types is a follow-up.

Aspida client is auto-generated from Swagger. After API changes, run `./sync-type.bash` to update frontend types.

> **Resource/telemetry access façade:** UI/components do **not** import aspida `@types` directly for
> resources or telemetry. `src/lib/resources/` (domain types + `repository.ts` + pure `mapping.ts`/
> `search.ts`/`keys.ts` + injectable `tree-loaders.ts`) and `src/lib/telemetry/` (`repository.ts` +
> pure `mapping.ts`) wrap `apiClient()` so an API/Swagger change is absorbed in one place. Telemetry
> reads route through the unified `GET /telemetries/query` (tier auto-selection + granularity +
> latest), not the per-tier hot/warm/cold endpoints. The resource explorer at `/resources`
> (`components/resources/`: tree view + detail + search) is the operator landing page; the old
> `/buildings` list redirects there while the per-resource detail deep links
> (`/buildings/[id]`, `/floors/[id]`, …) are unchanged. Cross-resource search is `GET /resources/search`
> (OxiGraph SPARQL via `ResourceSearchQueryBuilder`, authorized by `AuthorizedTwinView.SearchAsync`).

### Help content (content-as-code, #149)

In-app help is **content-as-code** in `web-client/src/lib/help/` (typed TS, i18n = ja): `content.ts`
holds the glossary + help entries (the single source of truth, also intended as future LLM prompt
material), `resolve.ts` has the pure resolution logic (`resolveHelp` / `resolveTerm` / `relatedTerms`).
UI: `HelpButton` (binds a screen to its `helpKey`, opens `HelpDrawer`) and `GlossaryTooltip`
(`src/components/help/`). Add help by extending the arrays in `content.ts` — no RAG/runtime fetch.

### Onboarding tour (#150)

First-login guided tour in `web-client/src/lib/onboarding/` (content reuses D-1 help by `helpKey`)
+ `src/components/onboarding/`. `select.ts` is pure (role-filter + resolve), `storage.ts` persists
skip/replay in localStorage (versioned key). `OnboardingTour` (mounted in `AppShell`, role-filtered,
auto-opens once) and `ReplayTourButton` (in the header, re-opens via a `REPLAY_EVENT`). Steps are
role-scoped (`admin`/`operator`/`viewer`).

### Help assistant (experimental/optional, #151)

Optional local-LLM help Q&A. **Off by default.** Backend: `POST /api/assistant/chat` (JWT-gated, no
backdoor) returns 503 unless `ASSISTANT_LLM_URL` points at an OpenAI-compatible base (the local Ollama
optional profile: `docker compose --profile assistant up`, `ASSISTANT_LLM_URL=http://building-os.ollama:11434/v1`).
`AssistantPromptBuilder` (pure) injects the client-sent D-1 context + read-only guardrails and drops
any client "system" message; `AssistantService` proxies to `IAssistantLlmClient` (Ollama). **No control
actions, no RAG/vector DB.** Frontend: `src/lib/assistant/` (pure `buildAssistantContext`/`helpKeyForPath`)
+ `src/components/assistant/` (`AssistantChat` mounted in `AppShell`, shown only when
`NEXT_PUBLIC_ASSISTANT_ENABLED=true`).

### gRPC / Proto Workflow
1. Edit `.proto` files in `proto/`
2. Run `yarn generate` from `web-client/` to regenerate TypeScript types via Buf
3. Generated files land in `web-client/src/lib/gen/`
4. Backend `.csproj` files auto-compile protos at build time

### Authorization Model

The `AuthorizationContext` (populated from the Keycloak JWT) carries `IsAdmin`, the user's role, and a list of permission strings. Permission strings follow the format `{resourceType}:{resourceId}:{actions}`. Resource IDs that are not group IDs are hashed (SHA-256 prefix) before storage.

Authorization checks: controllers call `HttpContext.GetAuthorizationContext()` then either check `IsAdmin` directly or delegate to `IAuthorizationService.CanAccessAsync()`.

The `resourceType` values (`building`, `floor`, `space`, `device`, `point`) correspond to SBCO ontology classes (`sbco:Building`, `sbco:Level`, `sbco:Room`, `sbco:EquipmentExt`, `sbco:PointExt`); `bos:` is retained only for the `ControlSchema` extension. See `docs/standard-mapping.md` for the mapping between SBCO / `bos:` vocabulary and Brick / REC / IFC / DTDL standards.

## Technology Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 8.0, ASP.NET Core |
| Workers | .NET 8.0, `BackgroundService` (ConnectorWorker) |
| Frontend | Next.js 16, React 19, TypeScript 5, Tailwind CSS 4 (web-client; `(admin)` workspace at `/admin`) |
| UI Components | Radix UI, Recharts, React Hook Form |
| Authentication | Keycloak (OIDC / JWT), `JwtBearerDefaults` in ASP.NET Core |
| Message Bus | NATS JetStream |
| Telemetry Lake | MinIO (Parquet files — warm + cold unified lake) |
| Relational DB | PostgreSQL 16 via EF Core (user/group/point_control_audit) |
| Digital Twin | OxiGraph (SPARQL / RDF) |
| Blob Storage | MinIO (S3-compatible) |
| Streaming API | gRPC-web (ConnectRPC) |
| Observability | OpenTelemetry → Prometheus + Grafana + Loki + Tempo |
| IaC | OpenTofu (Terraform), Helm, ArgoCD |
| CI/CD | GitHub Actions (test workflows are **manual-trigger only** — see note below) |
| IoT connectivity | MQTT (Mosquitto), Hono bridge, Azure IoT Hub (optional connector) |

> **CI is manual-trigger only (credit conservation):** the test/validation workflows
> (`oss-ci`, `integration-tests`, `golden-tests`, `helm-chart-install-test`, `parity-harness`) run on
> `workflow_dispatch` only — they do **not** fire on push/PR. Run them from the Actions tab when
> needed. **Local verification is the primary gate** (`dotnet test`, `yarn test`/`typecheck`/`lint`,
> `yarn build`). The release/deploy chain (`harbor-push` → `argocd-image-update`, `generate-swagger`)
> still runs on `main` merges.

## Environment Variables

### API Server

| Variable | Description | Default |
|----------|-------------|---------|
| `NATS_URL` | NATS connection URL | `nats://localhost:4222` |
| `POSTGRES_CONNECTION_STRING` | PostgreSQL (user/group/authorization, shared OSS instance) | — |
| `KEYCLOAK_AUTHORITY` | Keycloak issuer URL | — |
| `KEYCLOAK_CLIENT_ID` | JWT audience | — |
| `KEYCLOAK_REALM` | Keycloak realm | — |
| `KEYCLOAK_ADMIN_CLIENT_ID` | Admin API client ID | — |
| `KEYCLOAK_ADMIN_CLIENT_SECRET` | Admin API client secret | — |
| `DISABLE_AUTH` | skip auth (local dev) | `false` |
| `CONTROL_RESULT_TIMEOUT_SEC` | Online point-control result wait (`WaitForResult`) timeout in seconds; offline gateways already fail fast with 503 (#186), so this only bounds the "connected but slow" round-trip. | `10` |
| `WARM_STORE` | Warm-tier storage mode (#216): `parquet` reads the unified Parquet lake (MinIO) for warm+cold+aggregate so TimescaleDB is not required for telemetry; `timescale` opts back into the TimescaleDB warm/aggregate stores (+ MinIO cold reader). **Default `parquet`** (any value except `timescale` → parquet). | `parquet` |
| `MINIO_ENDPOINT` / `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY` | MinIO/S3 for the Parquet lake (read path). Required in parquet mode; the cold-tier reader in timescale mode. | — / `buildingos` / `buildingos123` |
| `PARQUET_LATEST_LOOKBACK_HOURS` | parquet mode: hours the latest-value fallback scans back from now (Hot KV stays primary). | `24` |
| `PARQUET_QUERY_MAX_FILES` | parquet mode: per-query object cap; over it → partial result (most-recent partitions) + warning. `0` = unlimited. | `0` |
| `PROMETHEUS_URL` | Prometheus query API for KPIs in the built-in simple-monitoring endpoint (`GET /api/system/status`). Unset → KPIs degrade to null, Grafana not required. | — |
| `SYSTEM_STATUS_HEALTH_TARGETS` | Per-service up/down for `GET /api/system/status` via `/health` fan-out (Prometheus-independent). Comma-separated `name=healthUrl`. Unset → only the API server is reported. | — |

> **Effective-config view (#147):** `GET /api/system/config` (admin-only) returns the API server's effective configuration for a **read-only** allowlist (`ConfigAllowlist.ApiServer`); secrets report presence only (no value). IaC/ArgoCD stays the source of truth — this is observability, not editing. Wired into the web-client `(platform)` workspace at `/platform/config`.

> **App settings store (#148):** `GET/PUT/DELETE /api/system/settings[/{key}]` (admin-only) is the **editable** counterpart for app/domain settings that do not conflict with GitOps (feature flags / thresholds). Only keys in `SettingsRegistry` are writable and values are type-validated; the override is persisted in the `system_config` table (PostgreSQL, EF Core) with provenance, and `DELETE` resets to the registry default. Wired into `(platform)` at `/platform/settings`.
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint (traces+metrics+logs → otel-collector). No-op when unset. | — |
| `OTEL_SERVICE_NAME` | OTLP `service.name` → Prometheus `job` label | `building-os-api` |
| `Logging__LogLevel__Default` | Min log level; per-category override via `Logging__LogLevel__<Category>` (e.g. `Logging__LogLevel__BuildingOs.ApiServer=Debug`) | `Information` |

> `LOG_LEVEL` is **deprecated** (no effect) — use `Logging__LogLevel__*` instead.

### ConnectorWorker

| Variable | Description |
|----------|-------------|
| `NATS_URL` | NATS connection URL |
| `WARM_STORE` | Warm-tier storage mode (#216). **Default `parquet`** (any value except `timescale`): the `ParquetLakeWriterWorker` persists `building-os.validated.telemetry` straight to the Parquet lake (MinIO) and the TimescaleDB `ColdExportWorker` is disabled (mutually exclusive). `timescale` restores the cold-export path (+ Python `telemetry-consumer`). Parquet mode requires `MINIO_ENDPOINT` (fail-fast if unset). |
| `PARQUET_FLUSH_INTERVAL` | parquet mode: writer flush cadence in **minutes** (also the idle wake-up). AckWait floor is derived from it. | 
| `PARQUET_FLUSH_MAX_ROWS` | parquet mode: flush when this many rows have buffered (default `50000`). |
| `PARQUET_STREAM_MAX_BYTES` | parquet mode: optional `BUILDING_OS_VALIDATED` stream MaxBytes cap (0 = unbounded). MaxAge is set to 24h. |
| `LAKE_COMPACTION_INTERVAL` | parquet mode: `CompactionWorker` scan cadence in **minutes** (default `15`). Merges per-flush `part-*.parquet` into one `compact-*.parquet` per settled building-hour. |
| `LAKE_COMPACTION_SETTLE_MINUTES` | parquet mode: grace after an hour ends before it is compacted (default `30`). |
| `LAKE_COMPACTION_MIN_PARTS` | parquet mode: minimum parts in a settled hour before compaction (default `2`, min `2`). |
| `LAKE_RETENTION_DAYS` | parquet mode: applies an S3/MinIO ILM rule expiring lake objects after N days (the `drop_chunks` replacement). Unset/`0` → unlimited. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint (traces+metrics+logs → otel-collector). No-op when unset. |
| `OTEL_SERVICE_NAME` | OTLP `service.name` → Prometheus `job` label (default: `building-os-connector-worker`) |
| `Logging__LogLevel__Default` | Min log level; per-category override via `Logging__LogLevel__<Category>` (default: `Information`) |
| `HEALTH_PORT` | Internal HTTP/1.1 health surface port (default `8081`). Always on (the worker is always a WebApplication): `/health/live` (liveness = process serves HTTP), `/health/ready` (readiness = NATS connection Open), `/health` (overall). Consumed by the system-status fan-out (#144) and orchestrator/compose probes. Does **not** open the external gRPC ingest surface. |
| `GRPC_INGRESS_PORT` | Enables the gRPC GatewayIngress telemetry ingest on a Kestrel h2c listener (separate from `HEALTH_PORT`). **Unset → no gRPC listener** (health-only). The canonical ingest path; set in deployed/Helm, unset in OSS/local/CI. |
| `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY` | #296 identity binding: truthy (`true`/`1`/`yes`/`on`) rejects any ingress frame whose `gateway_id` does not match the mTLS-ingress trusted header (a duplicate/ambiguous trusted header also fails closed); the effective state is logged at startup. (skip + metric `result=identity_mismatch`/`identity_missing`). **Default off** (frame `gateway_id` trusted as provenance, twin ownership still checked) so local/CI without trusted-header injection keep working; set `true` in production behind an mTLS ingress. Only used when `GRPC_INGRESS_PORT` is set. |
| `GRPC_INGRESS_GATEWAY_ID_HEADER` | Trusted header carrying the ingress-verified gateway id for the binding above (default `X-Gateway-Id`, matches the #224 pointlist resolver). The route must be reachable only via the mTLS ingress and the header stripped on every untrusted path. |
| `OXIGRAPH_ENDPOINT` | Digital twin (OxiGraph) SPARQL base URL. **Required by the gRPC GatewayIngress path** — `IPointMetadataCache` resolves `point_id` → building/device/name/gatewayId. Default `http://localhost:7878` (wrong inside a container; the OSS compose sets the service DNS). |
| `GRPC_KEEPALIVE_PING_DELAY_SEC` | HTTP/2 keepalive ping delay for the ingress listener; must be > 0, else default `20`. Only used when `GRPC_INGRESS_PORT` is set. |
| `GRPC_KEEPALIVE_PING_TIMEOUT_SEC` | HTTP/2 keepalive ping ack timeout before closing; must be > 0, else default `10`. Only used when `GRPC_INGRESS_PORT` is set. |
| `ENABLE_SIM_CONTROL` | register only the simulated device-control handler (OSS/local; skips the Kandt IoT Hub handler) |
| `IOT_HUB_CONNECTION_STRING` | Azure IoT Hub (Kandt gateway control) |
| `IOT_EDGE_MODULE_ID` | IoT Edge module ID |
| `MQTT_HOST` | Mosquitto host (enables MqttIngressWorker when set) |
| `MQTT_PORT` | MQTT port (default: 1883) |
| `MQTT_USERNAME` / `MQTT_PASSWORD` | MQTT credentials |
| `MQTT_TOPIC_FILTER` | MQTT subscribe filter (default: `telemetry/#`) |
| `HONO_AMQP_HOST` | Hono AMQP Northbound host (enables AmqpIngressWorker + HonoConnectorWorker) |
| `HONO_AMQP_PORT` | AMQP port (default: 5672, TLS: 5671) |
| `HONO_AMQP_TLS` | `true` → TLS (`amqps`); default plaintext (`amqp`). Set `true` for a TLS Northbound on 5671 |
| `HONO_AMQP_TENANT` | Hono tenant ID (default: `building-os`) |
| `HONO_AMQP_USER` / `HONO_AMQP_PASSWORD` | SASL Plain credentials for AMQP connection |

### GatewayBridge

| Variable | Description | Default |
|----------|-------------|---------|
| `NATS_URL` | NATS connection URL | `nats://localhost:4222` |
| `GRPC_PORT` | gRPC (HTTP/2 h2c) listen port; TLS/mTLS terminates at the ingress (Traefik/Envoy) | `8080` |
| `GRPC_KEEPALIVE_PING_DELAY_SEC` | HTTP/2 keepalive ping delay; must be > 0, else default | `20` |
| `GRPC_KEEPALIVE_PING_TIMEOUT_SEC` | HTTP/2 keepalive ping ack timeout before closing the connection; must be > 0, else default | `10` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint (traces+metrics+logs). No-op when unset. | — |
| `OTEL_SERVICE_NAME` | OTLP `service.name` | `building-os-gateway-bridge` |

> **Gateway connection registry** (`IGatewayConnectionRegistry`, both ApiServer + ConnectorWorker via
> the shared `GatewayConnectionRegistryFactory`). Binding per gateway: `GatewayConnectionTypes:Default`
> / `GatewayConnectionTypes:Map:{gatewayId}` (env: `GatewayConnectionTypes__Default` /
> `GatewayConnectionTypes__Map__<gatewayId>`) → `hono` / `kandt` / `bacnet-sim`; default `hono`
> preserves prior behaviour (the ConnectorWorker falls back to `simulated` when
> `ENABLE_SIM_CONTROL=true` and no default is set, matching its only registered handler). Per-gateway connection settings: `Gateways:{gatewayId}:Settings:{key}`
> (env: `Gateways__<gatewayId>__Settings__<key>`), e.g. `host` / `port` / `tenant` / `user` /
> `password` / `tls` for Hono, `iotHubConnectionString` / `moduleId` for Kandt. Per-gateway settings
> are **merged on top of** the binding's defaults (synthesised from the existing single-instance env
> `HONO_AMQP_*` / `IOT_HUB_*`), so a partial override (e.g. only `host`) inherits the rest and
> single-gateway deployments keep working unchanged. Gateway-id and settings-key lookups are
> case-insensitive. The reserved `credentialsRef` (external secret-store resolution) is out of scope
> for this slice.

## Environment Requirements

- .NET SDK 8.0+
- Node.js 20.19.5+ minimum (`web-client/.nvmrc` + `package.json` `engines`); 22.x recommended (CI runs 22.x)
- Docker Desktop (for local development)
- Buf CLI (for proto → TypeScript generation)
