using System.Net;
using System.Text;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Startup ordering against OxiGraph (#321).
///
/// The API performs the seed import and the gateway-uniqueness validation while the host is
/// starting. In Compose, OxiGraph is started alongside the API and has no healthcheck, so the API
/// can reach it before port 7878 accepts connections. `ValidateGatewayUniquenessAsync` issued its
/// query with no retry, so a connection refused propagated out of StartAsync and the process
/// exited — recoverable only by a manual sleep and a container recreate.
///
/// A transient connection failure at startup is an ordering artefact, not a configuration error,
/// and must be waited out rather than treated as fatal.
/// </summary>
public class OxiGraphSeedHostedServiceStartupTest
{
    private static readonly string EmptyResults = "{\"results\":{\"bindings\":[]}}";

    [Fact]
    public async Task RunAsync_OxiGraphRefusesConnectionThenAccepts_WaitsAndSucceeds()
    {
        // Refuse the first few attempts the way a listener that has not bound yet does.
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: 3);
        var svc = BuildService(handler);

        await svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default);

        Assert.True(handler.Attempts > 3, $"expected retries past the refusals, saw {handler.Attempts} attempts");
    }

    [Fact]
    public async Task RunAsync_OxiGraphNeverBecomesReady_ThrowsWithActionableMessage()
    {
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: int.MaxValue);
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMilliseconds(300));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default));

        // The operator needs to know which dependency was unreachable and that it was waited for,
        // not just "connection refused" from somewhere in startup.
        Assert.Contains("OxiGraph", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RetryBudgetIsBounded_DoesNotHangForever()
    {
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: int.MaxValue);
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMilliseconds(300));

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default));
        var elapsed = DateTimeOffset.UtcNow - started;

        // Generous ceiling: the point is that it terminates on its own, not that it is fast.
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"waited {elapsed} — retry budget is not bounded");
    }

    [Fact]
    public async Task RunAsync_CancelledDuringWait_StopsPromptly()
    {
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: int.MaxValue);
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // A shutdown signal during the wait must not be ignored until the whole budget elapses.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: cts.Token));
    }

    [Fact]
    public async Task RunAsync_NoSeedConfigured_DoesNotWaitForOxiGraph()
    {
        // Without a seed path there is nothing to ask OxiGraph, so an unavailable store must not
        // delay or fail startup — the API still serves requests that do not touch the twin.
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: int.MaxValue);
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMinutes(5));

        await svc.RunAsync(seedTtlPath: null, templatePath: null, ct: default);

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public async Task RunAsync_OxiGraphReadyImmediately_DoesNotDelayStartup()
    {
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: 0);
        var svc = BuildService(handler);

        var started = DateTimeOffset.UtcNow;
        await svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"healthy startup paid {elapsed} of retry delay");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // A path that does not exist: TrySeedAsync skips the import but the run still reaches the
    // gateway-uniqueness query, which is the call that was crashing.
    private static string MissingSeedPath() => Path.Combine(Path.GetTempPath(), "no_such_seed_321.ttl");

    private static OxiGraphSeedHostedService BuildService(
        HttpMessageHandler handler, TimeSpan? startupTimeout = null)
    {
        var http = new HttpClient(handler);
        var client = new OxiGraphClient(http, "http://building-os.oxigraph:7878");
        var materializer = new OxiGraphIngestMaterializer(client, NullLogger<OxiGraphIngestMaterializer>.Instance);
        return new OxiGraphSeedHostedService(
            client, materializer, NullLogger<OxiGraphSeedHostedService>.Instance,
            pointListUpdatePublisher: null,
            startupTimeout: startupTimeout ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Refuses the first N requests with the exception shape a closed port produces, then serves
    /// empty SPARQL results.
    /// </summary>
    private sealed class FlakyOxiGraphHandler(int refusalsBeforeReady) : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref _attempts);
            if (n <= refusalsBeforeReady)
            {
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException(
                        "Connection refused (building-os.oxigraph:7878)",
                        new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyResults, Encoding.UTF8, "application/sparql-results+json"),
            });
        }
    }
}
