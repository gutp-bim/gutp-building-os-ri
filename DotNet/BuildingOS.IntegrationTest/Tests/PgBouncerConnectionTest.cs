using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using Npgsql;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.PgBouncer)]
public class PgBouncerConnectionTest(PgBouncerFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task TransactionPool_Routes_Query_To_Postgres()
    {
        await using var conn = await fixture.OpenTransactionPoolConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task TransactionPool_Supports_Multiple_Concurrent_Connections()
    {
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var conn = await fixture.OpenTransactionPoolConnectionAsync();
            await using var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", conn);
            return await cmd.ExecuteScalarAsync();
        });

        var pids = await Task.WhenAll(tasks);
        Assert.Equal(10, pids.Length);
        Assert.All(pids, pid => Assert.NotNull(pid));
    }

    [Fact]
    public async Task TransactionPool_Executes_Insert_And_Select_In_Transaction()
    {
        await using var direct = await fixture.OpenDirectConnectionAsync();
        await using var setup = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS pgbouncer_test (
                id   SERIAL PRIMARY KEY,
                val  TEXT NOT NULL
            )
            """, direct);
        await setup.ExecuteNonQueryAsync();

        await using var conn = await fixture.OpenTransactionPoolConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var ins = new NpgsqlCommand(
            "INSERT INTO pgbouncer_test (val) VALUES ('hello') RETURNING id", conn, tx);
        var id = await ins.ExecuteScalarAsync();
        await tx.CommitAsync();

        await using var sel = new NpgsqlCommand(
            $"SELECT val FROM pgbouncer_test WHERE id = {id}", conn);
        var val = await sel.ExecuteScalarAsync();
        Assert.Equal("hello", val?.ToString());
    }

    [Fact]
    public async Task DirectConnection_Bypasses_PgBouncer_For_Migrations()
    {
        await using var conn = await fixture.OpenDirectConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT current_database()", conn);
        var db = await cmd.ExecuteScalarAsync();
        Assert.Equal("buildingos_test", db?.ToString());
    }

    [Fact]
    public void TransactionPool_Respects_Max_Auto_Prepare_Zero()
    {
        // Max Auto Prepare=0 ensures Npgsql never sends Prepare commands,
        // which pgBouncer transaction pool cannot share across clients.
        var builder = new NpgsqlConnectionStringBuilder(fixture.TransactionPoolConnectionString);
        Assert.Equal(0, builder.MaxAutoPrepare);
    }
}
