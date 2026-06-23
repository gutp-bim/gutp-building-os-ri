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
