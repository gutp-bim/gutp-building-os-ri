using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Npgsql;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>The optional TimescaleDB warm store (WARM_STORE=timescale). Skipped unless INTEGRATION_TIMESCALE is set.</summary>
[Collection(Names.Timescale)]
public class NpgsqlWarmTelemetryStoreTest : IntegrationTestBase
{
    private readonly TimescaleFixture _fixture;
    private NpgsqlWarmTelemetryStore Sut => new(_fixture.ConnectionString);

    public NpgsqlWarmTelemetryStoreTest(TimescaleFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task SetupSchemaAsync()
    {
        // Locate sln root by walking up from BaseDirectory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        var sqlPath = Path.Combine(dir!.FullName, "BuildingOS.Shared", "Migrations", "Timescale", "V001__telemetry_hypertable.sql");
        var sql = await File.ReadAllTextAsync(Path.GetFullPath(sqlPath));
        await _fixture.ApplyMigrationAsync(sql);
    }

    private async Task InsertRowAsync(string pointId, DateTime time, double value, string? building = null)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO telemetry (time, point_id, building, value) VALUES (@t, @p, @b, @v) ON CONFLICT DO NOTHING",
            conn);
        cmd.Parameters.AddWithValue("t", time);
        cmd.Parameters.AddWithValue("p", pointId);
        cmd.Parameters.AddWithValue("b", (object?)building ?? DBNull.Value);
        cmd.Parameters.AddWithValue("v", value);
        await cmd.ExecuteNonQueryAsync();
    }

    [TimescaleFact]
    public async Task QueryAsync_ReturnsRowsInRange()
    {
        await SetupSchemaAsync();
        var now = DateTime.UtcNow;
        await InsertRowAsync("p-warm-1", now.AddHours(-2), 20.0);
        await InsertRowAsync("p-warm-1", now.AddHours(-1), 21.0);
        await InsertRowAsync("p-warm-1", now.AddHours(1), 22.0); // outside range

        var result = await Sut.QueryAsync("p-warm-1", now.AddHours(-3), now);

        Assert.Equal(2, result.Length);
        Assert.All(result, r => Assert.Equal("p-warm-1", r.PointId));
    }

    [TimescaleFact]
    public async Task QueryAsync_ReturnsEmpty_WhenNoData()
    {
        await SetupSchemaAsync();
        var result = await Sut.QueryAsync("no-such-point", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        Assert.Empty(result);
    }

    [TimescaleFact]
    public async Task QueryLatestAsync_ReturnsNewestRow()
    {
        await SetupSchemaAsync();
        var now = DateTime.UtcNow;
        await InsertRowAsync("p-warm-latest", now.AddHours(-2), 10.0);
        await InsertRowAsync("p-warm-latest", now.AddHours(-1), 99.0); // newest

        var result = await Sut.QueryLatestAsync("p-warm-latest");

        Assert.NotNull(result);
        Assert.Equal(99.0, result!.Value);
    }

    [TimescaleFact]
    public async Task QueryAsync_OrdersByTime()
    {
        await SetupSchemaAsync();
        var base_ = DateTime.UtcNow.AddDays(-1);
        await InsertRowAsync("p-warm-order", base_.AddHours(3), 3.0);
        await InsertRowAsync("p-warm-order", base_.AddHours(1), 1.0);
        await InsertRowAsync("p-warm-order", base_.AddHours(2), 2.0);

        var result = await Sut.QueryAsync("p-warm-order", base_, base_.AddHours(4));

        Assert.Equal(new double?[] { 1.0, 2.0, 3.0 }, result.Select(r => r.Value));
    }
}
