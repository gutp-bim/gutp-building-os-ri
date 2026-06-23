using System.Text.Json;
using System.Text.Json.Serialization;
using BuildingOS.GatewayBridge.Protos;
using BuildingOS.Shared.Domain;

namespace BuildingOS.GatewayBridge.Mapping;

/// <summary>
/// Translates between the NATS wire format (PointControlInfo JSON, with a point-id-canonical
/// <c>{ value, priority? }</c> body) and the gRPC egress proto messages, and back from a
/// ControlResult to the result-bus JSON (<c>{ "success", "response" }</c>) consumed by the existing
/// WaitForResult path. The gateway resolves <c>point_id</c> → BACnet object/instance from the shared
/// point list (#181), so the command carries only control_id + point_id + value (+ priority).
/// </summary>
public static class ControlCommandMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Maps a PointControlInfo (as published to the per-gateway request subject) to a gRPC
    /// <see cref="ControlCommand"/>. Returns null when the payload is unparseable, the body is
    /// missing, or the point_id is absent (the gateway cannot resolve the target without it).
    /// </summary>
    public static ControlCommand? ToControlCommand(string pointControlInfoJson)
    {
        PointControlInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<PointControlInfo>(pointControlInfoJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (info is null || string.IsNullOrEmpty(info.Body) || string.IsNullOrEmpty(info.PointId)) return null;

        BacnetSimBody? body;
        try
        {
            body = JsonSerializer.Deserialize<BacnetSimBody>(info.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        // value is required: a non-nullable double would deserialize an absent value as 0 and silently
        // write a wrong setpoint. Distinguish "absent" from a legitimate 0 by parsing it as nullable.
        if (body?.Value is not double value) return null;

        return new ControlCommand
        {
            ControlId = info.id.ToString(),
            PointId = info.PointId,
            PresentValue = value,
            Priority = body.Priority ?? 0,
        };
    }

    /// <summary>Serializes a ControlResult to the result-bus JSON the existing result bus consumes.</summary>
    public static string ToResultJson(ControlResult result)
        => JsonSerializer.Serialize(new { success = result.Success, response = result.Response ?? string.Empty });

    private sealed record BacnetSimBody(
        [property: JsonPropertyName("value")] double? Value,
        [property: JsonPropertyName("priority")] int? Priority);
}
