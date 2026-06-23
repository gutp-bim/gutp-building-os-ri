using System.Net;
using System.Text.Json;
using BuildingOS.Shared.Domain.OidcClients;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Domain.OidcClients;

public class KeycloakOidcClientServiceTest
{
    private static KeycloakOidcClientService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new OidcMockHttpHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };
        return new KeycloakOidcClientService(
            httpClient, "building-os", "admin-client", "secret",
            NullLogger<KeycloakOidcClientService>.Instance);
    }

    private static HttpResponseMessage Token() =>
        Json(new { access_token = "tok", token_type = "Bearer", expires_in = 300 });

    private static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o)) };

    private static bool IsToken(HttpRequestMessage r) =>
        r.RequestUri!.AbsolutePath.Contains("openid-connect/token");

    [Fact]
    public async Task ListClients_MapsSummaries_WithoutSecret()
    {
        var service = CreateService(req =>
        {
            if (IsToken(req)) return Token();
            return Json(new[]
            {
                new { id = "abc", clientId = "svc-1", enabled = true, serviceAccountsEnabled = true, description = "x", publicClient = false },
            });
        });

        var clients = await service.ListClientsAsync();

        var c = Assert.Single(clients);
        Assert.Equal("abc", c.Id);
        Assert.Equal("svc-1", c.ClientId);
        Assert.True(c.ServiceAccountsEnabled);
    }

    [Fact]
    public async Task CreateClient_ReturnsDetailAndOneTimeSecret()
    {
        var service = CreateService(req =>
        {
            if (IsToken(req)) return Token();
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/clients"))
            {
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.Location = new Uri("http://localhost:8080/admin/realms/building-os/clients/new-id");
                return created;
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/client-secret"))
                return Json(new { type = "secret", value = "S3CR3T" });
            // GET representation
            return Json(new { id = "new-id", clientId = "svc-2", enabled = true, serviceAccountsEnabled = true, publicClient = false });
        });

        var (client, secret) = await service.CreateClientAsync(
            new CreateOidcClientSpec("svc-2", "desc", ServiceAccountsEnabled: true));

        Assert.Equal("new-id", client.Id);
        Assert.Equal("svc-2", client.ClientId);
        Assert.Equal("S3CR3T", secret);
    }

    [Fact]
    public async Task RotateSecret_ReturnsNewSecret()
    {
        var service = CreateService(req =>
        {
            if (IsToken(req)) return Token();
            return Json(new { type = "secret", value = "ROTATED" });
        });

        var secret = await service.RotateSecretAsync("abc");

        Assert.Equal("ROTATED", secret);
    }

    [Fact]
    public async Task SetEnabled_PutsEnabledFlag()
    {
        string? body = null;
        var service = CreateService(req =>
        {
            if (IsToken(req)) return Token();
            if (req.Method == HttpMethod.Put)
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Json(new { id = "abc", clientId = "svc-1", enabled = false, serviceAccountsEnabled = false, publicClient = false });
        });

        var detail = await service.SetEnabledAsync("abc", enabled: false);

        Assert.NotNull(body);
        Assert.False(JsonDocument.Parse(body!).RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(detail.Enabled);
    }

    [Fact]
    public async Task GetClient_ReturnsNull_WhenNotFound()
    {
        var service = CreateService(req =>
        {
            if (IsToken(req)) return Token();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        Assert.Null(await service.GetClientAsync("missing"));
    }

    [Fact]
    public async Task DeleteClient_SendsDelete()
    {
        var deleted = false;
        var service = CreateService(req =>
        {
            if (IsToken(req)) return Token();
            if (req.Method == HttpMethod.Delete) deleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await service.DeleteClientAsync("abc");

        Assert.True(deleted);
    }
}

internal sealed class OidcMockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}
