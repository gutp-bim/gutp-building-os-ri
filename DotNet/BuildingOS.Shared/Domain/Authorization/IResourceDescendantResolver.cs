namespace BuildingOS.Shared.Domain.Authorization;

/// <summary>
/// The inverse of <see cref="IResourceHierarchyResolver"/> (#509): the descendants of granted nodes,
/// by business id. It must follow exactly the paths the ancestor chain follows — a descendant listed
/// here is one <see cref="IAuthorizationService.CanAccessAsync"/> lets the grant reach — so a client
/// never gets an id it cannot then read, nor misses one it can.
/// </summary>
public interface IResourceDescendantResolver
{
    /// <summary>The resource types in hierarchy order, root first.</summary>
    static readonly IReadOnlyList<string> Types = ["building", "floor", "space", "device", "point"];

    /// <summary>
    /// Descendant business ids of <paramref name="roots"/>, per resource type, for every type below a
    /// root down to and including <paramref name="targetType"/>. The roots themselves are not included.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDescendantsAsync(
        IReadOnlyCollection<(string ResourceType, string ResourceId)> roots,
        string targetType,
        CancellationToken ct = default);
}
