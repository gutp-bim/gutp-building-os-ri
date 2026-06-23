using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class CompactionWorkerTest
{
    // A settled hour well in the past (relative to now, so the test is deterministic at any clock).
    private static readonly DateTime Hour = Truncate(DateTime.UtcNow.AddDays(-10));

    private static DateTime Truncate(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    private static ValidTelemetryData Row(string id, DateTime t, double v) => new()
    {
        Id = id, PointId = "p1", Building = "b1", Datetime = t.ToString("O"), Value = v,
    };

    private static async Task PutPartAsync(InMemoryBlobStorage s, int first, int last, params ValidTelemetryData[] rows)
    {
        using var ms = new MemoryStream();
        await ParquetTelemetrySerializer.WriteAsync(rows, ms, default);
        s.Set("cold", LakePartitionKey.For("b1", Hour, (ulong)first, (ulong)last), ms.ToArray());
    }

    private static CompactionWorker NewWorker(InMemoryBlobStorage s) =>
        new(s, new MemoryCache(new MemoryCacheOptions()), NullLogger<CompactionWorker>.Instance,
            new CompactionWorkerOptions());

    private static ParquetLakeTelemetryStore NewStore(InMemoryBlobStorage s) =>
        new(s, new MemoryCache(new MemoryCacheOptions()), new ParquetLakeTelemetryStoreOptions(),
            NullLogger<ParquetLakeTelemetryStore>.Instance);

    [Fact]
    public async Task RunOnce_MergesPartsIntoOneCompact_DedupsById_PreservesQuery()
    {
        var s = new InMemoryBlobStorage();
        await PutPartAsync(s, 1, 2, Row("a", Hour.AddMinutes(5), 1), Row("b", Hour.AddMinutes(10), 2));
        await PutPartAsync(s, 3, 4, Row("b", Hour.AddMinutes(10), 2), Row("c", Hour.AddMinutes(15), 3)); // b dup

        var before = await NewStore(s).QueryAsync("p1", Hour, Hour.AddHours(1));
        Assert.Equal(3, before.Length); // a,b,c (already deduped on read)

        await NewWorker(s).RunOnceAsync(default);

        var remaining = await s.ListAsync("cold", "building_id=b1/");
        var compact = Assert.Single(remaining); // exactly one object for the hour
        Assert.EndsWith($"compact-{Hour:yyyyMMddHH}.parquet", compact);

        var after = await NewStore(s).QueryAsync("p1", Hour, Hour.AddHours(1));
        Assert.Equal(before.Select(r => r.Value), after.Select(r => r.Value)); // query invariant
    }

    [Fact]
    public async Task RunOnce_IsIdempotent_SecondRunNoOps()
    {
        var s = new InMemoryBlobStorage();
        await PutPartAsync(s, 1, 2, Row("a", Hour.AddMinutes(5), 1));
        await PutPartAsync(s, 3, 4, Row("b", Hour.AddMinutes(10), 2));

        await NewWorker(s).RunOnceAsync(default);
        var afterFirst = await s.ListAsync("cold", "building_id=b1/");
        await NewWorker(s).RunOnceAsync(default); // compact-only hour → planner skips
        var afterSecond = await s.ListAsync("cold", "building_id=b1/");

        Assert.Single(afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task RunOnce_AfterInterruptedDelete_FoldsLeftoverPartBackIn()
    {
        // Simulate a crash mid-delete: a compact plus one orphan part survive. The next run merges them.
        var s = new InMemoryBlobStorage();
        await PutPartAsync(s, 1, 2, Row("a", Hour.AddMinutes(5), 1));
        await PutPartAsync(s, 3, 4, Row("b", Hour.AddMinutes(10), 2));
        await NewWorker(s).RunOnceAsync(default); // → one compact

        await PutPartAsync(s, 5, 6, Row("c", Hour.AddMinutes(20), 3)); // a new/leftover part lands
        await NewWorker(s).RunOnceAsync(default);

        var remaining = await s.ListAsync("cold", "building_id=b1/");
        Assert.Single(remaining); // converged back to one object
        var rows = await NewStore(s).QueryAsync("p1", Hour, Hour.AddHours(1));
        Assert.Equal(new double?[] { 1, 2, 3 }, rows.Select(r => r.Value)); // a,b,c all present
    }

    private sealed class InMemoryBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new();
        public void Set(string container, string key, byte[] bytes) => _objects[$"{container}/{key}"] = bytes;

        public Task<Stream?> GetAsync(string c, string k, CancellationToken ct = default)
            => Task.FromResult(_objects.TryGetValue($"{c}/{k}", out var b) ? (Stream?)new MemoryStream(b) : null);

        public Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken ct = default)
        {
            var cp = container + "/";
            var keys = _objects.Keys.Where(k => k.StartsWith(cp, StringComparison.Ordinal))
                .Select(k => k[cp.Length..]).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            return Task.FromResult<IReadOnlyList<string>>(keys);
        }

        public Task PutAsync(string c, string k, Stream content, string ct2 = "application/octet-stream", CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _objects[$"{c}/{k}"] = ms.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string c, string k, CancellationToken ct = default) => Task.FromResult(_objects.ContainsKey($"{c}/{k}"));
        public Task DeleteAsync(string c, string k, CancellationToken ct = default) { _objects.Remove($"{c}/{k}"); return Task.CompletedTask; }
    }
}
