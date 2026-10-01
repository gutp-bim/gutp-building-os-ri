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

    // Learned point_id → building map (#273). A point lives in exactly one building, so once a read
    // resolves a point's building we prune subsequent scans to that one building instead of every
    // building in the lake. That only holds while the point's partition key does not change — see the
    // "keys changed at" rule below (#527).
    private const string PointBuildingCachePrefix = "lake:ptbldg:";
    private static readonly TimeSpan PointBuildingCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly object LearnGate = new();

    /// <summary>The partition a row with no building lands in (TelemetryBatchAccumulator).</summary>
    private const string UnknownBuilding = "unknown";

    // #527: "partition keys changed at". A point's partition key changes when the twin's topology does
    // (or when #527 moved it from the sbco:building literal to the topology's building). Before that
    // moment its rows may sit under another building, so a read whose range starts before it — plus a
    // grace for the ingest-side metadata cache and the writer's flush to catch up — is never pruned,
    // and is not learned from. Kept as an object in the lake bucket so every API replica and every
    // restart sees it; it expires with the lake's retention, together with the data it protects.
    public const string KeysChangedAtKey = "_meta/partition-keys-changed-at";
    public static readonly TimeSpan PruneGrace = TimeSpan.FromHours(1);
    private const string KeysChangedCacheKey = "lake:keys-changed-at";
    private static readonly TimeSpan KeysChangedCacheTtl = TimeSpan.FromMinutes(1);

    private readonly IBlobStorage _storage;
    private readonly IMemoryCache _cache;

    public ParquetLakeScan(IBlobStorage storage, IMemoryCache cache)
    {
        _storage = storage;
        _cache = cache;
    }

    private static string PointBuildingKey(string pointId)
        => $"{PointBuildingCachePrefix}{LakePointBuildingCache.Generation}:{pointId}";

    /// <summary>The learned building for a point, or null if not yet resolved.</summary>
    public string? GetCachedBuilding(string pointId)
        => _cache.TryGetValue(PointBuildingKey(pointId), out string? b) ? b : null;

    /// <summary>Records the building a point's data was found in, to prune later scans.</summary>
    public void CacheBuilding(string? pointId, string? building)
    {
        if (!string.IsNullOrEmpty(pointId) && !string.IsNullOrEmpty(building))
            _cache.Set(PointBuildingKey(pointId), building, PointBuildingCacheTtl);
    }

    /// <summary>
    /// Learns a point's building from the rows of a read that is allowed to learn (see
    /// <see cref="CanPruneAsync"/>). Rows under more than one building (a row with no building counts
    /// as the <c>unknown</c> partition it is stored in), or a building other than the one learned
    /// before, mean the point's key changed after the recorded time: nothing is learned, the entry is
    /// dropped, and the buildings involved are returned so the caller can warn and record the change.
    /// </summary>
    public string[]? LearnBuilding(string? pointId, IReadOnlyList<ValidTelemetryData> rows)
    {
        if (string.IsNullOrEmpty(pointId) || rows.Count == 0) return null;
        var buildings = rows
            .Select(r => string.IsNullOrEmpty(r.Building) ? UnknownBuilding : r.Building!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var key = PointBuildingKey(pointId);
        lock (LearnGate)
        {
            var current = GetCachedBuilding(pointId);
            if (buildings.Length > 1 || (current is not null && current != buildings[0]))
            {
                _cache.Remove(key);
                return [.. buildings.Append(current ?? buildings[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            }
            _cache.Set(key, buildings[0], PointBuildingCacheTtl);
            return null;
        }
    }

    /// <summary>When partition keys last changed (UTC), or null if never recorded. Cached briefly.</summary>
    public async Task<DateTime?> GetKeysChangedAtAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(KeysChangedCacheKey, out DateTime cached))
            return cached == DateTime.MinValue ? null : cached;
        var at = await ReadKeysChangedAtAsync(ct).ConfigureAwait(false);
        _cache.Set(KeysChangedCacheKey, at ?? DateTime.MinValue, KeysChangedCacheTtl);
        return at;
    }

    /// <summary>
    /// Whether a read whose range starts at <paramref name="rangeStart"/> may be pruned to a learned
    /// building and may learn from its rows: only when it starts after the recorded key change plus
    /// <see cref="PruneGrace"/>.
    /// </summary>
    public async Task<bool> CanPruneAsync(DateTime rangeStart, CancellationToken ct)
    {
        var changedAt = await GetKeysChangedAtAsync(ct).ConfigureAwait(false);
        var start = rangeStart.Kind == DateTimeKind.Utc ? rangeStart : rangeStart.ToUniversalTime();
        return changedAt is null || start >= changedAt.Value + PruneGrace;
    }

    /// <summary>
    /// Records that partition keys changed at <paramref name="at"/> (never moves the time backwards)
    /// and forgets this process's learned buildings. Other processes pick the time up within a minute.
    /// </summary>
    public async Task MarkKeysChangedAsync(DateTime at, CancellationToken ct)
    {
        var utc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
        var existing = await ReadKeysChangedAtAsync(ct).ConfigureAwait(false);
        if (existing is null || utc > existing.Value)
        {
            using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(utc.ToString("O")));
            await _storage.PutAsync(Bucket, KeysChangedAtKey, body, "text/plain", ct).ConfigureAwait(false);
            existing = utc;
        }
        _cache.Set(KeysChangedCacheKey, existing.Value, KeysChangedCacheTtl);
        LakePointBuildingCache.Reset();
    }

    private async Task<DateTime?> ReadKeysChangedAtAsync(CancellationToken ct)
    {
        var stream = await _storage.GetAsync(Bucket, KeysChangedAtKey, ct).ConfigureAwait(false);
        if (stream is null) return null;
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            var text = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                ? (at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime())
                : null;
        }
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

    /// <summary>
    /// Reads every row from the given objects (all points, all times) — for compaction — and reports
    /// the keys that were already gone when the read reached them (#447). Every other read path treats
    /// a vanished object as "no rows", which is right for a query; for a merge it is not. The compact
    /// key is deterministic, so writing the surviving subset to it would overwrite the object whoever
    /// deleted those sources has just written from the full set. The caller must therefore treat a
    /// non-empty <c>Missing</c> as "this plan is stale, abandon the target", not as a smaller merge.
    /// </summary>
    public async Task<(List<ValidTelemetryData> Rows, IReadOnlyList<string> Missing)> ReadAllRowsAsync(
        IEnumerable<string> keys, CancellationToken ct)
    {
        var rows = new List<ValidTelemetryData>();
        var missing = new List<string>();
        await ForEachObjectAsync(keys, DateTime.MinValue, DateTime.MaxValue,
            _ => true, (_, row) => rows.Add(row), ct, missing.Add).ConfigureAwait(false);
        return (rows, missing);
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
    /// <paramref name="onMissing"/> reports a key that no longer exists; read paths leave it null and
    /// keep skipping such a key silently (an object listed a moment ago and expired since is simply no
    /// rows for a query), while compaction uses it to detect a stale plan (#447).
    /// </summary>
    private async Task ForEachObjectAsync(
        IEnumerable<string> keys, DateTime start, DateTime end,
        Func<string?, bool> want, Action<string, ValidTelemetryData> emit, CancellationToken ct,
        Action<string>? onMissing = null)
    {
        foreach (var key in keys)
        {
            var stream = await _storage.GetAsync(Bucket, key, ct).ConfigureAwait(false);
            if (stream is null)
            {
                onMissing?.Invoke(key);
                continue;
            }
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
                    // Discriminated value columns (#152) — absent in old part-*.parquet (→ null), so a
                    // legacy row keeps only Value and reads back as numeric.
                    ValueType = columns.TryGetValue("value_type", out var vtCol) ? vtCol.GetValue(i)?.ToString() : null,
                    ValueText = columns.TryGetValue("value_text", out var vxCol) ? vxCol.GetValue(i)?.ToString() : null,
                    ValueBool = columns.TryGetValue("value_bool", out var vbCol) && vbCol.GetValue(i) is bool vb ? vb : null,
                });
            }
        }
    }
}
