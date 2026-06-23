using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

public class AuthorizedTwinViewWritableGateTest
{
    private static AuthorizationContext AdminAuth() => new()
    {
        UserId = "admin1", Role = "admin", Permissions = []
    };

    private static AuthorizationContext UserAuth() => new()
    {
        UserId = "user1", Role = "user", Permissions = []
    };

    private static Point MakePoint(bool? writable) => new()
    {
        DtId = "urn:pt:1", Id = "PT001", Name = "Temp", Writable = writable
    };

    private static AuthorizedTwinView BuildView(Point? point, bool aclAllows = true)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.GetPoint(It.IsAny<string>())).ReturnsAsync(point);

        var authSvc = new Mock<IAuthorizationService>();
        authSvc.Setup(s => s.CanAccessAsync(It.IsAny<AuthorizationContext>(), "point", It.IsAny<string>(), "write", It.IsAny<CancellationToken>()))
               .ReturnsAsync(aclAllows);

        return new AuthorizedTwinView(db.Object, authSvc.Object);
    }

    [Fact]
    public async Task CanWritePoint_WritableFalse_ReturnsFalse_EvenForAdmin()
    {
        var view = BuildView(MakePoint(writable: false));
        var result = await view.CanWritePointAsync(AdminAuth(), "PT001", default);
        Assert.False(result);
    }

    [Fact]
    public async Task CanWritePoint_WritableTrue_Admin_ReturnsTrue()
    {
        var view = BuildView(MakePoint(writable: true));
        var result = await view.CanWritePointAsync(AdminAuth(), "PT001", default);
        Assert.True(result);
    }

    [Fact]
    public async Task CanWritePoint_WritableNull_Admin_ReturnsTrue()
    {
        var view = BuildView(MakePoint(writable: null));
        var result = await view.CanWritePointAsync(AdminAuth(), "PT001", default);
        Assert.True(result);
    }

    [Fact]
    public async Task CanWritePoint_WritableTrue_UserWithAcl_ReturnsTrue()
    {
        var view = BuildView(MakePoint(writable: true), aclAllows: true);
        var result = await view.CanWritePointAsync(UserAuth(), "PT001", default);
        Assert.True(result);
    }

    [Fact]
    public async Task CanWritePoint_WritableTrue_UserWithoutAcl_ReturnsFalse()
    {
        var view = BuildView(MakePoint(writable: true), aclAllows: false);
        var result = await view.CanWritePointAsync(UserAuth(), "PT001", default);
        Assert.False(result);
    }

    [Fact]
    public async Task CanWritePoint_WritableFalse_UserWithAcl_ReturnsFalse()
    {
        var view = BuildView(MakePoint(writable: false), aclAllows: true);
        var result = await view.CanWritePointAsync(UserAuth(), "PT001", default);
        Assert.False(result);
    }

    [Fact]
    public async Task CanWritePoint_PointNotFound_ReturnsFalse()
    {
        var view = BuildView(point: null);
        var result = await view.CanWritePointAsync(AdminAuth(), "UNKNOWN", default);
        Assert.False(result);
    }
}
