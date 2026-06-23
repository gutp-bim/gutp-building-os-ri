namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// One drained, partition-scoped batch ready to write as a single Parquet object (#213). Carries the
/// JetStream sequence range of the contributing messages for deterministic, idempotent naming.
/// </summary>
public sealed record PartitionBatch(
    string Building, DateTime HourUtc, DateTime MaxEventUtc, ulong FirstSeq, ulong LastSeq,
    IReadOnlyList<ValidTelemetryData> Rows);

/// <summary>
/// Pure accumulator for the Parquet lake writer (#213). Rows from one or more validated-telemetry
/// messages are grouped by (building, event-hour) and de-duplicated by <c>id</c> (last wins; rows
/// without an id are kept). Each partition tracks the min/max JetStream sequence of the messages that
/// contributed to it, so <see cref="Drain"/> can produce deterministically-named objects. Rows whose
/// <c>datetime</c> cannot be parsed are skipped and counted (no silent partition).
/// </summary>
public sealed class TelemetryBatchAccumulator
{
    private const string UnknownBuilding = "unknown";

    private sealed class Bucket
    {
        public readonly Dictionary<string, ValidTelemetryData> ById = new(StringComparer.Ordinal);
        public readonly List<ValidTelemetryData> NoId = new();
        public ulong FirstSeq;
        public ulong LastSeq;
        public bool SeqInitialised;
        public DateTime MaxEventUtc;

        public void Observe(ulong seq)
        {
            if (!SeqInitialised)
            {
                FirstSeq = LastSeq = seq;
                SeqInitialised = true;
            }
            else
            {
                if (seq < FirstSeq) FirstSeq = seq;
                if (seq > LastSeq) LastSeq = seq;
            }
        }

        public void ObserveEvent(DateTime utc)
        {
            if (utc > MaxEventUtc) MaxEventUtc = utc;
        }

        public int Count => ById.Count + NoId.Count;
    }

    private readonly Dictionary<(string Building, DateTime Hour), Bucket> _buckets = new();

    /// <summary>Rows skipped because their <c>datetime</c> could not be parsed.</summary>
    public long SkippedNoTimestamp { get; private set; }

    /// <summary>Total rows currently buffered across all partitions (post-dedup).</summary>
    public int RowCount => _buckets.Values.Sum(b => b.Count);

    public bool IsEmpty => _buckets.Count == 0;

    /// <summary>
    /// Adds the rows of one message, tagged with its JetStream <paramref name="sequence"/>. Returns the
    /// number of rows accepted (parseable timestamp).
    /// </summary>
    public int Add(ulong sequence, IEnumerable<ValidTelemetryData> rows)
    {
        var accepted = 0;
        foreach (var row in rows)
        {
            if (!TelemetryTimestamp.TryParseUtc(row.Datetime, out var utc))
            {
                SkippedNoTimestamp++;
                continue;
            }

            var hour = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
            var building = string.IsNullOrEmpty(row.Building) ? UnknownBuilding : row.Building!;
            var key = (building, hour);
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
            }

            bucket.Observe(sequence);
            bucket.ObserveEvent(utc);
            if (string.IsNullOrEmpty(row.Id))
            {
                bucket.NoId.Add(row);
            }
            else
            {
                bucket.ById[row.Id!] = row; // dedup: last wins
            }
            accepted++;
        }
        return accepted;
    }

    /// <summary>Returns the accumulated partition batches and clears the accumulator.</summary>
    public IReadOnlyList<PartitionBatch> Drain()
    {
        var result = new List<PartitionBatch>(_buckets.Count);
        foreach (var ((building, hour), bucket) in _buckets)
        {
            var rows = bucket.ById.Values
                .Concat(bucket.NoId)
                .OrderBy(r => r.Datetime, StringComparer.Ordinal)
                .ToList();
            result.Add(new PartitionBatch(
                building, hour, bucket.MaxEventUtc, bucket.FirstSeq, bucket.LastSeq, rows));
        }
        _buckets.Clear();
        SkippedNoTimestamp = 0;
        return result;
    }
}
