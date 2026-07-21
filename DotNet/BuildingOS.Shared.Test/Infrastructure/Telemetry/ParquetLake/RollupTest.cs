using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

/// <summary>Unit tests for RollupPartitionKey, RollupAggregator, and RollupSerializer (#222).</summary>
public class RollupTest
{
    // ── RollupPartitionKey ──────────────────────────────────────────────────

    [Fact]
    public void AggKey_Format_IsCorrect()
    {
        var hour = new DateTime(2026, 6, 13, 14, 0, 0, DateTimeKind.Utc);
        var key = RollupPartitionKey.AggKey("building-A", hour);
        Assert.Equal(
            "agg_hourly/building_id=building-A/year=2026/month=06/day=13/hour=14/agg-2026061314.parquet",
            key);
    }

    [Fact]
    public void AggKey_HourPrefix_IsCorrect()
    {
        var hour = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
        var prefix = RollupPartitionKey.HourPrefix("bldg-1", hour);
        Assert.Equal("agg_hourly/building_id=bldg-1/year=2026/month=01/day=05/hour=08/", prefix);
    }

    [Fact]
    public void TryParseBuilding_ReturnsBuilding_WhenKeyValid()
    {
        var key = "agg_hourly/building_id=my-bldg/year=2026/month=06/day=01/hour=10/agg-2026060110.parquet";
        Assert.True(RollupPartitionKey.TryParseBuilding(key, out var b));
        Assert.Equal("my-bldg", b);
    }

    [Fact]
    public void TryParseBuilding_ReturnsFalse_ForNonRollupKey()
    {
        var key = "building_id=x/year=2026/month=06/day=01/hour=10/part-1-2.parquet";
        Assert.False(RollupPartitionKey.TryParseBuilding(key, out _));
    }

    [Fact]
    public void TryParseHour_RoundTrips()
    {
        var hour = new DateTime(2026, 3, 22, 9, 0, 0, DateTimeKind.Utc);
        var key = RollupPartitionKey.AggKey("b", hour);
        Assert.True(RollupPartitionKey.TryParseHour(key, out var parsed));
        Assert.Equal(hour, parsed);
    }

    // ── RollupAggregator ────────────────────────────────────────────────────

    private static ValidTelemetryData Row(string pid, double? v, string datetime, string building = "B", string device = "D", string name = "temp") =>
        new() { PointId = pid, Value = v, Datetime = datetime, Building = building, DeviceId = device, Name = name, Id = Guid.NewGuid().ToString(), ValueType = v is null ? null : "number" };

    private static ValidTelemetryData StringRow(string pid, string text, string datetime) =>
        new() { PointId = pid, Datetime = datetime, Building = "B", DeviceId = "D", Name = "mode", ValueType = "string", ValueText = text, Id = Guid.NewGuid().ToString() };

    private static ValidTelemetryData BoolRow(string pid, bool b, string datetime) =>
        new() { PointId = pid, Datetime = datetime, Building = "B", DeviceId = "D", Name = "run", ValueType = "boolean", ValueBool = b, Id = Guid.NewGuid().ToString() };

    [Fact]
    public void Compute_SinglePoint_AvgMinMaxCount()
    {
        var rows = new[]
        {
            Row("p1", 10.0, "2026-06-13T14:00:00Z"),
            Row("p1", 20.0, "2026-06-13T14:30:00Z"),
            Row("p1", 30.0, "2026-06-13T14:45:00Z"),
        };
        var rollup = RollupAggregator.Compute(rows);
        Assert.Single(rollup);
        var r = rollup[0];
        Assert.Equal("p1", r.PointId);
        Assert.Equal(20.0, r.Avg!.Value, precision: 10);
        Assert.Equal(10.0, r.MinValue!.Value, precision: 10);
        Assert.Equal(30.0, r.MaxValue!.Value, precision: 10);
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void Compute_MultiplePoints_IndependentRows()
    {
        var rows = new[]
        {
            Row("p1", 10.0, "2026-06-13T14:00:00Z"),
            Row("p2", 100.0, "2026-06-13T14:15:00Z"),
            Row("p2", 200.0, "2026-06-13T14:30:00Z"),
        };
        var rollup = RollupAggregator.Compute(rows).ToDictionary(r => r.PointId!);
        Assert.Equal(2, rollup.Count);
        Assert.Equal(10.0, rollup["p1"].Avg!.Value, precision: 10);
        Assert.Equal(150.0, rollup["p2"].Avg!.Value, precision: 10);
        Assert.Equal(2, rollup["p2"].Count);
    }

    [Fact]
    public void Compute_NullValues_CountIncludedAvgExcluded()
    {
        var rows = new[]
        {
            Row("p1", null, "2026-06-13T14:00:00Z"),
            Row("p1", 10.0, "2026-06-13T14:30:00Z"),
        };
        var rollup = RollupAggregator.Compute(rows);
        Assert.Single(rollup);
        var r = rollup[0];
        Assert.Equal(10.0, r.Avg!.Value, precision: 10);
        Assert.Equal(2, r.Count); // null rows counted
    }

    [Fact]
    public void Compute_AllNullValues_AvgMinMaxNull()
    {
        var rows = new[] { Row("p1", null, "2026-06-13T14:00:00Z") };
        var rollup = RollupAggregator.Compute(rows);
        Assert.Single(rollup);
        var r = rollup[0];
        Assert.Null(r.Avg);
        Assert.Null(r.MinValue);
        Assert.Null(r.MaxValue);
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Compute_HourUtc_IsTruncatedToHour()
    {
        var hour = new DateTime(2026, 6, 13, 14, 0, 0, DateTimeKind.Utc);
        var rows = new[] { Row("p1", 1.0, "2026-06-13T14:42:00Z") };
        var rollup = RollupAggregator.Compute(rows, hour);
        Assert.Equal(hour, rollup[0].HourUtc);
    }

    [Fact]
    public void Compute_EmptyRows_ReturnsEmpty()
    {
        var rollup = RollupAggregator.Compute(Array.Empty<ValidTelemetryData>());
        Assert.Empty(rollup);
    }

    [Fact]
    public void Compute_NumericPoint_TaggedNumber()
    {
        // #152 Phase B: numeric rollups keep avg/min/max and are tagged "number".
        var r = Assert.Single(RollupAggregator.Compute(new[] { Row("p1", 10.0, "2026-06-13T14:00:00Z") }));
        Assert.Equal("number", r.ValueType);
        Assert.Null(r.ValueText);
        Assert.Null(r.ValueBool);
    }

    [Fact]
    public void Compute_NonNumericString_LastInBucket()
    {
        // D3=last-in-bucket: representative = latest by timestamp, order-independent; numeric agg null.
        var rows = new[]
        {
            StringRow("p1", "off", "2026-06-13T14:45:00Z"),  // latest — out of order
            StringRow("p1", "auto", "2026-06-13T14:00:00Z"),
        };
        var r = Assert.Single(RollupAggregator.Compute(rows));
        Assert.Equal("string", r.ValueType);
        Assert.Equal("off", r.ValueText);
        Assert.Null(r.ValueBool);
        Assert.Null(r.Avg);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void Compute_NonNumericBoolean_LastInBucket()
    {
        var rows = new[]
        {
            BoolRow("p1", false, "2026-06-13T14:00:00Z"),
            BoolRow("p1", true, "2026-06-13T14:50:00Z"),  // latest
        };
        var r = Assert.Single(RollupAggregator.Compute(rows));
        Assert.Equal("boolean", r.ValueType);
        Assert.True(r.ValueBool);
        Assert.Null(r.ValueText);
        Assert.Null(r.Avg);
    }

    // ── RollupSerializer ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteRead_RoundTrips()
    {
        var hour = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new RollupRow("p1", "B", "D", "temp", 15.5, 10.0, 21.0, 4, hour),
            new RollupRow("p2", "B", "D2", "humi", null, null, null, 2, hour),
        };

        using var ms = new MemoryStream();
        await RollupSerializer.WriteAsync(rows, ms);
        ms.Position = 0;

        var read = await RollupSerializer.ReadAsync(ms);
        Assert.Equal(2, read.Count);

        var p1 = read.Single(r => r.PointId == "p1");
        Assert.Equal(15.5, p1.Avg!.Value, precision: 10);
        Assert.Equal(10.0, p1.MinValue!.Value, precision: 10);
        Assert.Equal(21.0, p1.MaxValue!.Value, precision: 10);
        Assert.Equal(4, p1.Count);
        Assert.Equal(hour, p1.HourUtc);

        var p2 = read.Single(r => r.PointId == "p2");
        Assert.Null(p2.Avg);
        Assert.Equal(2, p2.Count);
    }

    [Fact]
    public async Task WriteRead_EmptyRows_RoundTrips()
    {
        using var ms = new MemoryStream();
        await RollupSerializer.WriteAsync(Array.Empty<RollupRow>(), ms);
        ms.Position = 0;
        var read = await RollupSerializer.ReadAsync(ms);
        Assert.Empty(read);
    }

    [Fact]
    public async Task WriteRead_RoundTrips_DiscriminatedColumns()
    {
        // #152 Phase B: the last-in-bucket value_type/value_text/value_bool columns round-trip.
        var hour = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new RollupRow("pNum", "B", "D", "temp", 15.5, 10.0, 21.0, 4, hour, "number", null, null),
            new RollupRow("pStr", "B", "D", "mode", null, null, null, 3, hour, "string", "auto", null),
            new RollupRow("pBool", "B", "D", "run", null, null, null, 2, hour, "boolean", null, true),
        };

        using var ms = new MemoryStream();
        await RollupSerializer.WriteAsync(rows, ms);
        ms.Position = 0;
        var read = await RollupSerializer.ReadAsync(ms);

        var str = read.Single(r => r.PointId == "pStr");
        Assert.Equal("string", str.ValueType);
        Assert.Equal("auto", str.ValueText);
        Assert.Null(str.Avg);

        var boolean = read.Single(r => r.PointId == "pBool");
        Assert.Equal("boolean", boolean.ValueType);
        Assert.True(boolean.ValueBool);

        var num = read.Single(r => r.PointId == "pNum");
        Assert.Equal("number", num.ValueType);
        Assert.Equal(15.5, num.Avg!.Value, precision: 10);
        Assert.Null(num.ValueText);
    }

    [Fact]
    public async Task Read_LegacyRollupWithoutValueColumns_ReadsAsNumeric()
    {
        // Back-compat: an old 9-column rollup object (no value_* columns) must still read — the missing
        // columns default to null, so a legacy rollup keeps only its numeric aggregates.
        var hour = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc);
        var legacy = new Parquet.Schema.ParquetSchema(
            new Parquet.Schema.DataField<string>("point_id"),
            new Parquet.Schema.DataField<double?>("avg"),
            new Parquet.Schema.DataField<int>("count"),
            new Parquet.Schema.DataField<DateTime?>("hour_utc"));
        using var ms = new MemoryStream();
        using (var w = await Parquet.ParquetWriter.CreateAsync(legacy, ms))
        using (var rg = w.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn((Parquet.Schema.DataField)legacy[0], new[] { "p1" }));
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn((Parquet.Schema.DataField)legacy[1], new double?[] { 42.0 }));
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn((Parquet.Schema.DataField)legacy[2], new[] { 3 }));
            await rg.WriteColumnAsync(new Parquet.Data.DataColumn((Parquet.Schema.DataField)legacy[3], new DateTime?[] { hour }));
        }
        ms.Position = 0;

        var r = Assert.Single(await RollupSerializer.ReadAsync(ms));
        Assert.Equal(42.0, r.Avg!.Value, precision: 10);
        Assert.Equal(3, r.Count);
        Assert.Null(r.ValueType);
        Assert.Null(r.ValueText);
        Assert.Null(r.ValueBool);
    }
}
