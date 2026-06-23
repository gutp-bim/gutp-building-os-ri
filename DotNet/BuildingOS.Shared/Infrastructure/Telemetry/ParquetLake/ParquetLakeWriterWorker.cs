using System.Diagnostics;
using BuildingOS.Shared.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Configuration for the <see cref="ParquetLakeWriterWorker"/> (#213).</summary>
public sealed record ParquetLakeWriterOptions
{
    public string Subject { get; init; } = "building-os.validated.telemetry";
    public string DurableName { get; init; } = "parquetlakewriter";
    /// <summary>Flush when the buffered rows reach this count.</summary>
    public int FlushMaxRows { get; init; } = 50_000;
    /// <summary>Flush at least this often (also the idle wake-up cadence).</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>How long an un-acked message survives redelivery — must exceed <see cref="FlushInterval"/>.</summary>
    public TimeSpan AckWait { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Stream MaxAge for BUILDING_OS_VALIDATED — must exceed flush interval + AckWait.</summary>
    public TimeSpan StreamMaxAge { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Optional stream MaxBytes cap (0 = unbounded / server-limited).</summary>
    public long StreamMaxBytes { get; init; }
    /// <summary>Max messages per fetch poll.</summary>
    public int FetchMaxMsgs { get; init; } = 1_000;
}

/// <summary>
/// Writes <c>building-os.validated.telemetry</c> directly to the Parquet lake (#213), bypassing
/// TimescaleDB. A durable pull consumer fetches messages; rows are accumulated (grouped by
/// building×hour, de-duplicated by id) and flushed at most every <see cref="ParquetLakeWriterOptions.FlushInterval"/>
/// or when <see cref="ParquetLakeWriterOptions.FlushMaxRows"/> is reached. Messages are acked only after
/// every partition object is PUT, so a crash mid-flush redelivers the window (at-least-once); the
/// deterministic <c>part-{firstSeq}-{lastSeq}</c> naming makes the rewrite idempotent.
/// </summary>
public sealed class ParquetLakeWriterWorker : BackgroundService
{
    private readonly INatsJSContext _js;
    private readonly IParquetLakeWriter _writer;
    private readonly ILogger<ParquetLakeWriterWorker> _logger;
    private readonly ParquetLakeWriterOptions _options;

    public ParquetLakeWriterWorker(
        INatsJSContext js,
        IParquetLakeWriter writer,
        ILogger<ParquetLakeWriterWorker> logger,
        ParquetLakeWriterOptions options)
    {
        _js = js;
        _writer = writer;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Bounded fetch poll window for the pull consumer. The NATS client derives the idle-heartbeat as
    /// ≈ Expires/2 and rejects any value ≥ 30s ("idleHeartbeat must be less than 00:00:30"). The flush
    /// cadence is time-based (<see cref="FlushPolicy"/>), so the poll window is decoupled from
    /// <see cref="ParquetLakeWriterOptions.FlushInterval"/>: poll at most 20s (idle-heartbeat ≈ 10s),
    /// never longer than the flush interval. Without this, the default 5-minute flush interval made
    /// every fetch throw and the lake writer never persisted anything.
    /// </summary>
    public static TimeSpan ComputeFetchExpires(TimeSpan flushInterval) =>
        TimeSpan.FromSeconds(Math.Clamp(flushInterval.TotalSeconds, 1, 20));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (streamName, subjects) = NatsStreamTopology.Resolve(_options.Subject);
        await EnsureStreamWithLimitsAsync(streamName, subjects, stoppingToken).ConfigureAwait(false);

        var consumer = await _js.CreateOrUpdateConsumerAsync(streamName, new ConsumerConfig(_options.DurableName)
        {
            FilterSubject = _options.Subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            AckWait = _options.AckWait,
            MaxAckPending = Math.Max(_options.FlushMaxRows * 2, 100_000),
        }, stoppingToken).ConfigureAwait(false);

        var accumulator = new TelemetryBatchAccumulator();
        var pending = new List<NatsJSMsg<string>>();
        var sinceFlush = Stopwatch.StartNew();

        _logger.LogInformation(
            "ParquetLakeWriterWorker started (subject={Subject}, durable={Durable}, flush={Interval}/{Rows} rows)",
            _options.Subject, _options.DurableName, _options.FlushInterval, _options.FlushMaxRows);

        var fetchExpires = ComputeFetchExpires(_options.FlushInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fetchOpts = new NatsJSFetchOpts
                {
                    MaxMsgs = _options.FetchMaxMsgs,
                    Expires = fetchExpires,
                };

                await foreach (var msg in consumer.FetchAsync<string>(opts: fetchOpts, cancellationToken: stoppingToken)
                                   .ConfigureAwait(false))
                {
                    var seq = msg.Metadata?.Sequence.Stream ?? 0;
                    var rows = ValidTelemetryEnvelope.Parse(msg.Data ?? string.Empty);
                    accumulator.Add(seq, rows);
                    pending.Add(msg);

                    if (accumulator.RowCount >= _options.FlushMaxRows)
                    {
                        await FlushAsync(accumulator, pending, sinceFlush, stoppingToken).ConfigureAwait(false);
                    }
                }

                if (FlushPolicy.ShouldFlush(accumulator.RowCount, _options.FlushMaxRows, sinceFlush.Elapsed, _options.FlushInterval))
                {
                    await FlushAsync(accumulator, pending, sinceFlush, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                BuildingOsMetrics.ParquetWriterFailures.Add(1);
                _logger.LogError(ex, "ParquetLakeWriterWorker: fetch/flush loop error; messages will be redelivered");
                // Drop accumulated state without ack; the un-acked messages redeliver after AckWait.
                accumulator.Drain();
                pending.Clear();
                sinceFlush.Restart();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }

        // Best-effort final flush on shutdown.
        if (!accumulator.IsEmpty)
        {
            try { await FlushAsync(accumulator, pending, sinceFlush, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ParquetLakeWriterWorker: final flush failed"); }
        }
    }

    private async Task FlushAsync(
        TelemetryBatchAccumulator accumulator, List<NatsJSMsg<string>> pending, Stopwatch sinceFlush, CancellationToken ct)
    {
        var dropped = accumulator.SkippedNoTimestamp;
        var batches = accumulator.Drain();
        if (dropped > 0)
        {
            BuildingOsMetrics.ParquetWriterDropped.Add(dropped);
        }
        if (batches.Count == 0)
        {
            sinceFlush.Restart();
            // Nothing to write but ack what we consumed (e.g. all rows were unparseable/empty).
            await AckAllAsync(pending, ct).ConfigureAwait(false);
            return;
        }

        var sw = Stopwatch.StartNew();
        long rows = 0;
        DateTime newestEvent = DateTime.MinValue;

        foreach (var batch in batches)
        {
            var (key, _) = await _writer.WriteAsync(batch, ct).ConfigureAwait(false);
            rows += batch.Rows.Count;
            // Lag is measured against the newest actual event time in the batch (not the hour ceiling),
            // so the metric isn't under-reported by up to an hour.
            if (batch.MaxEventUtc > newestEvent) newestEvent = batch.MaxEventUtc;
            _logger.LogDebug("ParquetLakeWriter: wrote {Rows} rows to {Key}", batch.Rows.Count, key);
        }

        // All objects PUT — now ack the consumed messages (at-least-once boundary).
        await AckAllAsync(pending, ct).ConfigureAwait(false);

        sw.Stop();
        sinceFlush.Restart();
        BuildingOsMetrics.ParquetWriterRows.Add(rows);
        BuildingOsMetrics.ParquetWriterFlushes.Add(1);
        BuildingOsMetrics.ParquetWriterFlushDuration.Record(sw.Elapsed.TotalMilliseconds);
        if (newestEvent > DateTime.MinValue)
        {
            BuildingOsMetrics.ParquetWriterFreshnessLag.Record(Math.Max(0, (DateTime.UtcNow - newestEvent).TotalSeconds));
        }
    }

    private async Task AckAllAsync(List<NatsJSMsg<string>> pending, CancellationToken ct)
    {
        foreach (var msg in pending)
        {
            await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        pending.Clear();
    }

    private async Task EnsureStreamWithLimitsAsync(string streamName, string[] subjects, CancellationToken ct)
    {
        var config = ValidatedStreamLimits.Apply(
            new StreamConfig(streamName, subjects), _options.StreamMaxAge, _options.StreamMaxBytes);

        // Only the "stream not found" lookup result drives create-vs-update. A genuine UpdateStream
        // failure (permissions / invalid config / transient API error) must surface its own error
        // rather than being masked by a follow-up CreateStream that fails with "already in use".
        bool exists;
        try
        {
            await _js.GetStreamAsync(streamName, cancellationToken: ct).ConfigureAwait(false);
            exists = true;
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            exists = false;
        }

        if (exists)
            await _js.UpdateStreamAsync(config, ct).ConfigureAwait(false);
        else
            await _js.CreateStreamAsync(config, ct).ConfigureAwait(false);
    }
}
