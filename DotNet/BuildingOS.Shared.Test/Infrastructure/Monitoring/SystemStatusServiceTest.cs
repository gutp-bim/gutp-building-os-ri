using BuildingOS.Shared.Infrastructure.Monitoring;

namespace BuildingOS.Shared.Test.Infrastructure.Monitoring;

public class SystemStatusServiceTest
{
    [Fact]
    public async Task GetStatusAsync_IncludesSelfAndProbedServices()
    {
        var probe = new FakeHealthProbe(
            new ServiceStatus("nats", "up"),
            new ServiceStatus("oxigraph", "down"));
        var svc = new SystemStatusService(probe, new FakePrometheusClient { IsConfigured = true });

        var status = await svc.GetStatusAsync(CancellationToken.None);

        Assert.Equal("up", status.Services.Single(s => s.Name == SystemStatusService.SelfJob).Status);
        Assert.Equal("up", status.Services.Single(s => s.Name == "nats").Status);
        Assert.Equal("down", status.Services.Single(s => s.Name == "oxigraph").Status);
    }

    [Fact]
    public async Task GetStatusAsync_ServiceListIsLexicographicallySorted_WithSelfInPlace()
    {
        // Regression for the sort-invariant bug: self must be sorted into place, not forced first.
        var probe = new FakeHealthProbe(
            new ServiceStatus("zzz-service", "up"),
            new ServiceStatus("aaa-service", "up"));
        var svc = new SystemStatusService(probe, new FakePrometheusClient { IsConfigured = true });

        var status = await svc.GetStatusAsync(CancellationToken.None);

        var names = status.Services.Select(s => s.Name).ToList();
        var expected = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, names);
        // "aaa-service" < "building-os-api" < "zzz-service" → self is NOT first.
        Assert.Equal("aaa-service", names[0]);
    }

    [Fact]
    public async Task GetStatusAsync_PopulatesKpisFromScalars()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.MsgRate1mQuery] = 1240,
                [SystemStatusService.ControlReq5mQuery] = 3,
            }
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var status = await svc.GetStatusAsync(CancellationToken.None);

        Assert.True(status.MetricsAvailable);
        Assert.Equal(1240, status.Kpis.MsgRate1m);
        Assert.Equal(3, status.Kpis.ControlReq5m);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsServiceHealth_EvenWithoutPrometheus()
    {
        // The whole point of B-1: service up/down works without a metrics backend.
        var probe = new FakeHealthProbe(new ServiceStatus("nats", "up"));
        var svc = new SystemStatusService(probe, new FakePrometheusClient { IsConfigured = false });

        var status = await svc.GetStatusAsync(CancellationToken.None);

        Assert.False(status.MetricsAvailable);
        Assert.Null(status.Kpis.MsgRate1m);
        Assert.Equal("up", status.Services.Single(s => s.Name == SystemStatusService.SelfJob).Status);
        Assert.Equal("up", status.Services.Single(s => s.Name == "nats").Status);
    }

    [Fact]
    public async Task GetStatusAsync_DeduplicatesSelf_WhenAlsoProbed()
    {
        var probe = new FakeHealthProbe(new ServiceStatus(SystemStatusService.SelfJob, "down"));
        var svc = new SystemStatusService(probe, new FakePrometheusClient { IsConfigured = true });

        var status = await svc.GetStatusAsync(CancellationToken.None);

        var self = Assert.Single(status.Services, s => s.Name == SystemStatusService.SelfJob);
        Assert.Equal("up", self.Status); // self entry wins over a probed duplicate
    }
}


public class SystemStatusServicePipelineKpiTest
{
    private static PrometheusSample Sample(string label, string value, double v) =>
        new(new Dictionary<string, string> { [label] = value }, v);

    [Fact]
    public async Task PipelineKpis_AllPopulated_WhenPrometheusResponds()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.IngressRate1mQuery] = 1842,
                [SystemStatusService.MsgRate1mQuery] = 31,
                [SystemStatusService.ValidatedRate1mQuery] = 1831,
                [SystemStatusService.RejectedRate1mQuery] = 11,
                [SystemStatusService.EventLagP95Query] = 1.2,
                [SystemStatusService.ConsumerLagP95Query] = 0.082,
                [SystemStatusService.ParquetFreshnessP95Query] = 28,
                [SystemStatusService.ParquetDropped15mQuery] = 0,
                [SystemStatusService.NatsPendingQuery] = 124,
                [SystemStatusService.ControlReq5mQuery] = 37,
            },
            Vectors =
            {
                [SystemStatusService.IngressBySourceQuery] =
                [
                    Sample("source", "mqtt", 42),
                    Sample("source", "gateway-grpc", 1800),
                ],
                [SystemStatusService.RejectedByResultQuery] =
                [
                    Sample("result", "bad_payload", 3),
                    Sample("result", "unknown_point", 8),
                ],
            },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;

        Assert.Equal(1842, k.IngressRate1m);
        Assert.Equal(1831, k.ValidatedRate1m);
        Assert.Equal(31, k.MsgRate1m); // unchanged legacy field: connector-published only (also /home)
        Assert.Equal(11, k.RejectedRate1m);
        Assert.Equal(11d / 1842d * 100d, k.RejectedPercent!.Value, 6);
        Assert.Equal(1.2, k.EventLagP95Seconds);
        Assert.Equal(0.082, k.ConsumerLagP95Seconds);
        Assert.Equal(28, k.ParquetFreshnessP95Seconds);
        Assert.Equal(0, k.ParquetDropped15m);
        Assert.Equal(124, k.NatsPending);
        Assert.Equal(37, k.ControlReq5m);

        // Breakdowns are sorted by value (desc) so the tooltip leads with the dominant bucket.
        Assert.Equal(["gateway-grpc", "mqtt"], k.IngressBySource!.Select(b => b.Label));
        Assert.Equal(["unknown_point", "bad_payload"], k.RejectedByResult!.Select(b => b.Label));
        Assert.Equal(8, k.RejectedByResult![0].Value);
    }

    [Fact]
    public async Task PipelineKpis_AllNullAndEmpty_WhenPrometheusAbsent()
    {
        var svc = new SystemStatusService(new FakeHealthProbe(), new FakePrometheusClient { IsConfigured = false });

        var status = await svc.GetStatusAsync(CancellationToken.None);
        var k = status.Kpis;

        Assert.False(status.MetricsAvailable);
        Assert.Null(k.IngressRate1m);
        Assert.Null(k.ValidatedRate1m);
        Assert.Null(k.RejectedRate1m);
        Assert.Null(k.RejectedPercent);
        Assert.Null(k.EventLagP95Seconds);
        Assert.Null(k.ConsumerLagP95Seconds);
        Assert.Null(k.ParquetFreshnessP95Seconds);
        Assert.Null(k.ParquetDropped15m);
        Assert.Null(k.NatsPending);
        Assert.Null(k.ControlReq5m);
        Assert.Empty(k.IngressBySource!);
        Assert.Empty(k.RejectedByResult!);
    }

    [Fact]
    public async Task PipelineKpis_PartiallyMissing_DegradePerKpi()
    {
        // Ingress is flowing but nothing was ever rejected (no result!="published" series exist, so
        // the rejected query returns an empty vector), the lag histograms have no observations in the
        // window (histogram_quantile → NaN), and the NATS exporter is not wired (no recording rule).
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.IngressRate1mQuery] = 500,
                [SystemStatusService.ValidatedRate1mQuery] = 498,
                [SystemStatusService.EventLagP95Query] = double.NaN,
                [SystemStatusService.ConsumerLagP95Query] = double.PositiveInfinity,
            },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var status = await svc.GetStatusAsync(CancellationToken.None);
        var k = status.Kpis;

        Assert.True(status.MetricsAvailable);
        Assert.Equal(500, k.IngressRate1m);
        Assert.Equal(498, k.ValidatedRate1m);
        // No rejection series + live ingress = zero rejections, not "unknown".
        Assert.Equal(0, k.RejectedRate1m);
        Assert.Equal(0, k.RejectedPercent);
        // NaN / Inf never reach the wire (System.Text.Json cannot serialize them) — they mean "no data".
        Assert.Null(k.EventLagP95Seconds);
        Assert.Null(k.ConsumerLagP95Seconds);
        Assert.Null(k.ParquetFreshnessP95Seconds);
        Assert.Null(k.NatsPending);
        Assert.Empty(k.RejectedByResult!);
    }

    [Fact]
    public async Task RejectedRate_IsMeasuredDirectly_NotDerivedFromIngressMinusValidated()
    {
        // Under load validated trails ingress (queue backlog). That gap is NOT rejection.
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.IngressRate1mQuery] = 1000,
                [SystemStatusService.ValidatedRate1mQuery] = 400,
                [SystemStatusService.RejectedRate1mQuery] = 5,
            },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;

        Assert.Equal(5, k.RejectedRate1m);
        Assert.Equal(0.5, k.RejectedPercent!.Value, 6);
    }

    [Fact]
    public async Task RejectedPercent_IsNull_WhenThereIsNoIngressTraffic()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars = { [SystemStatusService.IngressRate1mQuery] = 0 },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;

        Assert.Equal(0, k.RejectedRate1m);
        Assert.Null(k.RejectedPercent);
    }

    [Fact]
    public void ValidatedQuery_SumsConnectorAndGatewayGrpcPublished_WithoutNullingWhenOneSideIsAbsent()
    {
        // gRPC GatewayIngress publishes straight to validated.telemetry (no connector), so a
        // connector-only Validated would read ≪ Ingress on a gRPC deployment and look like backlog.
        // `A + B` is empty when either side has no series, so the query falls back to each side alone;
        // with neither present it stays empty (→ null), like every other KPI.
        var connector = "sum(connector:messages_processed:rate1m)";
        var grpc = "sum(rate(building_os_ingress_messages_total{source=\"gateway-grpc\",result=\"published\"}[1m]))";
        Assert.Equal(SystemStatusService.ValidatedRate1mQuery, $"({connector} + {grpc}) or {connector} or {grpc}");
        Assert.DoesNotContain("vector(0)", SystemStatusService.ValidatedRate1mQuery);
        Assert.Contains(SystemStatusService.ValidatedRate1mQuery, SystemStatusService.AllKpiQueries);
    }

    [Fact]
    public async Task Validated_UsesCombinedQuery_AndLegacyMsgRateKeepsConnectorOnlyMeaning()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.IngressRate1mQuery] = 1000,
                [SystemStatusService.MsgRate1mQuery] = 40,          // connectors (MQTT/Hono)
                [SystemStatusService.ValidatedRate1mQuery] = 995,   // connectors + gateway-grpc published
            },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;

        Assert.Equal(995, k.ValidatedRate1m);
        Assert.Equal(40, k.MsgRate1m);
    }

    [Fact]
    public async Task Validated_IsNull_WhenNeitherSideHasData()
    {
        var fake = new FakePrometheusClient { IsConfigured = true, Scalars = { [SystemStatusService.MsgRate1mQuery] = 3 } };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;

        Assert.Null(k.ValidatedRate1m);
    }

    [Fact]
    public void RejectedQuery_AddsConnectorSkippedAndError_WithoutNullingWhenOneSideIsAbsent()
    {
        // MQTT/Hono messages the connector cannot resolve are counted by ingress as published (to
        // raw.*) and dropped later by ConnectorWorkerBase as result=skipped|error — still rejections.
        var ingress = "sum(rate(building_os_ingress_messages_total{result!=\"published\"}[1m]))";
        var connector = "sum(rate(building_os_connector_messages_processed_total{connector=~\"MqttConnectorWorker|HonoConnectorWorker\",result=~\"skipped|error\"}[1m]))";
        Assert.Equal(SystemStatusService.RejectedRate1mQuery, $"({ingress} + {connector}) or {ingress} or {connector}");
        Assert.Contains(SystemStatusService.ConnectorDroppedByResultQuery, SystemStatusService.AllKpiQueries);
    }

    [Fact]
    public async Task RejectedByResult_IncludesConnectorDrops_LabelledDistinctly()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.IngressRate1mQuery] = 200,
                [SystemStatusService.RejectedRate1mQuery] = 10,
            },
            Vectors =
            {
                [SystemStatusService.RejectedByResultQuery] = [Sample("result", "unknown_point", 3)],
                [SystemStatusService.ConnectorDroppedByResultQuery] =
                [
                    Sample("result", "skipped", 6),
                    Sample("result", "error", 1),
                ],
            },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);

        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;

        Assert.Equal(10, k.RejectedRate1m);
        Assert.Equal(5, k.RejectedPercent!.Value, 6); // still rejected ÷ ingress
        Assert.Equal(
            ["connector:skipped", "unknown_point", "connector:error"],
            k.RejectedByResult!.Select(b => b.Label));
        Assert.Equal([6d, 3d, 1d], k.RejectedByResult!.Select(b => b.Value));
    }

    [Fact]
    public void ConnectorDropFilter_NamesTheConnectorsBehindAnIngressTransport()
    {
        // ConnectorWorkerBase tags `connector` with GetType().Name. Only the MQTT / Hono connectors
        // consume what MqttIngressWorker / AmqpIngressWorker counted on the ingress denominator; the
        // hvac / bacnet / … connectors are fed straight from raw.* and must not inflate rejected %.
        Assert.Equal(
            $"connector=~\"{nameof(BuildingOS.ConnectorWorker.Connectors.MqttConnectorWorker)}|{nameof(BuildingOS.ConnectorWorker.Connectors.HonoConnectorWorker)}\"",
            SystemStatusService.IngressFedConnectorSelector);
        Assert.DoesNotContain("Hvac", SystemStatusService.RejectedRate1mQuery);
    }

    [Theory]
    [InlineData(600, "[900s]")]   // floor: 15 minutes
    [InlineData(1800, "[1800s]")] // otherwise the freshness warn threshold
    public void ParquetFlushesQuery_WindowIsMaxOfWarnThresholdAnd15m(double warnSeconds, string window)
    {
        var q = SystemStatusService.ParquetFlushesQuery(warnSeconds);
        Assert.Equal($"sum(increase(building_os_parquet_writer_freshness_lag_seconds_count{window}))", q);
    }

    private static FakePrometheusClient StallFake(double? flushes, double? validated) => new()
    {
        IsConfigured = true,
        Scalars =
        {
            [SystemStatusService.ParquetFlushesQuery(PipelineThresholdsDefaultFreshness)] = flushes,
            [SystemStatusService.ValidatedRate1mQuery] = validated,
        },
    };

    private const double PipelineThresholdsDefaultFreshness = 600;

    [Fact]
    public async Task ParquetFlushStalled_False_WhenFlushesHappen()
    {
        var svc = new SystemStatusService(new FakeHealthProbe(), StallFake(flushes: 3, validated: 100));
        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;
        Assert.False(k.ParquetFlushStalled);
    }

    [Fact]
    public async Task ParquetFlushStalled_True_WhenNoFlushInTheWindow_ButTelemetryIsValidated()
    {
        // The freshness histogram is only recorded on flush, so a stalled writer leaves the p95 NaN
        // (→ null). Without this flag that looked identical to "Prometheus not wired".
        var svc = new SystemStatusService(new FakeHealthProbe(), StallFake(flushes: 0, validated: 100));
        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;
        Assert.True(k.ParquetFlushStalled);
        Assert.Null(k.ParquetFreshnessP95Seconds);
    }

    [Fact]
    public async Task ParquetFlushStalled_False_WithoutTraffic()
    {
        var svc = new SystemStatusService(new FakeHealthProbe(), StallFake(flushes: 0, validated: 0));
        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;
        Assert.False(k.ParquetFlushStalled);
    }

    [Fact]
    public async Task ParquetFlushStalled_False_WhenTheWriterNeverFlushed()
    {
        // No _count series at all (timescale mode, or no writer): not a stall of this writer.
        var svc = new SystemStatusService(new FakeHealthProbe(), StallFake(flushes: null, validated: 100));
        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;
        Assert.False(k.ParquetFlushStalled);
    }

    [Fact]
    public async Task ParquetFlushStalled_Null_WhenPrometheusAbsent()
    {
        var svc = new SystemStatusService(new FakeHealthProbe(), new FakePrometheusClient { IsConfigured = false });
        var k = (await svc.GetStatusAsync(CancellationToken.None)).Kpis;
        Assert.Null(k.ParquetFlushStalled);
    }

    [Fact]
    public async Task ParquetFlushStalled_UsesTheConfiguredWarnThresholdAsWindow()
    {
        var fake = new FakePrometheusClient
        {
            IsConfigured = true,
            Scalars =
            {
                [SystemStatusService.ParquetFlushesQuery(3600)] = 0,
                [SystemStatusService.ValidatedRate1mQuery] = 50,
            },
        };
        var svc = new SystemStatusService(new FakeHealthProbe(), fake);
        var thresholds = BuildingOS.Shared.Domain.Configuration.PipelineKpiThresholds.Defaults with
        {
            ParquetFreshnessWarnSeconds = 3600,
        };

        var k = (await svc.GetStatusAsync(thresholds, CancellationToken.None)).Kpis;

        Assert.True(k.ParquetFlushStalled);
    }

    [Fact]
    public void Queries_UseTheOtelToPrometheusNames_AndExistingRecordingRules()
    {
        // OTLP→Prometheus: dots→underscores, counters gain _total, unit "s" histograms gain _seconds.
        Assert.Contains("building_os_ingress_messages_total", SystemStatusService.IngressRate1mQuery);
        Assert.Contains("result!=\"published\"", SystemStatusService.RejectedRate1mQuery);
        Assert.Contains("building_os_connector_messages_processed_total{connector=~\"MqttConnectorWorker|HonoConnectorWorker\",result=~\"skipped|error\"}", SystemStatusService.RejectedRate1mQuery);
        Assert.Contains("building_os_connector_messages_processed_total{connector=~\"MqttConnectorWorker|HonoConnectorWorker\",result=~\"skipped|error\"}", SystemStatusService.ConnectorDroppedByResultQuery);
        Assert.Contains("[1h]", SystemStatusService.ParquetFreshnessP95Query);
        Assert.Contains("connector:messages_processed:rate1m", SystemStatusService.ValidatedRate1mQuery);
        Assert.Contains("building_os_ingress_messages_total{source=\"gateway-grpc\",result=\"published\"}", SystemStatusService.ValidatedRate1mQuery);
        Assert.Contains("building_os_ingress_messages_total", SystemStatusService.RejectedRate1mQuery);
        Assert.Contains("building_os_ingress_event_lag_seconds_bucket", SystemStatusService.EventLagP95Query);
        Assert.Contains("building_os_ingestion_lag_seconds_bucket", SystemStatusService.ConsumerLagP95Query);
        Assert.Contains("building_os_parquet_writer_freshness_lag_seconds_bucket", SystemStatusService.ParquetFreshnessP95Query);
        Assert.Contains("building_os_parquet_writer_dropped_total", SystemStatusService.ParquetDropped15mQuery);
        Assert.Contains("building_os_control_requests_total", SystemStatusService.ControlReq5mQuery);
        Assert.Contains("nats:jetstream_consumer_pending:max", SystemStatusService.NatsPendingQuery);
        Assert.Contains("connector:messages_processed:rate1m", SystemStatusService.MsgRate1mQuery);

        // Cardinality policy: nothing the Platform UI asks for may aggregate by point/device.
        foreach (var q in SystemStatusService.AllKpiQueries)
        {
            Assert.DoesNotContain("point_id", q);
            Assert.DoesNotContain("device_id", q);
        }
    }
}

internal sealed class FakeHealthProbe : IServiceHealthProbe
{
    private readonly IReadOnlyList<ServiceStatus> _results;
    public FakeHealthProbe(params ServiceStatus[] results) => _results = results;
    public Task<IReadOnlyList<ServiceStatus>> ProbeAllAsync(CancellationToken ct)
        => Task.FromResult(_results);
}

internal sealed class FakePrometheusClient : IPrometheusQueryClient
{
    public bool IsConfigured { get; set; } = true;
    public Dictionary<string, double?> Scalars { get; } = new();
    public Dictionary<string, IReadOnlyList<PrometheusSample>> Vectors { get; } = new();

    public Task<double?> QueryScalarAsync(string query, CancellationToken ct)
        => Task.FromResult(Scalars.TryGetValue(query, out var v) ? v : null);

    public Task<IReadOnlyList<PrometheusSample>> QueryVectorAsync(string query, CancellationToken ct)
        => Task.FromResult(Vectors.TryGetValue(query, out var v) ? v : Array.Empty<PrometheusSample>());
}
