using System.Globalization;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Resolves a point's <see cref="ControlSchema"/> from the digital twin (point list, source of truth)
/// via SPARQL (#153): <c>bos:dataType</c> / <c>bos:enumLabels</c> / <c>bos:minValue</c> /
/// <c>bos:maxValue</c> on the <c>sbco:PointExt</c>. Replaces the former no-op placeholder. Returns
/// <c>null</c> when the point has no <c>bos:dataType</c> (unschematized → input validation is skipped).
/// </summary>
public class OssControlSchemaResolver(OxiGraphClient client, ILogger<OssControlSchemaResolver> logger)
    : IControlSchemaResolver
{
    // The query sits on the control POST path; cap it short so a slow/hung OxiGraph degrades to
    // fail-open (validation skipped) quickly instead of stalling the request up to the HttpClient
    // default timeout (~100s).
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    public async Task<ControlSchema?> ResolveAsync(Point point, Device? device)
    {
        if (string.IsNullOrEmpty(point.Id)) return null;

        var query = $$"""
            PREFIX sbco: <https://www.sbco.or.jp/ont/>
            PREFIX bos: <http://buildingos.gutp.jp/ontology#>
            SELECT ?dataType ?enumLabels ?minValue ?maxValue WHERE {
              ?point a sbco:PointExt ; sbco:id "{{Escape(point.Id)}}" .
              OPTIONAL { ?point bos:dataType ?dataType }
              OPTIONAL { ?point bos:enumLabels ?enumLabels }
              OPTIONAL { ?point bos:minValue ?minValue }
              OPTIONAL { ?point bos:maxValue ?maxValue }
            }
            LIMIT 1
            """;

        try
        {
            using var cts = new CancellationTokenSource(QueryTimeout);
            var rows = await client.QueryAsync(query, cts.Token).ConfigureAwait(false);
            var row = rows.FirstOrDefault();
            if (row is null) return null;

            var dataType = Get(row, "dataType");
            // No data type → the point is not schematized for control; skip validation (permissive).
            if (string.IsNullOrEmpty(dataType)) return null;

            return new ControlSchema
            {
                DataType = dataType,
                EnumLabels = NullIfEmpty(Get(row, "enumLabels")),
                MinValue = ParseDouble(Get(row, "minValue")),
                MaxValue = ParseDouble(Get(row, "maxValue")),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ControlSchema resolution failed for point {PointId}", point.Id);
            return null; // resolution failure → skip validation rather than block control
        }
    }

    private static string Get(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) ? v : string.Empty;

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static double? ParseDouble(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // Escape a SPARQL string literal (the id comes from the twin, but never trust it in a query).
    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
