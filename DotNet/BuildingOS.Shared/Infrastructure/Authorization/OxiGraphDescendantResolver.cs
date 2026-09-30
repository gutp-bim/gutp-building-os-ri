using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Infrastructure.Authorization;

using static OxiGraphOntology;

/// <summary>
/// <see cref="IResourceDescendantResolver"/> backed by OxiGraph SPARQL (#509), traversing topology only
/// (hasPart / locatedIn / hasPoint). Every pattern here is the inverse of one in
/// <see cref="OxiGraphHierarchyResolver"/>, with the same class checks and the same completeness
/// requirements — e.g. a room's grant reaches its equipment only when the room sits on a level of a
/// building, because that is when the equipment's ancestor chain contains the room. The ancestor side
/// is the union of every placement, so following every placement here is exact. Keep the two in step:
/// <c>DescendantExpansionTest</c> checks every expanded id against CanAccessAsync.
/// </summary>
public sealed class OxiGraphDescendantResolver(OxiGraphClient client) : IResourceDescendantResolver
{
    private static readonly IReadOnlyList<string> Types = IResourceDescendantResolver.Types;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDescendantsAsync(
        IReadOnlyCollection<(string ResourceType, string ResourceId)> roots,
        string targetType,
        CancellationToken ct = default)
    {
        var targetRank = Rank(targetType);
        var found = Types.ToDictionary(t => t, _ => new SortedSet<string>(StringComparer.Ordinal));

        // One query per (root type, child type); independent, so issued concurrently.
        var queries = new List<(string ChildType, Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> Rows)>();
        foreach (var group in roots.Where(r => Rank(r.ResourceType) >= 0).GroupBy(r => r.ResourceType))
        {
            var rootType = group.Key;
            var ids = group.Select(r => r.ResourceId).Distinct(StringComparer.Ordinal).ToList();
            for (var rank = Rank(rootType) + 1; rank <= targetRank; rank++)
            {
                var childType = Types[rank];
                var pattern = ChildPattern(rootType, childType);
                if (pattern is null) continue;
                queries.Add((childType, client.QueryAsync(Query(rootType, ids, pattern), ct)));
            }
        }
        await Task.WhenAll(queries.Select(q => q.Rows)).ConfigureAwait(false);
        foreach (var (childType, rows) in queries)
            foreach (var row in await rows.ConfigureAwait(false))
                if (row.TryGetValue("nid", out var nid)) found[childType].Add(nid);

        return Types
            .Where(t => Rank(t) <= targetRank)
            .ToDictionary(t => t, t => (IReadOnlyList<string>)found[t].ToList());
    }

    private static int Rank(string type)
    {
        for (var i = 0; i < Types.Count; i++)
            if (Types[i] == type) return i;
        return -1;
    }

    private static string RootClass(string type) => type switch
    {
        "building" => Cls_Building,
        "floor" => Cls_Level,
        "space" => Cls_Space,
        "device" => Cls_Equipment,
        _ => Cls_Point,
    };

    private static string Query(string rootType, IReadOnlyList<string> ids, string pattern)
    {
        var values = string.Join(" ", ids.Select(id => $"\"{EscapeLiteral(id)}\""));
        return $@"{Prefixes}
SELECT DISTINCT ?nid WHERE {{
  VALUES ?rootId {{ {values} }}
  ?root a <{RootClass(rootType)}> ; <{Prop_Id}> ?rootId .
{pattern}
}}";
    }

    // Equipment {e} under ?root, per the two placements GetDeviceAncestors accepts. Each branch keeps
    // the full chain up to a Building, as the ancestor query requires it to report that level.
    private static string EquipmentUnder(string rootType, string e) => rootType switch
    {
        "building" => $@"
  {e} a <{Cls_Equipment}> .
  {{ {e} <{Prop_LocatedIn}> ?s . ?s a <{Cls_Space}> . ?f <{Prop_HasPart}> ?s . ?f a <{Cls_Level}> . ?root <{Prop_HasPart}> ?f . }}
  UNION {{ {e} <{Prop_LocatedIn}> ?f . ?f a <{Cls_Level}> . ?root <{Prop_HasPart}> ?f . }}",
        "floor" => $@"
  ?b a <{Cls_Building}> ; <{Prop_HasPart}> ?root .
  {e} a <{Cls_Equipment}> .
  {{ {e} <{Prop_LocatedIn}> ?s . ?s a <{Cls_Space}> . ?root <{Prop_HasPart}> ?s . }}
  UNION {{ {e} <{Prop_LocatedIn}> ?root . }}",
        "space" => $@"
  ?f a <{Cls_Level}> ; <{Prop_HasPart}> ?root .
  ?b a <{Cls_Building}> ; <{Prop_HasPart}> ?f .
  {e} a <{Cls_Equipment}> ; <{Prop_LocatedIn}> ?root .",
        _ => throw new ArgumentOutOfRangeException(nameof(rootType), rootType, null),
    };

    private static string? ChildPattern(string rootType, string childType) => (rootType, childType) switch
    {
        ("building", "floor") => $@"
  ?root <{Prop_HasPart}> ?n . ?n a <{Cls_Level}> ; <{Prop_Id}> ?nid .",
        ("building", "space") => $@"
  ?root <{Prop_HasPart}> ?f . ?f a <{Cls_Level}> . ?f <{Prop_HasPart}> ?n . ?n a <{Cls_Space}> ; <{Prop_Id}> ?nid .",
        ("floor", "space") => $@"
  ?b a <{Cls_Building}> ; <{Prop_HasPart}> ?root .
  ?root <{Prop_HasPart}> ?n . ?n a <{Cls_Space}> ; <{Prop_Id}> ?nid .",
        (_, "device") when rootType is "building" or "floor" or "space" =>
            EquipmentUnder(rootType, "?n") + $@"
  ?n <{Prop_Id}> ?nid .",
        // A point's ancestors come from its equipment (GetPointAncestors), so it is under ?root
        // exactly when its equipment is.
        (_, "point") when rootType is "building" or "floor" or "space" =>
            EquipmentUnder(rootType, "?dev") + $@"
  ?dev <{Prop_HasPoint}> ?n . ?n a <{Cls_Point}> ; <{Prop_Id}> ?nid .",
        ("device", "point") => $@"
  ?root <{Prop_HasPoint}> ?n . ?n a <{Cls_Point}> ; <{Prop_Id}> ?nid .",
        _ => null,
    };

    // SPARQL short string literal: backslash first, then quote, then the control characters that are
    // illegal raw in a short literal (a raw newline in one id would otherwise break the whole VALUES).
    private static string EscapeLiteral(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");
}
