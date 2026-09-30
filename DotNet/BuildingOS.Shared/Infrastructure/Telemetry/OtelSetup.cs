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

    /// <summary>
    /// Bucket boundaries (seconds) for every <see cref="BuildingOsMetrics"/> histogram whose unit is
    /// <c>s</c>. The SDK default (0, 5, 10, 25, 50, 75, 100, 250, …) is sized for milliseconds: an
    /// 80 ms lag lands in the (0, 5] bucket and <c>histogram_quantile</c> interpolates p95 to ~4.75 s.
    /// Spans 5 ms (sub-second consumer lag) to 2 h. <c>histogram_quantile</c> never returns more than the
    /// top finite bound, and the UI warns only when value &gt; threshold, so a warn threshold at or above
    /// 7200 s can never trigger — the top bucket must stay well above every threshold default (the
    /// Parquet freshness default is 600 s). Documented in
    /// docs/operations/observability-baseline.md; keep the two in step.
    /// </summary>
    public static readonly double[] SecondsHistogramBoundaries =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    /// <summary>Instruments that get <see cref="SecondsHistogramBoundaries"/> (all unit-"s" histograms).
    /// Millisecond histograms keep the SDK default, which already fits them.</summary>
    public static readonly IReadOnlyList<string> SecondsHistogramInstruments =
    [
        BuildingOsMetrics.IngestionLag.Name,
        BuildingOsMetrics.IngressEventLag.Name,
        BuildingOsMetrics.ParquetWriterFreshnessLag.Name,
    ];

    /// <summary>Registers the <see cref="SecondsHistogramBoundaries"/> view for every
    /// <see cref="SecondsHistogramInstruments"/> entry.</summary>
    public static MeterProviderBuilder AddBuildingOsHistogramViews(this MeterProviderBuilder builder)
    {
        foreach (var name in SecondsHistogramInstruments)
        {
            builder.AddView(name, new ExplicitBucketHistogramConfiguration
            {
                Boundaries = SecondsHistogramBoundaries,
            });
        }
        return builder;
    }

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
                    .AddBuildingOsHistogramViews()
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
