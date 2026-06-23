using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Caching.Memory;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry;

public class MinioParquetColdTelemetryStoreTest
{
    private static string Key(string b, DateTime t) =>
        $"building_id={b}/year={t.Year:D4}/month={t.Month:D2}/day={t.Day:D2}/hour={t.Hour:D2}/part-x.parquet";

    [Fact]
    public async Task QueryAsync_ReadsOnlyInRangeObjects_AndFiltersRows()
    {
        var inRangeHour = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var outOfRangeHour = new DateTime(2026, 6, 12, 3, 0, 0, DateTimeKind.Utc); // far outside grace

        var storage = new FakeBlobStorage();
        storage.Objects[Key("bldg-1", inRangeHour)] =
            await WriteParquet(("p1", inRangeHour.AddMinutes(10), 1.5), ("p2", inRangeHour.AddMinutes(20), 9.9));
        storage.Objects[Key("bldg-1", outOfRangeHour)] =
            await WriteParquet(("p1", outOfRangeHour.AddMinutes(5), 7.0));

        var store = new MinioParquetColdTelemetryStore(storage, new MemoryCache(new MemoryCacheOptions()));

        var rows = await store.QueryAsync("p1",
            new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 12, 13, 0, 0, DateTimeKind.Utc));

        // only p1 in the in-range hour, within [start,end]
        Assert.Single(rows);
        Assert.Equal(1.5, rows[0].Value);
        // the out-of-range object must never be fetched
        Assert.DoesNotContain(storage.Fetched, k => k == Key("bldg-1", outOfRangeHour));
        Assert.Contains(storage.Fetched, k => k == Key("bldg-1", inRangeHour));
    }

    [Fact]
    public async Task QueryAsync_NoBuildings_ReturnsEmpty()
    {
        var store = new MinioParquetColdTelemetryStore(new FakeBlobStorage(), new MemoryCache(new MemoryCacheOptions()));
        var rows = await store.QueryAsync("p1", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        Assert.Empty(rows);
    }

    private static async Task<byte[]> WriteParquet(params (string Point, DateTime Time, double Value)[] data)
    {
        var fPointId = new DataField<string>("point_id");
        var fBuilding = new DataField<string>("building");
        var fDeviceId = new DataField<string>("device_id");
        var fName = new DataField<string>("name");
        var fValue = new DataField<double?>("value");
        var fTime = new DataField<DateTime?>("time");
        var fData = new DataField<string>("data");
        var fId = new DataField<string>("id");
        var schema = new ParquetSchema(fPointId, fBuilding, fDeviceId, fName, fValue, fTime, fData, fId);

        using var ms = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, ms))
        using (var rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(fPointId, data.Select(d => d.Point).ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fBuilding, data.Select(_ => "bldg-1").ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fDeviceId, data.Select(_ => "dev").ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fName, data.Select(_ => "n").ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fValue, data.Select(d => (double?)d.Value).ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fTime, data.Select(d => (DateTime?)d.Time).ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fData, data.Select(_ => (string?)null).ToArray()));
            await rg.WriteColumnAsync(new DataColumn(fId, data.Select((_, i) => $"id-{i}").ToArray()));
        }
        return ms.ToArray();
    }

    private sealed class FakeBlobStorage : IBlobStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Fetched { get; } = new();

        public Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());

        public Task<Stream?> GetAsync(string container, string key, CancellationToken ct = default)
        {
            Fetched.Add(key);
            return Task.FromResult<Stream?>(Objects.TryGetValue(key, out var b) ? new MemoryStream(b) : null);
        }

        public Task PutAsync(string c, string k, Stream s, string ct = "application/octet-stream", CancellationToken token = default)
            => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string c, string k, CancellationToken token = default)
            => Task.FromResult(Objects.ContainsKey(k));
        public Task DeleteAsync(string c, string k, CancellationToken token = default)
            => throw new NotSupportedException();
    }
}
