using System.Text;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

using static OxiGraphOntology;

/// <summary>
/// Pure builder for the cross-resource search SPARQL (/resources/search). Emits one UNION branch per
/// resource type, each binding <c>?type</c>/<c>?dt</c>/<c>?id</c>/<c>?name</c>, with an optional
/// case-insensitive CONTAINS filter on name/id and an optional building scope.
///
/// Building scope covers every resource type. Devices and points are scoped through the owning
/// equipment's Room path or direct Level location (topology only; the <c>sbco:floor</c> literal places nothing).
/// </summary>
internal static class ResourceSearchQueryBuilder
{
    private record TypeBranch(string Token, string ClassIri);

    private static readonly TypeBranch[] AllBranches =
    [
        new("building", Cls_Building),
        new("floor", Cls_Level),
        new("space", Cls_Space),
        new("device", Cls_Equipment),
        new("point", Cls_Point),
    ];

    internal static string Build(
        string? q, string? typeFilter, string? buildingDtId, IReadOnlyList<string> tags, int limit, int offset,
        ResourceAttributeFilter? attrs = null)
    {
        var sb = new StringBuilder();
        sb.Append(Prefixes);
        sb.Append("SELECT ?type ?dt ?id ?name WHERE {\n");
        AppendMatchBody(sb, q, typeFilter, buildingDtId, tags, attrs);
        sb.Append("}\n");
        sb.Append("ORDER BY ?type ?name\n");
        // Return exactly up to `limit` rows. There is no paging envelope/hasMore on the response, so
        // callers page by advancing `offset`. (Authorization filtering happens after this in
        // AuthorizedTwinView, which may further reduce the count.)
        sb.Append($"LIMIT {limit} OFFSET {offset}");
        return sb.ToString();
    }

    /// <summary>
    /// The rows facet counts are built from: every resource matching q / building / tags — all types, and
    /// deliberately <b>no</b> type or attribute constraint — with its deviceType / pointType / unit / gatewayId.
    /// A facet group's counts must exclude that group's own selection (otherwise picking "AHU" hides "VAV"
    /// and OR-ing is impossible), so the caller applies type/attribute filters per group over this superset.
    /// Unauthorized — the caller authorizes each row before counting. <paramref name="rowCap"/> bounds the scan
    /// (one extra row is requested so the caller can tell the result was cut).
    /// </summary>
    internal static string BuildFacetRows(string? q, string? buildingDtId, IReadOnlyList<string> tags, int rowCap)
    {
        var sb = new StringBuilder();
        sb.Append(Prefixes);
        sb.Append("SELECT DISTINCT ?type ?dt ?id ?deviceType ?pointType ?unit ?gatewayId WHERE {\n");
        AppendMatchBody(sb, q, null, buildingDtId, tags, null);
        // A device carries its own type; a point takes the type of the device owning it, or — for
        // CSV-derived twins that repeat it on every point row — its own.
        sb.Append(
            $"  OPTIONAL {{ ?dt <{Prop_DeviceType}> ?ownDeviceType . }}\n" +
            $"  OPTIONAL {{ ?facetDevice <{Prop_HasPoint}> ?dt . ?facetDevice <{Prop_DeviceType}> ?ownerDeviceType . }}\n" +
            $"  BIND(COALESCE(?ownerDeviceType, ?ownDeviceType) AS ?deviceType)\n" +
            $"  OPTIONAL {{ ?dt <{Prop_PointType}> ?pointType . }}\n" +
            $"  OPTIONAL {{ ?dt <{Prop_Unit}> ?unit . }}\n" +
            $"  OPTIONAL {{ ?dt <{Prop_GatewayId}> ?gatewayId . }}\n");
        sb.Append("}\n");
        sb.Append($"LIMIT {rowCap + 1}");
        return sb.ToString();
    }

    // The match shared by the search and the facet rows: one UNION branch per type, then the q filter,
    // the customTags AND filter and the attribute constraints. Keeping it in one place is what makes the
    // facet counts describe exactly the set the search returns.
    private static void AppendMatchBody(
        StringBuilder sb, string? q, string? typeFilter, string? buildingDtId,
        IReadOnlyList<string> tags, ResourceAttributeFilter? attrs)
    {
        var hasBuildingScope = !string.IsNullOrEmpty(buildingDtId);
        var hasQuery = !string.IsNullOrWhiteSpace(q);

        var branches = AllBranches.AsEnumerable();
        if (!string.IsNullOrEmpty(typeFilter))
            branches = branches.Where(b => b.Token == typeFilter);
        var unions = branches.Select(b => BuildBranch(b, hasBuildingScope ? buildingDtId! : null)).ToArray();

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
        if (attrs is not null) AppendAttributeFilters(sb, attrs);
    }

    // Structured-attribute constraints (#454): values within a group are ORed (`IN`), groups are ANDed
    // (one FILTER EXISTS each). Compared on STR() so a typed and a plain literal both match.
    private static void AppendAttributeFilters(StringBuilder sb, ResourceAttributeFilter attrs)
    {
        var deviceTypes = InList(attrs.DeviceTypes);
        if (deviceTypes is not null)
            sb.Append(
                $"  FILTER EXISTS {{\n" +
                $"    {{ ?dt <{Prop_DeviceType}> ?fDeviceType . }} UNION {{ ?fDevice <{Prop_HasPoint}> ?dt . ?fDevice <{Prop_DeviceType}> ?fDeviceType . }}\n" +
                $"    FILTER(STR(?fDeviceType) IN ({deviceTypes}))\n" +
                $"  }}\n");
        AppendPredicateFilter(sb, Prop_PointType, "?fPointType", attrs.PointTypes);
        AppendPredicateFilter(sb, Prop_Unit, "?fUnit", attrs.Units);
        AppendPredicateFilter(sb, Prop_GatewayId, "?fGateway", attrs.GatewayIds);
    }

    private static void AppendPredicateFilter(StringBuilder sb, string predicate, string variable, IReadOnlyList<string> values)
    {
        var list = InList(values);
        if (list is null) return;
        sb.Append(
            $"  FILTER EXISTS {{\n" +
            $"    ?dt <{predicate}> {variable} .\n" +
            $"    FILTER(STR({variable}) IN ({list}))\n" +
            $"  }}\n");
    }

    // `"a", "b"` with every value escaped; blanks dropped; null when nothing is left (= no constraint).
    private static string? InList(IReadOnlyList<string> values)
    {
        var escaped = (values ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => $"\"{EscapeStringLiteral(v)}\"")
            .ToArray();
        return escaped.Length == 0 ? null : string.Join(", ", escaped);
    }

    /// <summary>
    /// (resource, tag) pairs for the tag suggestion endpoint: every resource type's customTags entries
    /// whose value is true, optionally narrowed to keys starting with <paramref name="prefix"/>. Rows are
    /// unauthorized and may repeat a pair (the caller de-duplicates). Deliberately not row-capped: authorization
    /// runs per resource after this query, so truncating here would drop arbitrary pairs and understate counts
    /// (the result is bounded by the tagged inventory, and a prefix narrows it further).
    /// </summary>
    internal static string BuildTagUsage(string? prefix)
    {
        var sb = new StringBuilder();
        sb.Append(Prefixes);
        sb.Append("SELECT DISTINCT ?type ?dt ?id ?tagKey WHERE {\n");
        sb.Append(string.Join("  UNION\n", AllBranches.Select(b => BuildBranch(b, null))));
        sb.Append(
            $"  ?dt <{Prop_CustomTags}> ?tagEntry .\n" +
            $"  ?tagEntry a <{Cls_KeyBoolMapEntry}> ;\n" +
            $"            <{Prop_Key}> ?tagKey ;\n" +
            $"            <{Prop_Value}> \"true\"^^xsd:boolean .\n");
        if (!string.IsNullOrWhiteSpace(prefix))
            sb.Append($"  FILTER(STRSTARTS(LCASE(?tagKey), LCASE(\"{EscapeStringLiteral(prefix!)}\")))\n");
        sb.Append('}');
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
            _ when b.Token == "device" => EquipmentBuildingScope("?dt", buildingDtId),
            _ when b.Token == "point" =>
                $"    ?scopeDevice a <{Cls_Equipment}> ; <{Prop_HasPoint}> ?dt .\n" +
                EquipmentBuildingScope("?scopeDevice", buildingDtId),
            _ => "",
        };
        return
            $"  {{\n" +
            $"    ?dt a <{b.ClassIri}> ; <{Prop_Id}> ?id ; <{Prop_Name}> ?name .\n" +
            scope +
            $"    BIND(\"{b.Token}\" AS ?type)\n" +
            $"  }}\n";
    }

    private static string EquipmentBuildingScope(string equipment, string buildingDtId) =>
        $"    FILTER EXISTS {{\n" +
        $"      <{buildingDtId}> <{Prop_HasPart}> ?scopeFloor .\n" +
        $"      ?scopeFloor a <{Cls_Level}> .\n" +
        $"      {{\n" +
        $"        {equipment} <{Prop_LocatedIn}> ?scopeRoom .\n" +
        $"        ?scopeRoom a <{Cls_Space}> .\n" +
        $"        ?scopeFloor <{Prop_HasPart}> ?scopeRoom .\n" +
        $"      }} UNION {{\n" +
        $"        {equipment} <{Prop_LocatedIn}> ?scopeFloor .\n" +
        $"      }}\n" +
        $"    }}\n";

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
