using System.Globalization;
using BuildingOS.ConnectorWorker.Protos;
using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using Corvus.Json;
using System.Threading.Channels;
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
/// A frame with an unknown <c>point_id</c>, whose <c>gateway_id</c> does not own that point in the
/// twin, or (opt-in, #292) whose point is not connected to the building hierarchy, is skipped
/// (logged + metered); the stream continues and <see cref="StreamAck"/>
/// counts only successfully-enqueued frames. The service is stateless, so Ingress pods scale
/// horizontally. Enabled only when GRPC_INGRESS_PORT is set (see Program.cs).
/// </summary>
public sealed class GatewayIngressService(
    IIngressTelemetryBus bus,
    IPointMetadataCache metadataCache,
    IngressIdentityOptions identity,
    IngressHierarchyOptions hierarchy,
    ILogger<GatewayIngressService> logger,
    RunContextVerifier? runContext = null,
    IngressPublishOptions? publish = null) : Protos.GatewayIngress.GatewayIngressBase
{
    private const string ValidatedSubject = "building-os.validated.telemetry";

    private readonly int _window = Math.Max(1, (publish ?? new IngressPublishOptions()).Window);

    /// <summary>A frame whose publish is in flight: its ack task plus what the result is tagged with.</summary>
    private readonly record struct PendingAck(string GatewayId, string PointId, string Message, Task Ack);

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

    /// <summary>
    /// Transport-agnostic core (testable without a live gRPC channel / NATS / OxiGraph).
    ///
    /// #625 slice B: publishes are pipelined. The read loop validates each frame, issues its publish
    /// through <see cref="IIngressTelemetryBus.BeginPublishAsync"/> and queues the ack task; a drain
    /// task awaits the acks in order and counts <c>accepted</c> only for the ones that succeeded
    /// (#187 unchanged: accepted = persisted). At most <see cref="IngressPublishOptions.Window"/> acks
    /// are outstanding — past that the read loop blocks until the drain frees a slot, so memory is
    /// bounded and the gateway sees backpressure instead of unbounded buffering. Window 1 is the
    /// pre-#625 serial loop. The stream's <see cref="StreamAck"/> is produced only after every queued
    /// ack has been resolved; on cancellation the still-unresolved ones are not counted.
    /// </summary>
    internal async Task<long> RunAsync(
        IAsyncStreamReader<TelemetryFrame> requestStream, CancellationToken ct, string? trustedGatewayId = null)
    {
        using var slots = new SemaphoreSlim(_window, _window);
        var pending = Channel.CreateBounded<PendingAck>(new BoundedChannelOptions(_window)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var drain = DrainAsync(pending.Reader, slots, ct);

        try
        {
            while (await requestStream.MoveNext(ct).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                if (!await TryBeginIngestAsync(frame, trustedGatewayId, slots, ct).ConfigureAwait(false)) continue;
                // TryBeginIngestAsync releases the slot on every non-queued outcome, so a frame that
                // reaches here holds exactly one slot until the drain releases it.
            }
        }
        catch (Exception)
        {
            // The gateway went away, the host is stopping, or the transport failed: stop reading and
            // settle the drain before surfacing the exception as before. Under cancellation the drain
            // gives up on the unresolved acks (not counted); otherwise it resolves what is in flight.
            // Either way it must finish before the slot pool is disposed.
            pending.Writer.TryComplete();
            ObserveLeftovers(await drain.ConfigureAwait(false));
            throw;
        }
        pending.Writer.TryComplete();

        var (accepted, _) = await drain.ConfigureAwait(false);
        logger.LogInformation("Ingress stream completed: {Accepted} telemetry frames enqueued (publish window {Window})", accepted, _window);
        return accepted;

        // Resolves queued acks in order. Returns the accepted count plus any acks it stopped awaiting
        // (cancellation), which the caller observes so a late fault is never an unobserved exception.
        async Task<(long Accepted, List<Task> Unresolved)> DrainAsync(
            ChannelReader<PendingAck> reader, SemaphoreSlim slotPool, CancellationToken token)
        {
            var count = 0L;
            var unresolved = new List<Task>();
            await foreach (var item in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    unresolved.Add(item.Ack);
                    slotPool.Release();
                    continue;
                }
                try
                {
                    await item.Ack.WaitAsync(token).ConfigureAwait(false);
                    count++;
                    Count(item.GatewayId, "published");
                    // In publish order (this loop is sequential): the hot-store put must not let an
                    // older frame overwrite a newer one. Persisted already, so it never un-accepts.
                    await bus.CompletePublishAsync(ValidatedSubject, item.Message, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Undetermined, not failed: the frame may well be persisted, but we cannot tell the
                    // gateway so (the stream is gone), hence it is not counted — same as before #625.
                    unresolved.Add(item.Ack);
                }
                catch (TelemetrySizeLimitException ex)
                {
                    // Only reachable through a transport that defers validation to the ack task.
                    logger.LogWarning(
                        "Ingress: telemetry {Dimension} exceeds byte limit ({ActualBytes} > {LimitBytes}), not accepted",
                        ex.Dimension, ex.ActualBytes, ex.LimitBytes);
                    Count(item.GatewayId, "size_exceeded");
                }
                catch (Exception ex)
                {
                    // The ack failed (stream refused/dropped the message, request timed out): not
                    // accepted, but the stream continues, exactly like the serial path's publish_failed.
                    logger.LogWarning(ex, "Ingress: publish failed for point '{PointId}', not accepted", item.PointId);
                    Count(item.GatewayId, "publish_failed");
                }
                finally
                {
                    slotPool.Release();
                }
            }
            return (count, unresolved);
        }

        void ObserveLeftovers((long Accepted, List<Task> Unresolved) result)
        {
            foreach (var task in result.Unresolved)
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        // Validates the frame, acquires a window slot and issues the publish; true only when an ack is
        // now queued for the drain. Every other outcome has already been counted and released its slot.
        async Task<bool> TryBeginIngestAsync(TelemetryFrame frame, string? trusted, SemaphoreSlim slotPool, CancellationToken token)
        {
            var message = await PrepareAsync(frame, trusted, token).ConfigureAwait(false);
            if (message is null) return false;

            await slotPool.WaitAsync(token).ConfigureAwait(false); // backpressure: the window is full
            Task ack;
            try
            {
                ack = await bus.BeginPublishAsync(ValidatedSubject, message, token).ConfigureAwait(false);
            }
            catch (TelemetrySizeLimitException ex)
            {
                slotPool.Release();
                logger.LogWarning(
                    "Ingress: telemetry {Dimension} exceeds byte limit ({ActualBytes} > {LimitBytes}), not accepted",
                    ex.Dimension, ex.ActualBytes, ex.LimitBytes);
                Count(frame.GatewayId, "size_exceeded");
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                slotPool.Release();
                logger.LogWarning(ex, "Ingress: publish failed for point '{PointId}', not accepted", frame.PointId);
                Count(frame.GatewayId, "publish_failed");
                return false;
            }
            catch
            {
                slotPool.Release();
                throw;
            }

            await pending.Writer.WriteAsync(new PendingAck(frame.GatewayId, frame.PointId, message, ack), token).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// The pre-publish checks (ids, identity binding, run context, twin lookup, ownership, hierarchy):
    /// returns the validated-telemetry message to publish, or null after counting the rejection.
    /// </summary>
    private async Task<string?> PrepareAsync(TelemetryFrame frame, string? trustedGatewayId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(frame.GatewayId) || string.IsNullOrEmpty(frame.PointId))
        {
            logger.LogWarning("Ingress: frame missing gateway_id/point_id, skipping");
            Count(frame.GatewayId, "missing_id");
            return null;
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
            return null;
        }

        // Unsigned run context (opt-in, GRPC_INGRESS_RUN_CONTEXT_TRUST_FILE, ADR-0012): a frame claiming a run
        // classification must match an operator-placed lease and the mTLS-verified gateway. Fail closed: a
        // claim arriving with no configuration is rejected, never passed through as valid. Checked before the
        // twin lookup, like identity binding.
        if (runContext is null && RunContextVerifier.HasRunClaim(frame))
        {
            logger.LogWarning("Ingress: run context claim rejected (run_context_unconfigured) for gateway '{Gateway}' point '{PointId}', skipping",
                frame.GatewayId, frame.PointId);
            Count(frame.GatewayId, "run_context_unconfigured");
            return null;
        }
        if (runContext is not null && !runContext.Validate(frame, trustedGatewayId, out var runReason))
        {
            logger.LogWarning("Ingress: run context claim rejected ({Reason}) for gateway '{Gateway}' point '{PointId}', skipping",
                runReason, frame.GatewayId, frame.PointId);
            Count(frame.GatewayId, runReason);
            return null;
        }

        var meta = await metadataCache.GetAsync(frame.PointId, ct).ConfigureAwait(false);
        if (meta is null)
        {
            logger.LogWarning("Ingress: unknown point_id '{PointId}', skipping frame", frame.PointId);
            Count(frame.GatewayId, "unknown_point");
            return null;
        }

        // Ownership: when the twin records which gateway owns the point, reject a mismatching sender.
        // When the twin has no gatewayId for the point, fall back to provenance-only (cannot verify).
        if (!string.IsNullOrEmpty(meta.GatewayId) &&
            !string.Equals(meta.GatewayId, frame.GatewayId, StringComparison.Ordinal))
        {
            logger.LogWarning("Ingress: gateway '{Gateway}' does not own point '{PointId}' (owner '{Owner}'), skipping",
                frame.GatewayId, frame.PointId, meta.GatewayId);
            Count(frame.GatewayId, "gateway_mismatch");
            return null;
        }

        // Hierarchy completeness (#292): a point the twin does not place under a building/device
        // yields telemetry no resource view can navigate to. Both fields were already resolved with
        // the cached metadata, so the check costs no extra query on the hot path. Opt-in — an
        // incomplete twin (#118) must keep flowing by default, or a deployment mid-modelling would
        // lose every reading.
        //
        // HasBuildingPath, not Building: the latter is the denormalized literal published as the
        // telemetry's building field, which is not evidence of placement in the hierarchy.
        var hierarchyDecision = IngressHierarchyPolicy.Check(hierarchy.Enforce, meta.HasBuildingPath, meta.DeviceId);
        if (hierarchyDecision != IngressHierarchyDecision.Allow)
        {
            var reason = hierarchyDecision == IngressHierarchyDecision.RejectNoBuildingPath
                ? "no_building_path"
                : "no_device_link";
            logger.LogWarning(
                "Ingress: point '{PointId}' is not connected to the building hierarchy ({Reason}), skipping",
                frame.PointId, reason);
            Count(frame.GatewayId, reason);
            return null;
        }

        // The publish itself is a JetStream publish-ack (#187): the frame is counted as accepted only
        // once its ack succeeds (see RunAsync's drain); a failure is logged + metered and the frame
        // is not acked to the gateway, but the stream continues.
        try
        {
            return BuildValidatedTelemetry(frame, meta);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Serialization can fail for one frame (e.g. a NaN/Infinity value_num). Before #625 this
            // ran inside the publish try and was counted as publish_failed; keep that boundary so one
            // bad frame never ends the whole stream (Codex review on #629).
            logger.LogWarning(ex, "Ingress: could not build telemetry for point '{PointId}', not accepted", frame.PointId);
            Count(frame.GatewayId, "publish_failed");
            return null;
        }
    }

    private string BuildValidatedTelemetry(TelemetryFrame frame, PointMetadata meta)
    {
        var (eventTime, timestamp) = NormalizeTimestamp(frame.Timestamp, frame.GatewayId);

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
            id: TelemetryId(meta.PointId, eventTime),
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

    /// <summary>
    /// The validated-telemetry <c>id</c> for a gRPC frame: <c>{pointId}.{event time in unix µs}</c>.
    ///
    /// The lake de-duplicates rows by <c>id</c> (the writer within a flush, compaction within an hour,
    /// reads across overlapping parts), so the id must name the <i>reading</i>, not the moment it
    /// arrived. It used to be <c>{pointId}.{receive time in unix ms}</c>: two distinct readings of one
    /// point received in the same millisecond — a gateway catching up a buffer, or simply a fast
    /// stream after #625's pipelining — collapsed into one lake row although both were acked as
    /// accepted (#625 S26: ~1.6 % of rows at 50 readings/s per point). A frame carries the point id and
    /// its own timestamp, which is enough to identify the reading, so the id is deterministic as
    /// <c>telemetry-specification.md</c> asks: a re-sent frame (e.g. one persisted but not counted in
    /// <see cref="StreamAck"/> when a stream dropped) now gets the same id and collapses instead of
    /// duplicating. Two frames of one point with the very same timestamp are one reading by this
    /// definition (the later one wins), which is also how S22 counts duplicates. A frame whose
    /// timestamp fell back to receive time (#418) gets a receive-time id, as before.
    /// </summary>
    internal static string TelemetryId(string pointId, DateTimeOffset eventTime)
        => $"{pointId}.{(eventTime.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond}";

    // A non-empty, parseable timestamp is normalized to round-trip ISO-8601; empty or unparseable
    // falls back to receive time so a malformed gateway timestamp cannot fail downstream date parsing.
    // #418: that fallback silently mixes clock semantics (the point's later readings carry the
    // gateway's own clock, this one carries ours) — logged + metered so it is observable after the
    // fact rather than a silent divergence.
    private (DateTimeOffset Instant, string Text) NormalizeTimestamp(string raw, string gatewayId)
    {
        if (!string.IsNullOrEmpty(raw) &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return (parsed, parsed.ToString("O", CultureInfo.InvariantCulture));
        }

        logger.LogWarning(
            "Ingress: frame timestamp missing/unparsable for gateway '{Gateway}', falling back to receive time",
            gatewayId);
        BuildingOsMetrics.IngressTimestampFallbacks.Add(
            1,
            new KeyValuePair<string, object?>("source", "gateway-grpc"),
            new KeyValuePair<string, object?>("gateway", gatewayId));
        var now = DateTime.UtcNow;
        return (new DateTimeOffset(now), now.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void Count(string gatewayId, string result) =>
        BuildingOsMetrics.IngressMessages.Add(
            1,
            new KeyValuePair<string, object?>("source", "gateway-grpc"),
            new KeyValuePair<string, object?>("gateway", gatewayId),
            new KeyValuePair<string, object?>("result", result));
}
