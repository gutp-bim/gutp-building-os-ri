using System.Globalization;
using System.Text.RegularExpressions;
using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOs.ApiServer.Routing;

/// <summary>
/// Keeps the pre-versioning REST paths working (#507, ADR-0008). Before <c>api/v1</c> the API was
/// split between un-prefixed roots (<c>/buildings</c>, <c>/telemetries</c>, …) and <c>/api/…</c>; clients
/// that still call those — the web client until it moves, gateways polling
/// <c>/gateways/{id}/pointlist</c>, external applications — are rewritten in-process to the
/// <c>/api/v1</c> route before routing, so every endpoint has exactly one definition and one OpenAPI
/// entry. A rewritten response carries <c>Deprecation</c> (RFC 9745) and a <c>successor-version</c>
/// link so a client can see it should move.
/// </summary>
public sealed partial class LegacyApiPathRewriter(RequestDelegate next)
{
    /// <summary>When the legacy paths were deprecated (ADR-0008), as the RFC 9745 structured date.</summary>
    private static readonly string DeprecatedSince =
        "@" + new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The un-prefixed first path segments the API answered on before versioning. <c>gateways</c> is
    /// absent on purpose: the point list stays at <c>/gateways/…</c> (see <see cref="ApiRoutes.GatewayProvisioning"/>).
    /// </summary>
    internal static readonly IReadOnlySet<string> LegacyRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "buildings", "floors", "spaces", "devices", "points", "telemetries", "resources",
        "point-details", "device-details",
    };

    /// <summary>
    /// The first segments under <c>/api/</c> that existed before versioning. Only these are aliased —
    /// not any <c>/api/…</c> path — so a typo 404s plainly instead of pointing at a successor that does
    /// not exist, and no path that was never an endpoint becomes one.
    /// </summary>
    internal static readonly IReadOnlySet<string> LegacyApiSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Auth", "Groups", "Users", "MyResources", "Permissions", "admin", "telemetry", "system",
        "operations", "assistant",
    };

    [GeneratedRegex(@"^/api/v\d+(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedApi();

    /// <summary>The <c>/api/v1</c> path a legacy <paramref name="path"/> maps to; false for anything else.</summary>
    public static bool TryRewrite(PathString path, out PathString rewritten)
    {
        rewritten = default;
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value == "/") return false;
        if (VersionedApi().IsMatch(value)) return false;

        // "/api/{segment}[/…]" (not versioned) → "/api/v1/{segment}[/…]"
        if (value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            if (!LegacyApiSegments.Contains(FirstSegment(value, 5))) return false;
            rewritten = new PathString("/" + ApiRoutes.V1 + value[4..]);
            return true;
        }

        // "/{legacy-root}[/…]" → "/api/v1/{legacy-root}[/…]"
        if (!LegacyRoots.Contains(FirstSegment(value, 1))) return false;
        rewritten = new PathString("/" + ApiRoutes.V1 + value);
        return true;
    }

    private static string FirstSegment(string path, int from)
    {
        var end = path.IndexOf('/', from);
        return end < 0 ? path[from..] : path[from..end];
    }

    /// <summary>The metric tag for a legacy path: its root, lower-cased, from the fixed sets above.</summary>
    private static string LegacyRootTag(string path)
        => path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? "api/" + FirstSegment(path, 5).ToLowerInvariant()
            : FirstSegment(path, 1).ToLowerInvariant();

    public Task InvokeAsync(HttpContext context)
    {
        if (TryRewrite(context.Request.Path, out var rewritten))
        {
            // Bounded tag (a root from the fixed sets), so this is safe as a metric label; it is the
            // evidence ADR-0008 §4 needs before the old paths can be given a Sunset date.
            BuildingOsMetrics.ApiLegacyRequests.Add(1,
                new KeyValuePair<string, object?>("root", LegacyRootTag(context.Request.Path.Value!)));
            context.Request.Path = rewritten;
            context.Response.Headers["Deprecation"] = DeprecatedSince;
            context.Response.Headers["Link"] =
                $"<{rewritten.Value}{context.Request.QueryString.Value}>; rel=\"successor-version\"";
        }
        return next(context);
    }
}
