using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.ColdExport;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class LakeBackfillServiceTest
{
    private static readonly DateTime From = new(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 6, 12, 14, 0, 0, DateTimeKind.Utc);

    private static ValidTelemetryData Row(string id, string building, DateTime t, double v) => new()
    {
        Id = id, PointId = "p1", Building = building, Datetime = t.ToString("O"), Value = v,
    };

    private static LakeBackfillService NewService(IExportDataReader reader, InMemoryBlobStorage s) =>
        new(reader, s, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task RunAsync_WritesDeterministicObjectsPerBuildingHour_ReconcilesCounts()
    {
        // hour 12 has b1 (2 rows) + b2 (1 row); hour 13 has b1 (1 row).
        var reader = new FakeReader(new Dictionary<(DateTime, DateTime), ValidTelemetryData[]>
        {
            [(From, From.AddHours(1))] = new[]
            {
                Row("a", "b1", From.AddMinutes(5), 1), Row("b", "b1", From.AddMinutes(6), 2),
                Row("c", "b2", From.AddMinutes(7), 3),
            },
            [(From.AddHours(1), To)] = new[] { Row("d", "b1", From.AddHours(1).AddMinutes(5), 4) },
        });
        var s = new InMemoryBlobStorage();

        var result = await NewService(reader, s).RunAsync(From, To);

        Assert.Equal(4, result.RowsRead);
        Assert.Equal(4, result.RowsWritten);
        Assert.Equal(3, result.ObjectsWritten); // (b1,12) (b2,12) (b1,13)
        Assert.Equal(2, result.HoursProcessed);
        Assert.True(s.Has("cold", BackfillPlanner.BackfillKey("b1", From)));
        Assert.True(s.Has("cold", BackfillPlanner.BackfillKey("b2", From)));
        Assert.True(s.Has("cold", BackfillPlanner.BackfillKey("b1", From.AddHours(1))));
    }

    [Fact]
    public async Task RunAsync_IsIdempotent_NoDuplicateObjectsOnRerun()
    {
        var reader = new FakeReader(new Dictionary<(DateTime, DateTime), ValidTelemetryData[]>
        {
            [(From, From.AddHours(1))] = new[] { Row("a", "b1", From.AddMinutes(5), 1) },
            [(From.AddHours(1), To)] = Array.Empty<ValidTelemetryData>(),
        });
        var s = new InMemoryBlobStorage();

        await NewService(reader, s).RunAsync(From, To);
        var afterFirst = (await s.ListAsync("cold", "building_id=")).Count;
        await NewService(reader, s).RunAsync(From, To); // re-run
        var afterSecond = (await s.ListAsync("cold", "building_id=")).Count;

        Assert.Equal(1, afterFirst);
        Assert.Equal(afterFirst, afterSecond); // deterministic key overwrote, no new object
    }

    [Fact]
    public async Task RunAsync_BuildingFilter_OnlyWritesMatching()
    {
        var reader = new FakeReader(new Dictionary<(DateTime, DateTime), ValidTelemetryData[]>
        {
            [(From, From.AddHours(1))] = new[] { Row("a", "b1", From.AddMinutes(5), 1), Row("c", "b2", From.AddMinutes(7), 3) },
            [(From.AddHours(1), To)] = Array.Empty<ValidTelemetryData>(),
        });
        var s = new InMemoryBlobStorage();

        var result = await NewService(reader, s).RunAsync(From, To, buildingFilter: "b1");

        Assert.Equal(1, result.ObjectsWritten);
        Assert.True(s.Has("cold", BackfillPlanner.BackfillKey("b1", From)));
        Assert.False(s.Has("cold", BackfillPlanner.BackfillKey("b2", From)));
    }

    private sealed class FakeReader : IExportDataReader
    {
        private readonly Dictionary<(DateTime, DateTime), ValidTelemetryData[]> _data;
        public FakeReader(Dictionary<(DateTime, DateTime), ValidTelemetryData[]> data) => _data = data;
        public Task<ValidTelemetryData[]> ReadAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue((from, to), out var r) ? r : Array.Empty<ValidTelemetryData>());
        public Task<DateTime?> GetLastExportEndAsync(CancellationToken ct = default) => Task.FromResult<DateTime?>(null);
    }

    private sealed class InMemoryBlobStorage : IBlobStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new();
        public bool Has(string c, string k) => _objects.ContainsKey($"{c}/{k}");

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
            using var ms = new MemoryStream(); content.CopyTo(ms); _objects[$"{c}/{k}"] = ms.ToArray(); return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(string c, string k, CancellationToken ct = default) => Task.FromResult(Has(c, k));
        public Task DeleteAsync(string c, string k, CancellationToken ct = default) { _objects.Remove($"{c}/{k}"); return Task.CompletedTask; }
    }
}
