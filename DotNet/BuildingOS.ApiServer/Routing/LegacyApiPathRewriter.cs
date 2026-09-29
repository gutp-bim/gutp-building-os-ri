using System.Diagnostics;
using System.Globalization;
using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOs.ApiServer.Routing;

/// <summary>
/// Keeps the pre-versioning REST paths working (#507, ADR-0008). Before <c>api/v1</c> the API was
/// split between un-prefixed roots (<c>/buildings</c>, <c>/telemetries</c>, …) and <c>/api/…</c>; clients
/// that still call those — external applications, scripts, an older web client — are rewritten
/// in-process to the <c>/api/v1</c> route before routing, so every endpoint has exactly one definition
/// and one OpenAPI entry. A rewritten response carries <c>Deprecation</c> (RFC 9745) and a
/// <c>successor-version</c> link so a client can see it should move.
/// <para>
/// Only the roots that existed before versioning are aliased (a typo under <c>/api</c> is not), and
/// the gateway point list is never aliased — it is not versioned at all (<see cref="ApiRoutes.GatewayProvisioning"/>).
/// </para>
/// </summary>
public sealed class LegacyApiPathRewriter(RequestDelegate next, ILogger<LegacyApiPathRewriter>? logger = null)
{
    /// <summary><see cref="HttpContext.Items"/> key holding the path the client actually sent.</summary>
    public const string OriginalPathItem = "BuildingOs.LegacyApiPath";

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
    /// The first segments under <c>/api/</c> that existed before versioning, and the v1 segment each
    /// maps to (v1 is lower-kebab, ADR-0008 §1: <c>MyResources</c> → <c>my-resources</c>). Only these are
    /// aliased — not any <c>/api/…</c> path — so no path that was never an endpoint becomes one.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> LegacyApiSegments =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Auth"] = "auth", ["Groups"] = "groups", ["Users"] = "users", ["MyResources"] = "my-resources",
            ["Permissions"] = "permissions", ["admin"] = "admin", ["telemetry"] = "telemetry",
            ["system"] = "system", ["operations"] = "operations", ["assistant"] = "assistant",
        };

    /// <summary>
    /// v1 segments first published in another spelling and renamed by the lower-kebab rule, which a
    /// case-insensitive route does not cover on its own (<c>/api/v1/MyResources</c> briefly existed).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RenamedV1Segments =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MyResources"] = "my-resources" };

    private static readonly PathString V1 = new("/" + ApiRoutes.V1);

    private static readonly PathString Api = new("/api");

    /// <summary>The <c>/api/v1</c> path a legacy <paramref name="path"/> maps to; false for anything else.</summary>
    public static bool TryRewrite(PathString path, out PathString rewritten)
        => TryRewrite(path, out rewritten, out _);

    /// <param name="path">The (decoded) request path.</param>
    /// <param name="rewritten">The <c>/api/v1</c> path to route instead.</param>
    /// <param name="root">Bounded tag naming the legacy root, e.g. <c>telemetries</c> or <c>api/groups</c>.</param>
    private static bool TryRewrite(PathString path, out PathString rewritten, out string root)
    {
        rewritten = default;
        root = "";

        // "/api/v1/MyResources[/…]" → "/api/v1/my-resources[/…]" (renamed within v1).
        if (path.StartsWithSegments(V1, StringComparison.OrdinalIgnoreCase, out var v1Rest))
        {
            var segment = FirstSegment(v1Rest);
            if (!RenamedV1Segments.TryGetValue(segment, out var renamed)
                || string.Equals(segment, renamed, StringComparison.OrdinalIgnoreCase)) return false;
            rewritten = V1.Add("/" + renamed + v1Rest.Value![(segment.Length + 1)..]);
            root = "api/v1/" + segment.ToLowerInvariant();
            return true;
        }

        // "/api/{segment}[/…]" → "/api/v1/{v1-segment}[/…]" for the pre-versioning segments only.
        if (path.StartsWithSegments(Api, StringComparison.OrdinalIgnoreCase, out var rest))
        {
            var segment = FirstSegment(rest);
            if (!LegacyApiSegments.TryGetValue(segment, out var target)) return false;
            rewritten = V1.Add("/" + target + rest.Value![(segment.Length + 1)..]);
            root = "api/" + segment.ToLowerInvariant();
            return true;
        }

        // "/{legacy-root}[/…]" → "/api/v1/{legacy-root}[/…]"
        var first = FirstSegment(path);
        if (!LegacyRoots.Contains(first)) return false;
        rewritten = new PathString("/" + ApiRoutes.V1).Add(path);
        root = first.ToLowerInvariant();
        return true;
    }

    /// <summary>The first segment of a path such as "/a/b" ("a"); empty for "" or "/".</summary>
    private static string FirstSegment(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value.Length < 2) return "";
        var end = value.IndexOf('/', 1);
        return end < 0 ? value[1..] : value[1..end];
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (TryRewrite(context.Request.Path, out var rewritten, out var root))
        {
            var original = context.Request.Path;
            // Bounded tag (a root from the fixed sets), so this is safe as a metric label; it is the
            // evidence ADR-0008 §4 needs before the old paths can be given a Sunset date. The exact
            // path (unbounded) goes to the trace and a Debug log instead, to find who still calls it.
            BuildingOsMetrics.ApiLegacyRequests.Add(1, new KeyValuePair<string, object?>("root", root));
            context.Items[OriginalPathItem] = original.Value;
            Activity.Current?.SetTag("building_os.api.legacy_path", original.Value);
            logger?.LogDebug(
                "Legacy API path {LegacyPath} rewritten to {Path} (User-Agent {UserAgent})",
                original.Value, rewritten.Value, context.Request.Headers.UserAgent.ToString());

            context.Request.Path = rewritten;
            context.Response.Headers["Deprecation"] = DeprecatedSince;
            // Header values must be ASCII: the path is decoded (a Japanese point id would throw), so
            // the link is built from the URI-encoded forms.
            context.Response.Headers["Link"] =
                $"<{rewritten.ToUriComponent()}{context.Request.QueryString.ToUriComponent()}>; rel=\"successor-version\"";
        }
        return next(context);
    }
}
