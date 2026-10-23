using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// #600 detection for <c>bos:releaseOf</c> declarations, shared by the two twin-mutation paths like
/// <see cref="ControlSchemaIssueDetection"/>: the admin import preview/apply and the startup seed.
/// A release point is an ordinary writable point whose ControlSchema accepts only the code 1 and
/// which names, by <c>bos:releaseOf</c>, a writable point of the same gateway to withdraw. A twin
/// that breaks any of that would hand the gateway an ordinary enum point, so the import is refused
/// (the seed only logs, as every other seed-time check does).
/// </summary>
internal static class ReleaseOfIssueDetection
{
    private const string Bos = "http://buildingos.gutp.jp/ontology#";

    /// <summary>
    /// One row per (release point, target) with the facts the rules need. graph/mode null means the
    /// plain default graph (post-seed check); non-null scopes the candidate to the staging graph — an
    /// append judges only the release points it adds — while the lookups span both graphs via
    /// <see cref="OxiGraphTwinAdminService.Link"/>, since the target may already live in the twin.
    /// </summary>
    public static string BuildQuery(string? graph, TwinImportMode? mode)
    {
        var sbco = OxiGraphTwinAdminService.Sbco;
        var candidate = graph is null
            ? $"?pt a <{sbco}PointExt> ; <{Bos}releaseOf> ?target ."
            : $"GRAPH <{graph}> {{ ?pt a <{sbco}PointExt> ; <{Bos}releaseOf> ?target . }}";

        string L(string triple) => graph is null ? triple : OxiGraphTwinAdminService.Link(graph, mode!.Value, triple);

        return $@"SELECT DISTINCT ?pt ?target ?targetIsPoint ?targetWritable ?targetGw ?targetReleaseOf ?gw ?writable ?dataType ?enumLabels WHERE {{
{candidate}
OPTIONAL {{ {L($"?target a <{sbco}PointExt> .")} BIND(true AS ?targetIsPoint) }}
OPTIONAL {{ {L($"?target <{sbco}writable> ?targetWritable .")} }}
OPTIONAL {{ {L($"?target <{sbco}gatewayId> ?targetGw .")} }}
OPTIONAL {{ {L($"?target <{Bos}releaseOf> ?targetReleaseOf .")} }}
OPTIONAL {{ {L($"?pt <{sbco}gatewayId> ?gw .")} }}
OPTIONAL {{ {L($"?pt <{sbco}writable> ?writable .")} }}
OPTIONAL {{ {L($"?pt <{Bos}dataType> ?dataType .")} }}
OPTIONAL {{ {L($"?pt <{Bos}enumLabels> ?enumLabels .")} }}
}}";
    }

    /// <summary>
    /// Classifies each release point into the first rule it breaks (or drops it when it is sound).
    /// Rows are grouped by <c>?pt</c>: a point naming several targets is one issue, not several, and
    /// the OPTIONAL lookups can repeat a row across the staging/default graph union. Count is exact;
    /// Issues is capped for response-size safety.
    /// </summary>
    public static (int Count, List<TwinReleaseOfIssue> Issues) Classify(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int cap)
    {
        var issues = new List<TwinReleaseOfIssue>();
        foreach (var group in rows.GroupBy(r => r.GetValueOrDefault("pt", ""), StringComparer.Ordinal)
                                  .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var reason = ReasonFor(group.Key, group.ToList());
            if (reason is not null) issues.Add(new TwinReleaseOfIssue(group.Key, reason));
        }
        return (issues.Count, issues.Count > cap ? issues.Take(cap).ToList() : issues);
    }

    private static string? ReasonFor(string pt, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var targets = rows.Select(r => r.GetValueOrDefault("target", "")).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count > 1) return ReleaseOfIssueReasons.MultipleTargets;
        if (string.Equals(targets[0], pt, StringComparison.Ordinal)) return ReleaseOfIssueReasons.SelfReference;

        // Single target: every row describes the same pair, so the first carries the facts. A
        // multi-valued gatewayId/writable on one node is its own modelling error, not judged here.
        var row = rows[0];
        if (!row.ContainsKey("targetIsPoint")) return ReleaseOfIssueReasons.TargetMissing;
        if (!IsTrue(row.GetValueOrDefault("targetWritable"))) return ReleaseOfIssueReasons.TargetNotWritable;
        if (row.ContainsKey("targetReleaseOf")) return ReleaseOfIssueReasons.TargetIsRelease;

        var gw = row.GetValueOrDefault("gw");
        if (string.IsNullOrEmpty(gw)) return ReleaseOfIssueReasons.ReleaseNoGateway;
        if (!string.Equals(gw, row.GetValueOrDefault("targetGw"), StringComparison.Ordinal))
            return ReleaseOfIssueReasons.TargetOtherGateway;

        if (!IsTrue(row.GetValueOrDefault("writable"))) return ReleaseOfIssueReasons.ReleaseNotWritable;
        if (!AcceptsOnlyRelease(row.GetValueOrDefault("dataType"), row.GetValueOrDefault("enumLabels")))
            return ReleaseOfIssueReasons.SchemaNotReleaseOnly;
        return null;
    }

    private static bool IsTrue(string? v) => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    // Same spelling rule as ControlValueValidator (trimmed, case-insensitive dataType) and the same
    // enumLabels parser, so what is accepted here is exactly what the validator will enforce: the
    // allowed set must be {1}. A boolean (accepts 0) or an enum with no/other codes does not qualify.
    private static bool AcceptsOnlyRelease(string? dataType, string? enumLabels)
    {
        if (!string.Equals((dataType ?? "").Trim(), "enum", StringComparison.OrdinalIgnoreCase)) return false;
        var codes = ControlValueValidator.ParseAllowedCodes(enumLabels);
        return codes is { Count: 1 } && codes.Contains(1);
    }
}
