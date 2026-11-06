using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildingOS.Shared.Domain.GatewayProvisioning;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>The opt-in static clock contract. The legacy pointlist endpoint is unchanged.</summary>
public sealed class GatewayConfigurationResponse
{
    public string SchemaVersion { get; init; } = "clock-provisioning/1-draft";
    public required string GatewayId { get; init; }
    public string Revision { get; private set; } = "";
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public required GatewayPointDto[] Points { get; init; }
    public required GatewayClockSource[] ClockSources { get; init; }
    public required EquipmentClockBinding[] ClockBindings { get; init; }

    public static GatewayConfigurationResponse From(string gatewayId, GatewayProvisioningSnapshot snapshot)
    {
        var response = new GatewayConfigurationResponse
        {
            GatewayId = gatewayId,
            Points = snapshot.Points.OrderBy(p => p.PointId, StringComparer.Ordinal).Select(GatewayPointDto.From).ToArray(),
            ClockSources = snapshot.ClockSources.OrderBy(c => c.ClockId, StringComparer.Ordinal).ToArray(),
            ClockBindings = snapshot.ClockBindings.OrderBy(b => b.EquipmentId, StringComparer.Ordinal).ToArray(),
        };
        var content = JsonSerializer.SerializeToElement(new
        {
            response.SchemaVersion, response.GatewayId, response.Points, response.ClockSources, response.ClockBindings,
        }, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, content);
        response.Revision = $"\"sha256:{Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant()}\"";
        return response;
    }

    // Omit null DTO properties in the new contract only. This matches the review fixture and keeps
    // the legacy DTO/ETag byte representation untouched.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray()) WriteCanonical(writer, child);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
