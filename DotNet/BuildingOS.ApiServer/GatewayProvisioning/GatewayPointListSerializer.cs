using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingOS.Shared;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>
/// Pure canonical serialization of a gateway's point list (#224). Points are sorted by PointId and
/// emitted with a stable field order so the output is deterministic — the basis for the content-hash
/// ETag and for the response body.
/// </summary>
public static class GatewayPointListSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Deterministic JSON array of the points (sorted by PointId, ordinal).</summary>
    public static string SerializePoints(IReadOnlyList<GatewayPointEntry> entries)
    {
        var ordered = entries
            .OrderBy(e => e.PointId, StringComparer.Ordinal)
            .Select(Canonical)
            .ToArray();
        return JsonSerializer.Serialize(ordered, Options);
    }

    /// <summary>Deterministic JSON for a single entry — used for per-point change detection (diff).</summary>
    public static string CanonicalString(GatewayPointEntry e) => JsonSerializer.Serialize(Canonical(e), Options);

    // Explicit shape with a fixed property order so serialization is stable across runtimes.
    private static CanonicalPoint Canonical(GatewayPointEntry e) => new(
        e.PointId, e.LocalId, e.Protocol, e.BacnetDeviceId, e.BacnetObjectType, e.BacnetInstanceNo,
        e.Unit, e.Writable, e.DataType, e.MinValue, e.MaxValue, e.EnumLabels,
        e.DeviceDtId, e.DeviceId, e.DeviceName, e.ReleaseOf);

    // releaseOf (#600) is the only field omitted when null, and it is last: a point list with no
    // release point serializes byte-for-byte as before, so its ETag does not change and gateways
    // do not refetch on upgrade.
    private sealed record CanonicalPoint(
        string pointId, string? localId, string? protocol,
        string? bacnetDeviceId, string? bacnetObjectType, string? bacnetInstanceNo,
        string? unit, bool? writable, string? dataType, string? minValue, string? maxValue,
        string? enumLabels, string? deviceDtId, string? deviceId, string? deviceName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? releaseOf);
}

/// <summary>
/// Content-hash ETag over a gateway's point list. Order-independent (the serializer sorts), so the
/// ETag changes only when the actual content changes. Returned as a quoted strong validator.
/// </summary>
public static class PointListEtag
{
    public static string Compute(IReadOnlyList<GatewayPointEntry> entries)
    {
        var canonical = GatewayPointListSerializer.SerializePoints(entries);
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"\"sha256:{hex}\"";
    }
}
