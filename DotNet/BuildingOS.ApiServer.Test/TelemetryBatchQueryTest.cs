using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// <c>POST /api/v1/telemetries/query/batch</c> (#510): the multi-point counterpart of
/// <c>GET /telemetries/query</c> for a time range. Authorization mirrors batch-latest — each point is
/// checked on its own and an unreadable one is left out rather than failing the batch with 403.
/// </summary>
public class TelemetryBatchQueryTest
{
    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    private static ValidTelemetryData Sample(string pointId, string datetime, double value) =>
        new() { PointId = pointId, Datetime = datetime, Value = value };

    private static (TelemetryController controller, Mock<ITelemetryQueryRouter> router, Mock<IAuthorizationService> authz)
        Build(string role = "admin")
    {
        var router = new Mock<ITelemetryQueryRouter>();
        var authz = new Mock<IAuthorizationService>();
        router.Setup(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ValidTelemetryData>());

        var controller = new TelemetryController(
            new Mock<IDigitalTwinDatabase>().Object, new Mock<ITelemetryDatabase>().Object, router.Object, authz.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = role, Permissions = [] } },
                },
            },
        };
        return (controller, router, authz);
    }

    private static TelemetrySeries[] Body(ActionResult<TelemetrySeries[]> result) =>
        Assert.IsType<TelemetrySeries[]>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static BatchQueryRequest Req(string[] ids, TelemetryGranularity g = TelemetryGranularity.Hour,
        DateTime? start = null, DateTime? end = null) =>
        new(ids, start ?? Start, end ?? End, g);

    [Fact]
    public async Task ReturnsOneSeriesPerPoint_InRequestOrder_WithTheRequestedRangeAndGranularity()
    {
        var (controller, router, _) = Build();
        router.Setup(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p1"), It.IsAny<CancellationToken>()))
              .ReturnsAsync([Sample("p1", "2026-09-01T00:00:00Z", 1), Sample("p1", "2026-09-01T01:00:00Z", 2)]);
        router.Setup(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p2"), It.IsAny<CancellationToken>()))
              .ReturnsAsync([Sample("p2", "2026-09-01T00:00:00Z", 5)]);

        var body = Body(await controller.QueryBatch(Req(["p2", "p1", "p3"]), CancellationToken.None));

        Assert.Equal(["p2", "p1", "p3"], body.Select(s => s.PointId));
        Assert.Single(body[0].Readings);
        Assert.Equal(2, body[1].Readings.Length);
        Assert.Equal(5.0, body[0].Readings[0].Value);
        Assert.Empty(body[2].Readings); // no data (or unknown point) → empty, same as no readings
        router.Verify(r => r.QueryAsync(
            It.Is<TelemetryQueryRequest>(q => q.Start == Start && q.End == End
                && q.Granularity == TelemetryGranularity.Hour && !q.Latest),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.Equal("max-age=60", controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task DeduplicatesAndIgnoresBlankPointIds()
    {
        var (controller, router, _) = Build();

        var body = Body(await controller.QueryBatch(Req(["p1", "", "p1"]), CancellationToken.None));

        Assert.Equal("p1", Assert.Single(body).PointId);
        router.Verify(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OmitsPointsTheNonAdminCannotRead_AndNeverQueriesThem()
    {
        var (controller, router, authz) = Build(role: "operator");
        authz.Setup(a => a.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", "p1", "read", It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);
        authz.Setup(a => a.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", "p2", "read", It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);

        var body = Body(await controller.QueryBatch(Req(["p1", "p2"]), CancellationToken.None));

        Assert.Equal("p1", Assert.Single(body).PointId);
        router.Verify(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p2"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MarksAPartialResult_WhenAnyPointsReadWasTruncated()
    {
        var (controller, router, _) = Build();
        var coveredFrom = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        router.Setup(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p2"), It.IsAny<CancellationToken>()))
              .Returns(() =>
              {
                  TelemetryQueryCompleteness.ReportTruncated(coveredFrom);
                  return Task.FromResult(Array.Empty<ValidTelemetryData>());
              });

        await controller.QueryBatch(Req(["p1", "p2"]), CancellationToken.None);

        Assert.Equal("true", controller.Response.Headers["X-Partial-Result"].ToString());
        Assert.Equal("2026-09-01T12:00:00.0000000Z", controller.Response.Headers["X-Covered-From"].ToString());
    }

    [Fact]
    public async Task BoundsTheTelemetryFanOut()
    {
        var (controller, router, _) = Build();
        var active = 0;
        var max = 0;
        var gate = new object();
        router.Setup(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
              .Returns(async () =>
              {
                  lock (gate) { active++; max = Math.Max(max, active); }
                  await Task.Delay(10);
                  lock (gate) active--;
                  return Array.Empty<ValidTelemetryData>();
              });

        var ids = Enumerable.Range(0, 40).Select(i => $"p{i}").ToArray();
        var body = Body(await controller.QueryBatch(Req(ids), CancellationToken.None));

        Assert.Equal(40, body.Length);
        Assert.InRange(max, 1, TelemetryController.MaxBatchQueryConcurrency);
    }

    public static TheoryData<string[], DateTime?, DateTime?, TelemetryGranularity> BadRequests() => new()
    {
        { [], Start, End, TelemetryGranularity.Hour },                                     // no points
        { ["p1"], null, End, TelemetryGranularity.Hour },                                  // start missing
        { ["p1"], Start, null, TelemetryGranularity.Hour },                                // end missing
        { ["p1"], End, Start, TelemetryGranularity.Hour },                                 // end < start
        { Enumerable.Range(0, 501).Select(i => $"p{i}").ToArray(), Start, End, TelemetryGranularity.Hour }, // over the point cap
        // 500 points × 1 year of hourly buckets = 4.38M buckets — over the per-request budget
        { Enumerable.Range(0, 500).Select(i => $"p{i}").ToArray(), Start, Start.AddDays(365), TelemetryGranularity.Hour },
        // raw: 100 points × 31 days is far more point-time than a raw batch may scan
        { Enumerable.Range(0, 100).Select(i => $"p{i}").ToArray(), Start, Start.AddDays(31), TelemetryGranularity.Raw },
        // a numeric enum value the API does not define must not slip past both budgets
        { ["p1"], Start, Start.AddYears(5), (TelemetryGranularity)3 },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Returns400_ForInvalidOrOversizedRequests(string[] ids, DateTime? start, DateTime? end, TelemetryGranularity g)
    {
        var (controller, router, _) = Build();

        var result = await controller.QueryBatch(new BatchQueryRequest(ids, start, end, g), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        router.Verify(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    // a tenant portal's month of hourly data for 100 meters
    [InlineData(100, 31, TelemetryGranularity.Hour)]
    // a year of daily data for every point a batch may carry
    [InlineData(500, 365, TelemetryGranularity.Day)]
    // a day of raw data for 100 points
    [InlineData(100, 1, TelemetryGranularity.Raw)]
    public async Task AcceptsTheSizesTheEnergyViewsActuallyAskFor(int points, int days, TelemetryGranularity g)
    {
        var (controller, _, _) = Build();
        var ids = Enumerable.Range(0, points).Select(i => $"p{i}").ToArray();

        var result = await controller.QueryBatch(Req(ids, g, Start, Start.AddDays(days)), CancellationToken.None);

        Assert.Equal(points, Body(result).Length);
    }

    /// <summary>
    /// Same reason as batch-latest: the non-admin authorization chain reads the request-scoped EF
    /// context, which is not thread-safe, so points are authorized one at a time.
    /// </summary>
    [Fact]
    public async Task AuthorizesSequentially_ForNonAdmin()
    {
        var (controller, _, authz) = Build(role: "operator");
        var active = 0;
        var max = 0;
        authz.Setup(a => a.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", It.IsAny<string>(), "read", It.IsAny<CancellationToken>()))
             .Returns(async () =>
             {
                 max = Math.Max(max, Interlocked.Increment(ref active));
                 await Task.Delay(1);
                 Interlocked.Decrement(ref active);
                 return true;
             });

        await controller.QueryBatch(Req(Enumerable.Range(0, 10).Select(i => $"p{i}").ToArray()), CancellationToken.None);

        Assert.Equal(1, max);
    }

    public interface IMultiRouter : ITelemetryQueryRouter, IMultiPointTelemetryQueryRouter { }

    /// <summary>
    /// Raw history for many points goes through one multi-point read where the router supports it,
    /// so the lake objects are listed and decoded once rather than once per point (Codex on #510).
    /// </summary>
    [Fact]
    public async Task Raw_UsesTheRoutersMultiPointRead_Once()
    {
        var router = new Mock<IMultiRouter>();
        router.Setup(r => r.QueryRawMultiAsync(It.IsAny<string[]>(), Start, End, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Dictionary<string, ValidTelemetryData[]>
              {
                  ["p1"] = [Sample("p1", "2026-09-01T00:00:00Z", 1)],
                  ["p2"] = [],
              });
        var controller = new TelemetryController(
            new Mock<IDigitalTwinDatabase>().Object, new Mock<ITelemetryDatabase>().Object, router.Object,
            new Mock<IAuthorizationService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = "admin", Permissions = [] } },
                },
            },
        };

        var body = Body(await controller.QueryBatch(Req(["p2", "p1"], TelemetryGranularity.Raw), CancellationToken.None));

        Assert.Equal(["p2", "p1"], body.Select(b => b.PointId));
        Assert.Empty(body[0].Readings);
        Assert.Single(body[1].Readings);
        router.Verify(r => r.QueryRawMultiAsync(It.IsAny<string[]>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        router.Verify(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
