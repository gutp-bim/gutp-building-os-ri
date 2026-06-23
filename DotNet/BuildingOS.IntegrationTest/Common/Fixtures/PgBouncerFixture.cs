using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

/// <summary>
/// pgBouncer (transaction pool) + TimescaleDB fixture.
/// Tests Npgsql compatibility with pgBouncer in transaction pool mode.
/// </summary>
public class PgBouncerFixture : IAsyncLifetime
{
    private const int PgBouncerPort = 5432;
    private const string DbUser = "buildingos";
    private const string DbPassword = "buildingos";
    private const string DbName = "buildingos_test";

    private INetwork? _network;
    private PostgreSqlContainer? _postgres;
    private IContainer? _pgbouncer;

    public string TransactionPoolConnectionString { get; private set; } = string.Empty;

    // Direct connection to Postgres, bypassing pgBouncer — for EF Core migrations
    public string DirectConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder()
            .WithName($"pgbouncer-test-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync();

        _postgres = new PostgreSqlBuilder()
            .WithImage("timescale/timescaledb:latest-pg16")
            .WithDatabase(DbName)
            .WithUsername(DbUser)
            .WithPassword(DbPassword)
            // PostgreSQL 16 defaults pg_hba.conf to scram-sha-256 for host connections.
            // pgBouncer uses md5/plain to reach the backend, which PG16 rejects.
            // trust removes the backend auth requirement for this test-only container.
            .WithEnvironment("POSTGRES_HOST_AUTH_METHOD", "trust")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .Build();

        await _postgres.StartAsync();

        _pgbouncer = new ContainerBuilder()
            .WithImage("edoburu/pgbouncer:latest")
            .WithEnvironment("DB_USER", DbUser)
            .WithEnvironment("DB_PASSWORD", DbPassword)
            .WithEnvironment("DB_HOST", "postgres")
            .WithEnvironment("DB_PORT", "5432")
            .WithEnvironment("DB_NAME", DbName)
            .WithEnvironment("POOL_MODE", "transaction")
            .WithEnvironment("MAX_CLIENT_CONN", "200")
            .WithEnvironment("DEFAULT_POOL_SIZE", "10")
            // PostgreSQL 16 defaults to scram-sha-256; Npgsql 9 dropped MD5 fallback.
            // Use trust for test-only containers — the fixture never hits a real server.
            .WithEnvironment("AUTH_TYPE", "trust")
            .WithEnvironment("SERVER_RESET_QUERY", "")
            .WithNetwork(_network)
            .WithPortBinding(PgBouncerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(PgBouncerPort))
            .Build();

        await _pgbouncer.StartAsync();

        var host = _pgbouncer.Hostname;
        var port = _pgbouncer.GetMappedPublicPort(PgBouncerPort);
        // pgBouncer transaction pool: prepared statements must be disabled (Max Auto Prepare=0).
        // "No Reset On Close" was removed in Npgsql 8; SERVER_RESET_QUERY="" on the pgBouncer
        // side already suppresses server-side resets, so no Npgsql-level override is needed.
        // Npgsql 9 renamed "Max Pool Size" to "Maximum Pool Size".
        TransactionPoolConnectionString =
            $"Host={host};Port={port};Database={DbName};Username={DbUser};Password={DbPassword};" +
            "Max Auto Prepare=0;Pooling=true;Maximum Pool Size=20";

        DirectConnectionString = _postgres.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_pgbouncer is not null) await _pgbouncer.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }

    public async Task<NpgsqlConnection> OpenTransactionPoolConnectionAsync()
    {
        var conn = new NpgsqlConnection(TransactionPoolConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<NpgsqlConnection> OpenDirectConnectionAsync()
    {
        var conn = new NpgsqlConnection(DirectConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
