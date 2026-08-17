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

    [Fact]
    public async Task RunAsync_TemplateConfiguredWithoutSeed_StillWaitsForOxiGraph()
    {
        // Device-template validation issues its own SPARQL (DeviceTemplateValidator), so the
        // template-only startup path hits the store just like the seed path does. It sat outside
        // the readiness wait, which left the original crash reachable whenever
        // OXIGRAPH_DEVICE_TEMPLATE_PATH was set without OXIGRAPH_SEED_TTL_PATH.
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: 3);
        var svc = BuildService(handler);

        await svc.RunAsync(seedTtlPath: null, templatePath: MissingTemplatePath(), ct: default);

        Assert.True(
            handler.Attempts > 3,
            $"template-only startup did not wait for OxiGraph (saw {handler.Attempts} attempts)");
    }

    [Fact]
    public async Task RunAsync_RequestTimesOut_IsTreatedAsTransientAndRetried()
    {
        // A store that is up but not yet answering times the request out rather than refusing it.
        // That is the same "still starting" condition and must be waited out too.
        var handler = new TimingOutOxiGraphHandler(timeoutsBeforeReady: 2);
        var svc = BuildService(handler);

        await svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default);

        Assert.True(
            handler.Attempts > 2,
            $"a request timeout was not retried (saw {handler.Attempts} attempts)");
    }

    [Fact]
    public async Task RunAsync_BudgetShorterThanTheFirstBackoff_StillProbesAgainBeforeGivingUp()
    {
        // Giving up as soon as the *next* backoff step would overshoot spends only part of the
        // configured timeout, so a store that binds late inside its own budget is failed anyway.
        // A budget below the first 100ms backoff makes that structural rather than wall-clock:
        // clamping the sleep to what is left leaves room for a second probe, while overshoot-based
        // bail-out gives up after the first — a difference no amount of machine load can blur.
        var handler = new FlakyOxiGraphHandler(refusalsBeforeReady: int.MaxValue);
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default));

        Assert.True(
            handler.Attempts >= 2,
            $"spent only {handler.Attempts} attempt(s): the budget was abandoned before it elapsed");
    }

    [Fact]
    public async Task RunAsync_StoreAnswersWithServerError_FailsFastWithoutRetrying()
    {
        // A store that answers at all is up; a 500 from it is a real problem, not the startup
        // race. QueryAsync surfaces that through EnsureSuccessStatusCode as HttpRequestException —
        // the same type a refused connection produces — so treating the type alone as transient
        // would retry a genuine fault for the whole budget and bury the diagnosis.
        var handler = new ErroringOxiGraphHandler(HttpStatusCode.InternalServerError);
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task RunAsync_StoreAcceptsThenStalls_DoesNotOutrunTheBudget()
    {
        // The deadline is only examined between attempts, so a probe that never returns is not
        // bounded by the budget at all — it runs until HttpClient's own ~100s timeout, whatever
        // OXIGRAPH_STARTUP_TIMEOUT_SEC says.
        var handler = new StallingOxiGraphHandler();
        var svc = BuildService(handler, startupTimeout: TimeSpan.FromMilliseconds(200));

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunAsync(seedTtlPath: MissingSeedPath(), templatePath: null, ct: default));
        var elapsed = DateTimeOffset.UtcNow - started;

        // Well under HttpClient's default: the ceiling is the budget plus one probe grace.
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"a stalled probe ran for {elapsed}");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // A path that does not exist: TrySeedAsync skips the import but the run still reaches the
    // gateway-uniqueness query, which is the call that was crashing. The name is randomised so the
    // "missing" precondition is guaranteed rather than assumed — a stray file of a fixed name in
    // the temp directory would otherwise be imported and quietly change what these tests exercise.
    private static string MissingSeedPath() => MissingTempPath("seed_321", ".ttl");

    // Likewise for the template path: validation skips a missing file, but the readiness wait must
    // already have happened by then — that is what this pins.
    private static string MissingTemplatePath() => MissingTempPath("templates_321", ".csv");

    private static string MissingTempPath(string prefix, string extension) =>
        Path.Combine(Path.GetTempPath(), $"no_such_{prefix}_{Guid.NewGuid():N}{extension}");

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

    /// <summary>
    /// Accepts the request and never answers, standing in for a store whose listener is bound but
    /// which is not yet serving.
    /// </summary>
    private sealed class StallingOxiGraphHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable: the delay above only ends by cancellation");
        }
    }

    /// <summary>
    /// Always answers with the given non-2xx status, standing in for a store that is up and
    /// reporting a fault.
    /// </summary>
    private sealed class ErroringOxiGraphHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
            });
        }
    }

    /// <summary>
    /// Times the first N requests out the way HttpClient's own timeout does, then serves empty
    /// SPARQL results.
    /// </summary>
    private sealed class TimingOutOxiGraphHandler(int timeoutsBeforeReady) : HttpMessageHandler
    {
        // HttpClient cancels an internal linked source when its Timeout elapses, so the
        // TaskCanceledException carries *that* source's token: never `default`, and never the
        // caller's. Reproduce that shape rather than a bare throw, or the test would pass against
        // a predicate that only recognises `default`.
        private readonly CancellationTokenSource _internalTimeout = CancelledSource();

        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        private static CancellationTokenSource CancelledSource()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            return cts;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var n = Interlocked.Increment(ref _attempts);
            if (n <= timeoutsBeforeReady)
            {
                return Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout elapsing.",
                        new TimeoutException(),
                        _internalTimeout.Token));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyResults, Encoding.UTF8, "application/sparql-results+json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _internalTimeout.Dispose();
            base.Dispose(disposing);
        }
    }
}
