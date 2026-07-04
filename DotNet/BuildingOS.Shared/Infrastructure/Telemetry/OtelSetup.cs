using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

public static class OtelSetup
{
    /// <summary>
    /// Meter name emitted by <see cref="BuildingOsMetrics"/>. Registered on the metrics
    /// pipeline so custom application metrics are exported alongside the auto-instrumentation.
    /// </summary>
    public const string MeterName = "BuildingOS.Pipeline";

    private const string ServiceVersion = "1.0.0";

    private static ResourceBuilder BuildResource(string serviceName) =>
        ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: ServiceVersion);

    /// <summary>
    /// Registers OpenTelemetry tracing and metrics with OTLP export.
    /// No-op when otlpEndpoint is null or empty (Azure-only / disabled mode).
    /// <paramref name="sampleRatio"/> controls trace sampling (0.0–1.0, default 1.0 = AlwaysOn);
    /// read from OTEL_TRACES_SAMPLER_ARG in the host and pass it here.
    /// </summary>
    public static IServiceCollection AddOtlpTelemetry(
        this IServiceCollection services,
        string serviceName,
        string? otlpEndpoint,
        double sampleRatio = 1.0)
    {
        if (string.IsNullOrEmpty(otlpEndpoint))
            return services;

        var resource = BuildResource(serviceName);
        var endpoint = new Uri(otlpEndpoint);
        var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(Math.Clamp(sampleRatio, 0.0, 1.0)));

        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(resource)
                    .SetSampler(sampler)
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(opts => opts.Endpoint = endpoint);
            })
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(resource)
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(MeterName)
                    .AddOtlpExporter(opts => opts.Endpoint = endpoint);
            });

        return services;
    }

    /// <summary>
    /// Registers the OpenTelemetry logging provider with OTLP export so ILogger output
    /// is shipped to the collector (and on to Loki).
    /// No-op when otlpEndpoint is null or empty.
    ///
    /// Log verbosity is still governed by the standard Logging:LogLevel configuration
    /// (e.g. the Logging__LogLevel__* environment variables), so levels stay flexible.
    /// </summary>
    public static ILoggingBuilder AddOtlpLogging(
        this ILoggingBuilder logging,
        string serviceName,
        string? otlpEndpoint)
    {
        if (string.IsNullOrEmpty(otlpEndpoint))
            return logging;

        var resource = BuildResource(serviceName);
        var endpoint = new Uri(otlpEndpoint);

        logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.AddOtlpExporter(opts => opts.Endpoint = endpoint);
        });

        return logging;
    }
}
