using System.Text.Json;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class AggregateAndMultiPointTest
{
    private static ValidTelemetryData Row(string pointId, DateTime tUtc, double value) => new()
    {
        PointId = pointId, Building = "b1", DeviceId = "d1", Name = "temp",
        Datetime = tUtc.ToString("O"), Value = value,
    };

    private static async Task PutAsync(CountingBlobStorage s, string key, params ValidTelemetryData[] rows)
    {
        using var ms = new MemoryStream();
        await ParquetTelemetrySerializer.WriteAsync(rows, ms, default);
        s.Set("cold", key, ms.ToArray());
    }

    [Fact]
    public async Task AggregatingStore_Hourly_ValueIsAvg_WithMinMaxCountInData()
    {
        var h12 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var raw = new StubWarm(Row("p1", h12.AddMinutes(5), 10), Row("p1", h12.AddMinutes(55), 20));
        var store = new AggregatingParquetTelemetryStore(raw);

        var result = await store.QueryHourlyAsync("p1", h12, h12.AddHours(1));

        var row = Assert.Single(result);
        Assert.Equal(15, row.Value); // avg matches the Timescale continuous-aggregate contract
        Assert.Equal(h12.ToString("O"), row.Datetime);
        using var data = JsonDocument.Parse(row.Data!);
        Assert.Equal(10, data.RootElement.GetProperty("min").GetDouble());
        Assert.Equal(20, data.RootElement.GetProperty("max").GetDouble());
        Assert.Equal(2, data.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task QueryMultiAsync_ReadsEachObjectOnce_ForAllPoints()
    {
        var s = new CountingBlobStorage();
        var h12 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var key = LakePartitionKey.For("b1", h12, 1, 4);
        // one object holds rows for two points
        await PutAsync(s, key,
            Row("p1", h12.AddMinutes(5), 1),
            Row("p2", h12.AddMinutes(6), 2),
            Row("p1", h12.AddMinutes(7), 3));

        var store = new ParquetLakeTelemetryStore(
            s, new MemoryCache(new MemoryCacheOptions()),
            new ParquetLakeTelemetryStoreOptions(), NullLogger<ParquetLakeTelemetryStore>.Instance);

        var result = await store.QueryMultiAsync(new[] { "p1", "p2" }, h12, h12.AddHours(1));

        Assert.Equal(2, result["p1"].Length);
        Assert.Single(result["p2"]);
        Assert.Equal(1, s.GetCount(key)); // the object was read exactly once, not once per point
    }

    [Fact]
    public async Task QueryMultiAsync_RespectsQueryMaxFiles_KeepsNewestPartition()
    {
        var s = new CountingBlobStorage();
        var hOld = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);
        var hNew = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        await PutAsync(s, LakePartitionKey.For("b1", hOld, 1, 2), Row("p1", hOld.AddMinutes(5), 1));
        await PutAsync(s, LakePartitionKey.For("b1", hNew, 3, 4), Row("p1", hNew.AddMinutes(5), 100));

        var store = new ParquetLakeTelemetryStore(
            s, new MemoryCache(new MemoryCacheOptions()),
            new ParquetLakeTelemetryStoreOptions { QueryMaxFiles = 1 }, NullLogger<ParquetLakeTelemetryStore>.Instance);

        var result = await store.QueryMultiAsync(new[] { "p1" }, hOld, hNew.AddHours(1));

        // Cap=1 → only the newest partition read; multi-point honours the same safeguard as single-point.
        var row = Assert.Single(result["p1"]);
        Assert.Equal(100, row.Value);
        Assert.Equal(0, s.GetCount(LakePartitionKey.For("b1", hOld, 1, 2))); // older object never fetched
    }

    [Fact]
    public async Task QueryMultiAsync_MissingPoint_ReturnsEmptyArray()
    {
        var s = new CountingBlobStorage();
        var h12 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        await PutAsync(s, LakePartitionKey.For("b1", h12, 1, 2), Row("p1", h12.AddMinutes(5), 1));

        var store = new ParquetLakeTelemetryStore(
            s, new MemoryCache(new MemoryCacheOptions()),
            new ParquetLakeTelemetryStoreOptions(), NullLogger<ParquetLakeTelemetryStore>.Instance);

        var result = await store.QueryMultiAsync(new[] { "p1", "absent" }, h12, h12.AddHours(1));

        Assert.Single(result["p1"]);
        Assert.Empty(result["absent"]); // present key, empty array
    }

    private sealed class StubWarm : IWarmTelemetryStore
    {
        private readonly ValidTelemetryData[] _rows;
        public StubWarm(params ValidTelemetryData[] rows) => _rows = rows;
        public Task<ValidTelemetryData[]> QueryAsync(string p, DateTime s, DateTime e, CancellationToken ct = default)
            => Task.FromResult(_rows);
        public Task<ValidTelemetryData?> QueryLatestAsync(string p, CancellationToken ct = default)
            => Task.FromResult<ValidTelemetryData?>(null);
    }

    private sealed class CountingBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new();
        private readonly Dictionary<string, int> _gets = new();

        public void Set(string container, string key, byte[] bytes) => _objects[$"{container}/{key}"] = bytes;
        public int GetCount(string key) => _gets.TryGetValue($"cold/{key}", out var n) ? n : 0;

        public Task<Stream?> GetAsync(string container, string key, CancellationToken ct = default)
        {
            var full = $"{container}/{key}";
            _gets[full] = (_gets.TryGetValue(full, out var n) ? n : 0) + 1;
            return Task.FromResult(_objects.TryGetValue(full, out var b) ? (Stream?)new MemoryStream(b) : null);
        }

        public Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken ct = default)
        {
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

        public Task<bool> ExistsAsync(string c, string k, CancellationToken ct = default) => Task.FromResult(_objects.ContainsKey($"{c}/{k}"));
        public Task DeleteAsync(string c, string k, CancellationToken ct = default) { _objects.Remove($"{c}/{k}"); return Task.CompletedTask; }
    }
}
