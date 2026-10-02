using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

public class AuthorizedTwinViewFacetsTest
{
    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext GroupManager() => new() { UserId = "gm", Role = "group-manager", Permissions = [] };

    private static ResourceFacetRow Row(string type, string id, string? device = null, string? point = null,
        string? unit = null, string? gw = null, string? dtId = null) =>
        new() { Type = type, DtId = dtId ?? $"urn:{id}", Id = id, DeviceType = device, PointType = point, Unit = unit, GatewayId = gw };

    private static (AuthorizedTwinView view, Mock<IDigitalTwinDatabase> db, Mock<IAuthorizationService> authSvc) Build(
        params ResourceFacetRow[] rows)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListFacetRows(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(rows);
        var authSvc = new Mock<IAuthorizationService>();
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
        return (new AuthorizedTwinView(db.Object, authSvc.Object), db, authSvc);
    }

    private static Task<ResourceFacets> Facets(AuthorizedTwinView v, AuthorizationContext a) =>
        v.GetFacetsAsync(a, null, null, null, [], ResourceAttributeFilter.None, default);

    [Fact]
    public async Task Facets_Admin_CountsDistinctResourcesPerValue_MostUsedFirst()
    {
        var (view, _, _) = Build(
            Row("point", "P1", "AHU", "temperature", "degC", "GW-1"),
            Row("point", "P2", "AHU", "temperature", "degC", "GW-1"),
            Row("point", "P3", "VAV", "co2", "ppm", "GW-2"),
            Row("point", "P1", "AHU", "temperature", "degC", "GW-1"), // join fan-out duplicate
            Row("device", "D1", "AHU"));

        var f = await Facets(view, AdminAuth());

        Assert.Equal(4, f.Total);
        Assert.False(f.Truncated);
        Assert.Equal([("point", 3), ("device", 1)], f.Types.Select(v => (v.Value, v.Count)));
        Assert.Equal([("AHU", 3), ("VAV", 1)], f.DeviceTypes.Select(v => (v.Value, v.Count)));
        Assert.Equal([("temperature", 2), ("co2", 1)], f.PointTypes.Select(v => (v.Value, v.Count)));
        Assert.Equal([("degC", 2), ("ppm", 1)], f.Units.Select(v => (v.Value, v.Count)));
        Assert.Equal([("GW-1", 2), ("GW-2", 1)], f.Gateways.Select(v => (v.Value, v.Count)));
    }

    [Fact]
    public async Task Facets_ResourceWithoutAnAttribute_IsNotCountedUnderThatGroup()
    {
        var (view, _, _) = Build(Row("floor", "F1"), Row("point", "P1", point: "temperature"));

        var f = await Facets(view, AdminAuth());

        Assert.Equal(2, f.Total);
        Assert.Empty(f.DeviceTypes);
        Assert.Single(f.PointTypes);
    }

    [Fact]
    public async Task Facets_User_CountsOnlyReadableResources()
    {
        var (view, _, authSvc) = Build(
            Row("point", "P1", "AHU", "temperature"), Row("point", "P2", "VAV", "co2"));
        var auth = new AuthorizationContext { UserId = "user1", Role = "user", Permissions = [] };
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(auth, "point", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[PermissionHelper.HashResourceId("P2")]);

        var f = await Facets(view, auth);

        Assert.Equal(1, f.Total);
        Assert.Equal(["VAV"], f.DeviceTypes.Select(v => v.Value));   // AHU belongs to an unreadable point
        Assert.Equal(["co2"], f.PointTypes.Select(v => v.Value));
    }

    [Fact]
    public async Task Facets_GroupManager_GetsNothing_AndTheTwinIsNotAsked()
    {
        var (view, db, _) = Build(Row("point", "P1", "AHU"));

        var f = await Facets(view, GroupManager());

        Assert.Equal(0, f.Total);
        Assert.Empty(f.DeviceTypes);
        db.Verify(d => d.ListFacetRows(It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Facets_MoreRowsThanTheScanCap_AreMarkedTruncated()
    {
        var rows = Enumerable.Range(0, AuthorizedTwinView.FacetRowCap + 1).Select(i => Row("point", $"P{i}", "AHU")).ToArray();
        var (view, _, _) = Build(rows);

        var f = await Facets(view, AdminAuth());

        Assert.True(f.Truncated);
        Assert.Equal(AuthorizedTwinView.FacetRowCap, f.Total);
    }

    [Fact]
    public async Task Facets_UnusableBuildingScope_FindsNothing()
    {
        var (view, db, _) = Build(Row("point", "P1"));

        var f = await view.GetFacetsAsync(AdminAuth(), null, null, "not an iri", [], ResourceAttributeFilter.None, default);

        Assert.Equal(0, f.Total);
        db.Verify(d => d.ListFacetRows(It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Facets_AGroupsCounts_IgnoreItsOwnSelection_SoAlternativesStayVisible()
    {
        var (view, _, _) = Build(
            Row("point", "P1", "AHU", "Temperature", "degC"),
            Row("point", "P2", "AHU", "CO2", "ppm"),
            Row("point", "P3", "VAV", "Temperature", "degC"));

        var f = await view.GetFacetsAsync(
            AdminAuth(), null, null, null, [], new ResourceAttributeFilter(["AHU"], [], [], []), default);

        // deviceType counts ignore the deviceType selection: VAV is still offered next to the chosen AHU.
        Assert.Equal([("AHU", 2), ("VAV", 1)], f.DeviceTypes.Select(v => (v.Value, v.Count)));
        // Every other group (and the total) is narrowed by it: P3 (VAV) no longer counts.
        Assert.Equal([("CO2", 1), ("Temperature", 1)], f.PointTypes.Select(v => (v.Value, v.Count)));
        Assert.Equal(2, f.Total);
    }

    [Fact]
    public async Task Facets_OtherGroupsSelections_NarrowAGroupsCounts_ANDAcrossGroups()
    {
        var (view, _, _) = Build(
            Row("point", "P1", "AHU", "Temperature", "degC"),
            Row("point", "P2", "AHU", "CO2", "ppm"),
            Row("point", "P3", "VAV", "Temperature", "degC"));

        var f = await view.GetFacetsAsync(
            AdminAuth(), null, null, null, [], new ResourceAttributeFilter([], ["Temperature"], [], []), default);

        Assert.Equal([("AHU", 1), ("VAV", 1)], f.DeviceTypes.Select(v => (v.Value, v.Count))); // CO2 point is out
        Assert.Equal([("CO2", 1), ("Temperature", 2)], f.PointTypes.Select(v => (v.Value, v.Count)).OrderBy(v => v.Item1));
    }

    [Fact]
    public async Task Facets_TypeFilter_IsAppliedHere_AndTheTypeGroupIgnoresItsOwnSelection()
    {
        var (view, _, _) = Build(Row("point", "P1", "AHU"), Row("device", "D1", "AHU"), Row("floor", "F1"));

        var f = await view.GetFacetsAsync(AdminAuth(), null, "point", null, [], ResourceAttributeFilter.None, default);

        Assert.Equal(1, f.Total);
        Assert.Equal(["device", "floor", "point"], f.Types.Select(v => v.Value).Order()); // other types stay selectable
        Assert.Equal([("AHU", 1)], f.DeviceTypes.Select(v => (v.Value, v.Count)));         // the device row is out
    }

    [Fact]
    public async Task SearchFiltered_GroupManagerWithAttributeFilter_FindsNothing_NoOracle()
    {
        var db = new Mock<IDigitalTwinDatabase>();
        var view = new AuthorizedTwinView(db.Object, new Mock<IAuthorizationService>().Object);
        var attrs = new ResourceAttributeFilter(["AHU"], [], [], []);

        var hits = await view.SearchFilteredAsync(GroupManager(), null, null, null, [], attrs, 50, 0, default);

        Assert.Empty(hits);
        db.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SearchFiltered_User_ReturnsOnlyReadableHits()
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.SearchResourcesFiltered(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ResourceAttributeFilter>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync([
              new ResourceSearchHit { Type = "point", DtId = "urn:p1", Id = "P1", Name = "a" },
              new ResourceSearchHit { Type = "point", DtId = "urn:p2", Id = "P2", Name = "b" }]);
        var authSvc = new Mock<IAuthorizationService>();
        var auth = new AuthorizationContext { UserId = "u", Role = "user", Permissions = [] };
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(auth, "point", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[PermissionHelper.HashResourceId("P2")]);
        var view = new AuthorizedTwinView(db.Object, authSvc.Object);

        var hits = await view.SearchFilteredAsync(auth, null, "point", null, [], new ResourceAttributeFilter(["AHU"], [], [], []), 50, 0, default);

        Assert.Equal(["P2"], hits.Select(h => h.Id));
    }
}
