using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class TwinAdminControllerTest
{
    private static AuthorizationContext Auth(string role) =>
        new() { UserId = "actor", Role = role, Permissions = [] };

    private static (TwinAdminController c, Mock<ITwinAdminService> svc, Mock<IAdminAuditRecorder> audit)
        Build(AuthorizationContext auth, IPointListRevisionCoordinator? revisions = null)
    {
        var svc = new Mock<ITwinAdminService>();
        var audit = new Mock<IAdminAuditRecorder>();
        var controller = new TwinAdminController(
            svc.Object,
            audit.Object,
            revisions ?? new MemoryPointListRevisionCoordinator(),
            NullLogger<TwinAdminController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
        return (controller, svc, audit);
    }

    [Fact]
    public async Task Query_NonAdmin_IsForbidden()
    {
        var (c, _, _) = Build(Auth("operator"));
        Assert.IsType<ForbidResult>(await c.Query(new TwinAdminController.SparqlQueryRequest { Query = "SELECT ?s WHERE {?s ?p ?o}" }, default));
    }

    [Fact]
    public async Task Query_NonReadOnly_Returns400_AndAuditsFailure()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        var result = await c.Query(new TwinAdminController.SparqlQueryRequest { Query = "DROP ALL" }, default);
        Assert.IsType<BadRequestObjectResult>(result);
        svc.Verify(s => s.RunReadOnlyQueryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.SubjectType == "twin" && r.Action == "query" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Query_ReadOnly_RunsAndAuditsSuccess()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        svc.Setup(s => s.RunReadOnlyQueryAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SparqlQueryResult(["s"], [], 0, false, 5));

        var result = await c.Query(new TwinAdminController.SparqlQueryRequest { Query = "SELECT ?s WHERE {?s ?p ?o}" }, default);

        Assert.IsType<OkObjectResult>(result);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "query" && r.Result == AdminAuditResult.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PreviewImport_EmptyTurtle_Returns400()
    {
        var (c, _, _) = Build(Auth("admin"));
        Assert.IsType<BadRequestObjectResult>(await c.PreviewImport(new TwinAdminController.TwinImportRequest { Turtle = "" }, default));
    }

    [Fact]
    public async Task ApplyImport_Collision_Returns409_AndDoesNotApply()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        svc.Setup(s => s.PreviewImportAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwinImportPreview(10, 1, [new GatewayCollision("GW001", 2)]));

        var result = await c.ApplyImport(new TwinAdminController.TwinImportRequest { Turtle = "ttl", Mode = "replace" }, default);

        Assert.IsType<ConflictObjectResult>(result);
        svc.Verify(s => s.ApplyImportAsync(It.IsAny<string>(), It.IsAny<TwinImportMode>(), It.IsAny<CancellationToken>()), Times.Never);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "import-apply" && r.Result == AdminAuditResult.Failure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyImport_Valid_AppliesWithMode_AndAuditsSuccess()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        svc.Setup(s => s.PreviewImportAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwinImportPreview(10, 1, []));

        var result = await c.ApplyImport(new TwinAdminController.TwinImportRequest { Turtle = "ttl", Mode = "replace" }, default);

        Assert.IsType<OkObjectResult>(result);
        svc.Verify(s => s.ApplyImportAsync("ttl", TwinImportMode.Replace, It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "import-apply" && r.Result == AdminAuditResult.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyImport_DefaultsToAppend_WhenModeOmitted()
    {
        var (c, svc, _) = Build(Auth("admin"));
        svc.Setup(s => s.PreviewImportAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwinImportPreview(1, 0, []));

        await c.ApplyImport(new TwinAdminController.TwinImportRequest { Turtle = "ttl" }, default);

        svc.Verify(s => s.ApplyImportAsync("ttl", TwinImportMode.Append, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyImport_DoesNotMutate_WhenRevisionInvalidationFails()
    {
        var revisions = new Mock<IPointListRevisionCoordinator>();
        revisions.Setup(store => store.BeginUpdateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("revision store unavailable"));
        var (controller, twin, _) = Build(Auth("admin"), revisions.Object);
        twin.Setup(service => service.PreviewImportAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwinImportPreview(1, 1, []));

        var result = await controller.ApplyImport(
            new TwinAdminController.TwinImportRequest { Turtle = "ttl", Mode = "replace" }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        twin.Verify(service => service.ApplyImportAsync(
            It.IsAny<string>(), It.IsAny<TwinImportMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
