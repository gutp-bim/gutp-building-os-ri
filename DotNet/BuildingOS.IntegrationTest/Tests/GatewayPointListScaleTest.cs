using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.GatewayPointListCache;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.GatewayProvisioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// Scale/perf coverage for the Gateway Point List read path (#259/#260/#261). The 10k case is a
/// regression guard with hard budgets; the 100k/250k cases and the concurrent-gateway case are
/// diagnostic only (Phase A of the point-list-projection plan) — they report a latency breakdown
/// (per-SPARQL-query time, serialization time, cold vs. warm) instead of asserting a threshold, so
/// they can characterize where the documented non-linear 10k→50k growth (170.5ms→2,745ms,
/// docs/reference/performance-evaluation-report.md §7) comes from before any read-model work is
/// designed around it.
/// </summary>
public class GatewayPointListScaleTest(
    OxiGraphFixture oxiGraph,
    ITestOutputHelper output)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const int BuildingCount = 10;
    private const int PointsPerBuilding = 1_000;

    public async Task InitializeAsync() => await oxiGraph.ClearAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public Task PointList_ReturnsOneGatewayFromTenThousandPointTwin_WithinFiveSeconds()
        => RunScaleScenarioAsync(
            buildingCount: BuildingCount,
            pointsPerBuilding: PointsPerBuilding,
            fullResponseBudget: TimeSpan.FromSeconds(5),
            notModifiedBudget: TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// Diagnostic only (no budget assertion): performance-evaluation-report.md §7 flags 100k as the
    /// next measurement point past the documented 10k→50k non-linear jump, and no 100k fixture existed
    /// anywhere in the repo before this test.
    /// </summary>
    [Fact]
    public Task PointList_AtOneHundredThousandPoints_ReportsLatencyBreakdown()
        => RunScaleScenarioAsync(
            buildingCount: 10,
            pointsPerBuilding: 10_000,
            fullResponseBudget: null,
            notModifiedBudget: null);

    /// <summary>
    /// Opt-in stretch tier (250k points). Not wired into any CI filter — the repo has no existing
    /// slow-test-exclusion convention to extend, so this stays out of the default
    /// `Category=Integration` run (see integration-tests.yml) by requiring an explicit env var rather
    /// than relying on fragile multi-value trait filter semantics. Run locally with:
    ///   BUILDINGOS_SCALE_STRETCH=1 dotnet test --filter FullyQualifiedName~PointList_AtTwoHundredFiftyThousandPoints
    /// </summary>
    [Fact]
    [Trait("Category", "Stretch")]
    public async Task PointList_AtTwoHundredFiftyThousandPoints_ReportsLatencyBreakdown()
    {
        if (Environment.GetEnvironmentVariable("BUILDINGOS_SCALE_STRETCH") != "1")
        {
            output.WriteLine(
                "Skipped: set BUILDINGOS_SCALE_STRETCH=1 to run the 250k-point stretch scenario.");
            return;
        }

        await RunScaleScenarioAsync(
            buildingCount: 10,
            pointsPerBuilding: 25_000,
            fullResponseBudget: null,
            notModifiedBudget: null);
    }

    /// <summary>
    /// 20 gateways polling one API process concurrently (the s17 50k-point/20-gateway sweep tier),
    /// reporting p50/p95/max instead of a single sequential stopwatch — the existing test only ever
    /// measured one gateway at a time.
    /// </summary>
    [Fact]
    public async Task PointList_TwentyConcurrentGateways_AtFiftyThousandPoints_ReportsPercentiles()
    {
        const int concurrentGateways = 20;
        const int pointsPerBuilding = 2_500;
        await oxiGraph.Client.ImportTurtleAsync(BuildDataset(concurrentGateways, pointsPerBuilding));

        var timingHandler = new QueryTimingHandler();
        var client = new OxiGraphClient(new HttpClient(timingHandler), oxiGraph.BaseUrl);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(client, cache);
        using var snapshotCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var snapshotStore = new MemoryGatewayPointListSnapshotStore(snapshotCache);
        var revisionCoordinator = new MemoryPointListRevisionCoordinator();

        // Each concurrent caller gets its own controller/HttpContext (ControllerContext is a mutable
        // instance property — sharing one controller across concurrent calls would race); the
        // OxiGraph client/database/snapshot store/revision coordinator are shared, matching how one
        // API replica actually serves many gateways.
        var gatewayIds = Enumerable.Range(0, concurrentGateways).Select(i => $"GW-SCALE-{i:D2}").ToArray();
        var overallStopwatch = Stopwatch.StartNew();
        var latenciesMs = await Task.WhenAll(gatewayIds.Select(async gatewayId =>
        {
            var controller = CreateController(database, snapshotStore, revisionCoordinator, gatewayId);
            var stopwatch = Stopwatch.StartNew();
            var result = await controller.GetPointList(gatewayId, since: null, CancellationToken.None);
            stopwatch.Stop();
            Assert.IsType<OkObjectResult>(result);
            return stopwatch.Elapsed.TotalMilliseconds;
        }));
        overallStopwatch.Stop();

        var sorted = latenciesMs.OrderBy(ms => ms).ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            buildings = concurrentGateways,
            pointsPerBuilding,
            totalPoints = concurrentGateways * pointsPerBuilding,
            concurrentGateways,
            wallClockMilliseconds = overallStopwatch.Elapsed.TotalMilliseconds,
            p50Milliseconds = Percentile(sorted, 0.50),
            p95Milliseconds = Percentile(sorted, 0.95),
            maxMilliseconds = sorted[^1],
            minMilliseconds = sorted[0],
        }));

        Assert.Equal(concurrentGateways, latenciesMs.Length);
    }

    /// <summary>
    /// Seeds the given scale, then measures cold (first-ever query against the freshly-seeded
    /// dataset) vs. warm (immediate repeat) full-response latency — each broken down per SPARQL query
    /// (<see cref="OxiGraphDigitalTwinDatabase.ListGatewayPointList"/> issues three: point-URI
    /// resolution, the VALUES-constrained attribute query, the VALUES-constrained device query) plus
    /// JSON serialization time measured separately — and the ETag-matched 304 path. Budgets are
    /// optional: pass null to report-only (diagnostic scales) rather than assert (the 10k regression
    /// guard).
    /// </summary>
    private async Task RunScaleScenarioAsync(
        int buildingCount,
        int pointsPerBuilding,
        TimeSpan? fullResponseBudget,
        TimeSpan? notModifiedBudget)
    {
        await oxiGraph.Client.ImportTurtleAsync(BuildDataset(buildingCount, pointsPerBuilding));

        var timingHandler = new QueryTimingHandler();
        var client = new OxiGraphClient(new HttpClient(timingHandler), oxiGraph.BaseUrl);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(client, cache);
        using var snapshotCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var snapshotStore = new MemoryGatewayPointListSnapshotStore(snapshotCache);
        var revisionCoordinator = new MemoryPointListRevisionCoordinator();
        const string gatewayId = "GW-SCALE-00";

        // ── Cold: first-ever query against this freshly-seeded dataset ──────────────────
        timingHandler.Reset();
        var coldController = CreateController(database, snapshotStore, revisionCoordinator, gatewayId);
        var coldStopwatch = Stopwatch.StartNew();
        var coldResult = await coldController.GetPointList(gatewayId, since: null, CancellationToken.None);
        coldStopwatch.Stop();
        var coldBody = Assert.IsType<GatewayPointListResponse>(Assert.IsType<OkObjectResult>(coldResult).Value);
        var serializeStopwatch = Stopwatch.StartNew();
        var serialized = JsonSerializer.SerializeToUtf8Bytes(coldBody);
        serializeStopwatch.Stop();
        var coldBuckets = timingHandler.BucketMillisecondsSnapshot();
        var coldOxiGraphMs = timingHandler.TotalElapsed.TotalMilliseconds;

        // ── Warm: immediate repeat, no conditional headers, against the now-warm dataset ─
        timingHandler.Reset();
        var warmController = CreateController(database, snapshotStore, revisionCoordinator, gatewayId);
        var warmStopwatch = Stopwatch.StartNew();
        var warmResult = await warmController.GetPointList(gatewayId, since: null, CancellationToken.None);
        warmStopwatch.Stop();
        Assert.IsType<GatewayPointListResponse>(Assert.IsType<OkObjectResult>(warmResult).Value);
        var warmBuckets = timingHandler.BucketMillisecondsSnapshot();
        var warmOxiGraphMs = timingHandler.TotalElapsed.TotalMilliseconds;

        // ── 304: conditional request against the cold ETag — must add zero OxiGraph queries ─
        timingHandler.Reset();
        var notModifiedController = CreateController(database, snapshotStore, revisionCoordinator, gatewayId);
        notModifiedController.ControllerContext.HttpContext.Request.Headers.IfNoneMatch = coldBody.Revision;
        var notModifiedStopwatch = Stopwatch.StartNew();
        var notModifiedResult = await notModifiedController.GetPointList(
            gatewayId, since: null, CancellationToken.None);
        notModifiedStopwatch.Stop();
        var notModifiedOxiGraphQueries = timingHandler.RequestCount;

        output.WriteLine(JsonSerializer.Serialize(new
        {
            buildings = buildingCount,
            pointsPerBuilding,
            totalPoints = buildingCount * pointsPerBuilding,
            gatewayPoints = coldBody.Points.Length,
            cold = new
            {
                oxiGraphQueryMilliseconds = coldOxiGraphMs,
                bucketMilliseconds = coldBuckets,
                apiResponseMilliseconds = coldStopwatch.Elapsed.TotalMilliseconds,
                serializationMilliseconds = serializeStopwatch.Elapsed.TotalMilliseconds,
                responseBytes = serialized.Length,
            },
            warm = new
            {
                oxiGraphQueryMilliseconds = warmOxiGraphMs,
                bucketMilliseconds = warmBuckets,
                apiResponseMilliseconds = warmStopwatch.Elapsed.TotalMilliseconds,
            },
            notModified = new
            {
                milliseconds = notModifiedStopwatch.Elapsed.TotalMilliseconds,
                oxiGraphQueries = notModifiedOxiGraphQueries,
            },
            budgetMilliseconds = fullResponseBudget?.TotalMilliseconds,
            notModifiedBudgetMilliseconds = notModifiedBudget?.TotalMilliseconds,
        }));

        Assert.Equal(pointsPerBuilding, coldBody.Points.Length);
        Assert.All(coldBody.Points, entry => Assert.StartsWith("SCALE-B00-", entry.PointId));
        Assert.Equal(
            StatusCodes.Status304NotModified,
            Assert.IsType<StatusCodeResult>(notModifiedResult).StatusCode);
        Assert.Equal(0, notModifiedOxiGraphQueries);

        if (fullResponseBudget is { } budget)
        {
            Assert.True(
                coldStopwatch.Elapsed < budget,
                $"Point List took {coldStopwatch.Elapsed.TotalSeconds:F3}s; budget is {budget.TotalSeconds:F1}s");
        }

        if (notModifiedBudget is { } nmBudget)
        {
            Assert.True(
                notModifiedStopwatch.Elapsed < nmBudget,
                $"304 response took {notModifiedStopwatch.Elapsed.TotalMilliseconds:F1}ms; " +
                $"budget is {nmBudget.TotalMilliseconds:F0}ms");
        }
    }

    private static GatewayProvisioningController CreateController(
        OxiGraphDigitalTwinDatabase database,
        IGatewayPointListSnapshotStore snapshotStore,
        IPointListRevisionCoordinator revisionCoordinator,
        string gatewayId)
    {
        // Always-miss cache: this scale test deliberately measures the raw live-Twin-query path
        // (Phase A of the point-list-projection plan), not the Phase B materialized cache.
        var controller = new GatewayProvisioningController(
            database,
            new HeaderGatewayIdentityResolver(),
            snapshotStore,
            revisionCoordinator,
            new AlwaysMissGatewayPointListCacheStore(),
            NullLogger<GatewayProvisioningController>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Gateway-Id"] = gatewayId;
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p * sortedAscending.Count) - 1;
        return sortedAscending[Math.Clamp(rank, 0, sortedAscending.Count - 1)];
    }

    /// <summary>Always-miss stand-in so this scale test keeps measuring the raw live-Twin path.</summary>
    private sealed class AlwaysMissGatewayPointListCacheStore : IGatewayPointListCacheStore
    {
        public Task<GatewayPointListCacheEntry?> GetAsync(string gatewayId, CancellationToken ct = default)
            => Task.FromResult<GatewayPointListCacheEntry?>(null);

        public Task UpsertAsync(
            string gatewayId, string etag, IReadOnlyList<GatewayPointEntry> entries, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteAsync(string gatewayId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// Wraps each OxiGraph HTTP round-trip and buckets it by which of
    /// <see cref="OxiGraphDigitalTwinDatabase.ListGatewayPointList"/>'s three SPARQL queries it is
    /// (point-URI resolution / VALUES-constrained attributes / VALUES-constrained devices), so a scale
    /// scenario can report a per-query time breakdown instead of only a single total.
    /// </summary>
    private sealed class QueryTimingHandler : DelegatingHandler
    {
        private readonly object _sync = new();
        private TimeSpan _totalElapsed;
        private int _requestCount;
        private readonly Dictionary<string, TimeSpan> _bucketElapsed = new(StringComparer.Ordinal);

        public QueryTimingHandler() : base(new HttpClientHandler()) { }

        public TimeSpan TotalElapsed
        {
            get { lock (_sync) return _totalElapsed; }
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        /// <summary>Clears all counters so a subsequent phase (cold/warm/304) measures itself only.</summary>
        public void Reset()
        {
            lock (_sync)
            {
                _totalElapsed = TimeSpan.Zero;
                _bucketElapsed.Clear();
            }
            Volatile.Write(ref _requestCount, 0);
        }

        public IReadOnlyDictionary<string, double> BucketMillisecondsSnapshot()
        {
            lock (_sync)
                return _bucketElapsed.ToDictionary(
                    kv => kv.Key, kv => kv.Value.TotalMilliseconds, StringComparer.Ordinal);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bucket = await ClassifyAsync(request, ct).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            Interlocked.Increment(ref _requestCount);
            try
            {
                return await base.SendAsync(request, ct);
            }
            finally
            {
                stopwatch.Stop();
                lock (_sync)
                {
                    _totalElapsed += stopwatch.Elapsed;
                    _bucketElapsed[bucket] = _bucketElapsed.TryGetValue(bucket, out var existing)
                        ? existing + stopwatch.Elapsed
                        : stopwatch.Elapsed;
                }
            }
        }

        /// <summary>
        /// Identifies which SPARQL query a request carries by matching the SELECT clause's variable
        /// list (the three queries in ListGatewayPointList each have a distinct one) rather than
        /// re-deriving the query text, so this stays correct if the WHERE clause is reshaped later.
        /// </summary>
        private static async Task<string> ClassifyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is null) return "other";
            var body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var decoded = System.Net.WebUtility.UrlDecode(body);
            if (decoded.Contains("SELECT ?pt ?ptId", StringComparison.Ordinal)) return "resolvePoints";
            if (decoded.Contains("SELECT ?pt ?prop ?value", StringComparison.Ordinal)) return "attributes";
            if (decoded.Contains("SELECT ?pt ?devDt ?devId ?devName", StringComparison.Ordinal)) return "devices";
            return "other";
        }
    }

    /// <summary>
    /// Builds the scale dataset with a real spatial chain per building (#300).
    ///
    /// This previously emitted <c>sbco:BuildingExt</c> — a class that does not exist in the ontology
    /// (<c>OxiGraphOntology.Cls_Building</c>), so the building was invisible to every building-scoped
    /// read with no error to notice — and never linked it to a Level, which made every point an
    /// orphan by <c>OxiGraphTwinAdminService.OrphanPattern</c>'s definition. The point-list path
    /// keyed off <c>gatewayId</c> and so passed regardless; anything treating these as buildings did not.
    /// </summary>
    private static string BuildDataset(int buildingCount, int pointsPerBuilding)
    {
        var ttl = new StringBuilder("@prefix sbco: <https://www.sbco.or.jp/ont/> .\n");
        for (var building = 0; building < buildingCount; building++)
        {
            var buildingId = $"SCALE-B{building:D2}";
            var gatewayId = $"GW-SCALE-{building:D2}";
            var floorId = $"{buildingId}-F1";
            var buildingUri = $"urn:scale:building:{building}";
            var floorUri = $"urn:scale:level:{building}";
            var roomUri = $"urn:scale:room:{building}";

            // Building →hasPart→ Level →hasPart→ Room, the chain the reachability check walks.
            ttl.Append($"<{buildingUri}> a sbco:Building ; sbco:id \"{buildingId}\" ; sbco:name \"Scale Building {building}\" ; sbco:hasPart <{floorUri}> .\n");
            ttl.Append($"<{floorUri}> a sbco:Level ; sbco:id \"{floorId}\" ; sbco:name \"{floorId}\" ; sbco:hasPart <{roomUri}> .\n");
            ttl.Append($"<{roomUri}> a sbco:Room ; sbco:id \"{buildingId}-R1\" ; sbco:name \"Scale Room {building}\" .\n");

            for (var point = 0; point < pointsPerBuilding; point++)
            {
                var pointId = $"{buildingId}-P{point:D5}";
                var pointUri = $"urn:scale:point:{building}:{point}";
                ttl.Append($"<{pointUri}> a sbco:PointExt ; sbco:id \"{pointId}\" ; sbco:name \"{pointId}\" ; sbco:building \"{buildingId}\" ; sbco:writable false ; sbco:gatewayId \"{gatewayId}\" .\n");
                ttl.Append($"<urn:scale:device:{building}:{point}> a sbco:EquipmentExt ; sbco:id \"DEV-{pointId}\" ; sbco:name \"Device {pointId}\" ; sbco:locatedIn <{roomUri}> ; sbco:floor \"{floorId}\" ; sbco:hasPoint <{pointUri}> .\n");
            }
        }
        return ttl.ToString();
    }
}
