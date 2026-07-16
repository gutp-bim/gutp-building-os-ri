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
/// Unit tests for the batch-latest freshness endpoint (#182). The single-point <c>/query</c> path is
/// exercised via the integration tests; here we cover the batch fan-out, per-point authorization, and
/// input guards with mocked collaborators.
/// </summary>
public class TelemetryControllerTest
{
    private static ValidTelemetryData Sample(string pointId, string datetime, double value) =>
        new() { PointId = pointId, Datetime = datetime, Value = value };

    private static (TelemetryController controller, Mock<ITelemetryQueryRouter> router, Mock<IAuthorizationService> authz)
        Build(string role = "admin")
    {
        var twin = new Mock<IDigitalTwinDatabase>();
        var telemetryDb = new Mock<ITelemetryDatabase>();
        var router = new Mock<ITelemetryQueryRouter>();
        var authz = new Mock<IAuthorizationService>();

        // Default: no data for any point (tests override per point).
        router.Setup(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ValidTelemetryData>());

        var controller = new TelemetryController(twin.Object, telemetryDb.Object, router.Object, authz.Object)
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

    private static LatestSample[] Body(ActionResult<LatestSample[]> result) =>
        Assert.IsType<LatestSample[]>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task BatchLatest_ReturnsLatestPerPoint_ForAdmin()
    {
        var (controller, router, _) = Build();
        router.Setup(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p1" && q.Latest), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Sample("p1", "2026-07-15T00:00:00Z", 21.5) });

        var result = await controller.QueryBatchLatest(new BatchLatestRequest(["p1", "p2"]), CancellationToken.None);

        var body = Body(result);
        Assert.Equal(2, body.Length);
        var p1 = Assert.Single(body, s => s.PointId == "p1");
        Assert.Equal("2026-07-15T00:00:00Z", p1.Datetime);
        Assert.Equal(21.5, p1.Value);
        // p2 has no data → present but null.
        var p2 = Assert.Single(body, s => s.PointId == "p2");
        Assert.Null(p2.Datetime);
        Assert.Null(p2.Value);
    }

    [Fact]
    public async Task BatchLatest_DeduplicatesPointIds()
    {
        var (controller, router, _) = Build();
        router.Setup(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p1"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Sample("p1", "2026-07-15T00:00:00Z", 1) });

        var result = await controller.QueryBatchLatest(new BatchLatestRequest(["p1", "p1", "p1"]), CancellationToken.None);

        Assert.Single(Body(result));
    }

    [Fact]
    public async Task BatchLatest_OmitsPointsTheNonAdminCannotRead()
    {
        var (controller, router, authz) = Build(role: "operator");
        authz.Setup(a => a.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", "p1", "read", It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);
        authz.Setup(a => a.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", "p2", "read", It.IsAny<CancellationToken>()))
             .ReturnsAsync(false);
        router.Setup(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p1"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { Sample("p1", "2026-07-15T00:00:00Z", 1) });

        var result = await controller.QueryBatchLatest(new BatchLatestRequest(["p1", "p2"]), CancellationToken.None);

        var body = Body(result);
        Assert.Single(body);
        Assert.Equal("p1", body[0].PointId);
        // The inaccessible point's telemetry is never queried.
        router.Verify(r => r.QueryAsync(It.Is<TelemetryQueryRequest>(q => q.PointId == "p2"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BatchLatest_Returns400_WhenPointIdsEmpty()
    {
        var (controller, _, _) = Build();
        var result = await controller.QueryBatchLatest(new BatchLatestRequest([]), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BatchLatest_Returns400_WhenOverTheCap()
    {
        var (controller, _, _) = Build();
        var tooMany = Enumerable.Range(0, 501).Select(i => $"p{i}").ToArray();
        var result = await controller.QueryBatchLatest(new BatchLatestRequest(tooMany), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>
    /// The non-admin authorization path (<c>DefaultAuthorizationService → GroupMembershipResolver →
    /// GroupRepository</c>) reads the request-scoped EF <c>RelationalDbContext</c>, which is not
    /// thread-safe. The batch endpoint must therefore authorize points one at a time — never fan the
    /// authorization out concurrently over the shared context. This fake fails the moment two
    /// authorization calls overlap.
    /// </summary>
    [Fact]
    public async Task BatchLatest_AuthorizesSequentially_ForNonAdmin()
    {
        var twin = new Mock<IDigitalTwinDatabase>();
        var telemetryDb = new Mock<ITelemetryDatabase>();
        var router = new Mock<ITelemetryQueryRouter>();
        router.Setup(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ValidTelemetryData>());

        var authz = new ConcurrencyTrackingAuthorizationService();
        var controller = new TelemetryController(twin.Object, telemetryDb.Object, router.Object, authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = "operator", Permissions = [] } },
                },
            },
        };

        var ids = Enumerable.Range(0, 20).Select(i => $"p{i}").ToArray();
        await controller.QueryBatchLatest(new BatchLatestRequest(ids), CancellationToken.None);

        Assert.Equal(20, authz.CallCount);
        Assert.Equal(1, authz.MaxConcurrency);
    }

    /// <summary>An <see cref="IAuthorizationService"/> that records the peak number of overlapping
    /// <see cref="CanAccessAsync"/> calls, so a test can assert authorization is never concurrent.</summary>
    private sealed class ConcurrencyTrackingAuthorizationService : IAuthorizationService
    {
        private int _active;
        private readonly object _gate = new();
        public int MaxConcurrency { get; private set; }
        public int CallCount { get; private set; }

        public async Task<bool> CanAccessAsync(
            AuthorizationContext context, string resourceType, string resourceId, string action,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            lock (_gate)
            {
                CallCount++;
                if (active > MaxConcurrency) MaxConcurrency = active;
            }
            // Yield long enough that any concurrent callers would overlap here.
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref _active);
            return true;
        }

        public Task<IReadOnlyList<string>> GetAccessibleResourceIdsAsync(
            AuthorizationContext context, string resourceType, string action,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
