using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// #506: the <c>group-manager</c> role lets an external application (e.g. a tenant portal) keep
/// Building OS Groups in sync without being a full admin. To pick the resources a Group holds it reads
/// the twin's STRUCTURE — buildings, floors, rooms, equipment, the point list, search — in full. It
/// reads no VALUES: a single point (the gate for its control history), the per-building point ledger
/// behind data health, and every write stay where a non-admin without grants has them.
/// </summary>
public class AuthorizedTwinViewGroupManagerTest
{
    private const string Iri = "https://www.sbco.or.jp/ont/resource/";

    private static AuthorizationContext GroupManager() => new() { UserId = "svc-portal", Role = "group-manager", Permissions = [] };

    private readonly Mock<IDigitalTwinDatabase> _db = new();
    private readonly Mock<IAuthorizationService> _auth = new(MockBehavior.Strict);
    private AuthorizedTwinView View => new(_db.Object, _auth.Object);

    private void NoGrants()
    {
        _auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _auth.Setup(s => s.GetAccessibleResourceIdsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
    }

    // ── Structure: read in full, without asking the ACL ─────────────────────

    [Fact]
    public async Task ListsEveryBuilding()
    {
        _db.Setup(d => d.ListBuildings()).ReturnsAsync([new Building { DtId = Iri + "B1", Id = "B1" }, new Building { DtId = Iri + "B2", Id = "B2" }]);

        var result = await View.ListBuildingsAsync(GroupManager(), default);

        Assert.Equal(["B1", "B2"], result.Select(b => b.Id));
    }

    [Fact]
    public async Task ListsFloorsSpacesDevicesAndPoints_ScopedOrNot()
    {
        _db.Setup(d => d.ListFloors(It.IsAny<string>())).ReturnsAsync([new Floor { DtId = Iri + "F1", Id = "F1" }]);
        _db.Setup(d => d.ListSpaces(It.IsAny<string>())).ReturnsAsync([new Space { DtId = Iri + "S1", Id = "S1" }]);
        _db.Setup(d => d.ListDevices(It.IsAny<string>())).ReturnsAsync([new Device { DtId = Iri + "D1", Id = "D1" }]);
        _db.Setup(d => d.ListFloorDevices(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([new Device { DtId = Iri + "D2", Id = "D2" }]);
        _db.Setup(d => d.ListPoints(It.IsAny<string>())).ReturnsAsync([new Point { DtId = Iri + "pt-P1", Id = "P1" }]);
        var gm = GroupManager();

        Assert.Single(await View.ListFloorsAsync(gm, Iri + "B1", default));
        Assert.Single(await View.ListFloorsAsync(gm, "", default));
        Assert.Single(await View.ListSpacesAsync(gm, Iri + "F1", default));
        Assert.Single(await View.ListSpacesAsync(gm, "", default));
        Assert.Single(await View.ListDevicesAsync(gm, Iri + "S1", default));
        Assert.Single(await View.ListDevicesAsync(gm, "", default));
        Assert.Single(await View.ListFloorDevicesAsync(gm, Iri + "F1", default));
        Assert.Single(await View.ListPointsAsync(gm, Iri + "D1", default));
        Assert.Single(await View.ListPointsAsync(gm, "", default));
    }

    [Fact]
    public async Task GetsEachStructuralNode()
    {
        _db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(new Building { DtId = Iri + "B1", Id = "B1" });
        _db.Setup(d => d.GetFloor(Iri + "F1")).ReturnsAsync(new Floor { DtId = Iri + "F1", Id = "F1" });
        _db.Setup(d => d.GetSpace(Iri + "S1")).ReturnsAsync(new Space { DtId = Iri + "S1", Id = "S1" });
        _db.Setup(d => d.GetDevice(Iri + "D1")).ReturnsAsync(new Device { DtId = Iri + "D1", Id = "D1" });
        _db.Setup(d => d.ListAdjacentSpaces(Iri + "S1")).ReturnsAsync([new Space { DtId = Iri + "S2", Id = "S2" }]);
        var gm = GroupManager();

        Assert.IsType<TwinGetResult<Building>.Ok>(await View.GetBuildingAsync(gm, Iri + "B1", default));
        Assert.IsType<TwinGetResult<Floor>.Ok>(await View.GetFloorAsync(gm, Iri + "F1", default));
        Assert.IsType<TwinGetResult<Space>.Ok>(await View.GetSpaceAsync(gm, Iri + "S1", default));
        Assert.IsType<TwinGetResult<Device>.Ok>(await View.GetDeviceAsync(gm, Iri + "D1", default));
        var adjacent = Assert.IsType<TwinGetResult<Space[]>.Ok>(await View.ListAdjacentSpacesAsync(gm, Iri + "S1", default));
        Assert.Single(adjacent.Resource);
    }

    [Fact]
    public async Task SearchReturnsEveryHit()
    {
        _db.Setup(d => d.SearchResources(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([new ResourceSearchHit { Type = "space", Id = "S1", DtId = Iri + "S1" },
                           new ResourceSearchHit { Type = "point", Id = "P1", DtId = Iri + "pt-P1" }]);

        var hits = await View.SearchAsync(GroupManager(), "x", null, null, [], 50, 0, default);

        Assert.Equal(2, hits.Length);
    }

    /// <summary>
    /// #506 review: the structure is names and ids — not native addressing, gateways or thresholds,
    /// which the gateway point list guards with machine auth. Admins still get everything.
    /// </summary>
    [Fact]
    public async Task StructureIsNamesAndIdsOnly()
    {
        var point = new Point
        {
            DtId = Iri + "pt-P1", Id = "P1", Name = "Temp", LocalId = "L1", GatewayName = "GW1",
            DeviceIdBacnet = "1001", ObjectTypeBacnet = "AI", InstanceNoBacnet = 3, AlarmHigh = 30,
        };
        var device = new Device { DtId = Iri + "D1", Id = "D1", Name = "AHU", Owner = "Owner Co", GatewayId = "GW1" };
        _db.Setup(d => d.ListPoints(It.IsAny<string>())).ReturnsAsync([point]);
        _db.Setup(d => d.ListDevices(It.IsAny<string>())).ReturnsAsync([device]);
        _db.Setup(d => d.GetDevice(Iri + "D1")).ReturnsAsync(device);

        var p = Assert.Single(await View.ListPointsAsync(GroupManager(), "", default));
        Assert.Equal(("P1", "Temp", Iri + "pt-P1"), (p.Id, p.Name, p.DtId));
        Assert.Null(p.LocalId);
        Assert.Null(p.GatewayName);
        Assert.Null(p.DeviceIdBacnet);
        Assert.Null(p.InstanceNoBacnet);
        Assert.Null(p.AlarmHigh);
        var d = Assert.IsType<TwinGetResult<Device>.Ok>(await View.GetDeviceAsync(GroupManager(), Iri + "D1", default)).Resource;
        Assert.Null(d.Owner);
        Assert.Null(d.GatewayId);
        Assert.Null(Assert.Single(await View.ListDevicesAsync(GroupManager(), Iri + "S1", default)).Owner);

        var admin = new AuthorizationContext { UserId = "a", Role = "admin", Permissions = [] };
        Assert.Equal("L1", Assert.Single(await View.ListPointsAsync(admin, "", default)).LocalId);
    }

    /// <summary>Tags are hidden from a group-manager, so a tag filter must not answer for it either (an oracle).</summary>
    [Fact]
    public async Task SearchByTag_ReturnsNothing()
    {
        _db.Setup(d => d.SearchResources(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([new ResourceSearchHit { Type = "space", Id = "S1", DtId = Iri + "S1" }]);

        Assert.Empty(await View.SearchAsync(GroupManager(), null, null, null, ["vip-tenant"], 50, 0, default));
        _db.Verify(d => d.SearchResources(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    // ── Values and writes: exactly as a non-admin without grants ────────────

    /// <summary>
    /// A single point stays grant-gated: GET /points/{id}/control-audit authorizes on it, and the
    /// control history is a value, not structure.
    /// </summary>
    [Fact]
    public async Task SinglePointAndItsDetailStayGrantGated()
    {
        NoGrants();
        _db.Setup(d => d.GetPoint("P1")).ReturnsAsync(new Point { Id = "P1" });

        Assert.IsType<TwinGetResult<Point>.Forbidden>(await View.GetPointAsync(GroupManager(), "P1", default));
        Assert.IsType<TwinGetResult<PointDetail>.Forbidden>(await View.GetPointDetailAsync(GroupManager(), "P1", default));
    }

    /// <summary>The building's point ledger backs GET /telemetry/health (freshness) — values, not structure.</summary>
    [Fact]
    public async Task PointLedgerForDataHealth_IsNotOpened()
    {
        NoGrants();
        _db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(new Building { DtId = Iri + "B1", Id = "B1" });

        Assert.Empty(await View.ListPointDetailsAsync(GroupManager(), Iri + "B1", default));
        _db.Verify(d => d.ListPointDetails(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WritesAreNotGranted()
    {
        NoGrants();
        _db.Setup(d => d.GetPoint("P1")).ReturnsAsync(new Point { Id = "P1", Writable = true });
        _db.Setup(d => d.GetSpace(Iri + "S1")).ReturnsAsync(new Space { DtId = Iri + "S1", Id = "S1" });

        Assert.False(await View.CanWritePointAsync(GroupManager(), "P1", default));
        Assert.False(await View.CanWriteResourceAsync(GroupManager(), "point", "P1", default));
        Assert.False(await View.CanWriteResourceAsync(GroupManager(), "space", Iri + "S1", default));
    }
}
