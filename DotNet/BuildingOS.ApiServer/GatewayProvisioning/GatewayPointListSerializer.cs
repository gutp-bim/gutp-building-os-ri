using System.Text;
using System.Text.Json;
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
    private static object Canonical(GatewayPointEntry e) => new
    {
        pointId = e.PointId,
        localId = e.LocalId,
        bacnetDeviceId = e.BacnetDeviceId,
        bacnetObjectType = e.BacnetObjectType,
        bacnetInstanceNo = e.BacnetInstanceNo,
        unit = e.Unit,
        writable = e.Writable,
        dataType = e.DataType,
        minValue = e.MinValue,
        maxValue = e.MaxValue,
        enumLabels = e.EnumLabels,
        deviceDtId = e.DeviceDtId,
        deviceId = e.DeviceId,
        deviceName = e.DeviceName,
    };
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
