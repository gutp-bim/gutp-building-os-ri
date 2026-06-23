using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class ParquetLakeTelemetryStoreTest
{
    private static ParquetLakeTelemetryStore NewStore(InMemoryBlobStorage storage, int lookbackHours = 24, int maxFiles = 0) =>
        new(storage, new MemoryCache(new MemoryCacheOptions()),
            new ParquetLakeTelemetryStoreOptions { LatestLookbackHours = lookbackHours, QueryMaxFiles = maxFiles },
            NullLogger<ParquetLakeTelemetryStore>.Instance);

    private static ValidTelemetryData Row(string? id, string pointId, DateTime tUtc, double value) => new()
    {
        Id = id, PointId = pointId, Building = "b1", Datetime = tUtc.ToString("O"), Value = value,
    };

    private static async Task PutAsync(InMemoryBlobStorage s, string key, params ValidTelemetryData[] rows)
    {
        using var ms = new MemoryStream();
        await ParquetTelemetrySerializer.WriteAsync(rows, ms, default);
        s.Set("cold", key, ms.ToArray());
    }

    [Fact]
    public async Task QueryAsync_ReturnsRowsForPointAcrossHours_InRange()
    {
        var s = new InMemoryBlobStorage();
        var h12 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var h13 = new DateTime(2026, 6, 12, 13, 0, 0, DateTimeKind.Utc);
        await PutAsync(s, LakePartitionKey.For("b1", h12, 1, 2),
            Row("a", "p1", h12.AddMinutes(5), 1), Row("b", "p2", h12.AddMinutes(6), 99));
        await PutAsync(s, LakePartitionKey.For("b1", h13, 3, 4),
            Row("c", "p1", h13.AddMinutes(5), 2));

        var rows = await NewStore(s).QueryAsync("p1", h12, h13.AddHours(1));

        Assert.Equal(new double?[] { 1, 2 }, rows.Select(r => r.Value)); // p2 filtered out, time-sorted
    }

    [Fact]
    public async Task QueryAsync_PrefersCompactOverParts_InSameHour()
    {
        var s = new InMemoryBlobStorage();
        var h12 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        // raw parts (pre-compaction values)
        await PutAsync(s, LakePartitionKey.For("b1", h12, 1, 2), Row("a", "p1", h12.AddMinutes(5), 1));
        await PutAsync(s, LakePartitionKey.For("b1", h12, 3, 4), Row("b", "p1", h12.AddMinutes(6), 2));
        // compacted object supersedes the parts (one authoritative value)
        await PutAsync(s, LakePartitionKey.HourPrefix("b1", h12) + "compact-2026061212.parquet",
            Row("a", "p1", h12.AddMinutes(5), 42));

        var rows = await NewStore(s).QueryAsync("p1", h12, h12.AddHours(1));

        Assert.Single(rows);
        Assert.Equal(42, rows[0].Value); // only the compact object was read
    }

    [Fact]
    public async Task QueryAsync_DedupesById_AcrossOverlappingParts()
    {
        var s = new InMemoryBlobStorage();
        var h12 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        // same id "a" redelivered into two differently-named part files (overlap)
        await PutAsync(s, LakePartitionKey.For("b1", h12, 1, 5), Row("a", "p1", h12.AddMinutes(5), 1));
        await PutAsync(s, LakePartitionKey.For("b1", h12, 3, 7), Row("a", "p1", h12.AddMinutes(5), 1));

        var rows = await NewStore(s).QueryAsync("p1", h12, h12.AddHours(1));

        Assert.Single(rows); // de-duplicated by id
    }

    [Fact]
    public async Task QueryAsync_OverMaxFiles_KeepsNewestPartitions_AcrossBuildings()
    {
        // The early-sorting building "a1" holds the NEWER hour; the late-sorting "z9" holds the OLDER
        // hour. With max=1 the cap must keep a1's newer partition (by partition time, not object key).
        var s = new InMemoryBlobStorage();
        var hOld = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);
        var hNew = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        await PutAsync(s, LakePartitionKey.For("z9", hOld, 1, 2),
            new ValidTelemetryData { Id = "old", PointId = "p1", Building = "z9",
                Datetime = hOld.AddMinutes(5).ToString("O"), Value = 1 });
        await PutAsync(s, LakePartitionKey.For("a1", hNew, 3, 4),
            new ValidTelemetryData { Id = "new", PointId = "p1", Building = "a1",
                Datetime = hNew.AddMinutes(5).ToString("O"), Value = 100 });

        var rows = await NewStore(s, maxFiles: 1).QueryAsync("p1", hOld, hNew.AddHours(1));

        Assert.Single(rows);
        Assert.Equal(100, rows[0].Value); // newest partition kept, regardless of building-id sort order
    }

    [Fact]
    public async Task QueryLatestAsync_ReturnsNewestWithinLookback()
    {
        var s = new InMemoryBlobStorage();
        var now = DateTime.UtcNow;
        var thisHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var prevHour = thisHour.AddHours(-1);
        await PutAsync(s, LakePartitionKey.For("b1", prevHour, 1, 2), Row("old", "p1", prevHour.AddMinutes(10), 5));
        await PutAsync(s, LakePartitionKey.For("b1", thisHour, 3, 4), Row("new", "p1", thisHour.AddMinutes(1), 8));

        var latest = await NewStore(s).QueryLatestAsync("p1");

        Assert.NotNull(latest);
        Assert.Equal(8, latest!.Value); // newest hour wins
    }

    [Fact]
    public async Task QueryAsync_LearnsBuilding_ThenPrunesSubsequentScansToThatBuilding()
    {
        // Two buildings: p1 lives in b1, p2 in b2. After the first query learns p1→b1 (#273), the
        // next query for p1 must list only b1's partitions, never b2's.
        var s = new InMemoryBlobStorage();
        var h = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        await PutAsync(s, LakePartitionKey.For("b1", h, 1, 2),
            new ValidTelemetryData { Id = "a", PointId = "p1", Building = "b1", Datetime = h.AddMinutes(5).ToString("O"), Value = 1 });
        await PutAsync(s, LakePartitionKey.For("b2", h, 3, 4),
            new ValidTelemetryData { Id = "b", PointId = "p2", Building = "b2", Datetime = h.AddMinutes(5).ToString("O"), Value = 2 });

        var store = NewStore(s);                              // one store → shared learned cache
        var first = await store.QueryAsync("p1", h, h.AddHours(1)); // scans all, learns b1
        Assert.Single(first);

        s.ClearListLog();
        var second = await store.QueryAsync("p1", h, h.AddHours(1)); // pruned to b1
        Assert.Single(second);
        Assert.Equal(1, second[0].Value);
        Assert.NotEmpty(s.ListPrefixes);
        Assert.All(s.ListPrefixes, p => Assert.DoesNotContain("building_id=b2", p));
        Assert.Contains(s.ListPrefixes, p => p.Contains("building_id=b1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryLatestAsync_OutsideLookback_ReturnsNull()
    {
        var s = new InMemoryBlobStorage();
        var old = DateTime.UtcNow.AddHours(-48);
        var oldHour = new DateTime(old.Year, old.Month, old.Day, old.Hour, 0, 0, DateTimeKind.Utc);
        await PutAsync(s, LakePartitionKey.For("b1", oldHour, 1, 2), Row("x", "p1", oldHour.AddMinutes(5), 5));

        var latest = await NewStore(s, lookbackHours: 6).QueryLatestAsync("p1");

        Assert.Null(latest); // 48h old, lookback only 6h
    }

    /// <summary>Minimal in-memory IBlobStorage with prefix listing (recursive, no delimiter — like MinIO).</summary>
    private sealed class InMemoryBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new();

        /// <summary>Records the prefix of every ListAsync call (for asserting building pruning, #273).</summary>
        public List<string> ListPrefixes { get; } = new();
        public void ClearListLog() => ListPrefixes.Clear();

        public void Set(string container, string key, byte[] bytes) => _objects[$"{container}/{key}"] = bytes;

        public Task<Stream?> GetAsync(string container, string key, CancellationToken ct = default)
            => Task.FromResult(_objects.TryGetValue($"{container}/{key}", out var b)
                ? (Stream?)new MemoryStream(b)
                : null);

        public Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken ct = default)
        {
            ListPrefixes.Add(prefix);
            var cp = container + "/";
            var keys = _objects.Keys
                .Where(k => k.StartsWith(cp, StringComparison.Ordinal))
                .Select(k => k[cp.Length..])
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }

        public Task PutAsync(string c, string k, Stream content, string ct2 = "application/octet-stream", CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _objects[$"{c}/{k}"] = ms.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string c, string k, CancellationToken ct = default)
            => Task.FromResult(_objects.ContainsKey($"{c}/{k}"));

        public Task DeleteAsync(string c, string k, CancellationToken ct = default)
        {
            _objects.Remove($"{c}/{k}");
            return Task.CompletedTask;
        }
    }
}
