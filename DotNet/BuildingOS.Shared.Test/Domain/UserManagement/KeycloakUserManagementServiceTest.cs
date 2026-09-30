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
        Assert.Equal("id1", user!.Id);
        Assert.Equal("bob", user!.DisplayName);
        Assert.Null(user!.Role);
        Assert.Empty(user!.Permissions);
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
    public async Task UpdateUserAttributesAsync_WritesTheAttributesTheTokenMapperReads()
    {
        // #519: the realm's building-os-api mappers turn the `role` / `permissions` user attributes
        // into the building_os_role / permissions claims. A grant written anywhere else never reaches
        // the token.
        var keycloak = new FakeKeycloakUser("id1", "alice", new());
        var service = CreateService(keycloak.Handle);

        var updated = await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Role = "operator",
            Permissions = ["floor:2:write"]
        });

        var attrs = keycloak.LastPutAttributes!;
        Assert.Equal(["operator"], attrs["role"]);
        Assert.Equal(["floor:2:write"], attrs["permissions"]);
        Assert.False(attrs.ContainsKey("buildingos_role"));
        Assert.False(attrs.ContainsKey("buildingos_permissions"));

        Assert.Equal("operator", updated.Role);
        Assert.Equal(["floor:2:write"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_MigratesLegacyAttributes_AndClearsThem()
    {
        // A user granted before #519 has only buildingos_*. Adding one permission must carry the old
        // grants and role over to the new attributes and drop the legacy ones, otherwise the next read
        // would merge a permission the admin just removed back in.
        var keycloak = new FakeKeycloakUser("id1", "alice", new()
        {
            ["buildingos_role"] = ["operator"],
            ["buildingos_permissions"] = ["floor:1:read"],
            ["locale"] = ["ja"],
        });
        var service = CreateService(keycloak.Handle);

        var updated = await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:1:read", "floor:2:write"]
        });

        var attrs = keycloak.LastPutAttributes!;
        Assert.Equal(["operator"], attrs["role"]);
        Assert.Equal(["floor:1:read", "floor:2:write"], attrs["permissions"]);
        Assert.False(attrs.ContainsKey("buildingos_role"));
        Assert.False(attrs.ContainsKey("buildingos_permissions"));
        // Keycloak replaces the whole attribute map on PUT: unrelated attributes must be sent back.
        Assert.Equal(["ja"], attrs["locale"]);

        Assert.Equal("operator", updated.Role);
        Assert.Equal(["floor:1:read", "floor:2:write"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_SendsTheFullRepresentation_SoProfileFieldsSurvive()
    {
        // Keycloak 24+ (user profile) treats a PUT that carries `attributes` as a full profile update:
        // an absent email / firstName / lastName is cleared, and a user missing required profile
        // fields can no longer log in ("Account is not fully set up"). Verified against Keycloak 26.7.
        var keycloak = new FakeKeycloakUser("id1", "alice", new() { ["role"] = ["viewer"] },
            email: "alice@example.com", firstName: "Alice", lastName: "Liddell");
        var service = CreateService(keycloak.Handle);

        await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:2:read"]
        });

        var body = keycloak.LastPutBody!.RootElement;
        Assert.Equal("alice", body.GetProperty("username").GetString());
        Assert.Equal("alice@example.com", body.GetProperty("email").GetString());
        Assert.Equal("Alice", body.GetProperty("firstName").GetString());
        Assert.Equal("Liddell", body.GetProperty("lastName").GetString());
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_PermissionsOnly_KeepsExistingRole()
    {
        // Keycloak's PUT replaces the attribute map, so a permissions-only update that sent just
        // `permissions` would silently strip the user's role.
        var keycloak = new FakeKeycloakUser("id1", "alice", new() { ["role"] = ["viewer"] });
        var service = CreateService(keycloak.Handle);

        await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:2:read"]
        });

        Assert.Equal(["viewer"], keycloak.LastPutAttributes!["role"]);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_RemovingTheLastPermission_ClearsTheAttribute()
    {
        var keycloak = new FakeKeycloakUser("id1", "alice", new()
        {
            ["role"] = ["viewer"],
            ["buildingos_permissions"] = ["floor:1:read"],
        });
        var service = CreateService(keycloak.Handle);

        var updated = await service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = []
        });

        Assert.False(keycloak.LastPutAttributes!.ContainsKey("permissions"));
        Assert.False(keycloak.LastPutAttributes!.ContainsKey("buildingos_permissions"));
        Assert.Empty(updated.Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_HonorsLegacyAttributes()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return JsonResponse(BuildKeycloakUserWithAttributes("id1", "alice", new()
            {
                ["buildingos_role"] = ["operator"],
                ["buildingos_permissions"] = ["floor:1:read"],
            }));
        });

        var user = await service.GetUserByIdAsync("id1");

        Assert.Equal("operator", user!.Role);
        Assert.Equal(["floor:1:read"], user.Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_BothPresent_RolePrefersNewAndPermissionsAreUnioned()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return JsonResponse(BuildKeycloakUserWithAttributes("id1", "alice", new()
            {
                ["role"] = ["viewer"],
                ["permissions"] = ["floor:2:read", "floor:1:read"],
                ["buildingos_role"] = ["operator"],
                ["buildingos_permissions"] = ["floor:1:read", "floor:3:write"],
            }));
        });

        var user = await service.GetUserByIdAsync("id1");

        Assert.Equal("viewer", user!.Role);
        Assert.Equal(["floor:2:read", "floor:1:read", "floor:3:write"], user.Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_EmptyNewRole_FallsBackToLegacy()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return JsonResponse(BuildKeycloakUserWithAttributes("id1", "alice", new()
            {
                ["role"] = [""],
                ["buildingos_role"] = ["operator"],
            }));
        });

        var user = await service.GetUserByIdAsync("id1");

        Assert.Equal("operator", user!.Role);
    }

    [Fact]
    public void AttributeNames_MatchTheRealmTokenMappers()
    {
        // One definition of the names, pinned to the realm the stack actually imports.
        var realmPath = FindRepoFile(Path.Combine("oss-stack", "keycloak", "realm.json"));
        using var realm = JsonDocument.Parse(File.ReadAllText(realmPath));
        var mapped = realm.RootElement.GetProperty("clientScopes").EnumerateArray()
            .SelectMany(s => s.TryGetProperty("protocolMappers", out var m) ? m.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(m => m.GetProperty("protocolMapper").GetString() == "oidc-usermodel-attribute-mapper")
            .ToDictionary(
                m => m.GetProperty("config").GetProperty("claim.name").GetString()!,
                m => m.GetProperty("config").GetProperty("user.attribute").GetString()!);

        Assert.Equal(KeycloakUserAttributes.Role, mapped["building_os_role"]);
        Assert.Equal(KeycloakUserAttributes.Permissions, mapped["permissions"]);
    }

    [Fact]
    public void Realm_LetsTheAdminApiStoreTheBuildingOsAttributes()
    {
        // Keycloak 24+ drops attributes the user profile does not declare unless the realm allows
        // unmanaged attributes — a PUT of `role` / `permissions` would return 204 and store nothing.
        // ADMIN_EDIT lets only admins (the Admin API) write them; users cannot see or edit them.
        var realmPath = FindRepoFile(Path.Combine("oss-stack", "keycloak", "realm.json"));
        using var realm = JsonDocument.Parse(File.ReadAllText(realmPath));
        var provider = realm.RootElement.GetProperty("components")
            .GetProperty("org.keycloak.userprofile.UserProfileProvider")[0];
        var config = provider.GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!;
        using var profile = JsonDocument.Parse(config);

        Assert.Equal("ADMIN_EDIT", profile.RootElement.GetProperty("unmanagedAttributePolicy").GetString());
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
        if (role != null) attributes[KeycloakUserAttributes.Role] = [role];
        if (permissions.Length > 0) attributes[KeycloakUserAttributes.Permissions] = permissions;
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
        if (role != null) attributes[KeycloakUserAttributes.Role] = [role];
        if (permissions.Length > 0) attributes[KeycloakUserAttributes.Permissions] = permissions;
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
        if (role != null) attributes[KeycloakUserAttributes.Role] = [role];
        if (permissions.Length > 0) attributes[KeycloakUserAttributes.Permissions] = permissions;
        return new { id, username, email, firstName, lastName, attributes = (object)attributes };
    }

    private static object BuildKeycloakUserWithAttributes(
        string id, string username, Dictionary<string, string[]> attributes) =>
        new
        {
            id,
            username,
            email = (string?)null,
            firstName = (string?)null,
            lastName = (string?)null,
            attributes = (object)attributes
        };

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relative} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>A single Keycloak user whose PUT replaces the whole attribute map, as Keycloak does.</summary>
    private sealed class FakeKeycloakUser(
        string id,
        string username,
        Dictionary<string, string[]> attributes,
        string? email = null,
        string? firstName = null,
        string? lastName = null)
    {
        private Dictionary<string, string[]> _attributes = attributes;

        public Dictionary<string, string[]>? LastPutAttributes { get; private set; }
        public JsonDocument? LastPutBody { get; private set; }

        public HttpResponseMessage Handle(HttpRequestMessage req)
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            if (req.Method == HttpMethod.Put)
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                LastPutBody = JsonDocument.Parse(body);
                if (LastPutBody.RootElement.TryGetProperty("attributes", out var attrs))
                {
                    _attributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(attrs.GetRawText())!;
                    LastPutAttributes = _attributes;
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return JsonResponse(new
            {
                id,
                username,
                email,
                firstName,
                lastName,
                enabled = true,
                emailVerified = true,
                attributes = (object)_attributes
            });
        }
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
