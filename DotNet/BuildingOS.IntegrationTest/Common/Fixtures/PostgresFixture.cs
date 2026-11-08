using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

/// <summary>
/// Plain PostgreSQL — what the OSS stack runs (<c>postgres:16</c>, #216/#234). TimescaleDB is an optional warm store
/// and is not part of the default test run; its tests use <see cref="TimescaleFixture"/> and run only on request
/// (<see cref="TimescaleFactAttribute"/>). Override the image with <c>POSTGRES_TEST_IMAGE</c>.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage(Environment.GetEnvironmentVariable("POSTGRES_TEST_IMAGE") is { Length: > 0 } image ? image : "postgres:16")
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

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ApplyMigrationAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
