using BuildingOS.Shared.Infrastructure.OxiGraph;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

public class OxiGraphFixture : IAsyncLifetime
{
    private const int OxiGraphPort = 7878;

    private readonly IContainer _container = new ContainerBuilder()
        // Match docker-compose.oss.yaml so integration/performance results exercise the shipped
        // version rather than changing whenever the upstream latest tag moves.
        .WithImage("ghcr.io/oxigraph/oxigraph:0.5.0")
        .WithCommand("serve", "--location", "/data", "--bind", "0.0.0.0:7878")
        .WithPortBinding(OxiGraphPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening for requests"))
        .Build();

    public OxiGraphClient Client { get; private set; } = null!;
    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var http = new HttpClient();
        BaseUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(OxiGraphPort)}";
        Client = new OxiGraphClient(http, BaseUrl);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public Task ClearAsync() => Client.UpdateAsync("DROP ALL");
}
