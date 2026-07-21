using System.Globalization;
using BuildingOS.ConnectorWorker.Protos;
using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using Corvus.Json;
using Grpc.Core;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// gRPC <c>GatewayIngress</c>: client-streaming telemetry. The contract is point-id based (#181):
/// the gateway shares the point list and resolves protocol-native addressing to a <c>point_id</c>
/// locally, so each frame carries only <c>gateway_id</c> + <c>point_id</c> + <c>value</c> +
/// <c>timestamp</c>. This service enriches the frame with static metadata from the digital twin
/// (looked up by <c>point_id</c> via <see cref="IPointMetadataCache"/>) and publishes the resulting
/// validated telemetry straight to <c>building-os.validated.telemetry</c> — no raw.{protocol} hop
/// and no per-protocol connector needed for this path.
///
/// A frame with an unknown <c>point_id</c>, or whose <c>gateway_id</c> does not own that point in
/// the twin, is skipped (logged + metered); the stream continues and <see cref="StreamAck"/>
/// counts only successfully-enqueued frames. The service is stateless, so Ingress pods scale
/// horizontally. Enabled only when GRPC_INGRESS_PORT is set (see Program.cs).
/// </summary>
public sealed class GatewayIngressService(
    IIngressTelemetryBus bus,
    IPointMetadataCache metadataCache,
    IngressIdentityOptions identity,
    ILogger<GatewayIngressService> logger) : Protos.GatewayIngress.GatewayIngressBase
{
    private const string ValidatedSubject = "building-os.validated.telemetry";

    public override async Task<StreamAck> StreamTelemetry(
        IAsyncStreamReader<TelemetryFrame> requestStream, ServerCallContext context)
    {
        // The mTLS ingress (Traefik passTLSClientCert) injects the verified gateway id as a trusted
        // header; bind it to each frame's gateway_id when enforcement is on (#296).
        var trustedGatewayId = ResolveTrustedGatewayId(context.RequestHeaders, identity.HeaderName);
        return new StreamAck
        {
            Accepted = await RunAsync(requestStream, context.CancellationToken, trustedGatewayId).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Reads the ingress-verified gateway id from the trusted header, or null when absent/blank.
    /// gRPC normalises metadata keys to lower-case, so the match is case-insensitive and binary
    /// entries are ignored. Pure over <see cref="Metadata"/> so it is unit-tested without a live call.
    ///
    /// Fails closed on ambiguity: if more than one distinct non-blank value is present (a duplicate
    /// trusted header — e.g. an ingress that appends instead of strip+set, or a client smuggling its
    /// own header), returns null so the frame is rejected rather than trusting a first-wins value.
    /// </summary>
    internal static string? ResolveTrustedGatewayId(Metadata headers, string headerName)
    {
        string? found = null;
        foreach (var entry in headers)
        {
            if (entry.IsBinary || !string.Equals(entry.Key, headerName, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = entry.Value?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (found is not null && !string.Equals(found, value, StringComparison.Ordinal))
                return null; // ambiguous trusted identity → fail closed
            found = value;
        }
        return found;
    }

    /// <summary>Transport-agnostic core (testable without a live gRPC channel / NATS / OxiGraph).</summary>
    internal async Task<long> RunAsync(
        IAsyncStreamReader<TelemetryFrame> requestStream, CancellationToken ct, string? trustedGatewayId = null)
    {
        var accepted = 0L;
        while (await requestStream.MoveNext(ct).ConfigureAwait(false))
        {
            if (await TryIngestAsync(requestStream.Current, trustedGatewayId, ct).ConfigureAwait(false)) accepted++;
        }
        logger.LogInformation("Ingress stream completed: {Accepted} telemetry frames enqueued", accepted);
        return accepted;
    }

    private async Task<bool> TryIngestAsync(TelemetryFrame frame, string? trustedGatewayId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(frame.GatewayId) || string.IsNullOrEmpty(frame.PointId))
        {
            logger.LogWarning("Ingress: frame missing gateway_id/point_id, skipping");
            Count(frame.GatewayId, "missing_id");
            return false;
        }

        // Identity binding (#296): reject a frame whose claimed gateway_id does not match the
        // ingress-verified identity. Checked before the twin lookup so spoofed frames cost nothing.
        var decision = IngressIdentityPolicy.Check(identity.Enforce, trustedGatewayId, frame.GatewayId);
        if (decision != IngressIdentityDecision.Allow)
        {
            var reason = decision == IngressIdentityDecision.RejectMissingIdentity
                ? "identity_missing"
                : "identity_mismatch";
            logger.LogWarning(
                "Ingress: gateway identity check failed ({Reason}) — frame gateway '{Gateway}' vs trusted '{Trusted}', skipping",
                reason, frame.GatewayId, trustedGatewayId ?? "(none)");
            Count(frame.GatewayId, reason);
            return false;
        }

        var meta = await metadataCache.GetAsync(frame.PointId, ct).ConfigureAwait(false);
        if (meta is null)
        {
            logger.LogWarning("Ingress: unknown point_id '{PointId}', skipping frame", frame.PointId);
            Count(frame.GatewayId, "unknown_point");
            return false;
        }

        // Ownership: when the twin records which gateway owns the point, reject a mismatching sender.
        // When the twin has no gatewayId for the point, fall back to provenance-only (cannot verify).
        if (!string.IsNullOrEmpty(meta.GatewayId) &&
            !string.Equals(meta.GatewayId, frame.GatewayId, StringComparison.Ordinal))
        {
            logger.LogWarning("Ingress: gateway '{Gateway}' does not own point '{PointId}' (owner '{Owner}'), skipping",
                frame.GatewayId, frame.PointId, meta.GatewayId);
            Count(frame.GatewayId, "gateway_mismatch");
            return false;
        }

        try
        {
            // The bus publish is a JetStream publish-ack (#187): it throws if the stream did not
            // persist the frame, so we count it as accepted only on success. A failure is logged +
            // metered and the frame is not acked to the gateway, but the stream continues.
            await bus.PublishAsync(ValidatedSubject, BuildValidatedTelemetry(frame, meta), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ingress: publish failed for point '{PointId}', not accepted", frame.PointId);
            Count(frame.GatewayId, "publish_failed");
            return false;
        }

        Count(frame.GatewayId, "published");
        return true;
    }

    private static string BuildValidatedTelemetry(TelemetryFrame frame, PointMetadata meta)
    {
        var timestamp = NormalizeTimestamp(frame.Timestamp);

        var dataProps = new List<JsonObjectProperty>
        {
            new("gatewayId", new JsonString(frame.GatewayId)),
        };
        foreach (var kv in frame.Attributes)
        {
            // "gatewayId" is reserved for authoritative provenance — never let an attribute shadow it
            // (would produce a duplicate JSON key whose last-wins value is attribute-controlled).
            if (string.Equals(kv.Key, "gatewayId", StringComparison.Ordinal)) continue;
            dataProps.Add(new JsonObjectProperty(kv.Key, new JsonString(kv.Value)));
        }

        var entity = ValidMessage.ValidTelemetryEntity.Create(
            id: $"{meta.PointId}.{DateTime.UtcNow.ToUnixTime()}",
            pointId: meta.PointId,
            building: new JsonString(meta.Building),
            datetime: new JsonString(timestamp).As<JsonDateTime>(),
            value: ToValueEntity(frame),
            deviceId: new JsonString(meta.DeviceId),
            name: new JsonString(meta.Name),
            data: new ValidMessage.ValidTelemetryEntity.DataEntity([.. dataProps]));

        return ValidMessage.Create(
            new ValidMessage.ValidTelemetryEntityArray([entity.AsAny])).ToString();
    }

    // Maps the frame's discriminated value (#152) to the validated-telemetry union value. A frame with
    // no value case set — e.g. a legacy numeric gateway that omitted its default-0.0 field-3 reading —
    // falls through to numeric value_num (0.0), preserving the pre-#152 numeric-only wire behavior.
    private static ValidMessage.ValidTelemetryEntity.ValueEntity ToValueEntity(TelemetryFrame frame) =>
        frame.ValueCase switch
        {
            TelemetryFrame.ValueOneofCase.ValueStr => frame.ValueStr,
            // Build the boolean from a JSON-backed value. The generated union's dotnet-bool backing
            // (ValueEntity(bool) / implicit-from-bool) is broken in the pinned Corvus.Json 2.0.20 — it
            // sets numberBacking but not boolBacking, so serialization always emits `false`. A
            // JSON-element-backed value round-trips correctly.
            TelemetryFrame.ValueOneofCase.ValueBool =>
                ValidMessage.ValidTelemetryEntity.ValueEntity.Parse(frame.ValueBool ? "true" : "false"),
            _ => (JsonNumber)frame.ValueNum,
        };

    // A non-empty, parseable timestamp is normalized to round-trip ISO-8601; empty or unparseable
    // falls back to receive time so a malformed gateway timestamp cannot fail downstream date parsing.
    private static string NormalizeTimestamp(string raw)
        => !string.IsNullOrEmpty(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToString("O", CultureInfo.InvariantCulture)
            : DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static void Count(string gatewayId, string result) =>
        BuildingOsMetrics.IngressMessages.Add(
            1,
            new KeyValuePair<string, object?>("source", "gateway-grpc"),
            new KeyValuePair<string, object?>("gateway", gatewayId),
            new KeyValuePair<string, object?>("result", result));
}
