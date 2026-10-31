using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOs.ApiServer.Middlewares;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.GatewayProvisioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

public sealed class GatewayConfigurationControllerTest
{
    private static GatewayProvisioningSnapshot Snapshot(int instance = 2) => new([], [new(
        "https://example.com/clock", "simulation", "s", true, "poll",
        new("GW-EXAMPLE", "bacnet-example", 1234, "analogValue", instance, "presentValue", "seconds"))], []);

    private static (GatewayConfigurationController Controller, Mock<IGatewayProvisioningSource> Source) Build(string? caller, bool admin = false)
    {
        var source = new Mock<IGatewayProvisioningSource>();
        source.Setup(s => s.ReadAsync("GW-EXAMPLE", It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        var controller = new GatewayConfigurationController(source.Object, new HeaderGatewayIdentityResolver(), NullLogger<GatewayConfigurationController>.Instance)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        if (caller is not null) controller.Request.Headers["X-Gateway-Id"] = caller;
        if (admin) controller.HttpContext.Items[AuthorizationContextMiddleware.HttpContextKey] = new AuthorizationContext { UserId = "ops", Role = "admin", Permissions = [] };
        return (controller, source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("GW-OTHER")]
    [InlineData("GW-EXAMPLE,GW-OTHER")]
    public async Task Unauthorized_ConditionalRequests_NeverReadTwinOrReturn304(string? caller)
    {
        var (c, source) = Build(caller);
        c.Request.Headers.IfNoneMatch = GatewayConfigurationResponse.From("GW-EXAMPLE", Snapshot()).Revision;
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await c.Get("GW-EXAMPLE", null, default)).StatusCode);
        source.Verify(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayOrAdmin_CanRead_AndRevalidate(bool admin)
    {
        var (c, source) = Build(admin ? null : "GW-EXAMPLE", admin);
        var first = Assert.IsType<JsonResult>(await c.Get("GW-EXAMPLE", null, default));
        var body = Assert.IsType<GatewayConfigurationResponse>(first.Value);
        Assert.Empty(body.Points);
        Assert.Single(body.ClockSources);
        Assert.Equal(body.Revision, c.Response.Headers.ETag);
        Assert.Equal("private, no-cache", c.Response.Headers.CacheControl);
        c.Request.Headers.IfNoneMatch = body.Revision;
        Assert.Equal(304, Assert.IsType<StatusCodeResult>(await c.Get("GW-EXAMPLE", null, default)).StatusCode);
        source.Verify(s => s.ReadAsync("GW-EXAMPLE", It.IsAny<CancellationToken>()), Times.Exactly(2));
        source.Setup(s => s.ReadAsync("GW-EXAMPLE", It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(3));
        Assert.IsType<JsonResult>(await c.Get("GW-EXAMPLE", null, default));
        Assert.NotEqual(body.Revision, c.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task SinceIsUnsupported_AndInvalidConfigIsWithheld()
    {
        var (c, source) = Build("GW-EXAMPLE");
        Assert.IsType<BadRequestObjectResult>(await c.Get("GW-EXAMPLE", "previous", default));
        source.Setup(s => s.ReadAsync("GW-EXAMPLE", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ClockConfigurationException([new("https://example.com/other-gateway", "gateway_mismatch")]));
        var failure = Assert.IsType<ConflictObjectResult>(await c.Get("GW-EXAMPLE", null, default));
        Assert.DoesNotContain("other-gateway", System.Text.Json.JsonSerializer.Serialize(failure.Value));
    }

    [Fact]
    public void RevisionIncludesClocksAndBindings_AndIsOrderIndependent()
    {
        var s = Snapshot();
        var first = GatewayConfigurationResponse.From("GW-EXAMPLE", s).Revision;
        Assert.Equal(first, GatewayConfigurationResponse.From("GW-EXAMPLE", s).Revision);
        Assert.NotEqual(first, GatewayConfigurationResponse.From("GW-EXAMPLE", Snapshot(3)).Revision);
        var bindings = new[] { new EquipmentClockBinding("https://example.com/e2", s.ClockSources[0].ClockId), new EquipmentClockBinding("https://example.com/e1", s.ClockSources[0].ClockId) };
        var bound = s with { ClockBindings = bindings };
        Assert.NotEqual(first, GatewayConfigurationResponse.From("GW-EXAMPLE", bound).Revision);
        Assert.Equal(GatewayConfigurationResponse.From("GW-EXAMPLE", bound).Revision,
            GatewayConfigurationResponse.From("GW-EXAMPLE", bound with { ClockBindings = bindings.Reverse().ToArray() }).Revision);
    }
}
