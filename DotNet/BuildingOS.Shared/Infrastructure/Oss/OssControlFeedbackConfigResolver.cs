using System.Globalization;
using BuildingOS.Shared.Domain.ControlFeedback;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>Reads how a control on a point is confirmed (#637).</summary>
public interface IControlFeedbackConfigResolver
{
    /// <summary>The point's feedback declaration; <see cref="ControlFeedbackConfig.None"/> when it has none.</summary>
    Task<ControlFeedbackConfig> ResolveAsync(string pointId, CancellationToken ct);
}

/// <summary>
/// Resolves <c>bos:feedbackPoint</c> / <c>bos:feedbackWindowSec</c> / <c>bos:feedbackTolerance</c> on the
/// controlled <c>sbco:PointExt</c> (#637). Control-path only, like <see cref="OssControlSchemaResolver"/>, so it is
/// its own short query rather than part of the shared point projection every read path pays for. A failure
/// (OxiGraph down / slow) degrades to "no feedback" — confirmation is an aid, never a reason to block control.
/// The import check (<c>FeedbackPointIssueDetection</c>) refuses a twin that names more than one feedback point —
/// including one an append would create against the existing twin — so taking one sample here is not a silent choice.
/// </summary>
public sealed class OssControlFeedbackConfigResolver(OxiGraphClient client, ILogger<OssControlFeedbackConfigResolver> logger)
    : IControlFeedbackConfigResolver
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    public async Task<ControlFeedbackConfig> ResolveAsync(string pointId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pointId)) return ControlFeedbackConfig.None;
        var query = $$"""
            PREFIX sbco: <https://www.sbco.or.jp/ont/>
            PREFIX bos: <http://buildingos.gutp.jp/ontology#>
            SELECT ?feedbackId ?windowSec ?tolerance WHERE {
              ?point a sbco:PointExt ; sbco:id "{{Escape(pointId)}}" .
              OPTIONAL { ?point bos:feedbackPoint ?fb . ?fb sbco:id ?feedbackId . }
              OPTIONAL { ?point bos:feedbackWindowSec ?windowSec }
              OPTIONAL { ?point bos:feedbackTolerance ?tolerance }
            }
            LIMIT 1
            """;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(QueryTimeout);
            var row = (await client.QueryAsync(query, cts.Token).ConfigureAwait(false)).FirstOrDefault();
            if (row is null) return ControlFeedbackConfig.None;
            var feedbackId = row.TryGetValue("feedbackId", out var f) && !string.IsNullOrWhiteSpace(f) ? f : null;
            return new ControlFeedbackConfig(
                feedbackId,
                ControlFeedbackConfig.ClampWindow(Parse(row, "windowSec")),
                Parse(row, "tolerance") is { } t && t >= 0 ? t : 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Control feedback config resolution failed for point {PointId}", pointId);
            return ControlFeedbackConfig.None;
        }
    }

    private static double? Parse(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var s)
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && double.IsFinite(v)
            ? v
            : null;

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
