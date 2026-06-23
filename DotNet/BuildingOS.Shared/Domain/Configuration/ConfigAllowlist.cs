namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// Curated allowlist of API-server configuration keys surfaced by the effective-config view (#147).
/// Only these keys are ever read — anything else (including unknown secrets) is never exposed, so a
/// mis-named secret cannot leak. Keys flagged <c>IsSecret</c> report presence only (no value). Keys
/// mirror the API Server env table in CLAUDE.md; the <c>__</c> form is the display name.
/// </summary>
public static class ConfigAllowlist
{
    public static readonly IReadOnlyList<(string Key, bool IsSecret)> ApiServer = new[]
    {
        ("NATS_URL", false),
        ("POSTGRES_CONNECTION_STRING", true),
        ("KEYCLOAK_AUTHORITY", false),
        ("KEYCLOAK_CLIENT_ID", false),
        ("KEYCLOAK_REALM", false),
        ("KEYCLOAK_ADMIN_CLIENT_ID", false),
        ("KEYCLOAK_ADMIN_CLIENT_SECRET", true),
        ("DISABLE_AUTH", false),
        ("PROMETHEUS_URL", false),
        ("SYSTEM_STATUS_HEALTH_TARGETS", false),
        ("OTEL_EXPORTER_OTLP_ENDPOINT", false),
        ("OTEL_SERVICE_NAME", false),
        ("Logging__LogLevel__Default", false),
    };
}
