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

    /// <summary>The stored form of a permission string (abbreviated, resource id hashed).</summary>
    private static string Hashed(string permission)
    {
        var (type, resourceId, actions) = PermissionHelper.ParsePermissionString(permission)!.Value;
        return PermissionHelper.BuildPermissionString(type, resourceId, actions);
    }

    private static EntraUser User(string id, string? role, bool enabled = true) => new()
    {
        Id = id, DisplayName = id, Role = role, Enabled = enabled
    };

    /// <param name="service">
    /// Overrides the mock, so a test can drive the controller with a real implementation —
    /// <see cref="UnconfiguredUserManagementService"/> for the unconfigured path (#293).
    /// </param>
    /// <param name="mapping">
    /// Overrides the mock so a test can make the reverse-lookup write fail, or assert whether it
    /// happened at all (#307).
    /// </param>
    private static (UsersController controller, Mock<IUserManagementService> svc, Mock<IAdminAuditRecorder> audit)
        Build(AuthorizationContext auth, IReadOnlyList<EntraUser>? users = null, IUserManagementService? service = null,
              Mock<IResourceIdMappingRepository>? mapping = null,
              IReadOnlyList<UserRoleState>? roleStates = null)
    {
        var svc = new Mock<IUserManagementService>();
        svc.Setup(s => s.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users ?? Array.Empty<EntraUser>());
        var states = roleStates
            ?? (users ?? Array.Empty<EntraUser>()).Select(u => new UserRoleState(u.Id, u.Role, u.Enabled)).ToList();
        svc.Setup(s => s.GetUserRoleStatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(states);
        svc.Setup(s => s.GetUserRoleStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => states.FirstOrDefault(s => s.Id == id));
        mapping ??= new Mock<IResourceIdMappingRepository>();
        var audit = new Mock<IAdminAuditRecorder>();
        var controller = new UsersController(
            service ?? svc.Object, mapping.Object, audit.Object, NullLogger<UsersController>.Instance)
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
    public async Task UpdateAttributes_GroupDerivedAdmin_SelfDemote_Returns409()
    {
        // #519 follow-up: an admin whose role comes from a group (building-os-admins role=admin) is an
        // admin in the token; an own `viewer` written now would override that and lock them out.
        var roleStates = new[]
        {
            new UserRoleState("actor", null, true, GroupRole: "admin"),
            new UserRoleState("admin-b", "admin", true),
        };
        var (controller, svc, _) = Build(Auth("admin", "actor"), roleStates: roleStates);

        var result = await controller.UpdateAttributes(
            "actor", new UsersController.UpdateUserAttributesApiRequest { Role = "viewer" }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        svc.Verify(s => s.UpdateUserAttributesAsync(It.IsAny<string>(), It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetEnabled_OnlyGroupDerivedAdmin_Returns409()
    {
        var users = new[] { User("group-admin", null), User("op", "operator") };
        var roleStates = new[]
        {
            new UserRoleState("group-admin", null, true, GroupRole: "admin"),
            new UserRoleState("op", "operator", true),
        };
        var (controller, svc, _) = Build(Auth("admin", "op"), users, roleStates: roleStates);

        var result = await controller.SetEnabled("group-admin", new UsersController.SetEnabledRequest { Enabled = false }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        svc.Verify(s => s.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("Admin")]
    public async Task UpdateAttributes_UnknownRole_Returns400_WithoutWriting(string role)
    {
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), users);

        var result = await controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Role = role }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        svc.Verify(s => s.UpdateUserAttributesAsync(It.IsAny<string>(), It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAttributes_TrimsTheRoleBeforeWriting()
    {
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), users);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "viewer"));

        var result = await controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Role = " viewer " }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.UpdateUserAttributesAsync("op",
            It.Is<UpdateUserAttributesRequest>(r => r.Role == "viewer"), It.IsAny<CancellationToken>()), Times.Once);
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

    // ── Reverse-lookup mapping is bookkeeping, not part of the update (#307) ──

    [Fact]
    public async Task UpdateAttributes_MappingSaveFails_StillSucceeds_BecauseKeycloakAlreadyCommitted()
    {
        // The attributes are committed in Keycloak before the mapping is written. Reporting the
        // mapping's failure as the request's failure told the caller nothing had happened while the
        // remote side had already changed, inviting a retry of something already done.
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var mapping = new Mock<IResourceIdMappingRepository>();
        mapping.Setup(m => m.SaveMappingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var (controller, svc, audit) = Build(Auth("admin", "admin-a"), users, mapping: mapping);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator"));

        var result = await controller.UpdateAttributes(
            "op",
            new UsersController.UpdateUserAttributesApiRequest
            {
                Role = "operator",
                Permissions = ["building:bldg-1:read"]
            },
            default);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateAttributes_MappingSaveFails_AuditsTheGapAndStillAuditsTheUpdate()
    {
        // The gap has to be findable: a warning alone would leave no record that a permission is in
        // effect but missing from accessible-resource listings.
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var mapping = new Mock<IResourceIdMappingRepository>();
        mapping.Setup(m => m.SaveMappingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var (controller, svc, audit) = Build(Auth("admin", "admin-a"), users, mapping: mapping);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator"));

        await controller.UpdateAttributes(
            "op",
            new UsersController.UpdateUserAttributesApiRequest { Permissions = ["building:bldg-1:read"] },
            default);

        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r =>
                r.Action == "set-attributes:resource-id-mapping" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-attributes" && r.Result == AdminAuditResult.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddPermission_GrantFails_LeavesNoMappingBehind()
    {
        // The mapping used to be written before the grant, so a failed grant left a reverse-lookup
        // record for a permission the caller was told was never given.
        var mapping = new Mock<IResourceIdMappingRepository>();
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), mapping: mapping);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("keycloak down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.AddPermission(
            "op", new UsersController.AddPermissionRequest { Permission = "building:bldg-1:read" }, default));

        mapping.Verify(m => m.SaveMappingAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── #531 review: skip the snapshot when the guard cannot trigger ─────────

    [Fact]
    public async Task SetEnabled_Enabling_DoesNotLoadRoleStates()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), [User("admin-a", "admin"), User("op", "operator", false)]);
        svc.Setup(s => s.SetEnabledAsync("op", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator"));

        var result = await controller.SetEnabled("op", new UsersController.SetEnabledRequest { Enabled = true }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.GetUserRoleStatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        svc.Verify(s => s.GetUserRoleStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetEnabled_Enabling_UnknownUser_Returns404()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.SetEnabledAsync("ghost", true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UserNotFoundException("ghost"));

        var result = await controller.SetEnabled("ghost", new UsersController.SetEnabledRequest { Enabled = true }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SetEnabled_DisablingANonAdmin_ResolvesOnlyTheTarget()
    {
        var users = new[] { User("admin-a", "admin"), User("op", "operator") };
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), users);
        svc.Setup(s => s.SetEnabledAsync("op", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator", enabled: false));

        var result = await controller.SetEnabled("op", new UsersController.SetEnabledRequest { Enabled = false }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.GetUserRoleStatesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAttributes_PromotionOrPermissionOnly_DoesNotLoadRoleStates()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), [User("admin-a", "admin"), User("op", "operator")]);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "admin"));

        await controller.UpdateAttributes("op", new UsersController.UpdateUserAttributesApiRequest { Role = "admin" }, default);
        await controller.UpdateAttributes("op", new UsersController.UpdateUserAttributesApiRequest { Permissions = [] }, default);

        svc.Verify(s => s.GetUserRoleStatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        svc.Verify(s => s.GetUserRoleStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAttributes_DemotingANonAdmin_ResolvesOnlyTheTarget()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"), [User("admin-a", "admin"), User("op", "operator")]);
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "viewer"));

        var result = await controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Role = "viewer" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.GetUserRoleStatesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAttributes_AmbiguousGroupAdminIsNotARemainingAdmin_Returns409()
    {
        var roleStates = new[]
        {
            new UserRoleState("admin-a", "admin", true),
            new UserRoleState("mixed", null, true, GroupRole: "admin", GroupRoleAmbiguous: true),
        };
        var (controller, svc, _) = Build(Auth("admin", "mixed"), roleStates: roleStates);

        var result = await controller.UpdateAttributes(
            "admin-a", new UsersController.UpdateUserAttributesApiRequest { Role = "viewer" }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        svc.Verify(s => s.UpdateUserAttributesAsync(It.IsAny<string>(), It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── #531 review: guard lookups are inside the error handling ─────────────

    [Fact]
    public async Task UpdateAttributes_RoleStateLookupFails_AuditsFailure_InsteadOfAn500()
    {
        var (controller, svc, audit) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.GetUserRoleStateAsync("op", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("keycloak group lookup failed"));

        var result = await controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Role = "viewer" }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-attributes" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetEnabled_RoleStateLookupFails_AuditsFailure_InsteadOfAn500()
    {
        var (controller, svc, audit) = Build(Auth("admin", "admin-a"), [User("admin-a", "admin"), User("admin-b", "admin")]);
        svc.Setup(s => s.GetUserRoleStatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("keycloak group lookup failed"));

        var result = await controller.SetEnabled("admin-b", new UsersController.SetEnabledRequest { Enabled = false }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-enabled" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAttributes_UnavailableDuringTheGuard_StillReachesThe503Filter()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.GetUserRoleStateAsync("op", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UserManagementUnavailableException("unconfigured"));

        await Assert.ThrowsAsync<UserManagementUnavailableException>(() => controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Role = "viewer" }, default));
    }

    // ── #531 review: an un-persisted write is not a success ──────────────────

    [Fact]
    public async Task UpdateAttributes_WriteNotPersisted_Returns502_AndAuditsFailure()
    {
        var (controller, svc, audit) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UserAttributesNotPersistedException("op"));

        var result = await controller.UpdateAttributes(
            "op", new UsersController.UpdateUserAttributesApiRequest { Permissions = ["floor:1:read"] }, default);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "set-attributes" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Result == AdminAuditResult.Success), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddPermission_WriteNotPersisted_Returns502_AndAuditsFailure()
    {
        var (controller, svc, audit) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UserAttributesNotPersistedException("op"));

        var result = await controller.AddPermission(
            "op", new UsersController.AddPermissionRequest { Permission = "group:g1:read" }, default);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "add-permission" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── #531 review: add/remove permission is one service call ───────────────

    [Fact]
    public async Task AddPermission_SendsAnAddOperation_WithoutReadingTheUserFirst()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator"));

        var result = await controller.AddPermission(
            "op", new UsersController.AddPermissionRequest { Permission = "group:g1:read" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.GetUserByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        svc.Verify(s => s.UpdateUserAttributesAsync("op", It.Is<UpdateUserAttributesRequest>(r =>
                r.Role == null && r.Permissions == null
                && r.PermissionsToAdd!.SequenceEqual(new[] { Hashed("group:g1:read") })
                && r.PermissionsToRemove == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemovePermission_SendsARemoveOperation_WithoutReadingTheUserFirst()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.UpdateUserAttributesAsync("op", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(User("op", "operator"));

        var result = await controller.RemovePermission(
            "op", new UsersController.RemovePermissionRequest { Permission = "group:g1:read" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        svc.Verify(s => s.GetUserByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        svc.Verify(s => s.UpdateUserAttributesAsync("op", It.Is<UpdateUserAttributesRequest>(r =>
                r.PermissionsToRemove!.SequenceEqual(new[] { Hashed("group:g1:read") }) && r.PermissionsToAdd == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddPermission_UnknownUser_Returns404()
    {
        var (controller, svc, _) = Build(Auth("admin", "admin-a"));
        svc.Setup(s => s.UpdateUserAttributesAsync("ghost", It.IsAny<UpdateUserAttributesRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UserNotFoundException("ghost"));

        var result = await controller.AddPermission(
            "ghost", new UsersController.AddPermissionRequest { Permission = "group:g1:read" }, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
