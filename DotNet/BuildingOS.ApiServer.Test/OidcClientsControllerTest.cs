using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.OidcClients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

public class OidcClientsControllerTest
{
    private static AuthorizationContext Auth(string role) =>
        new() { UserId = "actor", Role = role, Permissions = [] };

    private static (OidcClientsController c, Mock<IOidcClientManagementService> svc, Mock<IAdminAuditRecorder> audit)
        Build(AuthorizationContext auth, IOidcClientManagementService? service = null)
    {
        var svc = new Mock<IOidcClientManagementService>();
        var audit = new Mock<IAdminAuditRecorder>();
        var controller = new OidcClientsController(
            service ?? svc.Object, audit.Object, NullLogger<OidcClientsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Items = { ["AuthorizationContext"] = auth } },
            },
        };
        return (controller, svc, audit);
    }

    [Fact]
    public async Task List_NonAdmin_IsForbidden()
    {
        var (c, _, _) = Build(Auth("operator"));
        Assert.IsType<ForbidResult>(await c.List(default));
    }

    [Fact]
    public async Task List_Unconfigured_Returns503()
    {
        var (c, _, _) = Build(Auth("admin"), new UnconfiguredOidcClientService());
        var result = await c.List(default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
    }

    [Fact]
    public async Task Create_EmptyClientId_Returns400()
    {
        var (c, svc, _) = Build(Auth("admin"));
        var result = await c.Create(new OidcClientsController.CreateOidcClientRequest { ClientId = "" }, default);
        Assert.IsType<BadRequestObjectResult>(result);
        svc.Verify(s => s.CreateClientAsync(It.IsAny<CreateOidcClientSpec>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ReturnsSecretOnce_AndAuditsWithoutSecret()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        var detail = new OidcClientDetail("id1", "svc-1", true, true, false, null, []);
        svc.Setup(s => s.CreateClientAsync(It.IsAny<CreateOidcClientSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((detail, "PLAINTEXT"));

        var result = await c.Create(
            new OidcClientsController.CreateOidcClientRequest { ClientId = "svc-1", ServiceAccountsEnabled = true }, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status201Created, obj.StatusCode);
        var body = Assert.IsType<OidcClientsController.CreatedOidcClientResponse>(obj.Value);
        Assert.Equal("PLAINTEXT", body.Secret);
        // Audit must record the create but never the secret value.
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r =>
                r.SubjectType == "oidc-client" && r.Action == "create" && r.Result == AdminAuditResult.Success
                && (r.DetailJson == null || !r.DetailJson.Contains("PLAINTEXT"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RotateSecret_Audits_AndReturnsSecret()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        svc.Setup(s => s.RotateSecretAsync("id1", It.IsAny<CancellationToken>())).ReturnsAsync("NEWSECRET");

        var result = await c.RotateSecret("id1", default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<OidcClientsController.RotatedSecretResponse>(ok.Value);
        Assert.Equal("NEWSECRET", body.Secret);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "rotate-secret" && (r.DetailJson == null || !r.DetailJson.Contains("NEWSECRET"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_Audits_AndReturns204()
    {
        var (c, svc, audit) = Build(Auth("admin"));
        svc.Setup(s => s.DeleteClientAsync("id1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var result = await c.Delete("id1", default);

        Assert.IsType<NoContentResult>(result);
        audit.Verify(a => a.RecordAsync(
            It.Is<AdminAuditRecord>(r => r.Action == "delete" && r.Result == AdminAuditResult.Success),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        var (c, svc, _) = Build(Auth("admin"));
        svc.Setup(s => s.GetClientAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((OidcClientDetail?)null);
        Assert.IsType<NotFoundResult>(await c.Get("missing", default));
    }
}
