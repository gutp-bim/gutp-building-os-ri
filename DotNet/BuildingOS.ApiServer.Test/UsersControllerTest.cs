using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class UsersControllerTest
{
    private static AuthorizationContext Auth(string role, string userId = "actor") =>
        new() { UserId = userId, Role = role, Permissions = [] };

    private static EntraUser User(string id, string? role, bool enabled = true) => new()
    {
        Id = id, DisplayName = id, Role = role, Enabled = enabled
    };

    private static (UsersController controller, Mock<IUserManagementService> svc, Mock<IAdminAuditRecorder> audit)
        Build(AuthorizationContext auth, IReadOnlyList<EntraUser>? users = null)
    {
        var svc = new Mock<IUserManagementService>();
        svc.Setup(s => s.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users ?? Array.Empty<EntraUser>());
        var mapping = new Mock<IResourceIdMappingRepository>();
        var audit = new Mock<IAdminAuditRecorder>();
        var controller = new UsersController(
            svc.Object, mapping.Object, audit.Object, NullLogger<UsersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
        return (controller, svc, audit);
    }

    // ── Auth ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetEnabled_NonAdmin_IsForbidden()
    {
        var (controller, _, _) = Build(Auth("operator"));
        var result = await controller.SetEnabled("u1", new UsersController.SetEnabledRequest { Enabled = false }, default);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public void GetRoles_NonAdmin_IsForbidden()
    {
        var (controller, _, _) = Build(Auth("viewer"));
        var result = controller.GetRoles();
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public void GetRoles_Admin_ReturnsCatalog()
    {
        var (controller, _, _) = Build(Auth("admin"));
        var result = controller.GetRoles();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var roles = Assert.IsAssignableFrom<IReadOnlyList<RoleCatalogEntry>>(ok.Value);
        Assert.Equal(3, roles.Count);
    }

    // ── Lockout guard ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetEnabled_SelfDisable_Returns409_AndAuditsFailure()
    {
        var users = new[] { User("actor", "admin"), User("admin-b", "admin") };
        var (controller, svc, audit) = Build(Auth("admin", "actor"), users);

        var result = await controller.SetEnabled("actor", new UsersController.SetEnabledRequest { Enabled = false }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        svc.Verify(s => s.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-enabled" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetEnabled_LastAdmin_Returns409()
    {
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var (controller, svc, _) = Build(Auth("admin", "op-actor"), users);

        var result = await controller.SetEnabled("admin-a", new UsersController.SetEnabledRequest { Enabled = false }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        svc.Verify(s => s.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetEnabled_NotFound_When_UserMissing()
    {
        var users = new[] { User("admin-a", "admin") };
        var (controller, _, _) = Build(Auth("admin", "admin-a"), users);

        var result = await controller.SetEnabled("ghost", new UsersController.SetEnabledRequest { Enabled = false }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SetEnabled_Allowed_CallsServiceAndAuditsSuccess()
    {
        var users = new[] { User("admin-a", "admin"), User("admin-b", "admin"), User("op", "operator") };
        var (controller, svc, audit) = Build(Auth("admin", "admin-a"), users);
        svc.Setup(s => s.SetEnabledAsync("op", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator", enabled: false));

        var result = await controller.SetEnabled("op", new UsersController.SetEnabledRequest { Enabled = false }, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<UsersController.UserResponse>(ok.Value);
        Assert.False(body.Enabled);
        svc.Verify(s => s.SetEnabledAsync("op", false, It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-enabled" && r.Result == AdminAuditResult.Success && r.TargetId == "op"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAttributes_SelfDemote_Returns409()
    {
        var users = new[] { User("actor", "admin"), User("admin-b", "admin") };
        var (controller, svc, _) = Build(Auth("admin", "actor"), users);

        var result = await controller.UpdateAttributes(
            "actor", new UsersController.UpdateUserAttributesApiRequest { Role = "operator" }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        svc.Verify(s => s.UpdateUserAttributesAsync(It.IsAny<string>(), It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAttributes_RolePromotion_Succeeds_AndAudits()
    {
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var (controller, svc, audit) = Build(Auth("admin", "admin-a"), users);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "admin"));

        var result = await controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Role = "admin" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-attributes" && r.Result == AdminAuditResult.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
