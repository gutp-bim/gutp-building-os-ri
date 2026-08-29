using System.Globalization;
using System.Text.Json;
using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// Shared #336 detection for writable points whose <c>bos:</c> control schema is missing or unusable —
/// reused by both twin-mutation paths, since twin content only ever changes through them:
/// <see cref="OxiGraphTwinAdminService.PreviewImportAsync"/> (admin import UI) and
/// <see cref="OxiGraphSeedHostedService"/>'s post-seed check (the startup path, which never goes
/// through preview/apply). Kept out of <see cref="OxiGraphTwinAdminService"/> itself — that class's
/// own job is admin import preview/apply, not logic consumed by an unrelated hosted service — so a
/// future third ingestion path can reuse this without depending on it.
/// </summary>
internal static class ControlSchemaIssueDetection
{
    private const string Bos = "http://buildingos.gutp.jp/ontology#"; // same as OssControlSchemaResolver

    /// <summary>
    /// Full query selecting every candidate writable point + its (possibly absent) bos:dataType/
    /// bos:enumLabels. graph/mode both null means "the plain current default graph" (post-seed check,
    /// nothing staged); non-null scopes the candidate to the staging graph — mirrors
    /// OxiGraphTwinAdminService.OrphanPattern: an append judges only the points it adds, never
    /// re-judges the twin's existing points — while the OPTIONAL lookups span both graphs via
    /// <see cref="OxiGraphTwinAdminService.Link"/>, since a newly staged point's schema triples may
    /// already live in the default graph.
    /// </summary>
    public static string BuildQuery(string? graph, TwinImportMode? mode)
    {
        var sbco = OxiGraphTwinAdminService.Sbco;
        var candidate = graph is null
            ? $"?pt a <{sbco}PointExt> ; <{sbco}writable> \"true\" ."
            : $"GRAPH <{graph}> {{ ?pt a <{sbco}PointExt> ; <{sbco}writable> \"true\" . }}";

        string L(string triple) => graph is null ? triple : OxiGraphTwinAdminService.Link(graph, mode!.Value, triple);

        return $@"SELECT DISTINCT ?pt ?dataType ?enumLabels WHERE {{
{candidate}
OPTIONAL {{ {L($"?pt <{Bos}dataType> ?dataType .")} }}
OPTIONAL {{ {L($"?pt <{Bos}enumLabels> ?enumLabels .")} }}
}}";
    }

    /// <summary>
    /// Classifies every candidate row into a reason (or drops it, when the point's schema is fine).
    /// Dedupes by <c>?pt</c>: the OPTIONAL lookups above span both graphs via UNION in append mode, so
    /// a point whose schema triple exists in both the staging and default graph — or whose dataType/
    /// enumLabels was simply re-declared unchanged by the staged Turtle — can otherwise surface as more
    /// than one solution row for the same point (SPARQL has no implicit dedup across UNION branches).
    /// <c>SELECT DISTINCT</c> alone would not catch the case where the two graphs assert two
    /// *different* values for the same point, so the dedup here always keeps just the first row seen.
    /// No SPARQL COUNT — unlike OrphanPattern, "is bos:enumLabels valid JSON" cannot be pushed into
    /// SPARQL, so this runs the one query and classifies/counts/caps in C#. Count is exact (every
    /// distinct point classified); Issues is capped for response-size safety, same as Orphans.
    /// </summary>
    public static (int Count, List<TwinControlSchemaIssue> Issues) Classify(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int cap)
    {
        var issues = new List<TwinControlSchemaIssue>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var pt = row.GetValueOrDefault("pt", "");
            if (!seen.Add(pt)) continue;

            var dataType = row.GetValueOrDefault("dataType", "");
            if (string.IsNullOrEmpty(dataType))
            {
                issues.Add(new TwinControlSchemaIssue(pt, ControlSchemaIssueReasons.MissingDataType));
                continue;
            }

            if (string.Equals(dataType, "enum", StringComparison.OrdinalIgnoreCase)
                && !HasAtLeastOneAllowedCode(row.GetValueOrDefault("enumLabels", "")))
            {
                issues.Add(new TwinControlSchemaIssue(pt, ControlSchemaIssueReasons.MalformedEnumLabels));
            }
        }

        return (issues.Count, issues.Count > cap ? issues.Take(cap).ToList() : issues);
    }

    // Mirrors ControlValueValidator.ParseAllowedCodes fully — not just "is a JSON object" but "yields
    // at least one numeric-parseable key". An empty object ("{}") or one with no numeric keys parses
    // as a valid JSON object but ParseAllowedCodes turns it into an empty allowed-set, which
    // ValidateEnum then treats as permissive — the same silent fail-open #336 exists to catch, so it
    // must be reported here too, not just an outright parse failure.
    private static bool HasAtLeastOneAllowedCode(string? enumLabels)
    {
        if (string.IsNullOrWhiteSpace(enumLabels)) return false;
        try
        {
            using var doc = JsonDocument.Parse(enumLabels);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (double.TryParse(prop.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
