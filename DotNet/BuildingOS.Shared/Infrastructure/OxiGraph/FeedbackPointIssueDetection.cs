using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// #637 detection for <c>bos:feedbackPoint</c> declarations, shared like <see cref="ReleaseOfIssueDetection"/> by the
/// admin import (which refuses such a twin) and the startup seed (which logs). A feedback point must be one point of
/// the twin, with an id, on the controlled point's gateway: otherwise every confirmation of that control would be
/// decided on the wrong value (or never), which reads as "the write did not take effect" when it may well have.
/// Naming the controlled point itself is allowed — that is how a point that reports its own value is declared.
/// </summary>
internal static class FeedbackPointIssueDetection
{
    private const string Bos = "http://buildingos.gutp.jp/ontology#";

    public static string BuildQuery(string? graph, TwinImportMode? mode)
    {
        var sbco = OxiGraphTwinAdminService.Sbco;
        string L(string triple) => graph is null ? triple : OxiGraphTwinAdminService.Link(graph, mode!.Value, triple);

        // The import judges the points it touches (a staged bos:feedbackPoint edge), but against the merged graph:
        // an append may add a second target to a point that already has one, or add only the edge to a point whose
        // type lives in the existing twin — both must still be caught.
        var candidate = graph is null
            ? $"?pt a <{sbco}PointExt> ; <{Bos}feedbackPoint> ?target ."
            : $"GRAPH <{graph}> {{ ?pt <{Bos}feedbackPoint> ?staged . }}\n{L($"?pt a <{sbco}PointExt> .")}\n{L($"?pt <{Bos}feedbackPoint> ?target .")}";

        return $@"SELECT DISTINCT ?pt ?target ?targetIsPoint ?targetId ?targetGw ?gw WHERE {{
{candidate}
OPTIONAL {{ {L($"?target a <{sbco}PointExt> .")} BIND(true AS ?targetIsPoint) }}
OPTIONAL {{ {L($"?target <{sbco}id> ?targetId .")} }}
OPTIONAL {{ {L($"?target <{sbco}gatewayId> ?targetGw .")} }}
OPTIONAL {{ {L($"?pt <{sbco}gatewayId> ?gw .")} }}
}}";
    }

    /// <summary>One issue per controlled point (its first broken rule). Count exact, Issues capped.</summary>
    public static (int Count, List<TwinFeedbackPointIssue> Issues) Classify(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int cap)
    {
        var issues = new List<TwinFeedbackPointIssue>();
        foreach (var group in rows.GroupBy(r => r.GetValueOrDefault("pt", ""), StringComparer.Ordinal)
                                  .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var reason = ReasonFor(group.ToList());
            if (reason is not null) issues.Add(new TwinFeedbackPointIssue(group.Key, reason));
        }
        return (issues.Count, issues.Count > cap ? issues.Take(cap).ToList() : issues);
    }

    private static string? ReasonFor(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var targets = rows.Select(r => r.GetValueOrDefault("target", "")).Distinct(StringComparer.Ordinal).Count();
        if (targets > 1) return FeedbackPointIssueReasons.MultipleTargets;

        var row = rows[0];
        if (!row.ContainsKey("targetIsPoint")) return FeedbackPointIssueReasons.TargetMissing;
        if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("targetId"))) return FeedbackPointIssueReasons.TargetNoId;

        // The value is read from the telemetry of the feedback point's gateway; a point another gateway reports
        // says nothing about whether this gateway's write landed.
        var gw = row.GetValueOrDefault("gw");
        if (!string.IsNullOrEmpty(gw) && !string.Equals(gw, row.GetValueOrDefault("targetGw"), StringComparison.Ordinal))
            return FeedbackPointIssueReasons.TargetOtherGateway;
        return null;
    }
}
