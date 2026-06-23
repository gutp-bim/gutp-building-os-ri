using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Reads unflushed telemetry rows from the validated JetStream using an ephemeral ordered consumer
/// (#220). Decodes <see cref="ValidTelemetryEnvelope"/> JSON messages, flattens to rows, and filters
/// by <paramref name="pointId"/>. Failures propagate to the caller (<see cref="TailMergedTelemetryStore"/>
/// catches and degrades to lake-only).
/// </summary>
public sealed class NatsTailReader : IJetStreamTailReader
{
    private readonly INatsJSContext _js;
    private const string StreamName = "BUILDING_OS_VALIDATED";
    private const string Subject    = "building-os.validated.telemetry";

    public NatsTailReader(INatsJSContext js) => _js = js;

    public async Task<ValidTelemetryData[]> ReadSinceAsync(
        DateTime since, string pointId, int maxMsgs, TimeSpan timeout, CancellationToken ct)
    {
        // DateTimeOffset(DateTime, offset) throws ArgumentException when Kind == Local.
        var sinceUtc = since.Kind == DateTimeKind.Local ? since.ToUniversalTime()
            : DateTime.SpecifyKind(since, DateTimeKind.Utc);

        var consumer = await _js.CreateOrderedConsumerAsync(
            StreamName,
            new NatsJSOrderedConsumerOpts
            {
                FilterSubjects = new[] { Subject },
                DeliverPolicy  = ConsumerConfigDeliverPolicy.ByStartTime,
                OptStartTime   = new DateTimeOffset(sinceUtc, TimeSpan.Zero),
            },
            ct).ConfigureAwait(false);

        var result  = new List<ValidTelemetryData>();
        var fetched = 0;
        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fetchCts.CancelAfter(timeout);

        try
        {
            await foreach (var msg in consumer.ConsumeAsync<string>(
                opts: new NatsJSConsumeOpts { MaxMsgs = maxMsgs },
                cancellationToken: fetchCts.Token).ConfigureAwait(false))
            {
                fetched++;
                if (msg.Data is not null)
                {
                    var envelope = ValidTelemetryEnvelope.Parse(msg.Data);
                    foreach (var row in envelope)
                    {
                        if (row.PointId == pointId)
                            result.Add(row);
                    }
                }

                // Stop as soon as the consumer has caught up with the stream. ConsumeAsync is a
                // *continuous* pull that otherwise blocks for the full `timeout` waiting for new
                // messages even when the tail is already drained — so a query whose window ends near
                // `now` would pay the entire fetch timeout (~3s) on every call. NumPending == 0 means
                // there is nothing more to read; break immediately (the real lake read is ~10ms).
                if (fetched >= maxMsgs || msg.Metadata?.NumPending == 0)
                    break;
            }
        }
        catch (OperationCanceledException) when (fetchCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout backstop for the zero-message case (nothing from `since`): return what we have.
        }

        return result.ToArray();
    }
}
