using BuildingOS.Shared.Domain.Authorization;

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
/// </summary>
public sealed class NavigableAncestorResolver(
    IAuthorizationService authService,
    IResourceIdMappingRepository mapping,
    IResourceHierarchyResolver hierarchy) : INavigableAncestorResolver
{
    /// <summary>Building grants have no ancestors; the four types below are the ones that can.</summary>
    private static readonly string[] DescendantTypes = ["floor", "space", "device", "point"];

    public async Task<IReadOnlySet<(string ResourceType, string ResourceId)>> ResolveAsync(
        AuthorizationContext auth, CancellationToken ct)
    {
        var result = new HashSet<(string, string)>();
        if (auth.IsAdmin) return result;

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
