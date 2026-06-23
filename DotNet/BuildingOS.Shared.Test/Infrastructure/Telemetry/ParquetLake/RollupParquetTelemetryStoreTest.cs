using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

/// <summary>Tests for RollupParquetTelemetryStore (#222): pre-aggregated rollup reads with aggregate-on-read fallback.</summary>
public class RollupParquetTelemetryStoreTest
{
    private static readonly DateTime Hour1 = new(2026, 6, 13, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Hour2 = new(2026, 6, 13, 11, 0, 0, DateTimeKind.Utc);

    private static ValidTelemetryData RawRow(string pid, double v, DateTime t) => new()
    {
        Id = Guid.NewGuid().ToString(), PointId = pid, Building = "B", DeviceId = "D", Name = "temp",
        Value = v, Datetime = t.ToString("O"),
    };

    private static async Task PutRollupAsync(FakeStorage storage, string building, DateTime hour, params RollupRow[] rows)
    {
        var key = RollupPartitionKey.AggKey(building, hour);
        using var ms = new MemoryStream();
        await RollupSerializer.WriteAsync(rows, ms);
        storage.Set("cold", key, ms.ToArray());
    }

    private static RollupParquetTelemetryStore NewStore(FakeStorage storage, IWarmTelemetryStore rawStore)
    {
        var lake = new ParquetLakeTelemetryStore(
            storage, new MemoryCache(new MemoryCacheOptions()),
            new ParquetLakeTelemetryStoreOptions(), NullLogger<ParquetLakeTelemetryStore>.Instance);
        var fallback = new AggregatingParquetTelemetryStore(rawStore);
        return new RollupParquetTelemetryStore(
            storage, new MemoryCache(new MemoryCacheOptions()), fallback,
            NullLogger<RollupParquetTelemetryStore>.Instance);
    }

    // ── QueryHourlyAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task QueryHourly_WhenRollupPresent_ReturnsRollupData()
    {
        var storage = new FakeStorage();
        var rollup = new RollupRow("p1", "B", "D", "temp", 15.0, 10.0, 20.0, 4, Hour1);
        await PutRollupAsync(storage, "B", Hour1, rollup);

        var store = NewStore(storage, new EmptyRawStore());
        var result = await store.QueryHourlyAsync("p1", Hour1, Hour1.AddHours(1));

        Assert.Single(result);
        Assert.Equal(15.0, result[0].Value!.Value);        // avg
        Assert.Equal(Hour1.ToString("O"), result[0].Datetime);
    }

    [Fact]
    public async Task QueryHourly_WhenRollupMissing_FallsBackToAggregateOnRead()
    {
        var storage = new FakeStorage();
        // No rollup objects; raw store has data
        var raw = new FixedRawStore(new[]
        {
            RawRow("p1", 10.0, Hour1.AddMinutes(10)),
            RawRow("p1", 20.0, Hour1.AddMinutes(30)),
        });

        var store = NewStore(storage, raw);
        var result = await store.QueryHourlyAsync("p1", Hour1, Hour1.AddHours(1));

        Assert.Single(result);
        Assert.Equal(15.0, result[0].Value!.Value, precision: 10); // aggregate-on-read avg
    }

    [Fact]
    public async Task QueryHourly_TwoHours_BothFromRollup()
    {
        var storage = new FakeStorage();
        await PutRollupAsync(storage, "B", Hour1, new RollupRow("p1", "B", "D", "temp", 5.0, 1.0, 10.0, 3, Hour1));
        await PutRollupAsync(storage, "B", Hour2, new RollupRow("p1", "B", "D", "temp", 50.0, 40.0, 60.0, 2, Hour2));

        var store = NewStore(storage, new EmptyRawStore());
        var result = await store.QueryHourlyAsync("p1", Hour1, Hour2.AddHours(1));

        Assert.Equal(2, result.Length);
        Assert.Equal(5.0, result[0].Value!.Value);
        Assert.Equal(50.0, result[1].Value!.Value);
    }

    [Fact]
    public async Task QueryHourly_PointIdNotInRollup_FallsBackForThatHour()
    {
        // Rollup exists for "p1" only; "p2" must fall back to aggregate-on-read
        var storage = new FakeStorage();
        await PutRollupAsync(storage, "B", Hour1, new RollupRow("p1", "B", "D", "temp", 10.0, null, null, 1, Hour1));
        var raw = new FixedRawStore(new[] { RawRow("p2", 99.0, Hour1.AddMinutes(5)) });

        var store = NewStore(storage, raw);
        var result = await store.QueryHourlyAsync("p2", Hour1, Hour1.AddHours(1));

        Assert.Single(result);
        Assert.Equal(99.0, result[0].Value!.Value, precision: 10);
    }

    // ── QueryDailyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task QueryDaily_ReAggregatesHourlyRollups()
    {
        var storage = new FakeStorage();
        var day = new DateTime(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc);
        // Two hours with rollup: avg 10 (4 rows) + avg 20 (2 rows) → daily avg = (40+40)/6 ≈ 13.33
        await PutRollupAsync(storage, "B", day, new RollupRow("p1", "B", "D", "t", 10.0, 5.0, 15.0, 4, day));
        await PutRollupAsync(storage, "B", day.AddHours(1), new RollupRow("p1", "B", "D", "t", 20.0, 15.0, 25.0, 2, day.AddHours(1)));

        var store = NewStore(storage, new EmptyRawStore());
        var result = await store.QueryDailyAsync("p1", day, day.AddDays(1));

        Assert.Single(result);
        // avg = (10*4 + 20*2) / 6 = 80/6 ≈ 13.33
        Assert.Equal(80.0 / 6.0, result[0].Value!.Value, precision: 5);
    }

    [Fact]
    public async Task QueryHourly_EmptyStorage_ReturnsEmpty()
    {
        var storage = new FakeStorage();
        var store = NewStore(storage, new EmptyRawStore());
        var result = await store.QueryHourlyAsync("p1", Hour1, Hour1.AddHours(1));
        Assert.Empty(result);
    }

    // ── Gap coalescing (#242 follow-up: aggregate p95 optimization) ──────────

    [Fact]
    public async Task QueryHourly_AllRollupsMissing_CoalescesIntoSingleAggregateOnRead()
    {
        // 3 contiguous hours, no rollups. The optimization must do ONE aggregate-on-read over the
        // whole gap (not one scan per hour).
        var storage = new FakeStorage();
        var raw = new CountingRawStore(new[]
        {
            RawRow("p1", 10.0, Hour1.AddMinutes(5)),
            RawRow("p1", 20.0, Hour2.AddMinutes(5)),
            RawRow("p1", 30.0, Hour2.AddHours(1).AddMinutes(5)),
        });

        var store = NewStore(storage, raw);
        var result = await store.QueryHourlyAsync("p1", Hour1, Hour2.AddHours(2)); // 3-hour window

        Assert.Equal(1, raw.QueryCount);               // coalesced: a single scan, not 3
        Assert.Equal(3, result.Length);                // still one bucket per hour
        Assert.Equal(Hour1.ToString("O"), result[0].Datetime); // ascending
    }

    [Fact]
    public async Task QueryHourly_RollupInMiddle_SplitsGapsAroundIt_AndStaysOrdered()
    {
        // Hour1 missing, Hour2 has rollup, Hour3 missing → two gap runs (one fallback each), the
        // rollup hit between them; result ascending by time.
        var storage = new FakeStorage();
        var hour3 = Hour2.AddHours(1);
        await PutRollupAsync(storage, "B", Hour2, new RollupRow("p1", "B", "D", "temp", 99.0, 90.0, 100.0, 5, Hour2));
        var raw = new CountingRawStore(new[]
        {
            RawRow("p1", 10.0, Hour1.AddMinutes(5)),
            RawRow("p1", 30.0, hour3.AddMinutes(5)),
        });

        var store = NewStore(storage, raw);
        var result = await store.QueryHourlyAsync("p1", Hour1, hour3.AddHours(1));

        Assert.Equal(2, raw.QueryCount);               // two separate gaps → two fallback scans
        Assert.Equal(3, result.Length);
        Assert.Equal(Hour1.ToString("O"), result[0].Datetime);
        Assert.Equal(99.0, result[1].Value!.Value);    // the rollup hour, in order
        Assert.Equal(hour3.ToString("O"), result[2].Datetime);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class FakeStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _objs = new();
        public void Set(string c, string k, byte[] b) => _objs[$"{c}/{k}"] = b;

        public Task<Stream?> GetAsync(string c, string k, CancellationToken ct = default)
            => Task.FromResult(_objs.TryGetValue($"{c}/{k}", out var b) ? (Stream?)new MemoryStream(b) : null);

        public Task<IReadOnlyList<string>> ListAsync(string c, string prefix = "", CancellationToken ct = default)
        {
            var cp = c + "/";
            return Task.FromResult<IReadOnlyList<string>>(
                _objs.Keys.Where(k => k.StartsWith(cp, StringComparison.Ordinal))
                    .Select(k => k[cp.Length..])
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                    .ToList());
        }

        public Task PutAsync(string c, string k, Stream content, string ct2 = "application/octet-stream", CancellationToken ct = default)
        {
            using var ms = new MemoryStream(); content.CopyTo(ms); _objs[$"{c}/{k}"] = ms.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string c, string k, CancellationToken ct = default)
            => Task.FromResult(_objs.ContainsKey($"{c}/{k}"));
        public Task DeleteAsync(string c, string k, CancellationToken ct = default)
        { _objs.Remove($"{c}/{k}"); return Task.CompletedTask; }
    }

    private sealed class EmptyRawStore : IWarmTelemetryStore
    {
        public Task<ValidTelemetryData[]> QueryAsync(string pid, DateTime start, DateTime end, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<ValidTelemetryData>());
        public Task<ValidTelemetryData?> QueryLatestAsync(string pid, CancellationToken ct = default)
            => Task.FromResult<ValidTelemetryData?>(null);
    }

    private sealed class FixedRawStore : IWarmTelemetryStore
    {
        private readonly ValidTelemetryData[] _rows;
        public FixedRawStore(ValidTelemetryData[] rows) => _rows = rows;
        public Task<ValidTelemetryData[]> QueryAsync(string pid, DateTime start, DateTime end, CancellationToken ct = default)
            => Task.FromResult(_rows.Where(r => r.PointId == pid).ToArray());
        public Task<ValidTelemetryData?> QueryLatestAsync(string pid, CancellationToken ct = default)
            => Task.FromResult<ValidTelemetryData?>(null);
    }

    /// <summary>Raw store that counts QueryAsync invocations and returns only rows inside [start, end)
    /// — so the coalesced aggregate-on-read can be asserted (one call per contiguous gap).</summary>
    private sealed class CountingRawStore : IWarmTelemetryStore
    {
        private readonly ValidTelemetryData[] _rows;
        public int QueryCount { get; private set; }
        public CountingRawStore(ValidTelemetryData[] rows) => _rows = rows;
        public Task<ValidTelemetryData[]> QueryAsync(string pid, DateTime start, DateTime end, CancellationToken ct = default)
        {
            QueryCount++;
            return Task.FromResult(_rows.Where(r =>
                r.PointId == pid &&
                DateTime.TryParse(r.Datetime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                && t >= start && t < end).ToArray());
        }
        public Task<ValidTelemetryData?> QueryLatestAsync(string pid, CancellationToken ct = default)
            => Task.FromResult<ValidTelemetryData?>(null);
    }
}
