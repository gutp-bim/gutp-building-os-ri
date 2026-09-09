using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// #446 — every dtId the twin interpolates into a SPARQL IRI reference (<c>&lt;{dtId}&gt;</c>) is
/// rejected here, in the authorization layer, before the twin or the authorization service is
/// touched. #444 established the shape on <c>ListAdjacentSpacesAsync</c>
/// (<see cref="AuthorizedTwinViewAdjacencyTest"/>); this widens it to the pre-existing paths.
///
/// The controllers percent-unescape the route value, so a hostile <c>%3E</c> arrives here as a raw
/// <c>&gt;</c> that would terminate the <c>&lt;...&gt;</c> token and let the remainder be parsed as
/// query syntax. An IRI reference has no escape mechanism, so rejection is the only defence.
///
/// A single-resource read answers <b>NotFound</b> and a collection read answers <b>empty</b> — the
/// same answers an id that is simply absent from the twin gets, so a probe cannot tell a rejected
/// id apart from an unknown one. Point ids (<c>GetPointAsync</c> / <c>CanWritePointAsync</c>) are
/// deliberately absent from this test: they are matched as SPARQL string literals
/// (<c>FILTER(?ptId = "…")</c>, escaped by <c>EscapeStringLiteral</c>), not IRIs, and business ids
/// such as <c>PT001</c> are not IRIs at all.
/// </summary>
public class AuthorizedTwinViewIriGuardTest
{
    /// <summary>
    /// Four ways a caller-supplied dtId can fail to be a usable IRI reference. The first is the
    /// attack; the rest are the malformed values that would otherwise reach OxiGraph as a broken
    /// query (a 400 that surfaces as a 500) or, worse for the relative reference, as a *valid* query
    /// against a resource the caller did not name — it resolves against the query's base IRI.
    /// </summary>
    private static readonly string[] MalformedDtIds =
    [
        "urn:test:b1> } INSERT DATA { <urn:x> <urn:y> <urn:z>",  // terminates the IRIREF token
        "urn:test:b 1",                                          // raw space — illegal in IRIREF
        "<urn:test:b1>",                                         // already-bracketed, nests <<>>
        "b1",                                                    // relative reference
    ];

    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };

    /// <summary>
    /// Every twin read is stubbed to return a hit, so a path that fails to reject the dtId answers
    /// with that hit rather than throwing — the assertion then fails on the value, not on a NRE.
    /// </summary>
    private static (AuthorizedTwinView view, Mock<IDigitalTwinDatabase> db, Mock<IAuthorizationService> auth) Build()
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.GetBuilding(It.IsAny<string>())).ReturnsAsync(new Building { DtId = "urn:test:b1", Id = "B1", Name = "B" });
        db.Setup(d => d.GetFloor(It.IsAny<string>())).ReturnsAsync(new Floor { DtId = "urn:test:f1", Id = "F1", Name = "1F" });
        db.Setup(d => d.GetSpace(It.IsAny<string>())).ReturnsAsync(new Space { DtId = "urn:test:s1", Id = "S1", Name = "Room" });
        db.Setup(d => d.GetDevice(It.IsAny<string>())).ReturnsAsync(new Device { DtId = "urn:test:d1", Id = "D1", Name = "AC" });
        db.Setup(d => d.ListFloors(It.IsAny<string>())).ReturnsAsync([new Floor { DtId = "urn:test:f1", Id = "F1", Name = "1F" }]);
        db.Setup(d => d.ListSpaces(It.IsAny<string>())).ReturnsAsync([new Space { DtId = "urn:test:s1", Id = "S1", Name = "Room" }]);
        db.Setup(d => d.ListDevices(It.IsAny<string>())).ReturnsAsync([new Device { DtId = "urn:test:d1", Id = "D1", Name = "AC" }]);
        db.Setup(d => d.ListPoints(It.IsAny<string>())).ReturnsAsync([new Point { DtId = "urn:test:pt1", Id = "PT001", Name = "Temp" }]);
        db.Setup(d => d.SearchResources(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
          .ReturnsAsync([new ResourceSearchHit { Type = "building", DtId = "urn:test:b1", Id = "B1", Name = "B" }]);

        var authSvc = new Mock<IAuthorizationService>();
        // Permissive on purpose: the guard must fire even for a caller who would otherwise be allowed.
        authSvc.Setup(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        return (new AuthorizedTwinView(db.Object, authSvc.Object), db, authSvc);
    }

    // The paths are crossed with the malformed values rather than written out one test per method:
    // the obligation is identical for all of them, and a new dtId-taking read only has to be added
    // to the name list here.
    public static TheoryData<string, string> SingleResourceReads() => Cross("building", "floor", "space", "device");

    public static TheoryData<string, string> CollectionReads() => Cross("floors", "spaces", "devices", "points", "search");

    private static TheoryData<string, string> Cross(params string[] paths)
    {
        var data = new TheoryData<string, string>();
        foreach (var path in paths)
            foreach (var dtId in MalformedDtIds)
                data.Add(path, dtId);
        return data;
    }

    [Theory]
    [MemberData(nameof(SingleResourceReads))]
    public async Task SingleResourceRead_MalformedDtId_IsNotFound_AndNeverTouchesTheTwin(string path, string dtId)
    {
        var (view, db, _) = Build();
        var auth = AdminAuth();

        var isNotFound = path switch
        {
            "building" => await view.GetBuildingAsync(auth, dtId, default) is TwinGetResult<Building>.NotFound,
            "floor"    => await view.GetFloorAsync(auth, dtId, default) is TwinGetResult<Floor>.NotFound,
            "space"    => await view.GetSpaceAsync(auth, dtId, default) is TwinGetResult<Space>.NotFound,
            "device"   => await view.GetDeviceAsync(auth, dtId, default) is TwinGetResult<Device>.NotFound,
            _          => throw new ArgumentOutOfRangeException(nameof(path), path, "unknown read path"),
        };

        Assert.True(isNotFound, $"{path} did not answer NotFound for a malformed dtId");
        db.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(CollectionReads))]
    public async Task CollectionRead_MalformedDtId_IsEmpty_AndNeverTouchesTheTwin(string path, string dtId)
    {
        var (view, db, _) = Build();
        var auth = AdminAuth();

        System.Collections.IEnumerable result = path switch
        {
            "floors"  => await view.ListFloorsAsync(auth, dtId, default),
            "spaces"  => await view.ListSpacesAsync(auth, dtId, default),
            "devices" => await view.ListDevicesAsync(auth, dtId, default),
            "points"  => await view.ListPointsAsync(auth, dtId, default),
            "search"  => await view.SearchAsync(auth, "q", null, dtId, [], 50, 0, default),
            _         => throw new ArgumentOutOfRangeException(nameof(path), path, "unknown read path"),
        };

        Assert.Empty(result);
        db.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(SingleResourceReads))]
    public async Task SingleResourceRead_MalformedDtId_NeverConsultsTheAuthorizationService(string path, string dtId)
    {
        // The guard runs *before* authorization, not after: asking the ACL first would answer
        // Forbidden for one malformed id and NotFound for another, which is exactly the oracle the
        // uniform 404 exists to deny.
        var (view, _, authSvc) = Build();
        var auth = UserAuth();

        _ = path switch
        {
            "building" => (object)await view.GetBuildingAsync(auth, dtId, default),
            "floor"    => await view.GetFloorAsync(auth, dtId, default),
            "space"    => await view.GetSpaceAsync(auth, dtId, default),
            "device"   => await view.GetDeviceAsync(auth, dtId, default),
            _          => throw new ArgumentOutOfRangeException(nameof(path), path, "unknown read path"),
        };

        authSvc.VerifyNoOtherCalls();
    }

    // ── Characterization (added after the guard, not part of its RED) ─────────
    //
    // An absent/blank scope id is not a malformed IRI — it is the documented "no filter" input that
    // ListFloors("")/ListSpaces("")/… turn into the unscoped query, and SearchAsync into an
    // unscoped search. Pinning it here so the guard can never be tightened into rejecting it.

    [Fact]
    public async Task CollectionRead_BlankScopeId_StillReachesTheTwinUnscoped()
    {
        var (view, db, _) = Build();
        var auth = AdminAuth();

        Assert.Single(await view.ListFloorsAsync(auth, null, default));
        Assert.Single(await view.ListSpacesAsync(auth, "", default));
        Assert.Single(await view.ListDevicesAsync(auth, null, default));
        Assert.Single(await view.ListPointsAsync(auth, "", default));
        Assert.Single(await view.SearchAsync(auth, "q", null, null, [], 50, 0, default));

        db.Verify(d => d.ListFloors(""), Times.Once);
        db.Verify(d => d.ListSpaces(""), Times.Once);
        db.Verify(d => d.ListDevices(""), Times.Once);
        db.Verify(d => d.ListPoints(""), Times.Once);
    }

    [Fact]
    public async Task Reads_WellFormedDtId_ReachTheTwin()
    {
        // The dtIds the twin actually holds are the RDF node IRIs — urn:… in the fixtures,
        // https://www.sbco.or.jp/ont/resource/… in sbco-sample.ttl. Both must pass the guard.
        var (view, db, _) = Build();
        var auth = AdminAuth();

        foreach (var dtId in new[] { "urn:dtid:b1", "https://www.sbco.or.jp/ont/resource/bldg-1" })
        {
            Assert.IsType<TwinGetResult<Building>.Ok>(await view.GetBuildingAsync(auth, dtId, default));
            Assert.Single(await view.ListFloorsAsync(auth, dtId, default));
            db.Verify(d => d.GetBuilding(dtId), Times.Once);
            db.Verify(d => d.ListFloors(dtId), Times.Once);
        }
    }
}
