using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// #504: the hierarchy reads must authorize by the node's <b>business id</b> (<c>sbco:id</c>), the id
/// space the rest of authorization uses — Group items, the telemetry check and the ancestor chain
/// (<c>OxiGraphHierarchyResolver</c> matches <c>sbco:id</c> literals). Matching the dtId (IRI) instead
/// meant a user granted <c>space:R501</c> could read R501's telemetry yet saw nothing in the tree.
/// A grant recorded against the dtId keeps working during migration.
/// </summary>
public class AuthorizedTwinViewBusinessIdTest
{
    private const string Iri = "https://www.sbco.or.jp/ont/resource/";

    private static AuthorizationContext UserAuth() => new() { UserId = "u-office-a", Role = "viewer", Permissions = [] };

    private static Building B(string id) => new() { DtId = Iri + id, Id = id, Name = id };
    private static Floor F(string id) => new() { DtId = Iri + id, Id = id, Name = id };
    private static Space S(string id) => new() { DtId = Iri + id, Id = id, Name = id };
    private static Device D(string id) => new() { DtId = Iri + id, Id = id, Name = id };
    private static Point P(string id) => new() { DtId = Iri + "pt-" + id, Id = id, Name = id };

    private sealed class Harness
    {
        public readonly Mock<IDigitalTwinDatabase> Db = new();
        public readonly Mock<IAuthorizationService> Auth = new();
        public AuthorizedTwinView View => new(Db.Object, Auth.Object);

        public Harness()
        {
            Auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            Auth.Setup(s => s.GetAccessibleResourceIdsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());
        }

        /// <summary>CanAccessAsync(type, id, read) → true for exactly this id (and whatever else is granted).</summary>
        public void CanRead(string type, string id)
            => Auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), type, id, "read",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        public void Accessible(string type, params string[] rawIds)
            => Auth.Setup(s => s.GetAccessibleResourceIdsAsync(It.IsAny<AuthorizationContext>(), type, "read",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(rawIds.Select(PermissionHelper.HashResourceId).ToArray());
    }

    // ── List filters: match the item by business id ─────────────────────────

    [Fact]
    public async Task ListBuildings_MatchesAGrantOnTheBusinessId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.ListBuildings()).ReturnsAsync([B("B1"), B("B2")]);
        h.Accessible("building", "B1");

        var result = await h.View.ListBuildingsAsync(UserAuth(), default);

        Assert.Equal(["B1"], result.Select(b => b.Id));
    }

    [Fact]
    public async Task ListBuildings_StillMatchesALegacyGrantOnTheDtId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.ListBuildings()).ReturnsAsync([B("B1"), B("B2")]);
        h.Accessible("building", Iri + "B2");

        var result = await h.View.ListBuildingsAsync(UserAuth(), default);

        Assert.Equal(["B2"], result.Select(b => b.Id));
    }

    [Fact]
    public async Task ListFloors_ChildGrantOnTheBusinessId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(B("B1"));
        h.Db.Setup(d => d.ListFloors(Iri + "B1")).ReturnsAsync([F("F5"), F("F6")]);
        h.Accessible("floor", "F5");

        var result = await h.View.ListFloorsAsync(UserAuth(), Iri + "B1", default);

        Assert.Equal(["F5"], result.Select(f => f.Id));
    }

    [Fact]
    public async Task ListDevices_ChildGrantOnTheBusinessId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Db.Setup(d => d.ListDevices(Iri + "R501")).ReturnsAsync([D("AHU-1"), D("AHU-2")]);
        h.Accessible("device", "AHU-2");

        var result = await h.View.ListDevicesAsync(UserAuth(), Iri + "R501", default);

        Assert.Equal(["AHU-2"], result.Select(d => d.Id));
    }

    // ── Parent scope: authorize the parent by its business id ────────────────

    [Fact]
    public async Task ListFloors_BuildingGrant_OnTheBusinessId_ReturnsAll()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(B("B1"));
        h.Db.Setup(d => d.ListFloors(Iri + "B1")).ReturnsAsync([F("F5"), F("F6")]);
        h.CanRead("building", "B1");

        var result = await h.View.ListFloorsAsync(UserAuth(), Iri + "B1", default);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task ListSpaces_FloorReachable_ThroughTheBusinessIdAncestorChain()
    {
        // A building grant reaches the floor only via CanAccessAsync's ancestor chain, which resolves
        // sbco:id literals — so the floor must be asked about by business id.
        var h = new Harness();
        h.Db.Setup(d => d.GetFloor(Iri + "F5")).ReturnsAsync(F("F5"));
        h.Db.Setup(d => d.ListSpaces(Iri + "F5")).ReturnsAsync([S("R501"), S("R502")]);
        h.CanRead("floor", "F5");

        var result = await h.View.ListSpacesAsync(UserAuth(), Iri + "F5", default);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task ListDevices_SpaceGrant_OnTheBusinessId_ReturnsAll()
    {
        // The #504 contract test: u-office-a holds space:R501 and must be able to walk under it.
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Db.Setup(d => d.ListDevices(Iri + "R501")).ReturnsAsync([D("AHU-1")]);
        h.CanRead("space", "R501");

        var result = await h.View.ListDevicesAsync(UserAuth(), Iri + "R501", default);

        Assert.Equal(["AHU-1"], result.Select(d => d.Id));
    }

    [Fact]
    public async Task ListPoints_DeviceGrant_OnTheBusinessId_ReturnsAll()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetDevice(Iri + "AHU-1")).ReturnsAsync(D("AHU-1"));
        h.Db.Setup(d => d.ListPoints(Iri + "AHU-1")).ReturnsAsync([P("PT1"), P("PT2")]);
        h.CanRead("device", "AHU-1");

        var result = await h.View.ListPointsAsync(UserAuth(), Iri + "AHU-1", default);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task ListDevices_ParentGrant_StillMatchesALegacyDtIdGrant()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Db.Setup(d => d.ListDevices(Iri + "R501")).ReturnsAsync([D("AHU-1")]);
        h.CanRead("space", Iri + "R501");

        var result = await h.View.ListDevicesAsync(UserAuth(), Iri + "R501", default);

        Assert.Single(result);
    }

    [Fact]
    public async Task ListDevices_NoGrant_ReturnsNothing()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Db.Setup(d => d.ListDevices(Iri + "R501")).ReturnsAsync([D("AHU-1")]);
        h.CanRead("space", "R502"); // a neighbour, not this room

        var result = await h.View.ListDevicesAsync(UserAuth(), Iri + "R501", default);

        Assert.Empty(result);
    }

    // ── Get by dtId: authorize by the node's business id ─────────────────────

    [Fact]
    public async Task GetSpace_GrantOnTheBusinessId_IsOk()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.CanRead("space", "R501");

        var result = await h.View.GetSpaceAsync(UserAuth(), Iri + "R501", default);

        Assert.IsType<TwinGetResult<Space>.Ok>(result);
    }

    [Fact]
    public async Task GetBuilding_WithoutAGrant_IsForbidden()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(B("B1"));

        var result = await h.View.GetBuildingAsync(UserAuth(), Iri + "B1", default);

        Assert.IsType<TwinGetResult<Building>.Forbidden>(result);
    }

    [Fact]
    public async Task GetFloor_Absent_IsForbidden_ForANonAdmin()
    {
        // Loading the node first must not turn into an existence oracle: a non-admin gets the same
        // Forbidden for an absent node as for one it may not read.
        var h = new Harness();

        var result = await h.View.GetFloorAsync(UserAuth(), Iri + "F-missing", default);

        Assert.IsType<TwinGetResult<Floor>.Forbidden>(result);
    }

    [Fact]
    public async Task GetDevice_StillAcceptsALegacyDtIdGrant()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetDevice(Iri + "AHU-1")).ReturnsAsync(D("AHU-1"));
        h.CanRead("device", Iri + "AHU-1");

        var result = await h.View.GetDeviceAsync(UserAuth(), Iri + "AHU-1", default);

        Assert.IsType<TwinGetResult<Device>.Ok>(result);
    }

    // ── Adjacent spaces (review of #516) ─────────────────────────────────────

    [Fact]
    public async Task ListAdjacentSpaces_SubjectAndNeighboursAuthorizedByBusinessId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Db.Setup(d => d.ListAdjacentSpaces(Iri + "R501")).ReturnsAsync([S("R502"), S("R503")]);
        h.CanRead("space", "R501");
        h.CanRead("space", "R503");

        var result = await h.View.ListAdjacentSpacesAsync(UserAuth(), Iri + "R501", default);

        var ok = Assert.IsType<TwinGetResult<Space[]>.Ok>(result);
        Assert.Equal(["R503"], ok.Resource.Select(s => s.Id));
    }

    [Fact]
    public async Task ListAdjacentSpaces_Absent_IsForbidden_ForANonAdmin()
    {
        var h = new Harness();

        var result = await h.View.ListAdjacentSpacesAsync(UserAuth(), Iri + "R-missing", default);

        Assert.IsType<TwinGetResult<Space[]>.Forbidden>(result);
    }

    // ── Metadata write (review of #516) ──────────────────────────────────────

    [Fact]
    public async Task CanWriteResource_Space_WriteGrantOnTheBusinessId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), "space", "R501", "write",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Assert.True(await h.View.CanWriteResourceAsync(UserAuth(), "space", Iri + "R501", default));
    }

    [Fact]
    public async Task CanWriteResource_Space_LegacyDtIdWriteGrant_StillWorks()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetSpace(Iri + "R501")).ReturnsAsync(S("R501"));
        h.Auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), "space", Iri + "R501", "write",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Assert.True(await h.View.CanWriteResourceAsync(UserAuth(), "space", Iri + "R501", default));
    }

    [Fact]
    public async Task CanWriteResource_Point_StaysOnTheBusinessIdItIsAddressedBy()
    {
        var h = new Harness();
        h.Auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", "P-501-KWH", "write",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Assert.True(await h.View.CanWriteResourceAsync(UserAuth(), "point", "P-501-KWH", default));
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_MatchesNonPointHitsByBusinessId()
    {
        var h = new Harness();
        h.Db.Setup(d => d.SearchResources(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([
                new ResourceSearchHit { Type = "space", DtId = Iri + "R501", Id = "R501", Name = "501" },
                new ResourceSearchHit { Type = "space", DtId = Iri + "R502", Id = "R502", Name = "502" },
            ]);
        h.Accessible("space", "R501");

        var result = await h.View.SearchAsync(UserAuth(), "50", null, null, [], 50, 0, default);

        Assert.Equal(["R501"], result.Select(r => r.Id));
    }

    [Fact]
    public async Task Search_BuildingScoped_BuildingGrantOnTheBusinessId_ShowsItsDescendants()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(B("B1"));
        h.Db.Setup(d => d.SearchResources(It.IsAny<string?>(), It.IsAny<string?>(), Iri + "B1",
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([
                new ResourceSearchHit { Type = "floor", DtId = Iri + "F5", Id = "F5", Name = "5F", BuildingDtId = Iri + "B1" },
            ]);
        h.CanRead("building", "B1");

        var result = await h.View.SearchAsync(UserAuth(), null, null, Iri + "B1", [], 50, 0, default);

        Assert.Single(result);
    }

    // ── Building-scoped point ledger (#452) ──────────────────────────────────

    [Fact]
    public async Task ListPointDetails_BuildingGrantOnTheBusinessId_ReadsTheWholeBuilding()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(B("B1"));
        h.Db.Setup(d => d.ListPointDetails(Iri + "B1")).ReturnsAsync([
            new PointDetail { Point = P("PT1"), Device = D("AHU-1") },
            new PointDetail { Point = P("PT2"), Device = D("AHU-2") },
        ]);
        h.CanRead("building", "B1");

        var result = await h.View.ListPointDetailsAsync(UserAuth(), Iri + "B1", default);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task ListPointDetails_DeviceGrantOnTheBusinessId_ShowsThatDevicesPoints()
    {
        var h = new Harness();
        h.Db.Setup(d => d.GetBuilding(Iri + "B1")).ReturnsAsync(B("B1"));
        h.Db.Setup(d => d.ListPointDetails(Iri + "B1")).ReturnsAsync([
            new PointDetail { Point = P("PT1"), Device = D("AHU-1") },
            new PointDetail { Point = P("PT2"), Device = D("AHU-2") },
        ]);
        h.Accessible("device", "AHU-2");

        var result = await h.View.ListPointDetailsAsync(UserAuth(), Iri + "B1", default);

        Assert.Equal(["PT2"], result.Select(r => r.Point.Id));
    }
}
