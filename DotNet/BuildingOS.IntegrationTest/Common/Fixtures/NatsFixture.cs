using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

/// <summary>
/// NATS JetStream container fixture.
/// </summary>
public class NatsFixture : IAsyncLifetime
{
    private const int NatsPort = 4222;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("nats:2.10-alpine")
        .WithCommand("-js")  // enable JetStream
        .WithPortBinding(NatsPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(NatsPort))
        .Build();

    public string NatsUrl =>
        $"nats://{_container.Hostname}:{_container.GetMappedPublicPort(NatsPort)}";

    public async Task InitializeAsync() => await _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

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
