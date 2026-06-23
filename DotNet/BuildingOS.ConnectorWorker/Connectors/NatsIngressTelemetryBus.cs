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
/// The publish is a <b>JetStream publish-ack</b> (<see cref="INatsJSContext.PublishAsync"/>): the call
/// only returns successfully once the stream has persisted the message, so the gRPC ingest can count a
/// frame as accepted only after it is durably stored (#187) — a NATS-side drop surfaces as a thrown
/// ack error rather than a silent at-most-once loss. A publish to
/// <c>building-os.validated.telemetry</c> also updates the KV hot store (via
/// <see cref="ValidatedTelemetryHotStore"/>), exactly like the <c>NatsKvPublisher</c> decorator every
/// other validated-telemetry producer routes through. The pod stays stateless: each frame is forwarded
/// immediately.
/// </summary>
public sealed class NatsIngressTelemetryBus(
    INatsJSContext js, IHotTelemetryStore hot, ILogger<NatsIngressTelemetryBus> logger) : IIngressTelemetryBus
{
    private const string ValidatedSubject = "building-os.validated.telemetry";

    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _ensuredStreams = new(StringComparer.Ordinal);

    public async Task PublishAsync(string subject, string message, CancellationToken cancellationToken)
    {
        await EnsureStreamExistsAsync(subject, cancellationToken).ConfigureAwait(false);

        var bytes = Encoding.UTF8.GetBytes(message);
        var ack = await js.PublishAsync(subject, bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        // Throws if the stream did not persist the message (e.g. no stream, quota) — the caller then
        // does not count the frame as accepted and the ingest stream continues.
        ack.EnsureSuccess();

        if (subject == ValidatedSubject)
            await ValidatedTelemetryHotStore.WriteAsync(hot, message, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStreamExistsAsync(string subject, CancellationToken ct)
    {
        var (streamName, streamSubjects) = NatsStreamTopology.Resolve(subject);
        if (_ensuredStreams.ContainsKey(streamName)) return;

        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensuredStreams.ContainsKey(streamName)) return;
            try
            {
                await js.GetStreamAsync(streamName, cancellationToken: ct).ConfigureAwait(false);
            }
            catch
            {
                await js.CreateStreamAsync(new StreamConfig(streamName, streamSubjects), ct).ConfigureAwait(false);
            }
            _ensuredStreams[streamName] = true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }
}
