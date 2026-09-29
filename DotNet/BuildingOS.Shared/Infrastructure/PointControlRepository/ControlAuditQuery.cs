using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BuildingOS.Shared.Infrastructure.PointControlRepository;

/// <summary>
/// One page of a point's control audit, newest first (#478). <paramref name="Start"/> is inclusive and
/// <paramref name="End"/> exclusive on <c>created_at</c>; either may be null. <paramref name="After"/>
/// continues after the last row of the previous page. <paramref name="Limit"/> is the number of rows to
/// read — the caller asks for one more than it returns to learn whether another page exists.
/// </summary>
public sealed record ControlAuditQuery(
    string PointId,
    int Limit,
    DateTime? Start = null,
    DateTime? End = null,
    ControlAuditCursor? After = null);

/// <summary>
/// Keyset position in the newest-first audit order: the (<c>created_at</c>, <c>id</c>) of the last row
/// already returned. The next page is every row strictly before it in that order, so writes that land
/// while a client pages backwards neither shift nor duplicate rows (unlike an offset). Opaque to
/// clients: URL-safe base64 of a small JSON object.
/// </summary>
public sealed record ControlAuditCursor(DateTime CreatedAt, Guid Id)
{
    private sealed record Wire(string? T, string? Id);

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Wire(
            CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Id.ToString("D")));
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The cursor <paramref name="raw"/> encodes, or null when it is not one this API issued.</summary>
    public static ControlAuditCursor? TryDecode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var b64 = raw.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var wire = JsonSerializer.Deserialize<Wire>(Convert.FromBase64String(b64));
            if (wire?.T is null || wire.Id is null) return null;
            if (!DateTime.TryParseExact(wire.T, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var t) || t.Kind != DateTimeKind.Utc) return null;
            if (!Guid.TryParse(wire.Id, out var id)) return null;
            return new ControlAuditCursor(t, id);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
