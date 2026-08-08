using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Filters;
using BuildingOs.ApiServer.Middlewares;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
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
    private static ExceptionContext ExceptionContextFor(
        Exception ex, string method = "PATCH", string actionName = "UpdateAttributes", string? id = "u1")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Items[AuthorizationContextMiddleware.HttpContextKey] =
            new AuthorizationContext { UserId = "actor", Role = "admin", Permissions = [] };

        var route = new RouteData();
        if (id is not null) route.Values["id"] = id;

        return new ExceptionContext(
            new ActionContext(
                http,
                route,
                new ControllerActionDescriptor { ActionName = actionName },
                new ModelStateDictionary()),
            new List<IFilterMetadata>())
        {
            Exception = ex,
        };
    }

    private static (UserManagementUnavailableExceptionFilter Filter, Mock<IAdminAuditRecorder> Audit) NewFilter()
    {
        var audit = new Mock<IAdminAuditRecorder>();
        return (new UserManagementUnavailableExceptionFilter(
            audit.Object, NullLogger<UserManagementUnavailableExceptionFilter>.Instance), audit);
    }

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
    public async Task Filter_MapsUnavailableTo503()
    {
        var context = ExceptionContextFor(new UserManagementUnavailableException("not configured"));

        await NewFilter().Filter.OnExceptionAsync(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Fact]
    public async Task Filter_LeavesOtherExceptionsAlone()
    {
        var context = ExceptionContextFor(new InvalidOperationException("something else"));

        await NewFilter().Filter.OnExceptionAsync(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }

    // ── Filter: the 503 must always leave a trace ────────────────────────────
    //
    // The per-action catch blocks could only audit exceptions raised inside their try. The lockout
    // guard's GetUsersAsync throws before it, so those requests returned 503 with no record at all —
    // indistinguishable, afterwards, from a request nobody made.

    [Fact]
    public async Task Filter_RecordsAFailureAudit()
    {
        var (filter, audit) = NewFilter();
        AdminAuditRecord? recorded = null;
        audit.Setup(a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()))
            .Callback<AdminAuditRecord, CancellationToken>((r, _) => recorded = r)
            .Returns(Task.CompletedTask);

        await filter.OnExceptionAsync(
            ExceptionContextFor(new UserManagementUnavailableException("not configured")));

        Assert.NotNull(recorded);
        Assert.Equal(AdminAuditResult.Failure, recorded!.Result);
        Assert.Equal(AdminAuditSubjects.User, recorded.SubjectType);
        Assert.Equal("actor", recorded.ActorSub);
        Assert.Equal("u1", recorded.TargetId);
        // The name matches what the action used to write by hand, so existing audit queries still work.
        Assert.Equal("set-attributes", recorded.Action);
    }

    [Fact]
    public async Task Filter_DoesNotAuditReads()
    {
        // The audit log records mutations; a GET that could not be served is not one.
        var (filter, audit) = NewFilter();

        await filter.OnExceptionAsync(ExceptionContextFor(
            new UserManagementUnavailableException("not configured"), method: "GET", actionName: "GetAll"));

        audit.Verify(a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Filter_StillReturns503WhenAuditingFails()
    {
        // Auditing is best-effort: whatever goes wrong writing the record, the caller needs the 503.
        var audit = new Mock<IAdminAuditRecorder>();
        audit.Setup(a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));
        var filter = new UserManagementUnavailableExceptionFilter(
            audit.Object, NullLogger<UserManagementUnavailableExceptionFilter>.Instance);
        var context = ExceptionContextFor(new UserManagementUnavailableException("not configured"));

        await filter.OnExceptionAsync(context);

        Assert.True(context.ExceptionHandled);
        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    // A new action added to the controller is audited without anyone remembering to wire it up —
    // the whole point of moving this out of the per-action catch blocks.
    [Theory]
    [InlineData("UpdateAttributes", "set-attributes")]
    [InlineData("SetEnabled", "set-enabled")]
    [InlineData("AddPermission", "add-permission")]
    [InlineData("DeleteUser", "delete-user")]
    public async Task Filter_DerivesAnActionNameForAnyAction(string actionName, string expected)
    {
        var (filter, audit) = NewFilter();
        AdminAuditRecord? recorded = null;
        audit.Setup(a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()))
            .Callback<AdminAuditRecord, CancellationToken>((r, _) => recorded = r)
            .Returns(Task.CompletedTask);

        await filter.OnExceptionAsync(ExceptionContextFor(
            new UserManagementUnavailableException("not configured"), actionName: actionName));

        Assert.Equal(expected, recorded?.Action);
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

    // The failure audit is the filter's job (#303), so the controller must NOT also write one —
    // otherwise a single refused request produces two records.
    [Fact]
    public async Task UpdateAttributes_Unconfigured_LeavesTheAuditToTheFilter()
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

        audit.Verify(
            a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
