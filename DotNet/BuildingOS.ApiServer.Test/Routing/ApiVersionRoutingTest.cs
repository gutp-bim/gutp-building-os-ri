using BuildingOs.ApiServer;
using BuildingOs.ApiServer.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingOS.ApiServer.Test.Routing;

/// <summary>
/// #507: every REST endpoint lives under <c>/api/v1/</c>, and the pre-versioning paths keep working by
/// being rewritten to it (with a deprecation notice), so existing clients — the web client, gateways
/// polling the point list, external applications — are unaffected until they move.
/// </summary>
public class ApiVersionRoutingTest
{
    // ── every controller route is versioned ──────────────────────────────────

    [Fact]
    public void EveryControllerAction_IsRoutedUnderApiV1()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers()
            .ConfigureApplicationPartManager(m =>
            {
                m.ApplicationParts.Clear();
                m.ApplicationParts.Add(new AssemblyPart(typeof(Startup).Assembly));
            });
        var provider = services.BuildServiceProvider().GetRequiredService<IActionDescriptorCollectionProvider>();

        var templates = provider.ActionDescriptors.Items
            .Select(a => a.AttributeRouteInfo?.Template ?? $"<conventional:{a.DisplayName}>")
            .ToList();

        Assert.NotEmpty(templates);
        var unversioned = templates.Where(t => !t.StartsWith(ApiRoutes.V1 + "/", StringComparison.Ordinal)).ToList();
        Assert.True(unversioned.Count == 0, "Not under api/v1: " + string.Join(", ", unversioned));
        // A formerly absolute action route ("/buildings/{id}/metadata") must not have been combined
        // with a controller prefix into "api/v1/…/api/v1/…".
        var doubled = templates.Where(t => t.IndexOf(ApiRoutes.V1, 1, StringComparison.Ordinal) >= 0).ToList();
        Assert.True(doubled.Count == 0, "Doubled prefix: " + string.Join(", ", doubled));
        // Every pre-versioning root still has a v1 home, so the rewriter never points at nothing.
        foreach (var root in new[] { "buildings", "floors", "spaces", "devices", "points", "telemetries",
                     "resources", "gateways", "point-details", "device-details" })
            Assert.Contains(templates, t => t.StartsWith($"{ApiRoutes.V1}/{root}", StringComparison.OrdinalIgnoreCase));
    }

    // ── legacy paths are rewritten, nothing else is ──────────────────────────

    [Theory]
    [InlineData("/buildings", "/api/v1/buildings")]
    [InlineData("/buildings/https%3A%2F%2Fx%2Fb1/metadata", "/api/v1/buildings/https%3A%2F%2Fx%2Fb1/metadata")]
    [InlineData("/floors", "/api/v1/floors")]
    [InlineData("/spaces/x/adjacent-spaces", "/api/v1/spaces/x/adjacent-spaces")]
    [InlineData("/devices", "/api/v1/devices")]
    [InlineData("/points/PT001/control", "/api/v1/points/PT001/control")]
    [InlineData("/telemetries/query", "/api/v1/telemetries/query")]
    [InlineData("/resources/search", "/api/v1/resources/search")]
    [InlineData("/gateways/gw-1/pointlist", "/api/v1/gateways/gw-1/pointlist")]
    [InlineData("/point-details", "/api/v1/point-details")]
    [InlineData("/device-details", "/api/v1/device-details")]
    [InlineData("/Buildings", "/api/v1/Buildings")] // routing is case-insensitive, so is the alias
    [InlineData("/api/Groups", "/api/v1/Groups")]
    [InlineData("/api/MyResources", "/api/v1/MyResources")]
    [InlineData("/api/admin/twin/import/apply", "/api/v1/admin/twin/import/apply")]
    [InlineData("/api/telemetry/health/summary", "/api/v1/telemetry/health/summary")]
    [InlineData("/api/system/status", "/api/v1/system/status")]
    public void LegacyPath_IsRewrittenUnderApiV1(string legacy, string expected)
    {
        Assert.True(LegacyApiPathRewriter.TryRewrite(new PathString(legacy), out var rewritten));
        Assert.Equal(expected, rewritten.Value);
    }

    [Theory]
    [InlineData("/api/v1/buildings")]
    [InlineData("/api/V1/buildings")]
    [InlineData("/api/v2/buildings")] // a future version is not a legacy path
    [InlineData("/api/v1")]
    [InlineData("/health")]
    [InlineData("/swagger/building-os/swagger.json")]
    [InlineData("/api-docs")]
    [InlineData("/building_os.PointControlService/WaitForResult")] // gRPC(-web)
    [InlineData("/greet.Greeter/SayHello")]
    [InlineData("/")]
    [InlineData("/buildingsX")] // only whole first segments match
    [InlineData("/api")]
    public void NonLegacyPath_IsLeftAlone(string path)
        => Assert.False(LegacyApiPathRewriter.TryRewrite(new PathString(path), out _));

    // ── middleware ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Middleware_RewritesALegacyRequest_AndMarksItDeprecated()
    {
        string? seenPath = null;
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/points/PT001";
        ctx.Request.QueryString = new QueryString("?x=1");
        var mw = new LegacyApiPathRewriter(c => { seenPath = c.Request.Path; return Task.CompletedTask; });

        await mw.InvokeAsync(ctx);

        Assert.Equal("/api/v1/points/PT001", seenPath);
        Assert.Equal("?x=1", ctx.Request.QueryString.Value);
        Assert.StartsWith("@", ctx.Response.Headers["Deprecation"].ToString());
        Assert.Equal("</api/v1/points/PT001>; rel=\"successor-version\"", ctx.Response.Headers["Link"].ToString());
    }

    [Fact]
    public async Task Middleware_LeavesAVersionedRequest_Unmarked()
    {
        string? seenPath = null;
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/v1/points/PT001";
        var mw = new LegacyApiPathRewriter(c => { seenPath = c.Request.Path; return Task.CompletedTask; });

        await mw.InvokeAsync(ctx);

        Assert.Equal("/api/v1/points/PT001", seenPath);
        Assert.False(ctx.Response.Headers.ContainsKey("Deprecation"));
    }
}
