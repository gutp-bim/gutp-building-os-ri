using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Infrastructure.DeviceControlHandler;
using BuildingOS.ConnectorWorker.Startup;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.DeviceControlHandler;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// Phase 0 (#304): the connector-worker DI registration moved from Program.cs into capability-grouped
/// extension methods. These assert the same *registrations and gate conditions* the inline code had,
/// without building/resolving the live graph (no NATS/MinIO/OxiGraph contact).
/// </summary>
public class ConnectorWorkerServiceCollectionExtensionsTest
{
    private static HostApplicationBuilder NewBuilder(Dictionary<string, string?>? env = null)
    {
        // DisableDefaults so the machine's ambient environment variables are NOT loaded — these tests
        // pin gate conditions that depend on keys being absent (WARM_STORE / MINIO_ENDPOINT /
        // ENABLE_SIM_CONTROL / HONO_AMQP_HOST / MQTT_HOST), and a host that exports any of them would
        // otherwise flip a result. The in-memory collection is then the only configuration source.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(env ?? []);
        return builder;
    }

    private static int HostedServiceCount(IServiceCollection services)
        => services.Count(d => d.ServiceType == typeof(IHostedService));

    private static bool HasImpl<TService, TImpl>(IServiceCollection services)
        => services.Any(d => d.ServiceType == typeof(TService) && d.ImplementationType == typeof(TImpl));

    [Fact]
    public void Messaging_RegistersNatsAndPublisher()
    {
        var b = NewBuilder();
        b.AddConnectorWorkerMessaging();

        Assert.Contains(b.Services, d => d.ServiceType == typeof(INatsPublisher));
        Assert.Contains(b.Services, d => d.ServiceType == typeof(IHotTelemetryStore));
    }

    [Fact]
    public void Control_SimMode_RegistersSimulatedHandler_NotKandt()
    {
        var b = NewBuilder(new() { ["ENABLE_SIM_CONTROL"] = "true" });
        b.AddConnectorWorkerControl();

        Assert.True(HasImpl<IDeviceControlHandler, SimulatedDeviceControlHandler>(b.Services));
        Assert.False(HasImpl<IDeviceControlHandler, KandtDeviceControlHandler>(b.Services));
        Assert.Contains(b.Services, d => d.ServiceType == typeof(IGatewayConnectionRegistry));
    }

    [Fact]
    public void Control_NonSimMode_RegistersKandtHandler()
    {
        var b = NewBuilder();
        b.AddConnectorWorkerControl();

        Assert.True(HasImpl<IDeviceControlHandler, KandtDeviceControlHandler>(b.Services));
        Assert.False(HasImpl<IDeviceControlHandler, SimulatedDeviceControlHandler>(b.Services));
    }

    [Fact]
    public void Control_HonoHandler_OnlyWhenHonoHostSet()
    {
        var without = NewBuilder(new() { ["ENABLE_SIM_CONTROL"] = "true" });
        without.AddConnectorWorkerControl();
        Assert.False(HasImpl<IDeviceControlHandler, HonoDeviceControlHandler>(without.Services));

        var with = NewBuilder(new() { ["ENABLE_SIM_CONTROL"] = "true", ["HONO_AMQP_HOST"] = "hono.example" });
        with.AddConnectorWorkerControl();
        Assert.True(HasImpl<IDeviceControlHandler, HonoDeviceControlHandler>(with.Services));
    }

    [Fact]
    public void ProtocolConnectors_CoreAlways_MqttAndHonoGated()
    {
        var coreOnly = NewBuilder();
        coreOnly.AddConnectorWorkerMessaging(); // publisher dep present (registration-time only)
        coreOnly.AddProtocolConnectors();
        var coreCount = HostedServiceCount(coreOnly.Services);

        var withMqtt = NewBuilder(new() { ["MQTT_HOST"] = "mosquitto" });
        withMqtt.AddConnectorWorkerMessaging();
        withMqtt.AddProtocolConnectors();

        var withHono = NewBuilder(new() { ["HONO_AMQP_HOST"] = "hono.example" });
        withHono.AddConnectorWorkerMessaging();
        withHono.AddProtocolConnectors();

        // MQTT adds 2 hosted services (ingress + connector); Hono adds 2 (amqp ingress + connector).
        Assert.Equal(coreCount + 2, HostedServiceCount(withMqtt.Services));
        Assert.Equal(coreCount + 2, HostedServiceCount(withHono.Services));
    }

    [Fact]
    public void TelemetryIngress_RegistersOnlyWhenPortProvided()
    {
        var off = NewBuilder();
        off.AddTelemetryIngress(null);
        Assert.DoesNotContain(off.Services, d => d.ServiceType == typeof(IIngressTelemetryBus));

        var on = NewBuilder();
        on.AddTelemetryIngress(5051);
        Assert.Contains(on.Services, d => d.ServiceType == typeof(IIngressTelemetryBus));
        Assert.Contains(on.Services, d => d.ServiceType == typeof(IPointMetadataCache));
    }

    [Fact]
    public void ParquetLakeWriter_DefaultMode_RequiresMinioEndpoint()
    {
        // WARM_STORE unset → parquet (default since #216); MINIO_ENDPOINT missing must fail fast.
        var b = NewBuilder();
        Assert.Throws<InvalidOperationException>(() => b.AddParquetLakeWriter());
    }

    [Fact]
    public void ParquetLakeWriter_DefaultMode_WithMinio_RegistersWriter()
    {
        var b = NewBuilder(new() { ["MINIO_ENDPOINT"] = "http://localhost:9000" });
        b.AddParquetLakeWriter();
        Assert.Contains(b.Services, d => d.ServiceType == typeof(IParquetLakeWriter));
        Assert.True(HostedServiceCount(b.Services) >= 3); // writer + compaction + retention
    }

    [Fact]
    public void ParquetLakeWriter_TimescaleMode_DoesNothing()
    {
        var b = NewBuilder(new() { ["WARM_STORE"] = "timescale" });
        b.AddParquetLakeWriter(); // must not throw despite no MINIO_ENDPOINT
        Assert.DoesNotContain(b.Services, d => d.ServiceType == typeof(IParquetLakeWriter));
    }

    [Fact]
    public void ColdExport_ParquetMode_DoesNothing()
    {
        var b = NewBuilder(); // default parquet
        b.AddColdExportWorker();
        Assert.Equal(0, HostedServiceCount(b.Services));
    }
}
