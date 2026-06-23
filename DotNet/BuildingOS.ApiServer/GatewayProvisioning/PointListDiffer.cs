using BuildingOS.Shared;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>Delta between two gateway point lists (#224/diff). Identity is PointId.</summary>
public sealed class PointListDiff
{
    public GatewayPointEntry[] Added { get; init; } = [];
    public string[] Removed { get; init; } = []; // pointIds
    public GatewayPointEntry[] Changed { get; init; } = [];

    public bool HasChanges => Added.Length > 0 || Removed.Length > 0 || Changed.Length > 0;
}

/// <summary>
/// Pure diff of two gateway point lists. Added = in current not previous; Removed = in previous not
/// current; Changed = same PointId but different canonical content. Order-independent.
/// </summary>
public static class PointListDiffer
{
    public static PointListDiff Diff(
        IReadOnlyList<GatewayPointEntry> previous,
        IReadOnlyList<GatewayPointEntry> current)
    {
        var prevById = previous.ToDictionary(e => e.PointId, GatewayPointListSerializer.CanonicalString);
        var currById = current.ToDictionary(e => e.PointId);

        var added = current.Where(e => !prevById.ContainsKey(e.PointId)).ToArray();
        var removed = previous.Where(e => !currById.ContainsKey(e.PointId))
                              .Select(e => e.PointId).ToArray();
        var changed = current
            .Where(e => prevById.TryGetValue(e.PointId, out var prevCanon)
                        && prevCanon != GatewayPointListSerializer.CanonicalString(e))
            .ToArray();

        return new PointListDiff { Added = added, Removed = removed, Changed = changed };
    }
}
