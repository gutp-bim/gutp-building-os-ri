using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class AdminAuditControllerTest
{
    private static AuthorizationContext Auth(string role) =>
        new() { UserId = "u1", Role = role, Permissions = [] };

    private static AdminAuditController BuildController(
        AuthorizationContext auth,
        Mock<IAdminAuditRecorder>? recorder = null)
    {
        recorder ??= new Mock<IAdminAuditRecorder>();
        var controller = new AdminAuditController(recorder.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
        return controller;
    }

    [Fact]
    public async Task GetAudit_ReturnsForbid_ForNonAdmin()
    {
        var controller = BuildController(Auth("operator"));

        var result = await controller.GetAudit(null, null, 100, default);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetAudit_ReturnsRecords_ForAdmin()
    {
        var recorder = new Mock<IAdminAuditRecorder>();
        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.Gateway, "set-enabled", "GW001", "admin1", "Admin", AdminAuditResult.Success, null);
        recorder
            .Setup(r => r.ListAsync(It.IsAny<AdminAuditQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { record });

        var controller = BuildController(Auth("admin"), recorder);

        var result = await controller.GetAudit(null, null, 100, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<AdminAuditResponse>>(ok.Value);
        var item = Assert.Single(list);
        Assert.Equal("gateway", item.SubjectType);
        Assert.Equal("set-enabled", item.Action);
        Assert.Equal("success", item.Result);
    }

    [Fact]
    public async Task GetAudit_PassesFiltersToRecorder()
    {
        var recorder = new Mock<IAdminAuditRecorder>();
        AdminAuditQuery? captured = null;
        recorder
            .Setup(r => r.ListAsync(It.IsAny<AdminAuditQuery>(), It.IsAny<CancellationToken>()))
            .Callback<AdminAuditQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<AdminAuditRecord>());

        var controller = BuildController(Auth("admin"), recorder);

        await controller.GetAudit("twin", "GW9", 25, default);

        Assert.NotNull(captured);
        Assert.Equal("twin", captured!.SubjectType);
        Assert.Equal("GW9", captured.TargetId);
        Assert.Equal(25, captured.Limit);
    }
}
