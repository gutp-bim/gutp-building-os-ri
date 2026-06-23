using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.Postgres)]
public class PostgresConnectionTest(PostgresFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task Can_Connect_And_Query()
    {
        await using var conn = await fixture.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task TimescaleDB_Extension_Is_Loaded()
    {
        await using var conn = await fixture.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extname FROM pg_extension WHERE extname = 'timescaledb'";
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal("timescaledb", result?.ToString());
    }

    [Fact]
    public async Task Can_Create_Hypertable()
    {
        await using var conn = await fixture.OpenConnectionAsync();

        await using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS telemetry_test (
                time        TIMESTAMPTZ NOT NULL,
                point_id    TEXT NOT NULL,
                value       DOUBLE PRECISION
            );
            """;
        await createCmd.ExecuteNonQueryAsync();

        await using var hypertableCmd = conn.CreateCommand();
        hypertableCmd.CommandText = """
            SELECT create_hypertable('telemetry_test', 'time', if_not_exists => TRUE);
            """;
        var result = await hypertableCmd.ExecuteScalarAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Can_Insert_And_Query_Telemetry()
    {
        await using var conn = await fixture.OpenConnectionAsync();

        // Create hypertable
        await using var setupCmd = conn.CreateCommand();
        setupCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS telemetry_insert_test (
                time     TIMESTAMPTZ NOT NULL,
                point_id TEXT NOT NULL,
                value    DOUBLE PRECISION
            );
            SELECT create_hypertable('telemetry_insert_test', 'time', if_not_exists => TRUE);
            """;
        await setupCmd.ExecuteNonQueryAsync();

        // Insert data
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO telemetry_insert_test (time, point_id, value)
            VALUES (NOW(), 'test-point-001', 22.5)
            """;
        var rows = await insertCmd.ExecuteNonQueryAsync();
        Assert.Equal(1, rows);

        // Query back
        await using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = "SELECT value FROM telemetry_insert_test WHERE point_id = 'test-point-001'";
        var value = await selectCmd.ExecuteScalarAsync();
        Assert.Equal(22.5, (double)value!);
    }
}
