using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

/// <summary>
/// NATS JetStream container fixture. Set <c>NATS_TEST_URL</c> (e.g. <c>nats://localhost:4222</c>) to
/// run against an already-running JetStream-enabled server instead of a Testcontainers one — for a
/// host that cannot pull images. The server must be disposable: tests create/delete streams and
/// KV buckets freely.
/// </summary>
public class NatsFixture : IAsyncLifetime
{
    private const int NatsPort = 4222;
    private static readonly string? ExternalUrl = Environment.GetEnvironmentVariable("NATS_TEST_URL") is { Length: > 0 } u ? u : null;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(Environment.GetEnvironmentVariable("NATS_TEST_IMAGE") ?? "nats:2.10-alpine")
        .WithCommand("-js")  // enable JetStream
        .WithPortBinding(NatsPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
        .Build();

    public string NatsUrl => ExternalUrl ??
        $"nats://{_container.Hostname}:{_container.GetMappedPublicPort(NatsPort)}";

    public async Task InitializeAsync()
    {
        if (ExternalUrl is null) await _container.StartAsync();
    }

    public Task DisposeAsync() => ExternalUrl is null ? _container.DisposeAsync().AsTask() : Task.CompletedTask;

    public NatsConnection CreateConnection() =>
        new NatsConnection(new NatsOpts { Url = NatsUrl });

    public async Task<(NatsConnection conn, INatsJSContext js)> CreateJetStreamAsync()
    {
        var conn = new NatsConnection(new NatsOpts { Url = NatsUrl });
        await conn.ConnectAsync();
        var js = new NatsJSContext(conn);
        return (conn, js);
    }
}
