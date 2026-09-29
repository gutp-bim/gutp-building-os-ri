using System.Globalization;
using System.Text.RegularExpressions;

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

    /// <summary>The un-prefixed first path segments the API answered on before versioning.</summary>
    private static readonly HashSet<string> LegacyRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "buildings", "floors", "spaces", "devices", "points", "telemetries", "resources", "gateways",
        "point-details", "device-details",
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

        // "/api/…" (not versioned) → "/api/v1/…"
        if (value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            rewritten = new PathString("/" + ApiRoutes.V1 + value[4..]);
            return true;
        }

        // "/{legacy-root}[/…]" → "/api/v1/{legacy-root}[/…]"
        var end = value.IndexOf('/', 1);
        var first = end < 0 ? value[1..] : value[1..end];
        if (!LegacyRoots.Contains(first)) return false;
        rewritten = new PathString("/" + ApiRoutes.V1 + value);
        return true;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (TryRewrite(context.Request.Path, out var rewritten))
        {
            context.Request.Path = rewritten;
            context.Response.Headers["Deprecation"] = DeprecatedSince;
            context.Response.Headers["Link"] = $"<{rewritten.Value}>; rel=\"successor-version\"";
        }
        return next(context);
    }
}
