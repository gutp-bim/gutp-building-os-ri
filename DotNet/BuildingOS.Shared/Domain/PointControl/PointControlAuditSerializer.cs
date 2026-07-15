using System.Text.Json;

namespace BuildingOS.Shared.Domain.PointControl;

/// <summary>
/// Pure serialization helpers that map between <see cref="PointControlInfo"/> (domain) and
/// <see cref="PointControlAuditEntry"/> (EF entity). JSON format is intentionally compatible
/// with the schema written by the legacy NpgsqlPointControlRepository.
/// </summary>
public static class PointControlAuditSerializer
{
    public static PointControlAuditEntry ToEntry(PointControlInfo info) => new()
    {
        Id        = info.id,
        // Back-compat with the prior Npgsql writer: persist "" (not null) when no point id is present,
        // so existing point_id-based queries / aggregates / index selectivity are unchanged (#235 review).
        PointId   = info.PointId ?? ExtractProperty(info.Body, "pointId") ?? string.Empty,
        Request   = info.Body ?? "{}",
        CreatedAt = DateTime.UtcNow,
    };

    public static void ApplyResult(PointControlAuditEntry entry, PointControlInfo info)
    {
        if (info.Result is null) return;

        var status = info.Result == PointControlResult.Success ? "success" : "failed";
        entry.Result      = JsonSerializer.Serialize(new { status, response = info.Response ?? "{}" });
        entry.CompletedAt = DateTime.UtcNow;
    }

    public static PointControlInfo ToDomain(PointControlAuditEntry entry)
    {
        PointControlResult? result = null;
        if (entry.Result is not null)
        {
            try
            {
                var doc = JsonDocument.Parse(entry.Result);
                if (doc.RootElement.TryGetProperty("status", out var s))
                    result = s.GetString() == "success" ? PointControlResult.Success : PointControlResult.Failed;
            }
            catch { /* ignore malformed */ }
        }

        return new PointControlInfo
        {
            id       = entry.Id,
            PointId  = entry.PointId,
            Type     = ExtractProperty(entry.Request, "type") ?? string.Empty,
            Body     = entry.Request,
            Result   = result,
            Response = entry.Result,
        };
    }

    /// <summary>
    /// Read the persisted result's status string ("success" / "failed") for the audit view (#162), or
    /// "pending" when the command has no result yet (null) or the stored JSON is malformed / carries no
    /// status. Pure; distinct from <see cref="ToDomain"/> which maps to the success/failed enum only.
    /// </summary>
    public static string ReadStatus(string? resultJson)
    {
        if (resultJson is null) return "pending";
        try
        {
            var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty("status", out var s))
                return s.GetString() ?? "pending";
        }
        catch { /* malformed → pending */ }
        return "pending";
    }

    private static string? ExtractProperty(string? json, string key)
    {
        if (json is null) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var el) ? el.GetString() : null;
        }
        catch { return null; }
    }
}
