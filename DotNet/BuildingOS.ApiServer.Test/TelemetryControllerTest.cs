using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Grouping.Entities;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOs.ApiServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Same guarantee as <see cref="BatchLatest_AuthorizesSequentially_ForNonAdmin"/>, but wired
    /// through the <b>real</b> authorization composition — <see cref="DefaultAuthorizationService"/> →
    /// <see cref="GroupMembershipResolver"/> → <see cref="IGroupRepository"/> — with only the DB
    /// boundary faked. A group permission forces the resolver to hit the repository for every point;
    /// the fake repository models the request-scoped EF <c>RelationalDbContext</c>'s contract by
    /// throwing the moment two calls overlap (as EF Core's concurrency detector would). So this fails
    /// if the endpoint ever fans the real authorization chain out over the shared context.
    ///
    /// The end-to-end variant over a real Postgres <c>RelationalDbContext</c> (Testcontainers) is
    /// tracked as a follow-up (#202).
    /// </summary>
    [Fact]
    public async Task BatchLatest_DrivesRealAuthorizationChainSequentially_ForNonAdmin()
    {
        var repo = new NonReentrantGroupRepository();
        var resolver = new GroupMembershipResolver(repo);
        var hierarchy = new Mock<IResourceHierarchyResolver>();
        hierarchy.Setup(h => h.GetAncestorsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Array.Empty<(string, string)>());
        var authz = new DefaultAuthorizationService(
            resolver, hierarchy.Object, new Mock<ILogger<DefaultAuthorizationService>>().Object);

        var router = new Mock<ITelemetryQueryRouter>();
        router.Setup(r => r.QueryAsync(It.IsAny<TelemetryQueryRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Array.Empty<ValidTelemetryData>());

        // A group permission means every point's read check must resolve group membership via the repo.
        var permission = PermissionHelper.BuildPermissionString("group", "hvac-team", "read");
        var controller = new TelemetryController(
            new Mock<IDigitalTwinDatabase>().Object, new Mock<ITelemetryDatabase>().Object, router.Object, authz)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = "u1", Role = "operator", Permissions = new[] { permission } } },
                },
            },
        };

        var ids = Enumerable.Range(0, 25).Select(i => $"p{i}").ToArray();
        var result = await controller.QueryBatchLatest(new BatchLatestRequest(ids), CancellationToken.None);

        // The group grants read on every point → all present, none dropped, and the shared-context
        // non-reentrancy contract was never tripped.
        Assert.Equal(25, Body(result).Length);
        Assert.True(repo.CallCount >= ids.Length,
            $"the real resolver should hit the repository at least once per point; got {repo.CallCount}");
        Assert.Equal(1, repo.MaxConcurrency);
    }

    /// <summary>
    /// An <see cref="IGroupRepository"/> that models the request-scoped EF <c>RelationalDbContext</c>'s
    /// thread-safety contract: it throws if a second read overlaps a first (as EF Core's concurrency
    /// detector does), and records peak concurrency. Only the two reverse-lookup reads the
    /// authorization path uses are implemented; the CRUD surface is unused here.
    /// </summary>
    private sealed class NonReentrantGroupRepository : IGroupRepository
    {
        private int _active;
        private readonly object _gate = new();
        public int MaxConcurrency { get; private set; }
        public int CallCount { get; private set; }

        public async Task<IReadOnlyList<string>> GetGroupIdsForResourceAsync(
            string resourceType, string resourceId, CancellationToken ct = default)
        {
            var active = Interlocked.Increment(ref _active);
            lock (_gate)
            {
                CallCount++;
                if (active > MaxConcurrency) MaxConcurrency = active;
            }
            try
            {
                if (active > 1)
                {
                    throw new InvalidOperationException(
                        "A second operation was started on this context instance before a previous " +
                        "operation completed. (simulated RelationalDbContext concurrency guard)");
                }
                await Task.Delay(15, ct).ConfigureAwait(false);
                return new[] { "hvac-team" };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<IReadOnlyList<string>> GetResourceIdsInGroupAsync(
            string groupId, string resourceType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        // CRUD surface — unused by the authorization read path.
        public Task<ResourceGroup?> GetByIdAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResourceGroup?> GetByIdWithItemsAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceGroup>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResourceGroup> CreateAsync(ResourceGroup group, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateAsync(ResourceGroup group, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GroupResourceItem> AddResourceItemAsync(string groupId, string resourceType, string resourceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveResourceItemAsync(string itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GroupResourceItem>> GetResourceItemsAsync(string groupId, CancellationToken ct = default) => throw new NotSupportedException();
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
