using BuildingOS.Shared.Helpers;

namespace BuildingOs.ApiServer.Modules;

public class EnvModule
{
    public readonly string AspNetCoreEnvironment = EnvHelper.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
    // Deprecated and optional: log verbosity is governed by the standard Logging:LogLevel
    // configuration (the Logging__LogLevel__Default / Logging__LogLevel__<Category> env vars),
    // not by this value. Kept nullable for backward compatibility so a missing LOG_LEVEL no
    // longer aborts startup.
    public readonly string? LogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
    public readonly string? OtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    public readonly string OtlpServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "building-os-api";
    public readonly string PostgresConnectionString = EnvHelper.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
    public readonly string? PostgresMigrationConnectionString = Environment.GetEnvironmentVariable("POSTGRES_MIGRATION_CONNECTION_STRING");
    public readonly bool DisableAuth = Environment.GetEnvironmentVariable("DISABLE_AUTH")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
    public readonly string NatsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
    public readonly string? TimescaleConnectionString = Environment.GetEnvironmentVariable("TIMESCALE_CONNECTION_STRING");
    public readonly string OxiGraphEndpoint = Environment.GetEnvironmentVariable("OXIGRAPH_ENDPOINT") ?? "http://localhost:7878";

    // Optional: MinIO/S3 for the Cold Parquet lake. When set, the cold-tier telemetry store is wired
    // into the query router so /telemetries/cold and the boundary-spanning /telemetries/query read the
    // lake instead of degrading to warm (#212).
    public readonly string? MinioEndpoint = Environment.GetEnvironmentVariable("MINIO_ENDPOINT");
    public readonly string MinioAccessKey = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "buildingos";
    public readonly string MinioSecretKey = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "buildingos123";

    // Warm-tier storage mode (#216). Unset → parquet (default; reads the unified Parquet lake for
    // warm+cold+aggregate, TimescaleDB optional). WARM_STORE=timescale opts back into the TimescaleDB
    // warm/aggregate stores. See WarmStoreMode.
    public readonly string? WarmStore = Environment.GetEnvironmentVariable("WARM_STORE");
    // Parquet-lake read tuning (parquet mode only). Latest-value fallback lookback (hours) and the
    // per-query object cap (0 = unlimited).
    public readonly int ParquetLatestLookbackHours =
        int.TryParse(Environment.GetEnvironmentVariable("PARQUET_LATEST_LOOKBACK_HOURS"), out var h) && h > 0 ? h : 24;
    public readonly int ParquetQueryMaxFiles =
        int.TryParse(Environment.GetEnvironmentVariable("PARQUET_QUERY_MAX_FILES"), out var f) && f > 0 ? f : 0;
    // Optional: Prometheus base URL for the built-in simple-monitoring endpoint
    // (GET /api/system/status). Unset → KPIs degrade to null (service up/down still works via
    // the /health fan-out below, so the endpoint is usable without Prometheus/Grafana).
    public readonly string? PrometheusUrl = Environment.GetEnvironmentVariable("PROMETHEUS_URL");
    // Optional: comma-separated "name=healthUrl" targets probed by GET /api/system/status to report
    // per-service up/down via /health fan-out (Prometheus-independent). Unset → only the API server
    // itself is reported. Example: "nats=http://nats:8222/healthz,minio=http://minio:9000/minio/health/live"
    public readonly string? SystemStatusHealthTargets = Environment.GetEnvironmentVariable("SYSTEM_STATUS_HEALTH_TARGETS");

    // Experimental/optional help assistant (#151). Unset → the assistant is disabled (POST
    // /api/assistant/chat returns 503). Point at an OpenAI-compatible base URL (e.g. the local Ollama
    // optional profile: http://ollama:11434/v1) to enable it.
    public readonly string? AssistantLlmUrl = Environment.GetEnvironmentVariable("ASSISTANT_LLM_URL");
    public readonly string AssistantLlmModel = Environment.GetEnvironmentVariable("ASSISTANT_LLM_MODEL") ?? "llama3.2";

    // JetStream tail-merge (#220): appends unflushed NATS messages to warm queries near "now".
    // Disabled when LookbackSec ≤ 0 or PARQUET_TAIL_MERGE_ENABLED=false.
    // Set PARQUET_TAIL_LOOKBACK_SEC=0 to disable via window (TailMergePolicy.ShouldMergeTail checks > 0).
    public readonly bool TailMergeEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("PARQUET_TAIL_MERGE_ENABLED"), "false", StringComparison.OrdinalIgnoreCase);
    public readonly int TailMergeLookbackSec =
        int.TryParse(Environment.GetEnvironmentVariable("PARQUET_TAIL_LOOKBACK_SEC"), out var tls) ? tls : 900;
    public readonly int TailMergeMaxMsgs =
        int.TryParse(Environment.GetEnvironmentVariable("PARQUET_TAIL_MAX_MSGS"), out var tmm) && tmm > 0 ? tmm : 2000;
    public readonly int TailMergeTimeoutMs =
        int.TryParse(Environment.GetEnvironmentVariable("PARQUET_TAIL_TIMEOUT_MS"), out var tmt) && tmt > 0 ? tmt : 3000;

    // OIDC: "keycloak" | "none" (DISABLE_AUTH=true)
    public readonly string AuthProvider = Environment.GetEnvironmentVariable("AUTH_PROVIDER") ?? "keycloak";
    public readonly string? KeycloakAuthority = Environment.GetEnvironmentVariable("KEYCLOAK_AUTHORITY");
    public readonly string? KeycloakClientId = Environment.GetEnvironmentVariable("KEYCLOAK_CLIENT_ID");
    // Optional: override the token issuer ('iss') to validate against, independent of Authority.
    // Use when the API fetches OIDC metadata via an internal URL (e.g. building-os.keycloak:8080)
    // but tokens are issued with a public-facing URL (e.g. http://localhost:8080/realms/building-os).
    public readonly string? KeycloakValidIssuer = Environment.GetEnvironmentVariable("KEYCLOAK_VALID_ISSUER");
    public readonly string? KeycloakAdminClientId = Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_CLIENT_ID");
    public readonly string? KeycloakAdminClientSecret = Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_CLIENT_SECRET");
    public readonly string? KeycloakRealm = Environment.GetEnvironmentVariable("KEYCLOAK_REALM");
}
