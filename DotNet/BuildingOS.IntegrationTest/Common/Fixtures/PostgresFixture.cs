using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("timescale/timescaledb:latest-pg15")
        .WithDatabase("buildingos_test")
        .WithUsername("buildingos")
        .WithPassword("buildingos")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await EnableTimescaleDbAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private async Task EnableTimescaleDbAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS timescaledb;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ApplyMigrationAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
