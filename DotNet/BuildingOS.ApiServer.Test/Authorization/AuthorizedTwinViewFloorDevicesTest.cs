using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// #544: devices placed directly on a Level, listed under that Level. Same rules as the room-scoped
/// list: a read on the floor (or above) shows all of them; otherwise only granted devices, plus
/// navigation-only ancestors of granted points (#548), redacted to name and position.
/// </summary>
public class AuthorizedTwinViewFloorDevicesTest
{
    private const string F1 = "urn:t:f1";

    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };

    private static (AuthorizedTwinView View, Mock<IDigitalTwinDatabase> Db, Mock<IAuthorizationService> Auth) Build(
        params (string Type, string Id)[] ancestors)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.GetFloor(F1)).ReturnsAsync(new Floor { DtId = F1, Id = "F1", Name = "1F" });
        db.Setup(d => d.ListFloorDevices(F1, It.IsAny<CancellationToken>())).ReturnsAsync([
            new Device { DtId = "urn:t:meter", Id = "METER", Name = "Meter", Owner = "Tenant A" },
            new Device { DtId = "urn:t:ahu", Id = "AHU", Name = "AHU" },
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
        return (new AuthorizedTwinView(db.Object, auth.Object, null, resolver.Object), db, auth);
    }

    [Fact]
    public async Task Admin_SeesEveryDeviceOnTheFloor()
    {
        var (view, _, _) = Build();
        Assert.Equal(2, (await view.ListFloorDevicesAsync(AdminAuth(), F1, default)).Length);
    }

    [Fact]
    public async Task FloorRead_SeesEveryDeviceOnTheFloor_WithMetadata()
    {
        var (view, _, auth) = Build();
        auth.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), "floor", F1, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await view.ListFloorDevicesAsync(UserAuth(), F1, default);

        Assert.Equal(2, result.Length);
        Assert.Equal("Tenant A", result.Single(d => d.Id == "METER").Owner);
    }

    [Fact]
    public async Task DeviceGrant_SeesOnlyThatDevice()
    {
        var (view, _, auth) = Build();
        auth.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), "device", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync([PermissionHelper.HashResourceId("AHU")]);

        Assert.Equal(["AHU"], (await view.ListFloorDevicesAsync(UserAuth(), F1, default)).Select(d => d.Id));
    }

    /// <summary>A point grant makes its Level-placed device navigable — name and position only.</summary>
    [Fact]
    public async Task NavigableAncestorDevice_IsListedRedacted()
    {
        var (view, _, _) = Build(("device", "METER"));

        var device = Assert.Single(await view.ListFloorDevicesAsync(UserAuth(), F1, default));

        Assert.Equal("METER", device.Id);
        Assert.Null(device.Owner);
    }

    /// <summary>The request's token reaches the twin query, so an aborted request stops it.</summary>
    [Fact]
    public async Task TheCallersToken_ReachesTheTwinQuery()
    {
        var (view, db, _) = Build();
        using var cts = new CancellationTokenSource();

        await view.ListFloorDevicesAsync(AdminAuth(), F1, cts.Token);

        db.Verify(d => d.ListFloorDevices(F1, cts.Token), Times.Once());
    }

    [Fact]
    public async Task NoGrant_SeesNothing()
    {
        var (view, _, _) = Build();
        Assert.Empty(await view.ListFloorDevicesAsync(UserAuth(), F1, default));
    }

    [Fact]
    public async Task MalformedDtId_ReturnsEmptyWithoutTouchingTheTwin()
    {
        var (view, db, _) = Build();

        Assert.Empty(await view.ListFloorDevicesAsync(AdminAuth(), "not an iri>", default));
        db.Verify(d => d.ListFloorDevices(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }
}
