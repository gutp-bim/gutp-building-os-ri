using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// Keycloak admin being unconfigured must surface as 503 rather than an unhandled DI failure (#293).
/// Before the fallback existed, <c>IUserManagementService</c> was simply not registered and every
/// <c>/api/Users</c> request died at controller activation with a 500.
/// </summary>
public class UserManagementUnavailableTest
{
    private static ExceptionContext ExceptionContextFor(Exception ex) =>
        new(
            new ActionContext(
                new DefaultHttpContext(),
                new RouteData(),
                new ActionDescriptor(),
                new ModelStateDictionary()),
            new List<IFilterMetadata>())
        {
            Exception = ex,
        };

    // ── Fallback service ─────────────────────────────────────────────────────

    [Fact]
    public async Task Unconfigured_EveryOperation_Throws()
    {
        var svc = new UnconfiguredUserManagementService();

        await Assert.ThrowsAsync<UserManagementUnavailableException>(() => svc.GetUsersAsync());
        await Assert.ThrowsAsync<UserManagementUnavailableException>(() => svc.GetUserByIdAsync("u1"));
        await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => svc.UpdateUserAttributesAsync("u1", new UpdateUserAttributesRequest()));
        await Assert.ThrowsAsync<UserManagementUnavailableException>(() => svc.SetEnabledAsync("u1", false));
    }

    [Fact]
    public async Task Unconfigured_MessageNamesTheMissingSettings()
    {
        var ex = await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => new UnconfiguredUserManagementService().GetUsersAsync());

        Assert.Contains("KEYCLOAK_AUTHORITY", ex.Message);
        Assert.Contains("KEYCLOAK_ADMIN_CLIENT_ID", ex.Message);
        Assert.Contains("KEYCLOAK_REALM", ex.Message);
    }

    // ── Filter ───────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_MapsUnavailableTo503()
    {
        var context = ExceptionContextFor(new UserManagementUnavailableException("not configured"));

        new UserManagementUnavailableFilter().OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Fact]
    public void Filter_LeavesOtherExceptionsAlone()
    {
        var context = ExceptionContextFor(new InvalidOperationException("something else"));

        new UserManagementUnavailableFilter().OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }

    // ── Controller: the generic catch blocks must not swallow it into a 400 ──

    private static UsersController UnconfiguredController()
    {
        var auth = new AuthorizationContext { UserId = "actor", Role = "admin", Permissions = [] };
        return new UsersController(
            new UnconfiguredUserManagementService(),
            new Mock<IResourceIdMappingRepository>().Object,
            new Mock<IAdminAuditRecorder>().Object,
            NullLogger<UsersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
    }

    [Fact]
    public async Task UpdateAttributes_Unconfigured_PropagatesForTheFilter()
    {
        // This action already had a catch-all returning 400; the unavailable exception has to escape
        // it so the filter can answer 503 instead of reporting a configuration gap as a client error.
        await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => UnconfiguredController().UpdateAttributes(
                "u1", new UsersController.UpdateUserAttributesApiRequest { Role = "viewer" }, default));
    }

    [Fact]
    public async Task SetEnabled_Unconfigured_PropagatesForTheFilter()
    {
        await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => UnconfiguredController().SetEnabled(
                "u1", new UsersController.SetEnabledRequest { Enabled = false }, default));
    }

    [Fact]
    public async Task GetAll_Unconfigured_PropagatesForTheFilter()
    {
        await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => UnconfiguredController().GetAll(default));
    }

    // ── No side effects for an operation that returns 503 ────────────────────
    //
    // UpdateAttributes used to persist the hash→resource-id mappings *before* calling the service.
    // With Keycloak unconfigured the call then threw, the caller got 503 — and the mappings stayed
    // written. A write committed for an operation the caller was told did not happen, with nothing
    // in the audit trail to explain it.

    private static (UsersController Controller, Mock<IResourceIdMappingRepository> Mapping, Mock<IAdminAuditRecorder> Audit)
        UnconfiguredControllerWithSpies()
    {
        var auth = new AuthorizationContext { UserId = "actor", Role = "admin", Permissions = [] };
        var mapping = new Mock<IResourceIdMappingRepository>();
        var audit = new Mock<IAdminAuditRecorder>();
        var controller = new UsersController(
            new UnconfiguredUserManagementService(),
            mapping.Object,
            audit.Object,
            NullLogger<UsersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
        return (controller, mapping, audit);
    }

    [Fact]
    public async Task UpdateAttributes_Unconfigured_PersistsNoResourceIdMapping()
    {
        var (controller, mapping, _) = UnconfiguredControllerWithSpies();

        // Permissions without Role: the path that reaches the service without an earlier
        // GetUsersAsync short-circuiting it, i.e. the one that actually wrote.
        await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => controller.UpdateAttributes(
                "u1",
                new UsersController.UpdateUserAttributesApiRequest
                {
                    Permissions = new List<string> { "building:bldg-1:read" },
                    ResourceDisplayNames = new Dictionary<string, string> { ["bldg-1"] = "Building 1" },
                },
                default));

        mapping.Verify(
            m => m.SaveMappingAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a request answered with 503 must not leave a resource-id mapping behind");
    }

    [Fact]
    public async Task UpdateAttributes_Unconfigured_RecordsAFailureAudit()
    {
        var (controller, _, audit) = UnconfiguredControllerWithSpies();

        await Assert.ThrowsAsync<UserManagementUnavailableException>(
            () => controller.UpdateAttributes(
                "u1",
                new UsersController.UpdateUserAttributesApiRequest
                {
                    Permissions = new List<string> { "building:bldg-1:read" },
                },
                default));

        // A rejected admin mutation that leaves no trace is indistinguishable from one never attempted.
        audit.Verify(
            a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void GetRoles_Unconfigured_StillWorks()
    {
        // The role catalogue is a static list that never touches the service — it only used to fail
        // because the controller could not be activated at all.
        var result = UnconfiguredController().GetRoles();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }
}
