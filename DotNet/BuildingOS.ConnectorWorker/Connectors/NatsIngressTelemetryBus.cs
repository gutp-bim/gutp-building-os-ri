using System.Collections.Concurrent;
using System.Text;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Publishes ingress telemetry to a <c>building-os.*</c> subject (raw or validated). The owning
/// stream is resolved from the subject and ensured before the first publish to that stream, so a cold
/// start / ConnectorWorker restart cannot silently drop telemetry (the same startup-order guard the
/// MQTT/AMQP ingress workers use).
///
/// The publish is a <b>JetStream publish-ack</b> (<see cref="INatsJSContext.PublishAsync"/>, or the
/// pipelined <see cref="BeginPublishAsync"/> whose ack task the caller awaits within a bounded window,
/// #625): a frame counts as accepted only once the stream has persisted it (#187) — a NATS-side drop
/// surfaces as a thrown ack error rather than a silent at-most-once loss. A publish to
/// <c>building-os.validated.telemetry</c> also updates the KV hot store (via
/// <see cref="ValidatedTelemetryHotStore"/>), exactly like the <c>NatsKvPublisher</c> decorator every
/// other validated-telemetry producer routes through. The pod stays stateless: each frame is forwarded
/// immediately.
/// </summary>
public sealed class NatsIngressTelemetryBus(
    INatsJSContext js, IHotTelemetryStore hot, ILogger<NatsIngressTelemetryBus> logger,
    TelemetrySizeLimits? sizeLimits = null) : IIngressTelemetryBus
{
    private const string ValidatedSubject = "building-os.validated.telemetry";

    /// <summary>#415: event-lag provenance; same vocabulary GatewayIngressService tags its counters with.</summary>
    private const string LagSource = "gateway-grpc";

    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _ensuredStreams = new(StringComparer.Ordinal);

    public async Task PublishAsync(string subject, string message, CancellationToken cancellationToken)
    {
        if (subject == ValidatedSubject) (sizeLimits ?? TelemetrySizeLimits.Default).Validate(message, LagSource);
        await EnsureStreamExistsAsync(subject, cancellationToken).ConfigureAwait(false);

        var bytes = Encoding.UTF8.GetBytes(message);
        var ack = await js.PublishAsync(subject, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        // Throws if the stream did not persist the message (e.g. no stream, quota) — the caller then
        // does not count the frame as accepted and the ingest stream continues.
        ack.EnsureSuccess();

        if (subject == ValidatedSubject)
            await ValidatedTelemetryHotStore.WriteAsync(hot, message, LagSource, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// #625 slice B: same validation and stream guard as <see cref="PublishAsync"/>, but the publish
    /// goes out through <see cref="INatsJSContext.PublishConcurrentAsync{T}"/>, which returns before
    /// the ack arrives. The returned ack task performs the same <c>EnsureSuccess</c> once the ack
    /// lands, so what "accepted" means (#187) is unchanged — only where the wait happens. The hot-store
    /// put is not done here but in <see cref="CompletePublishAsync"/>, which the caller runs in publish
    /// order: ack tasks complete concurrently, and the latest value must not be overwritten by an
    /// older frame. Messages on one connection are still written in call order.
    /// </summary>
    public async Task<Task> BeginPublishAsync(string subject, string message, CancellationToken cancellationToken)
    {
        if (subject == ValidatedSubject) (sizeLimits ?? TelemetrySizeLimits.Default).Validate(message, LagSource);
        await EnsureStreamExistsAsync(subject, cancellationToken).ConfigureAwait(false);

        var bytes = Encoding.UTF8.GetBytes(message);
        var future = await js.PublishConcurrentAsync(subject, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        return AwaitAckAsync(future, cancellationToken);
    }

    /// <inheritdoc />
    public Task CompletePublishAsync(string subject, string message, CancellationToken cancellationToken)
        => subject == ValidatedSubject
            ? ValidatedTelemetryHotStore.WriteAsync(hot, message, LagSource, logger, cancellationToken)
            : Task.CompletedTask;

    private static async Task AwaitAckAsync(NatsJSPublishConcurrentFuture future, CancellationToken cancellationToken)
    {
        PubAckResponse ack;
        await using (future.ConfigureAwait(false))
        {
            ack = await future.GetResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        ack.EnsureSuccess();
    }

    private async Task EnsureStreamExistsAsync(string subject, CancellationToken ct)
    {
        var (streamName, streamSubjects) = NatsStreamTopology.Resolve(subject);
        if (_ensuredStreams.ContainsKey(streamName)) return;

        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensuredStreams.ContainsKey(streamName)) return;
            await NatsStreamInitialization.EnsureAsync(
                js, new StreamConfig(streamName, streamSubjects), ct).ConfigureAwait(false);
            _ensuredStreams[streamName] = true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }
}
