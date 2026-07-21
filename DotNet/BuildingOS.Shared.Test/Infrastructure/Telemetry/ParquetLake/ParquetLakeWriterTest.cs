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

    [Fact]
    public async Task Serializer_RoundTrips_DiscriminatedValueColumns()
    {
        // #152: numeric / string / boolean rows all round-trip through the additive value_type /
        // value_text / value_bool columns; numeric keeps its double `value` column unchanged.
        var rows = new[]
        {
            new ValidTelemetryData { PointId = "p1", Datetime = "2026-06-12T12:00:00Z", Value = 7.5, ValueType = "number" },
            new ValidTelemetryData { PointId = "p2", Datetime = "2026-06-12T12:00:00Z", ValueText = "auto", ValueType = "string" },
            new ValidTelemetryData { PointId = "p3", Datetime = "2026-06-12T12:00:00Z", ValueBool = true, ValueType = "boolean" },
        };

        using var ms = new MemoryStream();
        await ParquetTelemetrySerializer.WriteAsync(rows, ms);
        ms.Position = 0;
        var read = await ReadBackFull(ms);

        Assert.Equal(3, read.Count);
        var num = read.Single(r => r.PointId == "p1");
        Assert.Equal(7.5, num.Value);
        Assert.Equal("number", num.ValueType);
        Assert.Null(num.ValueText);
        Assert.Null(num.ValueBool);

        var str = read.Single(r => r.PointId == "p2");
        Assert.Null(str.Value);
        Assert.Equal("string", str.ValueType);
        Assert.Equal("auto", str.ValueText);

        var boolean = read.Single(r => r.PointId == "p3");
        Assert.Null(boolean.Value);
        Assert.Equal("boolean", boolean.ValueType);
        Assert.True(boolean.ValueBool);
    }

    [Fact]
    public async Task Reader_LegacyFileWithoutValueColumns_ReadsAsNumeric()
    {
        // Back-compat: an old part-*.parquet (no value_type/text/bool columns) must still read — the
        // missing columns default to null, so a legacy row keeps only its numeric Value.
        var legacySchema = new Parquet.Schema.ParquetSchema(
            new Parquet.Schema.DataField<string>("point_id"),
            new Parquet.Schema.DataField<double?>("value"),
            new Parquet.Schema.DataField<DateTime?>("time"));
        using var ms = new MemoryStream();
        using (var w = await Parquet.ParquetWriter.CreateAsync(legacySchema, ms))
        using (var rg = w.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn(
                (Parquet.Schema.DataField)legacySchema[0], new[] { "p1" }));
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn(
                (Parquet.Schema.DataField)legacySchema[1], new double?[] { 3.14 }));
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn(
                (Parquet.Schema.DataField)legacySchema[2], new DateTime?[] { new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc) }));
        }
        ms.Position = 0;
        var read = await ReadBackFull(ms);

        var r = Assert.Single(read);
        Assert.Equal(3.14, r.Value);
        Assert.Null(r.ValueType);
        Assert.Null(r.ValueText);
        Assert.Null(r.ValueBool);
    }

    private static async Task<List<ValidTelemetryData>> ReadBackFull(Stream s)
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
                    PointId   = cols.TryGetValue("point_id", out var p) ? p.GetValue(i)?.ToString() : null,
                    Value     = cols.TryGetValue("value", out var v) && v.GetValue(i) is double d ? d : null,
                    ValueType = cols.TryGetValue("value_type", out var vt) ? vt.GetValue(i)?.ToString() : null,
                    ValueText = cols.TryGetValue("value_text", out var vx) ? vx.GetValue(i)?.ToString() : null,
                    ValueBool = cols.TryGetValue("value_bool", out var vb) && vb.GetValue(i) is bool b ? b : null,
                });
            }
        }
        return results;
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
