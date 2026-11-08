using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

/// <summary>
/// TimescaleDB, for the optional <c>WARM_STORE=timescale</c> path only (#216/#234). Every test in
/// <see cref="Collections.Names.Timescale"/> is a <see cref="TimescaleFactAttribute"/>, which skips unless
/// <c>INTEGRATION_TIMESCALE</c> is truthy — and since xUnit may still create the collection fixture for skipped
/// tests, the fixture itself builds and starts nothing unless the flag is set. Override the image with
/// <c>TIMESCALE_TEST_IMAGE</c>.
/// </summary>
public class TimescaleFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _container = TimescaleFactAttribute.Enabled
        ? new PostgreSqlBuilder()
            .WithImage(Environment.GetEnvironmentVariable("TIMESCALE_TEST_IMAGE") is { Length: > 0 } image ? image : "timescale/timescaledb:latest-pg16")
            .WithDatabase("buildingos_test")
            .WithUsername("buildingos")
            .WithPassword("buildingos")
            .Build()
        : null;

    public string ConnectionString => (_container ?? throw new InvalidOperationException(
        "TimescaleDB tests are disabled; set INTEGRATION_TIMESCALE=true")).GetConnectionString();

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task InitializeAsync()
    {
        if (_container is null) return; // opt-in: nothing is pulled or started by default
        await _container.StartAsync();
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS timescaledb;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    public async Task ApplyMigrationAsync(string sql)
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A test of the optional TimescaleDB warm store. Skipped unless <c>INTEGRATION_TIMESCALE</c> is
/// <c>true</c>/<c>1</c>/<c>yes</c>/<c>on</c> — TimescaleDB is opt-in in the product, so it is opt-in in the tests.
/// </summary>
public sealed class TimescaleFactAttribute : FactAttribute
{
    public TimescaleFactAttribute()
    {
        if (!Enabled) Skip = "TimescaleDB is optional; set INTEGRATION_TIMESCALE=true to run";
    }

    public static bool Enabled =>
        (Environment.GetEnvironmentVariable("INTEGRATION_TIMESCALE") ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
}
