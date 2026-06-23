using System.Text;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

using static OxiGraphOntology;

/// <summary>
/// Pure builder for the cross-resource search SPARQL (/resources/search). Emits one UNION branch per
/// resource type, each binding <c>?type</c>/<c>?dt</c>/<c>?id</c>/<c>?name</c>, with an optional
/// case-insensitive CONTAINS filter on name/id and an optional building scope.
///
/// Building scope only covers building/floor/space (reachable via sbco:hasPart). Device/point join to
/// a building via the sbco:floor string literal, not hasPart, so they cannot be reliably building-
/// scoped here; their branches are omitted when a building scope is requested.
/// </summary>
internal static class ResourceSearchQueryBuilder
{
    private record TypeBranch(string Token, string ClassIri, bool BuildingScopeable);

    private static readonly TypeBranch[] AllBranches =
    [
        new("building", Cls_Building, true),
        new("floor", Cls_Level, true),
        new("space", Cls_Space, true),
        new("device", Cls_Equipment, false),
        new("point", Cls_Point, false),
    ];

    internal static string Build(
        string? q, string? typeFilter, string? buildingDtId, IReadOnlyList<string> tags, int limit, int offset)
    {
        var hasBuildingScope = !string.IsNullOrEmpty(buildingDtId);
        var hasQuery = !string.IsNullOrWhiteSpace(q);

        var branches = AllBranches.AsEnumerable();
        if (!string.IsNullOrEmpty(typeFilter))
            branches = branches.Where(b => b.Token == typeFilter);
        if (hasBuildingScope)
            branches = branches.Where(b => b.BuildingScopeable);

        var unions = branches.Select(b => BuildBranch(b, hasBuildingScope ? buildingDtId! : null)).ToArray();

        var sb = new StringBuilder();
        sb.Append(Prefixes);
        sb.Append("SELECT ?type ?dt ?id ?name WHERE {\n");
        sb.Append(string.Join("  UNION\n", unions));
        if (hasQuery)
        {
            var esc = EscapeStringLiteral(q!);
            sb.Append($"  FILTER(CONTAINS(LCASE(?name), LCASE(\"{esc}\")) || CONTAINS(LCASE(?id), LCASE(\"{esc}\")))\n");
        }
        // customTags (KeyBoolMapEntry) AND filter (#332): one FILTER EXISTS per requested tag matching
        // customTags[key] == true. ?dt is bound in every UNION branch above, so the filter applies
        // uniformly. Blank/whitespace tags are skipped so a stray "?tag=" does not break the query.
        var tagIndex = 0;
        foreach (var tag in tags ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;
            var tagEsc = EscapeStringLiteral(tag);
            var entry = $"?tagEntry{tagIndex++}";
            sb.Append(
                $"  FILTER EXISTS {{\n" +
                $"    ?dt <{Prop_CustomTags}> {entry} .\n" +
                $"    {entry} a <{Cls_KeyBoolMapEntry}> ;\n" +
                $"            <{Prop_Key}> \"{tagEsc}\" ;\n" +
                $"            <{Prop_Value}> \"true\"^^xsd:boolean .\n" +
                $"  }}\n");
        }
        sb.Append("}\n");
        sb.Append("ORDER BY ?type ?name\n");
        // Return exactly up to `limit` rows. There is no paging envelope/hasMore on the response, so
        // callers page by advancing `offset`. (Authorization filtering happens after this in
        // AuthorizedTwinView, which may further reduce the count.)
        sb.Append($"LIMIT {limit} OFFSET {offset}");
        return sb.ToString();
    }

    private static string BuildBranch(TypeBranch b, string? buildingDtId)
    {
        var scope = buildingDtId switch
        {
            null => "",
            _ when b.Token == "building" => $"    FILTER(?dt = <{buildingDtId}>)\n",
            _ when b.Token == "floor" => $"    <{buildingDtId}> <{Prop_HasPart}> ?dt .\n",
            // space: building → floor → space
            _ when b.Token == "space" => $"    <{buildingDtId}> <{Prop_HasPart}> ?mid . ?mid <{Prop_HasPart}> ?dt .\n",
            _ => "",
        };
        return
            $"  {{\n" +
            $"    ?dt a <{b.ClassIri}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name .\n" +
            scope +
            $"    BIND(\"{b.Token}\" AS ?type)\n" +
            $"  }}\n";
    }

    // Escape for a SPARQL short string literal ("..."). Backslash first, then quote, then the control
    // characters that are illegal raw inside a short literal — a raw newline/CR would otherwise break
    // out of the literal and allow query injection via the q/tag inputs.
    private static string EscapeStringLiteral(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
}
