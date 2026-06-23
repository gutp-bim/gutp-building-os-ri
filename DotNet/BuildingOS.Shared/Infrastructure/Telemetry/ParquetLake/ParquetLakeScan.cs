using BuildingOS.Shared.Infrastructure.BlobStorage;
using Microsoft.Extensions.Caching.Memory;
using Parquet;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Shared MinIO/Parquet read primitives for the lake (#214): building discovery (cached), range/hour
/// key listing, and row reading. Used by both <see cref="MinioParquetColdTelemetryStore"/> (cold tier,
/// timescale mode) and <see cref="ParquetLakeTelemetryStore"/> (warm+cold, parquet mode) so the layout
/// and the on-the-wire Parquet decode live in exactly one place.
/// </summary>
internal sealed class ParquetLakeScan
{
    public const string Bucket = "cold";
    private const string BuildingsCacheKey = "lake:buildings";
    private static readonly TimeSpan BuildingsCacheTtl = TimeSpan.FromMinutes(5);

    // Learned point_id → building map (#273). A point lives in exactly one building (enforced at seed),
    // so once a read resolves a point's building we prune subsequent single-point scans to that one
    // building instead of every building in the lake. TTL bounds the rare re-assignment case.
    private const string PointBuildingCachePrefix = "lake:ptbldg:";
    private static readonly TimeSpan PointBuildingCacheTtl = TimeSpan.FromMinutes(30);

    private readonly IBlobStorage _storage;
    private readonly IMemoryCache _cache;

    public ParquetLakeScan(IBlobStorage storage, IMemoryCache cache)
    {
        _storage = storage;
        _cache = cache;
    }

    /// <summary>The learned building for a point, or null if not yet resolved.</summary>
    public string? GetCachedBuilding(string pointId)
        => _cache.TryGetValue(PointBuildingCachePrefix + pointId, out string? b) ? b : null;

    /// <summary>Records the building a point's data was found in, to prune later scans.</summary>
    public void CacheBuilding(string? pointId, string? building)
    {
        if (!string.IsNullOrEmpty(pointId) && !string.IsNullOrEmpty(building))
            _cache.Set(PointBuildingCachePrefix + pointId, building, PointBuildingCacheTtl);
    }

    /// <summary>
    /// Parquet object keys whose hour partition overlaps [start, end]. Scans every building unless
    /// <paramref name="buildingsFilter"/> restricts it (point→building pruning, #273).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListKeysInRangeAsync(
        DateTime start, DateTime end, CancellationToken ct, IReadOnlyList<string>? buildingsFilter = null)
    {
        var buildings = buildingsFilter ?? await GetBuildingsAsync(ct).ConfigureAwait(false);
        if (buildings.Count == 0)
        {
            return Array.Empty<string>();
        }

        var keys = new List<string>();
        foreach (var prefix in PartitionKeyRangePlanner.MonthPrefixes(buildings, start, end))
        {
            var listed = await _storage.ListAsync(Bucket, prefix, ct).ConfigureAwait(false);
            keys.AddRange(listed.Where(k =>
                k.EndsWith(".parquet", StringComparison.Ordinal) &&
                PartitionKeyRangePlanner.IsKeyInRange(k, start, end)));
        }
        return keys;
    }

    /// <summary>Parquet object keys in one building's hour partition.</summary>
    public async Task<IReadOnlyList<string>> ListHourKeysAsync(string building, DateTime hourUtc, CancellationToken ct)
    {
        var listed = await _storage.ListAsync(Bucket, LakePartitionKey.HourPrefix(building, hourUtc), ct).ConfigureAwait(false);
        return listed.Where(k => k.EndsWith(".parquet", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Reads the given objects and returns the rows for <paramref name="pointId"/> within [start, end].</summary>
    public async Task<List<ValidTelemetryData>> ReadKeysAsync(
        IEnumerable<string> keys, string pointId, DateTime start, DateTime end, CancellationToken ct)
    {
        var results = new List<ValidTelemetryData>();
        await ForEachObjectAsync(keys, start, end,
            pid => pid == pointId,
            (_, row) => results.Add(row), ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Reads the given objects ONCE each and returns the rows for every requested point id within
    /// [start, end], grouped by point id (#215). Avoids the N-times-the-IO of a per-point loop.
    /// </summary>
    public async Task<Dictionary<string, List<ValidTelemetryData>>> ReadKeysMultiAsync(
        IEnumerable<string> keys, ISet<string> pointIds, DateTime start, DateTime end, CancellationToken ct)
    {
        var byPoint = new Dictionary<string, List<ValidTelemetryData>>();
        await ForEachObjectAsync(keys, start, end,
            pid => pid is not null && pointIds.Contains(pid),
            (pid, row) =>
            {
                if (!byPoint.TryGetValue(pid, out var list))
                {
                    list = new List<ValidTelemetryData>();
                    byPoint[pid] = list;
                }
                list.Add(row);
            }, ct).ConfigureAwait(false);
        return byPoint;
    }

    /// <summary>All Parquet object keys in the lake (used by the compactor to plan, #217).</summary>
    public async Task<IReadOnlyList<string>> ListAllKeysAsync(CancellationToken ct)
    {
        var listed = await _storage.ListAsync(Bucket, "building_id=", ct).ConfigureAwait(false);
        return listed.Where(k => k.EndsWith(".parquet", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Reads every row from the given objects (all points, all times) — for compaction.</summary>
    public async Task<List<ValidTelemetryData>> ReadAllRowsAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var rows = new List<ValidTelemetryData>();
        await ForEachObjectAsync(keys, DateTime.MinValue, DateTime.MaxValue,
            _ => true, (_, row) => rows.Add(row), ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>Serializes the rows and PUTs them to <paramref name="key"/>; returns the byte length.</summary>
    public async Task<long> WriteObjectAsync(string key, IReadOnlyList<ValidTelemetryData> rows, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await ParquetTelemetrySerializer.WriteAsync(rows, ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        var bytes = ms.Length;
        await _storage.PutAsync(Bucket, key, ms, "application/octet-stream", ct).ConfigureAwait(false);
        return bytes;
    }

    public Task DeleteAsync(string key, CancellationToken ct) => _storage.DeleteAsync(Bucket, key, ct);

    /// <summary>Writes a raw stream to the lake bucket (used for rollup objects that are not telemetry-schema Parquet).</summary>
    public Task WriteRawAsync(string key, Stream content, CancellationToken ct)
        => _storage.PutAsync(Bucket, key, content, "application/octet-stream", ct);

    /// <summary>Distinct building ids in the lake, cached briefly (new buildings appear within the TTL).</summary>
    public async Task<IReadOnlyList<string>> GetBuildingsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(BuildingsCacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        var allKeys = await _storage.ListAsync(Bucket, "building_id=", ct).ConfigureAwait(false);
        var buildings = PartitionKeyRangePlanner.ExtractBuildings(allKeys);
        _cache.Set(BuildingsCacheKey, buildings, BuildingsCacheTtl);
        return buildings;
    }

    /// <summary>
    /// Streams each object once and emits every in-range row whose point id passes <paramref name="want"/>.
    /// The decode lives here so the single- and multi-point readers share exactly one Parquet code path.
    /// </summary>
    private async Task ForEachObjectAsync(
        IEnumerable<string> keys, DateTime start, DateTime end,
        Func<string?, bool> want, Action<string, ValidTelemetryData> emit, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            var stream = await _storage.GetAsync(Bucket, key, ct).ConfigureAwait(false);
            if (stream is null) continue;
            try
            {
                await ReadObjectAsync(stream, start, end, want, emit, ct).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Normalizes a Parquet <c>time</c> column value to a UTC <see cref="DateTime"/> for range
    /// comparison, or null when the value is not a timestamp. The writer stores UTC instants, but
    /// Parquet.Net decodes them as <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>;
    /// calling <c>ToUniversalTime()</c> on those assumes *local* time and shifts by the host offset, so
    /// on a non-UTC host (e.g. JST) every row falls outside the UTC query window and reads return empty.
    /// Unspecified values are therefore treated as the UTC they were written as.
    /// </summary>
    internal static DateTime? NormalizeUtc(object? timeVal) => timeVal switch
    {
        DateTimeOffset dto => dto.UtcDateTime,
        DateTime { Kind: DateTimeKind.Unspecified } dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        DateTime dt => dt.ToUniversalTime(),
        _ => null,
    };

    private static async Task ReadObjectAsync(
        Stream stream, DateTime start, DateTime end,
        Func<string?, bool> want, Action<string, ValidTelemetryData> emit, CancellationToken ct)
    {
        using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using var rgReader = reader.OpenRowGroupReader(rg);
            var columns = new Dictionary<string, Array>();
            foreach (var field in reader.Schema.GetDataFields())
            {
                var col = await rgReader.ReadColumnAsync(field).ConfigureAwait(false);
                columns[field.Name] = col.Data;
            }

            int rowCount = columns.Values.First().Length;
            for (int i = 0; i < rowCount; i++)
            {
                var pid = columns.TryGetValue("point_id", out var pCol) ? pCol.GetValue(i)?.ToString() : null;
                if (!want(pid)) continue;

                var timeVal = columns.TryGetValue("time", out var tCol) ? tCol.GetValue(i) : null;
                if (NormalizeUtc(timeVal) is not { } rowTime) continue;

                if (rowTime < start || rowTime > end) continue;

                emit(pid!, new ValidTelemetryData
                {
                    Datetime = rowTime.ToString("O"),
                    PointId  = pid,
                    Building = columns.TryGetValue("building", out var bCol) ? bCol.GetValue(i)?.ToString() : null,
                    DeviceId = columns.TryGetValue("device_id", out var dCol) ? dCol.GetValue(i)?.ToString() : null,
                    Name     = columns.TryGetValue("name", out var nCol) ? nCol.GetValue(i)?.ToString() : null,
                    Value    = columns.TryGetValue("value", out var vCol) ? vCol.GetValue(i) is double d ? d : null : null,
                    Data     = columns.TryGetValue("data", out var dataCol) ? dataCol.GetValue(i)?.ToString() : null,
                    Id       = columns.TryGetValue("id", out var idCol) ? idCol.GetValue(i)?.ToString() : null,
                });
            }
        }
    }
}
