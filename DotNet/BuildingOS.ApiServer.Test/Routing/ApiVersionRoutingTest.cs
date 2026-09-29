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
        // The gateway point list is the one deliberate exception: machine auth behind the mTLS ingress,
        // so it must stay off /api (whose ingress route neither requires mTLS nor strips X-Gateway-Id).
        var unversioned = templates
            .Where(t => !t.StartsWith(ApiRoutes.V1 + "/", StringComparison.Ordinal)
                        && !t.StartsWith(ApiRoutes.GatewayProvisioning + "/", StringComparison.Ordinal))
            .ToList();
        Assert.True(unversioned.Count == 0, "Not under api/v1: " + string.Join(", ", unversioned));
        Assert.DoesNotContain(templates, t => t.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
                                              && t.EndsWith("/{gatewayId}/pointlist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(templates, t => t == ApiRoutes.GatewayProvisioning + "/{gatewayId}/pointlist");
        // Naming rule (ADR-0008 §1): every literal segment is lower-kebab; only {parameters} may differ.
        var badlyNamed = templates
            .Where(t => t.Split('/').Any(seg => !seg.StartsWith('{')
                && !System.Text.RegularExpressions.Regex.IsMatch(seg, "^[a-z0-9]+(-[a-z0-9]+)*$")))
            .ToList();
        Assert.True(badlyNamed.Count == 0, "Not lower-kebab: " + string.Join(", ", badlyNamed));
        // A formerly absolute action route ("/buildings/{id}/metadata") must not have been combined
        // with a controller prefix into "api/v1/…/api/v1/…".
        var doubled = templates.Where(t => t.IndexOf(ApiRoutes.V1, 1, StringComparison.Ordinal) >= 0).ToList();
        Assert.True(doubled.Count == 0, "Doubled prefix: " + string.Join(", ", doubled));
        // Every alias the rewriter creates lands on a real v1 route, so it never points at nothing.
        foreach (var root in LegacyApiPathRewriter.LegacyRoots)
            Assert.Contains(templates, t => t.StartsWith($"{ApiRoutes.V1}/{root}", StringComparison.OrdinalIgnoreCase));
        foreach (var target in LegacyApiPathRewriter.LegacyApiSegments.Values)
            Assert.Contains(templates, t => t.StartsWith($"{ApiRoutes.V1}/{target}", StringComparison.Ordinal));
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
    [InlineData("/point-details", "/api/v1/point-details")]
    [InlineData("/device-details", "/api/v1/device-details")]
    [InlineData("/Buildings", "/api/v1/Buildings")] // routing is case-insensitive, so is the alias
    // The pre-versioning PascalCase segments land on the lower-kebab v1 spelling (ADR-0008 §1).
    [InlineData("/api/Groups", "/api/v1/groups")]
    [InlineData("/api/Groups/g1/resources/bulk", "/api/v1/groups/g1/resources/bulk")]
    [InlineData("/api/MyResources", "/api/v1/my-resources")]
    [InlineData("/api/MyResources/accessible", "/api/v1/my-resources/accessible")]
    [InlineData("/api/myresources", "/api/v1/my-resources")]
    [InlineData("/api/Users/u1/permissions", "/api/v1/users/u1/permissions")]
    [InlineData("/api/Permissions/resolve", "/api/v1/permissions/resolve")]
    [InlineData("/api/Auth/me", "/api/v1/auth/me")]
    // The PascalCase v1 spelling that briefly existed before the casing fix keeps working.
    [InlineData("/api/v1/MyResources", "/api/v1/my-resources")]
    [InlineData("/api/v1/MyResources/accessible", "/api/v1/my-resources/accessible")]
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
    [InlineData("/api/v1/my-resources")]
    [InlineData("/api/v1/Groups")] // differs only in case: routing is case-insensitive, no alias needed
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
    // Never aliased: the point list is not versioned, and must not become reachable under /api.
    [InlineData("/gateways/gw-1/pointlist")]
    [InlineData("/api/gateways/gw-1/pointlist")]
    // Only prefixes that existed before versioning are aliased, not any /api path.
    [InlineData("/api/buildings/x")]
    [InlineData("/api/typo")]
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
        Assert.Equal("</api/v1/points/PT001?x=1>; rel=\"successor-version\"", ctx.Response.Headers["Link"].ToString());
    }

    [Fact]
    public async Task Middleware_NonAsciiLegacyPath_GetsAnEncodedLinkHeader()
    {
        // Kestrel has already decoded the path; a header must be ASCII, so the successor link is the
        // URI-encoded form (a Japanese point id would otherwise throw and turn a 200 into a 500).
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/points/温度>1/metadata";
        ctx.Request.QueryString = QueryString.Create("q", "a b");
        await new LegacyApiPathRewriter(_ => Task.CompletedTask).InvokeAsync(ctx);

        var link = ctx.Response.Headers["Link"].ToString();
        Assert.All(link, ch => Assert.InRange(ch, (char)0x20, (char)0x7E));
        // ">" would end the <URI-Reference> early, so it must be encoded too.
        Assert.Equal("</api/v1/points/%E6%B8%A9%E5%BA%A6%3E1/metadata?q=a%20b>; rel=\"successor-version\"", link);
        Assert.Equal("/api/v1/points/温度>1/metadata", ctx.Request.Path.Value);
    }

    [Fact]
    public async Task Middleware_KeepsTheOriginalLegacyPath_ForLogsAndTraces()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/telemetries/query";
        await new LegacyApiPathRewriter(_ => Task.CompletedTask).InvokeAsync(ctx);

        Assert.Equal("/telemetries/query", ctx.Items[LegacyApiPathRewriter.OriginalPathItem]);
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

    [Fact]
    public async Task Middleware_CountsLegacyRequests_ByRoot()
    {
        // ADR-0008 §4: removing the old paths needs evidence that nobody still calls them.
        var seen = new List<(long Value, string? Root)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "building_os.api.legacy_requests") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? root = null;
            foreach (var tag in tags) if (tag.Key == "root") root = tag.Value as string;
            lock (seen) seen.Add((value, root));
        });
        listener.Start();

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/Groups/g1";
        await new LegacyApiPathRewriter(_ => Task.CompletedTask).InvokeAsync(ctx);
        ctx = new DefaultHttpContext();
        ctx.Request.Path = "/Telemetries/query";
        await new LegacyApiPathRewriter(_ => Task.CompletedTask).InvokeAsync(ctx);
        ctx = new DefaultHttpContext();
        ctx.Request.Path = "/api/v1/buildings";
        await new LegacyApiPathRewriter(_ => Task.CompletedTask).InvokeAsync(ctx);

        lock (seen)
        {
            Assert.Contains((1L, "api/groups"), seen);
            Assert.Contains((1L, "telemetries"), seen);
            Assert.Equal(2, seen.Count);
        }
    }
}
