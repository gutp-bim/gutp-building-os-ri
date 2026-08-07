using System.Text.RegularExpressions;

namespace BuildingOS.Shared;

/// <summary>
/// Resolves the final <see cref="GatewayPointEntry.Protocol"/> for a gateway point-list row (#224).
/// Pure and order-sensitive:
///   1. an explicit bos:protocol value always wins;
///   2. else BACnet native fields (deviceIdBacnet/objectTypeBacnet/instanceNoBacnet) being present means
///      "bacnet" — this must be checked before shape-inference, since a BACnet point's localId can
///      legitimately look like something else (e.g. an OPC-UA nodeId used for a simulator, see
///      fixtures/e2e/twin.ttl);
///   3. else infer from localId's shape (same heuristic as nexus-gateway's csv.go, kept in sync
///      deliberately so both sides classify the same shapes the same way);
///   4. else null — left unresolved for the caller to decide a default.
/// </summary>
public static class GatewayPointProtocolResolver
{
    private const string Bacnet = "bacnet";
    private const string OpcUa = "opcua";
    private const string Mqtt = "mqtt";

    // Mirrors nexus-gateway's internal/pointlist/csv.go protocolPatterns. Order matters: first match wins.
    private static readonly (string Protocol, Regex Pattern)[] ShapePatterns =
    [
        (OpcUa, new Regex(@"^ns=\d+;[isgb]=", RegexOptions.Compiled)), // OPC-UA NodeId, e.g. "ns=2;s=PT001"
        (Mqtt, new Regex(@"/", RegexOptions.Compiled)),                // MQTT topic, e.g. "sensors/room1/temp"
    ];

    public static string? Resolve(
        string? explicitProtocol,
        string? bacnetDeviceId,
        string? bacnetObjectType,
        string? bacnetInstanceNo,
        string? localId)
    {
        if (!string.IsNullOrWhiteSpace(explicitProtocol)) return explicitProtocol;

        if (!string.IsNullOrWhiteSpace(bacnetDeviceId)
            || !string.IsNullOrWhiteSpace(bacnetObjectType)
            || !string.IsNullOrWhiteSpace(bacnetInstanceNo))
            return Bacnet;

        if (!string.IsNullOrEmpty(localId))
        {
            foreach (var (protocol, pattern) in ShapePatterns)
                if (pattern.IsMatch(localId))
                    return protocol;
        }

        return null;
    }
}
