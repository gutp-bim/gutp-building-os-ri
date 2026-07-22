using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.GatewayProvisioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace BuildingOS.IntegrationTest.Tests;

public class GatewayPointListScaleTest(
    OxiGraphFixture oxiGraph,
    ITestOutputHelper output)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const int BuildingCount = 10;
    private const int PointsPerBuilding = 1_000;

    public async Task InitializeAsync()
    {
        await oxiGraph.ClearAsync();
        await oxiGraph.Client.ImportTurtleAsync(BuildDataset());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PointList_ReturnsOneGatewayFromTenThousandPointTwin_WithinFiveSeconds()
    {
        var timingHandler = new QueryTimingHandler();
        var client = new OxiGraphClient(new HttpClient(timingHandler), oxiGraph.BaseUrl);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(client, cache);
        using var snapshotCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var controller = new GatewayProvisioningController(
            database,
            new HeaderGatewayIdentityResolver(),
            new MemoryGatewayPointListSnapshotStore(snapshotCache),
            new MemoryPointListRevisionCoordinator());
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Gateway-Id"] = "GW-SCALE-00";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var stopwatch = Stopwatch.StartNew();

        var result = await controller.GetPointList("GW-SCALE-00", since: null, CancellationToken.None);
        var response = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<GatewayPointListResponse>(response.Value);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(body);

        stopwatch.Stop();
        var queryCountAfterFullResponse = timingHandler.RequestCount;
        context.Request.Headers.IfNoneMatch = body.Revision;
        var notModifiedStopwatch = Stopwatch.StartNew();
        var notModifiedResult = await controller.GetPointList(
            "GW-SCALE-00", since: null, CancellationToken.None);
        notModifiedStopwatch.Stop();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            buildings = BuildingCount,
            totalPoints = BuildingCount * PointsPerBuilding,
            gatewayPoints = body.Points.Length,
            oxiGraphQueryMilliseconds = timingHandler.TotalElapsed.TotalMilliseconds,
            apiResponseMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            notModifiedMilliseconds = notModifiedStopwatch.Elapsed.TotalMilliseconds,
            notModifiedOxiGraphQueries = timingHandler.RequestCount - queryCountAfterFullResponse,
            responseBytes = serialized.Length,
            budgetMilliseconds = 5_000,
        }));
        Assert.Equal(PointsPerBuilding, body.Points.Length);
        Assert.All(body.Points, entry => Assert.StartsWith("SCALE-B00-", entry.PointId));
        Assert.Equal(
            StatusCodes.Status304NotModified,
            Assert.IsType<StatusCodeResult>(notModifiedResult).StatusCode);
        Assert.Equal(queryCountAfterFullResponse, timingHandler.RequestCount);
        Assert.True(
            notModifiedStopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"304 response took {notModifiedStopwatch.Elapsed.TotalMilliseconds:F1}ms; budget is 500ms");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Point List took {stopwatch.Elapsed.TotalSeconds:F3}s; budget is 5s");
    }

    private sealed class QueryTimingHandler : DelegatingHandler
    {
        private readonly object _sync = new();
        private TimeSpan _totalElapsed;
        private int _requestCount;

        public QueryTimingHandler() : base(new HttpClientHandler()) { }

        public TimeSpan TotalElapsed
        {
            get { lock (_sync) return _totalElapsed; }
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            Interlocked.Increment(ref _requestCount);
            try
            {
                return await base.SendAsync(request, ct);
            }
            finally
            {
                stopwatch.Stop();
                lock (_sync) _totalElapsed += stopwatch.Elapsed;
            }
        }
    }

    private static string BuildDataset()
    {
        var ttl = new StringBuilder("@prefix sbco: <https://www.sbco.or.jp/ont/> .\n");
        for (var building = 0; building < BuildingCount; building++)
        {
            var buildingId = $"SCALE-B{building:D2}";
            var gatewayId = $"GW-SCALE-{building:D2}";
            ttl.Append($"<urn:scale:building:{building}> a sbco:BuildingExt ; sbco:id \"{buildingId}\" ; sbco:name \"Scale Building {building}\" .\n");
            for (var point = 0; point < PointsPerBuilding; point++)
            {
                var pointId = $"{buildingId}-P{point:D5}";
                var pointUri = $"urn:scale:point:{building}:{point}";
                ttl.Append($"<{pointUri}> a sbco:PointExt ; sbco:id \"{pointId}\" ; sbco:name \"{pointId}\" ; sbco:building \"{buildingId}\" ; sbco:writable false ; sbco:gatewayId \"{gatewayId}\" .\n");
                ttl.Append($"<urn:scale:device:{building}:{point}> a sbco:EquipmentExt ; sbco:id \"DEV-{pointId}\" ; sbco:name \"Device {pointId}\" ; sbco:hasPoint <{pointUri}> .\n");
            }
        }
        return ttl.ToString();
    }
}
