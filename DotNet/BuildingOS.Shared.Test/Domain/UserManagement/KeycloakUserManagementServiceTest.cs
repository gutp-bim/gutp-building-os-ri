using System.Net;
using System.Text.Json;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class KeycloakUserManagementServiceTest
{
    private static KeycloakUserManagementService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new MockHttpHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:8080")
        };
        return new KeycloakUserManagementService(
            httpClient,
            realm: "building-os",
            adminClientId: "admin-client",
            adminClientSecret: "secret",
            NullLogger<KeycloakUserManagementService>.Instance);
    }

    [Fact]
    public async Task GetUsersAsync_ReturnsUsers()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return UsersListResponse([
                BuildKeycloakUserJson("id1", "alice", "alice@example.com", "admin", ["floor:1:read"])
            ]);
        });

        var users = await service.GetUsersAsync();

        Assert.Single(users);
        Assert.Equal("id1", users[0].Id);
        Assert.Equal("alice", users[0].DisplayName);
        Assert.Equal("alice@example.com", users[0].Email);
        Assert.Equal("admin", users[0].Role);
        Assert.Equal(["floor:1:read"], users[0].Permissions);
    }

    [Fact]
    public async Task GetUsersAsync_ReturnsFullName_WhenFirstLastNamePresent()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return UsersListResponse([
                BuildKeycloakUserWithNameJson("id2", "bob.smith", "Bob", "Smith", "bob@example.com", null, [])
            ]);
        });

        var users = await service.GetUsersAsync();

        Assert.Equal("Bob Smith", users[0].DisplayName);
    }

    [Fact]
    public async Task GetUserByIdAsync_ReturnsUser()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return SingleUserResponse("id1", "bob", "bob@example.com", null, []);
        });

        var user = await service.GetUserByIdAsync("id1");

        Assert.NotNull(user);
        Assert.Equal("id1", user.Id);
        Assert.Equal("bob", user.DisplayName);
        Assert.Null(user.Role);
        Assert.Empty(user.Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_ReturnsNull_WhenNotFound()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var user = await service.GetUserByIdAsync("nonexistent");

        Assert.Null(user);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_PutsAttributesAndReturnsUpdatedUser()
    {
        string? capturedBody = null;
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return SingleUserResponse("id1", "alice", "alice@example.com", "manager", ["floor:2:write"]);
        });

        var updated = await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Role = "manager",
            Permissions = ["floor:2:write"]
        });

        Assert.NotNull(capturedBody);
        var doc = JsonDocument.Parse(capturedBody);
        var attrs = doc.RootElement.GetProperty("attributes");
        Assert.Equal("manager", attrs.GetProperty("buildingos_role")[0].GetString());
        Assert.Equal("floor:2:write", attrs.GetProperty("buildingos_permissions")[0].GetString());

        Assert.Equal("manager", updated.Role);
        Assert.Equal(["floor:2:write"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_SendsTokenInAuthHeader()
    {
        string? capturedToken = null;
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            capturedToken = req.Headers.Authorization?.Parameter;
            if (req.Method == HttpMethod.Put)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return SingleUserResponse("id1", "alice", "alice@example.com", null, []);
        });

        await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest());

        Assert.Equal("fake-token", capturedToken);
    }

    [Fact]
    public async Task SetEnabledAsync_PutsEnabledFlagAndReturnsUpdatedUser()
    {
        string? capturedBody = null;
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return JsonResponse(BuildKeycloakUserJsonWithEnabled("id1", "alice", "alice@example.com", null, [], enabled: false));
        });

        var updated = await service.SetEnabledAsync("id1", enabled: false);

        Assert.NotNull(capturedBody);
        var doc = JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(updated.Enabled);
    }

    [Fact]
    public async Task GetUsersAsync_DefaultsEnabledToTrue_WhenFlagMissing()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return UsersListResponse([
                BuildKeycloakUserJson("id1", "alice", "alice@example.com", "admin", [])
            ]);
        });

        var users = await service.GetUsersAsync();

        Assert.True(users[0].Enabled);
    }

    [Fact]
    public async Task GetUsersAsync_MapsDisabledFlag()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return UsersListResponse([
                BuildKeycloakUserJsonWithEnabled("id1", "alice", "alice@example.com", "admin", [], enabled: false)
            ]);
        });

        var users = await service.GetUsersAsync();

        Assert.False(users[0].Enabled);
    }

    // === Helpers ===

    private static object BuildKeycloakUserJsonWithEnabled(
        string id, string username, string email, string? role, string[] permissions, bool enabled)
    {
        var attributes = new Dictionary<string, string[]>();
        if (role != null) attributes["buildingos_role"] = [role];
        if (permissions.Length > 0) attributes["buildingos_permissions"] = permissions;
        return new
        {
            id,
            username,
            email,
            firstName = (string?)null,
            lastName = (string?)null,
            attributes = (object)attributes,
            enabled
        };
    }

    private static HttpResponseMessage TokenResponse() =>
        JsonResponse(new { access_token = "fake-token", token_type = "Bearer", expires_in = 300 });

    private static HttpResponseMessage UsersListResponse(object[] users) =>
        JsonResponse(users);

    private static HttpResponseMessage SingleUserResponse(
        string id, string username, string email, string? role, string[] permissions) =>
        JsonResponse(BuildKeycloakUserJson(id, username, email, role, permissions));

    private static object BuildKeycloakUserJson(
        string id, string username, string email, string? role, string[] permissions)
    {
        var attributes = new Dictionary<string, string[]>();
        if (role != null) attributes["buildingos_role"] = [role];
        if (permissions.Length > 0) attributes["buildingos_permissions"] = permissions;
        return new
        {
            id,
            username,
            email,
            firstName = (string?)null,
            lastName = (string?)null,
            attributes = (object)attributes
        };
    }

    private static object BuildKeycloakUserWithNameJson(
        string id, string username, string firstName, string lastName,
        string email, string? role, string[] permissions)
    {
        var attributes = new Dictionary<string, string[]>();
        if (role != null) attributes["buildingos_role"] = [role];
        if (permissions.Length > 0) attributes["buildingos_permissions"] = permissions;
        return new { id, username, email, firstName, lastName, attributes = (object)attributes };
    }

    private static HttpResponseMessage JsonResponse(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}
