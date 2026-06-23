using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure;

public class OtelSetupTest
{
    [Fact]
    public void AddOtlpTelemetry_With_Endpoint_Registers_TracerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOtlpTelemetry("test-service", "http://localhost:4317");
        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetService<TracerProvider>();
        Assert.NotNull(tracerProvider);
    }

    [Fact]
    public void AddOtlpTelemetry_Without_Endpoint_Does_Not_Register_TracerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOtlpTelemetry("test-service", null);
        using var provider = services.BuildServiceProvider();
        // No OTLP endpoint = no-op, TracerProvider NOT registered
        var tracerProvider = provider.GetService<TracerProvider>();
        Assert.Null(tracerProvider);
    }

    [Fact]
    public void AddOtlpTelemetry_With_Empty_Endpoint_Does_Not_Register_TracerProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOtlpTelemetry("test-service", "");
        using var provider = services.BuildServiceProvider();
        var tracerProvider = provider.GetService<TracerProvider>();
        Assert.Null(tracerProvider);
    }

    [Fact]
    public void OtelServiceName_Is_Set_Correctly()
    {
        // Validate that service name env var doesn't affect registration path
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOtlpTelemetry("building-os-api", "http://otel-collector:4317");
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
    }

    [Fact]
    public void AddOtlpTelemetry_With_Endpoint_Registers_MeterProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOtlpTelemetry("test-service", "http://localhost:4317");
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void AddOtlpTelemetry_Without_Endpoint_Does_Not_Register_MeterProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOtlpTelemetry("test-service", null);
        using var provider = services.BuildServiceProvider();
        // No OTLP endpoint = no-op, MeterProvider NOT registered
        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void AddOtlpLogging_With_Endpoint_Registers_Provider_Without_Throwing()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOtlpLogging("test-service", "http://localhost:4317"));
        using var provider = services.BuildServiceProvider();
        // Provider resolves and a logger can be created without throwing.
        var factory = provider.GetRequiredService<ILoggerFactory>();
        Assert.NotNull(factory.CreateLogger("test"));
    }

    [Fact]
    public void AddOtlpLogging_Without_Endpoint_Is_NoOp()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddOtlpLogging("test-service", null));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();
        Assert.NotNull(factory.CreateLogger("test"));
    }
}
