using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// #548: a user holding a grant below building level (e.g. <c>space:R501</c>) must be able to reach
/// that node through the hierarchy lists. The granted node's ancestors (its building and floor) are
/// listed as navigation containers — only in the lists; reading the ancestor itself stays Forbidden,
/// and its other children stay hidden.
/// </summary>
public class AuthorizedTwinViewNavigableAncestorsTest
{
    private const string B1 = "urn:t:b1";
    private const string B2 = "urn:t:b2";
    private const string F5 = "urn:t:f5";
    private const string F6 = "urn:t:f6";
    private const string R501 = "urn:t:r501";
    private const string R502 = "urn:t:r502";

    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };

    private sealed record Setup(
        AuthorizedTwinView View,
        Mock<IDigitalTwinDatabase> Db,
        Mock<IAuthorizationService> Auth,
        Mock<INavigableAncestorResolver> Ancestors);

    private static Setup Build(params (string Type, string Id)[] ancestors)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListBuildings()).ReturnsAsync([
            new Building
            {
                DtId = B1, Id = "B1", Name = "本館",
                Identifiers = { ["ifc"] = "x" }, CustomTags = { ["vip"] = true },
            },
            new Building { DtId = B2, Id = "B2", Name = "別館", CustomTags = { ["vip"] = true } },
        ]);
        db.Setup(d => d.GetBuilding(B1)).ReturnsAsync(new Building { DtId = B1, Id = "B1", Name = "本館" });
        db.Setup(d => d.ListFloors(B1)).ReturnsAsync([
            new Floor { DtId = F5, Id = "F5", Name = "5F" },
            new Floor { DtId = F6, Id = "F6", Name = "6F" },
        ]);
        db.Setup(d => d.GetFloor(F5)).ReturnsAsync(new Floor { DtId = F5, Id = "F5", Name = "5F" });
        db.Setup(d => d.ListSpaces(F5)).ReturnsAsync([
            new Space { DtId = R501, Id = "R501", Name = "501" },
            new Space { DtId = R502, Id = "R502", Name = "502" },
        ]);
        db.Setup(d => d.GetSpace(R501)).ReturnsAsync(new Space { DtId = R501, Id = "R501", Name = "501" });
        db.Setup(d => d.ListDevices(R501)).ReturnsAsync([
            new Device
            {
                DtId = "urn:t:dev-a", Id = "DEV-A", Name = "A",
                Owner = "Tenant A", Supplier = "ACME", GatewayId = "GW-1", DeviceType = "AHU",
                BuildingName = "本館", Site = "S1", CustomTags = { ["critical"] = true },
            },
            new Device { DtId = "urn:t:dev-b", Id = "DEV-B", Name = "B" },
        ]);

        var auth = new Mock<IAuthorizationService>();
        auth.Setup(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        auth.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var resolver = new Mock<INavigableAncestorResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ancestors.ToHashSet());

        return new Setup(new AuthorizedTwinView(db.Object, auth.Object, null, resolver.Object), db, auth, resolver);
    }

    [Fact]
    public async Task Buildings_IncludeTheAncestorOfAGrantedNode_AndNoOther()
    {
        var s = Build(("building", "B1"), ("floor", "F5"));

        var result = await s.View.ListBuildingsAsync(UserAuth(), default);

        Assert.Equal(["B1"], result.Select(b => b.Id));
    }

    [Fact]
    public async Task Floors_IncludeTheAncestorFloor_NotItsSiblings()
    {
        var s = Build(("building", "B1"), ("floor", "F5"));

        var result = await s.View.ListFloorsAsync(UserAuth(), B1, default);

        Assert.Equal(["F5"], result.Select(f => f.Id));
    }

    [Fact]
    public async Task Spaces_IncludeTheAncestorSpaceOfAGrantedDeviceOrPoint()
    {
        // A point grant makes its device's room (and floor, building) navigable.
        var s = Build(("building", "B1"), ("floor", "F5"), ("space", "R501"), ("device", "DEV-A"));

        var spaces = await s.View.ListSpacesAsync(UserAuth(), F5, default);
        var devices = await s.View.ListDevicesAsync(UserAuth(), R501, default);

        Assert.Equal(["R501"], spaces.Select(x => x.Id));
        Assert.Equal(["DEV-A"], devices.Select(x => x.Id));
    }

    /// <summary>The ancestor is listed, not granted: reading it directly is still Forbidden.</summary>
    [Fact]
    public async Task AnAncestorIsNotReadable_OnlyListed()
    {
        var s = Build(("building", "B1"));

        var result = await s.View.GetBuildingAsync(UserAuth(), B1, default);

        Assert.IsType<TwinGetResult<Building>.Forbidden>(result);
    }

    [Fact]
    public async Task Admin_NeverAsksForAncestors()
    {
        var s = Build();

        Assert.Equal(2, (await s.View.ListBuildingsAsync(AdminAuth(), default)).Length);
        s.Ancestors.Verify(
            r => r.ResolveAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    /// <summary>
    /// One request walks several levels (home: buildings → floors → spaces); the ancestor set is
    /// resolved once and reused, not once per list call.
    /// </summary>
    [Fact]
    public async Task AncestorsAreResolvedOncePerViewInstance()
    {
        var s = Build(("building", "B1"), ("floor", "F5"));
        var user = UserAuth();

        await s.View.ListBuildingsAsync(user, default);
        await s.View.ListFloorsAsync(user, B1, default);
        await s.View.ListSpacesAsync(user, F5, default);

        s.Ancestors.Verify(r => r.ResolveAsync(user, It.IsAny<CancellationToken>()), Times.Once());
    }

    /// <summary>Without a resolver (older wiring, tests) the lists behave exactly as before #548.</summary>
    [Fact]
    public async Task NoResolver_KeepsThePreviousBehaviour()
    {
        var s = Build(("building", "B1"));
        var view = new AuthorizedTwinView(s.Db.Object, s.Auth.Object);

        Assert.Empty(await view.ListBuildingsAsync(UserAuth(), default));
    }

    /// <summary>A direct building grant still lists the building (the ancestor set only adds to it).</summary>
    [Fact]
    public async Task DirectGrantsStillWork()
    {
        var s = Build();
        s.Auth.Setup(a => a.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), "building", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync([PermissionHelper.HashResourceId("B2")]);

        var result = await s.View.ListBuildingsAsync(UserAuth(), default);

        Assert.Equal(["B2"], result.Select(b => b.Id));
    }

    /// <summary>
    /// A navigation-only entry carries its name and position, nothing else — not the owner, supplier,
    /// gateway, identifiers or tags of a node the user cannot read (Codex review on #553).
    /// </summary>
    [Fact]
    public async Task NavigationOnlyEntries_AreRedactedToNameAndPosition()
    {
        var s = Build(("building", "B1"), ("space", "R501"), ("device", "DEV-A"));

        var building = Assert.Single(await s.View.ListBuildingsAsync(UserAuth(), default));
        var device = Assert.Single(await s.View.ListDevicesAsync(UserAuth(), R501, default));

        Assert.Equal((B1, "B1", "本館"), (building.DtId, building.Id, building.Name));
        Assert.Empty(building.Identifiers);
        Assert.Empty(building.CustomTags);
        Assert.Equal(("urn:t:dev-a", "DEV-A", "A"), (device.DtId, device.Id, device.Name));
        Assert.Null(device.Owner);
        Assert.Null(device.Supplier);
        Assert.Null(device.GatewayId);
        Assert.Null(device.DeviceType);
        Assert.Null(device.BuildingName);
        Assert.Null(device.Site);
        Assert.Empty(device.CustomTags);
    }

    /// <summary>A node the user is granted keeps its full metadata, even if it is also an ancestor.</summary>
    [Fact]
    public async Task GrantedEntries_KeepTheirMetadata()
    {
        var s = Build(("building", "B2"));
        s.Auth.Setup(a => a.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), "building", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync([PermissionHelper.HashResourceId("B2")]);

        var building = Assert.Single(await s.View.ListBuildingsAsync(UserAuth(), default));

        Assert.True(building.CustomTags["vip"]);
    }
}
