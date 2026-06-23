using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class ParquetLakeWriterTest
{
    [Fact]
    public void FlushPolicy_FlushesOnRowsOrInterval_NotWhenEmpty()
    {
        Assert.False(FlushPolicy.ShouldFlush(0, 100, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)));
        Assert.True(FlushPolicy.ShouldFlush(100, 100, TimeSpan.Zero, TimeSpan.FromMinutes(5)));
        Assert.True(FlushPolicy.ShouldFlush(1, 100, TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(FlushPolicy.ShouldFlush(1, 100, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task MinioParquetLakeWriter_PutsDeterministicKey_AndRoundtrips()
    {
        var storage = new CapturingBlobStorage();
        var writer = new MinioParquetLakeWriter(storage);

        var hour = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var maxEvent = new DateTime(2026, 6, 12, 12, 5, 0, DateTimeKind.Utc);
        var batch = new PartitionBatch("bldg-1", hour, maxEvent, 10, 42, new[]
        {
            new ValidTelemetryData { Id = "e1", PointId = "p1", Building = "bldg-1",
                Datetime = "2026-06-12T12:05:00Z", Value = 7.5 },
        });

        var (key, bytes) = await writer.WriteAsync(batch);

        Assert.Equal("building_id=bldg-1/year=2026/month=06/day=12/hour=12/part-10-42.parquet", key);
        Assert.True(bytes > 0);
        Assert.Equal("cold", storage.Container);
        Assert.Equal(key, storage.Key);

        // roundtrip via the cold reader's parquet path → the written bytes are valid lake objects
        using var ms = new MemoryStream(storage.Bytes!);
        var read = await ReadBack(ms);
        Assert.Single(read);
        Assert.Equal(7.5, read[0].Value);
    }

    [Fact]
    public async Task ZonelessTimestamp_PartitionHourMatchesTimeColumn()
    {
        // A datetime with no zone must be treated as UTC by BOTH the partition-hour computation and the
        // time column, regardless of the host's local zone — otherwise the reader prunes the row away.
        var accumulator = new TelemetryBatchAccumulator();
        accumulator.Add(1, new[]
        {
            new ValidTelemetryData { Id = "e1", PointId = "p1", Building = "b1",
                Datetime = "2026-06-12T12:05:00", Value = 1.0 },
        });
        var batch = Assert.Single(accumulator.Drain());
        Assert.Equal(12, batch.HourUtc.Hour); // partition hour = UTC 12

        var storage = new CapturingBlobStorage();
        var (key, _) = await new MinioParquetLakeWriter(storage).WriteAsync(batch);
        Assert.Contains("hour=12/", key);

        using var ms = new MemoryStream(storage.Bytes!);
        using var reader = await Parquet.ParquetReader.CreateAsync(ms);
        using var rg = reader.OpenRowGroupReader(0);
        var timeField = reader.Schema.GetDataFields().First(f => f.Name == "time");
        var time = (DateTime?)(await rg.ReadColumnAsync(timeField)).Data.GetValue(0);
        Assert.Equal(12, time!.Value.ToUniversalTime().Hour); // time column = UTC 12 (agrees)
    }

    private static async Task<List<ValidTelemetryData>> ReadBack(Stream s)
    {
        var results = new List<ValidTelemetryData>();
        using var reader = await Parquet.ParquetReader.CreateAsync(s);
        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using var rgReader = reader.OpenRowGroupReader(rg);
            var cols = new Dictionary<string, Array>();
            foreach (var f in reader.Schema.GetDataFields())
            {
                cols[f.Name] = (await rgReader.ReadColumnAsync(f)).Data;
            }
            int n = cols.Values.First().Length;
            for (int i = 0; i < n; i++)
            {
                results.Add(new ValidTelemetryData
                {
                    PointId = cols["point_id"].GetValue(i)?.ToString(),
                    Value = cols["value"].GetValue(i) is double d ? d : null,
                });
            }
        }
        return results;
    }

    private sealed class CapturingBlobStorage : IBlobStorage
    {
        public string? Container { get; private set; }
        public string? Key { get; private set; }
        public byte[]? Bytes { get; private set; }

        public async Task PutAsync(string container, string key, Stream content, string contentType = "application/octet-stream", CancellationToken ct = default)
        {
            Container = container;
            Key = key;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Bytes = ms.ToArray();
        }

        public Task<Stream?> GetAsync(string c, string k, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task<bool> ExistsAsync(string c, string k, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListAsync(string c, string p = "", CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task DeleteAsync(string c, string k, CancellationToken ct = default) => Task.CompletedTask;
    }
}
