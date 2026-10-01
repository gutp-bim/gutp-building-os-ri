using System.Security.Cryptography;
using System.Text;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOs.ApiServer.Authorization;

/// <summary>
/// The nodes a non-admin may see in the hierarchy lists as navigation containers (#548): the
/// ancestors (building, floor, room, device) of every floor, space, device and point they can read.
/// </summary>
public interface INavigableAncestorResolver
{
    /// <summary>
    /// Ancestors as <c>(resourceType, businessId)</c>. Empty for admin (who sees everything anyway).
    /// </summary>
    Task<IReadOnlySet<(string ResourceType, string ResourceId)>> ResolveAsync(
        AuthorizationContext auth, CancellationToken ct);
}

/// <summary>
/// Without this a user granted only <c>space:R501</c> (or a floor, device or point) saw no building at
/// all — every hierarchy list shows a node only for a grant on it or on its parent — so the room was
/// reachable by URL alone, and <c>/home</c> / <c>/health</c> had no building to pick.
///
/// <para>Listing an ancestor reveals its name and position, nothing more: reading it
/// (<c>GET /buildings/{id}</c>) stays Forbidden, and its other children stay hidden because each list
/// still filters them by grant or by this same ancestor set.</para>
///
/// <para>Grants are matched by business id (#504). A direct grant stores only a hash, so its id comes
/// from the id-mapping table; a hash with no mapping cannot be placed in the twin and is skipped.</para>
///
/// <para><b>Cached across requests</b> for <see cref="CacheTtl"/>, keyed by the user and their exact
/// permission set: <c>/home</c> asks for the devices of every visible space, one HTTP request (and one
/// scoped view) each, so a request-scoped cache alone would re-resolve a large point grant set per
/// space. Concurrent requests share one resolution; a failed one is not cached. A changed grant or
/// Group membership, or a twin edit, shows after at most the TTL — the same trade-off as the
/// data-health inventory cache (#452).</para>
/// </summary>
public sealed class NavigableAncestorResolver(
    IAuthorizationService authService,
    IResourceIdMappingRepository mapping,
    IResourceHierarchyResolver hierarchy,
    IMemoryCache cache) : INavigableAncestorResolver
{
    /// <summary>How long one user's ancestor set is reused across requests.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Building grants have no ancestors; the four types below are the ones that can.</summary>
    private static readonly string[] DescendantTypes = ["floor", "space", "device", "point"];

    private static readonly IReadOnlySet<(string, string)> None = new HashSet<(string, string)>();
    private static readonly object Gate = new();

    public async Task<IReadOnlySet<(string ResourceType, string ResourceId)>> ResolveAsync(
        AuthorizationContext auth, CancellationToken ct)
    {
        if (auth.IsAdmin) return None;

        var key = CacheKey(auth);
        var (shared, startedHere) = GetOrStart(key, auth);
        try
        {
            return await shared.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!startedHere && !ct.IsCancellationRequested)
        {
            // The resolution we joined ran on another request's scoped services (its DbContext), and
            // that request may have ended under it. Failures are never cached, so this starts afresh on
            // our own scope — once; a second failure is ours to report.
            var (retry, _) = GetOrStart(key, auth);
            return await retry.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private (Task<IReadOnlySet<(string ResourceType, string ResourceId)>> Task, bool StartedHere) GetOrStart(
        string key, AuthorizationContext auth)
    {
        lock (Gate)
        {
            if (cache.TryGetValue(key, out Task<IReadOnlySet<(string ResourceType, string ResourceId)>>? cached)
                && cached is { IsFaulted: false, IsCanceled: false })
                return (cached, false);

            // Not tied to this caller's token: other requests may be awaiting the same task.
            var started = ResolveUncachedAsync(auth, CancellationToken.None);
            cache.Set(key, started, CacheTtl);
            _ = started.ContinueWith(
                _ =>
                {
                    lock (Gate)
                    {
                        if (cache.TryGetValue(key, out object? current) && ReferenceEquals(current, started))
                            cache.Remove(key);
                    }
                },
                CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            return (started, true);
        }
    }

    /// <summary>User + the exact permission set (order-insensitive), hashed so the key stays short.</summary>
    private static string CacheKey(AuthorizationContext auth)
    {
        var material = auth.UserId + "\n" + string.Join("\n", auth.Permissions.Order(StringComparer.Ordinal));
        return "nav-ancestors:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private async Task<IReadOnlySet<(string ResourceType, string ResourceId)>> ResolveUncachedAsync(
        AuthorizationContext auth, CancellationToken ct)
    {
        var result = new HashSet<(string, string)>();

        var granted = new Dictionary<string, IReadOnlyList<AccessibleResource>>();
        foreach (var type in DescendantTypes)
            granted[type] = await authService.GetAccessibleResourcesAsync(auth, type, "read", ct).ConfigureAwait(false);

        // One mapping lookup for every hash-only grant across the four types.
        var unknown = granted.Values.SelectMany(rs => rs).Where(r => r.OriginalId is null).Select(r => r.Hash)
            .Distinct(StringComparer.Ordinal).ToList();
        var originals = unknown.Count == 0
            ? new Dictionary<string, string>()
            : await mapping.ResolveOriginalIdsAsync(unknown, ct).ConfigureAwait(false);

        foreach (var (type, resources) in granted)
        {
            var ids = resources
                .Select(r => r.OriginalId ?? (originals.TryGetValue(r.Hash, out var id) ? id : null))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0) continue;

            foreach (var ancestor in await hierarchy.GetAncestorUnionAsync(type, ids, ct).ConfigureAwait(false))
                result.Add(ancestor);
        }
        return result;
    }
}
