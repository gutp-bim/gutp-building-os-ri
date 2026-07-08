using System.Diagnostics.Metrics;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Process-wide custom metrics for the BuildingOS pipeline, emitted on the
/// "BuildingOS.Pipeline" meter (registered in <see cref="OtelSetup.AddOtlpTelemetry"/>).
///
/// Static because connector/worker instances are constructed with <c>new</c> rather than
/// resolved from DI. <see cref="Meter"/> instruments are inert unless the meter is wired to
/// an exporter, so referencing these members is safe even when OpenTelemetry is disabled.
///
/// Instrument names use dots; the OTLP→Prometheus mapping renders them as
/// <c>building_os_connector_messages_processed_total</c> etc. (dots→underscores, counters
/// gain a <c>_total</c> suffix), which is what the Grafana dashboards query.
/// </summary>
public static class BuildingOsMetrics
{
    private static readonly Meter Meter = new(OtelSetup.MeterName, "1.0.0");

    /// <summary>Messages processed by a connector worker. Tags: connector, result (published|skipped|error).</summary>
    public static readonly Counter<long> ConnectorMessagesProcessed =
        Meter.CreateCounter<long>(
            "building_os.connector.messages_processed",
            unit: "{message}",
            description: "Messages processed by a connector worker, by result.");

    /// <summary>Per-message connector processing duration (ms). Tag: connector.</summary>
    public static readonly Histogram<double> ConnectorProcessDuration =
        Meter.CreateHistogram<double>(
            "building_os.connector.process.duration",
            unit: "ms",
            description: "Connector message processing duration in milliseconds.");

    /// <summary>Messages received by an ingress transport worker. Tag: source (mqtt|amqp).</summary>
    public static readonly Counter<long> IngressMessages =
        Meter.CreateCounter<long>(
            "building_os.ingress.messages",
            unit: "{message}",
            description: "Messages received by an ingress transport worker, by source.");

    /// <summary>Device control requests handled. Tags: handler, result.</summary>
    public static readonly Counter<long> ControlRequests =
        Meter.CreateCounter<long>(
            "building_os.control.requests",
            unit: "{request}",
            description: "Device control requests handled, by handler and result.");

    /// <summary>Rows exported to cold storage (Parquet/MinIO).</summary>
    public static readonly Counter<long> ColdExportRows =
        Meter.CreateCounter<long>(
            "building_os.cold_export.rows",
            unit: "{row}",
            description: "Rows exported to cold storage.");

    /// <summary>Cold export run failures.</summary>
    public static readonly Counter<long> ColdExportFailures =
        Meter.CreateCounter<long>(
            "building_os.cold_export.failures",
            unit: "{failure}",
            description: "Cold export run failures.");

    /// <summary>Telemetry queries served by the API. Tags: tier (hot|warm|none), result.</summary>
    public static readonly Counter<long> TelemetryQueries =
        Meter.CreateCounter<long>(
            "building_os.telemetry.queries",
            unit: "{query}",
            description: "Telemetry queries served, by tier and result.");

    // === Parquet lake writer (#213) ===

    /// <summary>Rows written to the Parquet lake by the writer.</summary>
    public static readonly Counter<long> ParquetWriterRows =
        Meter.CreateCounter<long>(
            "building_os.parquet_writer.rows",
            unit: "{row}",
            description: "Rows written to the Parquet lake.");

    /// <summary>Completed flushes (one or more objects PUT then acked).</summary>
    public static readonly Counter<long> ParquetWriterFlushes =
        Meter.CreateCounter<long>(
            "building_os.parquet_writer.flushes",
            unit: "{flush}",
            description: "Parquet lake writer flushes that PUT objects and acked.");

    /// <summary>Flush duration (PUT all partitions + ack) in milliseconds.</summary>
    public static readonly Histogram<double> ParquetWriterFlushDuration =
        Meter.CreateHistogram<double>(
            "building_os.parquet_writer.flush_duration",
            unit: "ms",
            description: "Parquet lake writer flush duration.");

    /// <summary>Flush failures (PUT/ack error; messages are redelivered).</summary>
    public static readonly Counter<long> ParquetWriterFailures =
        Meter.CreateCounter<long>(
            "building_os.parquet_writer.failures",
            unit: "{failure}",
            description: "Parquet lake writer flush failures.");

    /// <summary>Freshness lag at flush: now − max(event time) in the flushed rows, in seconds.</summary>
    public static readonly Histogram<double> ParquetWriterFreshnessLag =
        Meter.CreateHistogram<double>(
            "building_os.parquet_writer.freshness_lag",
            unit: "s",
            description: "Seconds between flush time and the newest event time written.");

    /// <summary>Rows dropped before write because their timestamp could not be parsed.</summary>
    public static readonly Counter<long> ParquetWriterDropped =
        Meter.CreateCounter<long>(
            "building_os.parquet_writer.dropped",
            unit: "{row}",
            description: "Rows dropped (unparseable timestamp) by the Parquet lake writer.");

    // === Parquet lake compactor (#217) ===

    /// <summary>Hour partitions compacted (one compact object written, its parts deleted).</summary>
    public static readonly Counter<long> CompactionPartitions =
        Meter.CreateCounter<long>(
            "building_os.compaction.partitions",
            unit: "{partition}",
            description: "Hour partitions compacted into a single object.");

    /// <summary>Source part objects merged then deleted by compaction.</summary>
    public static readonly Counter<long> CompactionPartsDeleted =
        Meter.CreateCounter<long>(
            "building_os.compaction.parts_deleted",
            unit: "{object}",
            description: "Part objects deleted after a successful compaction merge.");

    /// <summary>Compaction failures (read/write/verify error; parts are left for retry).</summary>
    public static readonly Counter<long> CompactionFailures =
        Meter.CreateCounter<long>(
            "building_os.compaction.failures",
            unit: "{failure}",
            description: "Compaction failures; source parts are retained for a later retry.");

    // Rollup metrics (#222)
    public static readonly Counter<long> CompactionRollupsWritten =
        Meter.CreateCounter<long>(
            "building_os.compaction.rollup_written",
            unit: "{object}",
            description: "Hourly rollup objects written by CompactionWorker (#222).");

    public static readonly Counter<long> CompactionRollupFailures =
        Meter.CreateCounter<long>(
            "building_os.compaction.rollup_failures",
            unit: "{failure}",
            description: "Rollup write failures (non-fatal; aggregate-on-read fallback used).");

    // Tail-merge metrics (#220)
    public static readonly Counter<long> TailMergeRowsMerged =
        Meter.CreateCounter<long>(
            "building_os.parquet_lake.tail_merge_rows",
            unit: "{row}",
            description: "Telemetry rows appended from JetStream tail-merge (#220).");

    public static readonly Counter<long> TailMergeErrors =
        Meter.CreateCounter<long>(
            "building_os.parquet_lake.tail_merge_errors",
            unit: "{error}",
            description: "Tail-merge JetStream fetch errors (degraded to lake-only result).");

    // Point-list push fan-out after seed (#224/push)

    /// <summary>Post-seed point-list-update push signals, by gateway and result (published|failed|query_failed).</summary>
    public static readonly Counter<long> PointListPushSignals =
        Meter.CreateCounter<long>(
            "building_os.pointlist.push_signals",
            unit: "{signal}",
            description: "Post-seed point-list-update push signals sent per gateway, by result.");
}
