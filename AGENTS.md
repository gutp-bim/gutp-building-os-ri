# AGENTS.md

Guidance for AI coding agents (Codex, Claude Code, Copilot Workspace, etc.) working in this repository.

## What this repo is

Building OS OSS — an open-source IoT platform for smart building management. The stack is entirely self-hosted: NATS JetStream for messaging, MinIO Parquet lake for telemetry (default; TimescaleDB is opt-in via `WARM_STORE=timescale`), OxiGraph for the digital-twin graph, Keycloak for authentication.

The .NET solution (`DotNet/`) is the primary codebase. The Next.js frontend lives at `web-client/`; user/group/permission management is its `(admin)` workspace under `/admin` (the former standalone `admin-console` app was merged in).

## Before you start

1. Read `CLAUDE.md` — it has the full architecture diagram, build commands, project structure, and all coding conventions.
2. Read `README.md` — quick-start instructions and environment variable reference.
3. Do **not** re-introduce Azure cloud dependencies (CosmosDB, Event Hub, Azure Digital Twins, Azure Functions runtime, Bicep, MSAL / Microsoft Identity Web). The only Azure SDK intentionally kept is `Microsoft.Azure.Devices` in `BuildingOS.ConnectorWorker` — it bridges existing BACnet edge devices via IoT Hub direct methods.

## Orientation

```
DotNet/
├── BuildingOS.ApiServer/        ← REST + gRPC API (ASP.NET Core)
├── BuildingOS.ConnectorWorker/  ← NATS connector workers + device control handlers
├── BuildingOS.Shared/           ← domain, infrastructure, shared libraries
├── BuildingOS.IntegrationTest/  ← Testcontainers integration tests
└── BuildingOS.Shared.Test/      ← xUnit unit tests
```

## High-priority rules

### Frontend API access
Standard REST goes through the generated Aspida/Zodios clients; gRPC device control uses `useControlExecution()`. The `(admin)` workspace calls the admin endpoints (`/api/Users`, `/api/Groups`, `/api/Permissions`) via the authenticated bespoke fetch helpers in `web-client/src/lib/admin/` (Keycloak bearer from the `oidc.access_token` cookie).

### Never edit auto-generated files
- `DotNet/BuildingOS.Shared/Defines/Entities/` — regenerated from JSON Schema via `Tools/generate-dotnet-entities-from-schema.bash`
- `web-client/src/lib/infra/aspida-client/` — regenerated from Swagger via `Tools/sync-type.bash`
- `web-client/src/lib/gen/` — regenerated from `.proto` files via `yarn generate`

### Always pass `CancellationToken` in async .NET methods
```csharp
public async Task<T> GetAsync(CancellationToken ct)
{
    return await _repo.FindAsync(ct).ConfigureAwait(false);
}
```

### Use structured logging
```csharp
_logger.LogInformation("Received telemetry from {DeviceId}", deviceId);
// NOT: _logger.LogInformation($"Received telemetry from {deviceId}");
```

## Adding a new connector

A connector is a `BackgroundService` that subscribes to a `building-os.raw.*` NATS subject, normalises the payload, and publishes to `building-os.validated.telemetry`.

Steps:
1. Define the raw message schema in `BuildingOS.Shared/Defines/Schemas/`
2. Run `./Tools/generate-dotnet-entities-from-schema.bash` to generate the entity class
3. Create `MyProtocolConnectorWorker : ConnectorWorkerBase` in `BuildingOS.ConnectorWorker/Connectors/`
4. Register it in `BuildingOS.ConnectorWorker/Program.cs` with `builder.Services.AddHostedService<MyProtocolConnectorWorker>()`
5. Add unit tests in `BuildingOS.Shared.Test/`

## Running tests

```bash
# Unit tests (fast, no Docker needed)
cd DotNet
dotnet test --filter "FullyQualifiedName!~IntegrationTest"

# Integration tests (requires Docker)
dotnet test BuildingOS.IntegrationTest
```

Integration tests use Testcontainers. They spin up NATS, MinIO, and PostgreSQL (TimescaleDB) automatically.

## Keycloak / authentication

The API server validates JWT tokens issued by Keycloak using `JwtBearerDefaults`. The issuer and audience are configured via `KEYCLOAK_AUTHORITY` and `KEYCLOAK_CLIENT_ID` environment variables. Set `DISABLE_AUTH=true` to skip auth in local development.

Frontend apps read the Keycloak config from `NEXT_PUBLIC_KEYCLOAK_*` environment variables.

## OxiGraph (digital twin)

The building → floor → space → device hierarchy is stored as RDF triples in OxiGraph. Queries use SPARQL via `IDigitalTwinDatabase` (`OxiGraphDigitalTwinDatabase`). Do not use raw SPARQL strings inline — add typed query methods to the database interface.

## NATS subjects

| Subject | Direction | Purpose |
|---------|-----------|---------|
| `building-os.raw.*` | inbound | raw device telemetry per protocol |
| `building-os.validated.telemetry` | internal | normalised telemetry → Parquet lake (MinIO) / NATS KV latest |
| `building-os.control.request` | API → Worker | point control commands |
| `building-os.control.result.*` | Worker → API | control execution results (gRPC streaming) |

## Common mistakes to avoid

- Do not add `BackendSelector` / `BUILDING_OS_BACKEND` env var logic — the codebase is OSS-only; the dual-backend switch has been removed.
- Do not add `AsNoTracking()` omission for read-only EF Core queries.
- Do not use `DateTime.Now` — use `DateTime.UtcNow` or `DateTimeOffset.UtcNow`.
- Do not hardcode connection strings — always read from environment variables via `EnvModule`.
- Do not skip `ConfigureAwait(false)` in library/infrastructure code.
