using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOs.ApiServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

public class GatewayProvisioningControllerTest
{
    private static GatewayPointEntry Pt(string id, string? unit = "C") =>
        new() { PointId = id, Unit = unit, Writable = false };

    private static AuthorizationContext Admin() => new() { UserId = "ops", Role = "admin", Permissions = [] };

    private static IGatewayPointListSnapshotStore NewSnapshots() =>
        new MemoryGatewayPointListSnapshotStore(new MemoryCache(Options.Create(new MemoryCacheOptions())));

    private static (GatewayProvisioningController controller, DefaultHttpContext ctx) Build(
        GatewayPointEntry[] entries,
        string? callerGatewayHeader = null,
        AuthorizationContext? auth = null,
        string? ifNoneMatch = null,
        IGatewayPointListSnapshotStore? snapshots = null,
        string gatewayId = "GW001",
        Mock<IDigitalTwinDatabase>? db = null)
    {
        // #114: unless a shared `db` is supplied (multi-gateway tests below), the mock is scoped to
        // `gatewayId` (every single-gateway call site here targets "GW001", the default) and returns
        // empty for any *other* gatewayId, so a regression that drops/ignores the controller's
        // gatewayId argument (e.g. always querying a hardcoded id) would surface as an empty-points
        // assertion failure instead of silently passing every test.
        db ??= NewScopedDb(new Dictionary<string, GatewayPointEntry[]> { [gatewayId] = entries });

        var controller = new GatewayProvisioningController(
            db.Object, new HeaderGatewayIdentityResolver(), snapshots ?? NewSnapshots());

        var ctx = new DefaultHttpContext();
        if (callerGatewayHeader is not null) ctx.Request.Headers["X-Gateway-Id"] = callerGatewayHeader;
        if (ifNoneMatch is not null) ctx.Request.Headers.IfNoneMatch = ifNoneMatch;
        if (auth is not null) ctx.Items[AuthorizationContextMiddleware.HttpContextKey] = auth;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return (controller, ctx);
    }

    /// <summary>A db double keyed by gatewayId — unknown gatewayIds resolve to an empty list, never
    /// another gateway's entries.</summary>
    private static Mock<IDigitalTwinDatabase> NewScopedDb(IReadOnlyDictionary<string, GatewayPointEntry[]> byGatewayId)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListGatewayPointList(It.IsAny<string>()))
            .ReturnsAsync((string gw) => byGatewayId.TryGetValue(gw, out var e) ? e : []);
        return db;
    }

    // ── Full path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns200_WhenCallerGatewayMatchesPath()
    {
        var (c, ctx) = Build([Pt("PT001")], callerGatewayHeader: "GW001");
        var result = await c.GetPointList("GW001", null, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<GatewayPointListResponse>(ok.Value);
        Assert.Equal("GW001", body.GatewayId);
        Assert.Single(body.Points);
        Assert.False(string.IsNullOrEmpty(body.Revision));
        Assert.Equal(body.Revision, ctx.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Returns403_WhenCallerGatewayDiffersFromPath()
    {
        var (c, _) = Build([Pt("PT001")], callerGatewayHeader: "GW999");
        var result = await c.GetPointList("GW001", null, default);
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task Returns403_WhenNoGatewayHeaderAndNotAdmin()
    {
        var (c, _) = Build([Pt("PT001")]);
        var result = await c.GetPointList("GW001", null, default);
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task Returns200_ForAdminJwt_WithoutGatewayHeader()
    {
        var (c, _) = Build([Pt("PT001")], auth: Admin());
        var result = await c.GetPointList("GW001", null, default);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Returns304_WhenIfNoneMatchEqualsCurrentEtag()
    {
        var entries = new[] { Pt("PT001") };
        var etag = PointListEtag.Compute(entries);
        var (c, _) = Build(entries, callerGatewayHeader: "GW001", ifNoneMatch: etag);

        var result = await c.GetPointList("GW001", null, default);
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task Returns200WithEmptyPoints_WhenGatewayOwnsNothing()
    {
        var (c, _) = Build([], callerGatewayHeader: "GW001");
        var result = await c.GetPointList("GW001", null, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<GatewayPointListResponse>(ok.Value);
        Assert.Empty(body.Points);
    }

    [Fact]
    public async Task Returns400_WhenGatewayIdBlank()
    {
        var (c, _) = Build([Pt("PT001")], auth: Admin());
        var result = await c.GetPointList("  ", null, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Diff path (?since=) ────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_Returns304_WhenSinceEqualsCurrentEtag()
    {
        var entries = new[] { Pt("PT001") };
        var etag = PointListEtag.Compute(entries);
        var (c, _) = Build(entries, callerGatewayHeader: "GW001");

        var result = await c.GetPointList("GW001", since: etag, default);
        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task Diff_ReturnsDelta_WhenSnapshotResolvable()
    {
        var snapshots = NewSnapshots();
        var v1 = new[] { Pt("PT001"), Pt("PT002") };
        var etag1 = PointListEtag.Compute(v1);

        // First fetch (full) retains the v1 snapshot under etag1.
        var (c1, _) = Build(v1, callerGatewayHeader: "GW001", snapshots: snapshots);
        await c1.GetPointList("GW001", null, default);

        // v2: PT002 removed, PT003 added, PT001 changed.
        var v2 = new[] { Pt("PT001", unit: "F"), Pt("PT003") };
        var (c2, _) = Build(v2, callerGatewayHeader: "GW001", snapshots: snapshots);
        var result = await c2.GetPointList("GW001", since: etag1, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var diff = Assert.IsType<GatewayPointListDiffResponse>(ok.Value);
        Assert.False(diff.Full);
        Assert.Equal(["PT003"], diff.Added.Select(p => p.PointId));
        Assert.Equal(["PT002"], diff.Removed);
        Assert.Equal(["PT001"], diff.Changed.Select(p => p.PointId));
    }

    [Fact]
    public async Task Diff_FallsBackToFull_WhenSnapshotMissing()
    {
        var v2 = new[] { Pt("PT001"), Pt("PT003") };
        var (c, _) = Build(v2, callerGatewayHeader: "GW001"); // fresh store → unknown since

        var result = await c.GetPointList("GW001", since: "\"sha256:unknown\"", default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var diff = Assert.IsType<GatewayPointListDiffResponse>(ok.Value);
        Assert.True(diff.Full);
        Assert.Equal(2, diff.Points.Length);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    // ── Multi-gateway isolation (#114) ──────────────────────────────────────────

    [Fact]
    public async Task Returns200_TwoRegisteredGateways_EachSeesOnlyOwnPoints()
    {
        var db = NewScopedDb(new Dictionary<string, GatewayPointEntry[]>
        {
            ["GW001"] = [Pt("PT001")],
            ["GW002"] = [Pt("PT101"), Pt("PT102")],
        });
        var snapshots = NewSnapshots();
        var (c1, _) = Build([], callerGatewayHeader: "GW001", snapshots: snapshots, db: db);
        var (c2, _) = Build([], callerGatewayHeader: "GW002", snapshots: snapshots, db: db);

        var result1 = Assert.IsType<GatewayPointListResponse>(Assert.IsType<OkObjectResult>(await c1.GetPointList("GW001", null, default)).Value);
        var result2 = Assert.IsType<GatewayPointListResponse>(Assert.IsType<OkObjectResult>(await c2.GetPointList("GW002", null, default)).Value);

        Assert.Equal(["PT001"], result1.Points.Select(p => p.PointId));
        Assert.Equal(["PT101", "PT102"], result2.Points.Select(p => p.PointId));
        Assert.NotEqual(result1.Revision, result2.Revision);
    }

    [Fact]
    public async Task Returns403_WhenLegitGatewayRequestsAnotherLegitGatewaysPath()
    {
        // Both GW001 and GW002 are real, registered gateways with their own points — the auth check
        // must still reject GW002 reading GW001's path (not just the bogus-caller case already covered
        // by Returns403_WhenCallerGatewayDiffersFromPath).
        var db = NewScopedDb(new Dictionary<string, GatewayPointEntry[]>
        {
            ["GW001"] = [Pt("PT001")],
            ["GW002"] = [Pt("PT101")],
        });
        var (controller, _) = Build([], callerGatewayHeader: "GW002", db: db);

        var result = await controller.GetPointList("GW001", null, default);

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }
}
