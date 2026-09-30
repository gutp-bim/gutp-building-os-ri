using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static KeycloakUserManagementService CreateService(FakeRealm realm) => CreateService(realm.Handle);

    // ── Reads ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUsersAsync_ReturnsUsers()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["admin"], ["permissions"] = ["floor:1:read"] },
            email: "alice@example.com");

        var users = await CreateService(realm).GetUsersAsync();

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
        var realm = new FakeRealm();
        realm.AddUser("id2", "bob.smith", new(), firstName: "Bob", lastName: "Smith");

        var users = await CreateService(realm).GetUsersAsync();

        Assert.Equal("Bob Smith", users[0].DisplayName);
    }

    [Fact]
    public async Task GetUserByIdAsync_ReturnsUser()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "bob", new());

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.NotNull(user);
        Assert.Equal("id1", user!.Id);
        Assert.Equal("bob", user.DisplayName);
        Assert.Null(user.Role);
        Assert.Empty(user.Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_ReturnsNull_WhenNotFound()
    {
        var user = await CreateService(new FakeRealm()).GetUserByIdAsync("nonexistent");

        Assert.Null(user);
    }

    [Fact]
    public async Task GetUserByIdAsync_MatchesPropertiesCaseInsensitively()
    {
        // The read path deserializes with JsonSerializerOptions.Web (case-insensitive), as before #519.
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token"))
                return TokenResponse();
            return JsonResponse(new Dictionary<string, object>
            {
                ["Id"] = "id1",
                ["Username"] = "alice",
                ["Attributes"] = new Dictionary<string, string[]> { ["role"] = ["viewer"] },
                ["Enabled"] = false,
            });
        });

        var user = await service.GetUserByIdAsync("id1");

        Assert.Equal("id1", user!.Id);
        Assert.Equal("viewer", user.Role);
        Assert.False(user.Enabled);
    }

    [Fact]
    public async Task GetUserByIdAsync_HonorsLegacyAttributes_WhenNothingElseCarriesARole()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new()
        {
            ["buildingos_role"] = ["operator"],
            ["buildingos_permissions"] = ["floor:1:read"],
        });

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.Equal("operator", user!.Role);
        Assert.Equal(["floor:1:read"], user.Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_BothPresent_OwnRoleWins_AndPermissionsAreUnioned()
    {
        // The role shown in /admin (and used by the Admin-API fallback) is the one the token carries:
        // the mapper reads `role`, never buildingos_role.
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new()
        {
            ["role"] = ["viewer"],
            ["permissions"] = ["floor:2:read", "floor:1:read"],
            ["buildingos_role"] = ["operator"],
            ["buildingos_permissions"] = ["floor:1:read", "floor:3:write"],
        });

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.Equal("viewer", user!.Role);
        Assert.Equal(["floor:2:read", "floor:1:read", "floor:3:write"], user.Permissions);
        Assert.Empty(realm.GroupListGets); // an own role needs no group lookup
    }

    [Fact]
    public async Task GetUserByIdAsync_NoOwnRole_ShowsTheGroupRole_NotLegacy()
    {
        var realm = new FakeRealm();
        realm.AddGroup("g-view", "/viewers", role: "viewer");
        realm.AddUser("id1", "alice", new() { ["buildingos_role"] = ["admin"] }, groups: ["g-view"]);

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.Equal("viewer", user!.Role);
    }

    [Fact]
    public async Task GetUserByIdAsync_EmptyOwnRole_IsAbsent()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = [""], ["buildingos_role"] = ["operator"] });

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.Equal("operator", user!.Role);
    }

    [Fact]
    public async Task GetUserByIdAsync_WhitespaceOwnRole_IsShownAsStored()
    {
        // The mapper emits the own value untrimmed and it shadows the group: the token is not admin.
        var realm = new FakeRealm();
        realm.AddGroup("g-admin", "/admins", role: "admin");
        realm.AddUser("id1", "alice", new() { ["role"] = [" "] }, groups: ["g-admin"]);

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.Equal(" ", user!.Role);
    }

    [Fact]
    public async Task GetUserByIdAsync_AmbiguousGroupRoles_ReportTheNonAdminRole()
    {
        // Keycloak's non-aggregating mapper emits one group's value and does not define which. The
        // Admin-API fallback authorizes from this role, so it fails closed.
        var realm = new FakeRealm();
        realm.AddGroup("g-admin", "/admins", role: "admin");
        realm.AddGroup("g-view", "/viewers", role: "viewer");
        realm.AddUser("id1", "alice", new(), groups: ["g-admin", "g-view"]);

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.Equal("viewer", user!.Role);
    }

    [Fact]
    public async Task GetUsersAsync_DefaultsEnabledToTrue_WhenFlagMissing()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["admin"] }, enabled: null);

        var users = await CreateService(realm).GetUsersAsync();

        Assert.True(users[0].Enabled);
    }

    [Fact]
    public async Task GetUsersAsync_MapsDisabledFlag()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["admin"] }, enabled: false);

        var users = await CreateService(realm).GetUsersAsync();

        Assert.False(users[0].Enabled);
    }

    // ── Effective role for the lockout guard ─────────────────────────────────

    [Fact]
    public async Task GetUserRoleStatesAsync_ResolvesTheRoleInheritedFromGroups_ByParentId()
    {
        // The building-os-role mapper is non-aggregating: the user's own `role` wins, otherwise a
        // group's (or a parent group's) `role` reaches the token. Parents are walked by id (Keycloak 23+
        // `parentId`), each at most once per call.
        var realm = new FakeRealm();
        realm.AddGroup("g1", "/building-os-admins", role: "admin");
        realm.AddGroup("g2", "/ops", role: "admin");
        realm.AddGroup("g3", "/ops/tokyo", parentId: "g2");
        realm.AddUser("u-group-admin", "ga", new(), groups: ["g1"]);
        realm.AddUser("u-sub-admin", "sa", new(), groups: ["g3"]);
        realm.AddUser("u-sub-admin2", "sa2", new(), groups: ["g3"]);
        realm.AddUser("u-own", "own", new() { ["role"] = ["viewer"] }, groups: ["g1"]);
        realm.AddUser("u-none", "none", new());

        var states = (await CreateService(realm).GetUserRoleStatesAsync()).ToDictionary(s => s.Id);

        Assert.Equal("admin", states["u-group-admin"].GroupRole);
        Assert.Null(states["u-group-admin"].Role);
        Assert.Equal("admin", states["u-group-admin"].EffectiveRole);
        Assert.Equal("admin", states["u-sub-admin"].EffectiveRole);
        Assert.Equal("admin", states["u-sub-admin2"].EffectiveRole);
        Assert.Equal("viewer", states["u-own"].EffectiveRole);
        Assert.Null(states["u-none"].EffectiveRole);
        Assert.DoesNotContain("u-own", realm.GroupListGets);
        Assert.Equal(["g2"], realm.GroupByIdGets);
        Assert.Empty(realm.GroupByPathGets);
        Assert.Equal(1, realm.TokenRequests);
    }

    [Fact]
    public async Task GetUserRoleStatesAsync_GroupNameWithSlash_IsNotMistakenForASubgroup()
    {
        // Keycloak 23+ escapes a '/' inside a group name as "~/": "/a~/b" is ONE top-level group. Splitting
        // the path on '/' would look up a non-existent parent "/a" (or, worse, a real unrelated one).
        var realm = new FakeRealm();
        realm.AddGroup("g-a", "/a", role: "admin");
        realm.AddGroup("g-ab", "/a~/b");
        realm.AddUser("u1", "u1", new(), groups: ["g-ab"]);

        var states = await CreateService(realm).GetUserRoleStatesAsync();

        Assert.Null(states.Single().EffectiveRole);
        Assert.Empty(realm.GroupByIdGets);
        Assert.Empty(realm.GroupByPathGets);
    }

    [Fact]
    public async Task GetUserRoleStatesAsync_WithoutParentId_FallsBackToThePath()
    {
        // Keycloak < 23 has no `parentId` (and no "~/" escaping); the path is then the only link.
        var realm = new FakeRealm { EmitParentId = false };
        realm.AddGroup("g2", "/ops", role: "admin");
        realm.AddGroup("g3", "/ops/tokyo", parentId: "g2");
        realm.AddUser("u1", "u1", new(), groups: ["g3"]);

        var states = await CreateService(realm).GetUserRoleStatesAsync();

        Assert.Equal("admin", states.Single().EffectiveRole);
        Assert.Equal(["/ops"], realm.GroupByPathGets);
    }

    [Fact]
    public async Task GetUserRoleStatesAsync_FlagsDisagreeingGroups()
    {
        var realm = new FakeRealm();
        realm.AddGroup("g-admin", "/admins", role: "admin");
        realm.AddGroup("g-view", "/viewers", role: "viewer");
        realm.AddGroup("g-admin2", "/admins2", role: "admin");
        realm.AddUser("mixed", "mixed", new(), groups: ["g-view", "g-admin"]);
        realm.AddUser("agreed", "agreed", new(), groups: ["g-admin", "g-admin2"]);

        var states = (await CreateService(realm).GetUserRoleStatesAsync()).ToDictionary(s => s.Id);

        // Conservative for the target (any admin → admin), not countable as another admin.
        Assert.Equal("admin", states["mixed"].GroupRole);
        Assert.True(states["mixed"].GroupRoleAmbiguous);
        Assert.False(states["mixed"].IsUnambiguouslyAdmin);
        Assert.False(states["agreed"].GroupRoleAmbiguous);
        Assert.True(states["agreed"].IsUnambiguouslyAdmin);
    }

    [Fact]
    public async Task GetUserRoleStatesAsync_LegacyRole_DoesNotMaskTheRoleThatReachesTheToken()
    {
        var realm = new FakeRealm();
        realm.AddGroup("g", "/viewers", role: "viewer");
        // Token carries role=viewer; the legacy admin never reaches it.
        realm.AddUser("u-stale", "u-stale", new() { ["role"] = ["viewer"], ["buildingos_role"] = ["admin"] });
        // No own `role`: the group's viewer reaches the token, not the legacy admin.
        realm.AddUser("u-legacy-grouped", "u-lg", new() { ["buildingos_role"] = ["admin"] }, groups: ["g"]);
        // No own `role`, no group: the Admin-API fallback reads the legacy admin.
        realm.AddUser("u-legacy-only", "u-lo", new() { ["buildingos_role"] = ["admin"] });

        var states = (await CreateService(realm).GetUserRoleStatesAsync()).ToDictionary(s => s.Id);

        Assert.Equal("viewer", states["u-stale"].EffectiveRole);
        Assert.Equal("viewer", states["u-legacy-grouped"].EffectiveRole);
        Assert.Equal("admin", states["u-legacy-only"].EffectiveRole);
        Assert.True(states["u-legacy-only"].IsUnambiguouslyAdmin);
    }

    [Fact]
    public async Task RoleLookup_ResolvesOneUser_WithoutListing()
    {
        var realm = new FakeRealm();
        realm.AddGroup("g1", "/admins", role: "admin");
        realm.AddUser("u1", "u1", new(), groups: ["g1"]);
        realm.AddUser("u2", "u2", new() { ["role"] = ["viewer"] });

        var lookup = await CreateService(realm).CreateRoleLookupAsync();
        var state = await lookup.GetUserAsync("u1", includeGroupRole: false);

        Assert.Equal("admin", state!.EffectiveRole);
        Assert.Equal(0, realm.UserListGets);
        Assert.Null(await lookup.GetUserAsync("ghost", includeGroupRole: false));
    }

    [Fact]
    public async Task RoleLookup_IncludeGroupRole_ResolvesTheGroupsEvenWithAnOwnRole()
    {
        // Clearing an own `admin` falls back to the groups; the guard needs that value even though the
        // own role hides it today.
        var realm = new FakeRealm();
        realm.AddGroup("g1", "/admins", role: "admin");
        realm.AddUser("u1", "u1", new() { ["role"] = ["admin"] }, groups: ["g1"]);

        var lookup = await CreateService(realm).CreateRoleLookupAsync();

        Assert.Null((await lookup.GetUserAsync("u1", includeGroupRole: false))!.GroupRole);
        Assert.Equal("admin", (await lookup.GetUserAsync("u1", includeGroupRole: true))!.GroupRole);
    }

    [Fact]
    public async Task RoleLookup_TargetAndSnapshot_ShareOneTokenAndTheGroupCache()
    {
        var realm = new FakeRealm();
        realm.AddGroup("g2", "/ops", role: "admin");
        realm.AddGroup("g3", "/ops/tokyo", parentId: "g2");
        realm.AddUser("u1", "u1", new(), groups: ["g3"]);
        realm.AddUser("u2", "u2", new(), groups: ["g3"]);

        var lookup = await CreateService(realm).CreateRoleLookupAsync();
        var target = await lookup.GetUserAsync("u1", includeGroupRole: false);
        var all = await lookup.GetAllAsync();

        Assert.Equal("admin", target!.EffectiveRole);
        Assert.All(all, s => Assert.Equal("admin", s.EffectiveRole));
        Assert.Equal(1, realm.TokenRequests);
        Assert.Single(realm.GroupListGets, "u1"); // the target's groups are not fetched twice
        Assert.Equal(["g2"], realm.GroupByIdGets);
    }

    [Fact]
    public async Task GetUserRoleStatesAsync_ManyUsers_KeepsTheListOrder_AndFetchesEachParentOnce()
    {
        // Group lookups run concurrently (bounded); the result must still follow Keycloak's user order,
        // and concurrent users of one parent group must share one fetch.
        var realm = new FakeRealm { Delay = TimeSpan.FromMilliseconds(5) };
        realm.AddGroup("g2", "/ops", role: "admin");
        realm.AddGroup("g3", "/ops/tokyo", parentId: "g2");
        realm.AddGroup("gv", "/viewers", role: "viewer");
        var ids = Enumerable.Range(0, 40).Select(i => $"u{i:D2}").ToList();
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
            realm.AddUser(id, id, new(), groups: [i % 2 == 0 ? "g3" : "gv"]);

        var states = await CreateService(realm).GetUserRoleStatesAsync();
        var users = await CreateService(realm).GetUsersAsync();

        Assert.Equal(ids, states.Select(s => s.Id));
        Assert.Equal(ids, users.Select(u => u.Id));
        Assert.All(states.Select((s, i) => (s, i)), x =>
            Assert.Equal(x.i % 2 == 0 ? "admin" : "viewer", x.s.EffectiveRole));
        Assert.Equal(2, realm.GroupByIdGets.Count); // once per service call
        Assert.InRange(realm.MaxConcurrentRequests, 2, 8);
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUserAttributesAsync_WritesTheAttributesTheTokenMapperReads()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new());

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Role = "operator",
            Permissions = ["floor:2:write"]
        });

        var attrs = realm.LastPutAttributes!;
        Assert.Equal(["operator"], attrs["role"]);
        Assert.Equal(["floor:2:write"], attrs["permissions"]);
        Assert.False(attrs.ContainsKey("buildingos_role"));
        Assert.False(attrs.ContainsKey("buildingos_permissions"));

        Assert.Equal("operator", updated.Role);
        Assert.Equal(["floor:2:write"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_PermissionOnly_MigratesPermissions_ButLeavesTheRoleAttributesAlone()
    {
        // Permissions move to `permissions` and the legacy attribute goes. The role does NOT: copying a
        // legacy buildingos_role into `role` would override a group role in the token.
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new()
        {
            ["buildingos_role"] = ["operator"],
            ["buildingos_permissions"] = ["floor:1:read"],
            ["locale"] = ["ja"],
        });

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:1:read", "floor:2:write"]
        });

        var attrs = realm.LastPutAttributes!;
        Assert.False(attrs.ContainsKey("role"));
        Assert.Equal(["operator"], attrs["buildingos_role"]);
        Assert.Equal(["floor:1:read", "floor:2:write"], attrs["permissions"]);
        Assert.False(attrs.ContainsKey("buildingos_permissions"));
        // Keycloak replaces the whole attribute map on PUT: unrelated attributes must be sent back.
        Assert.Equal(["ja"], attrs["locale"]);

        Assert.Equal("operator", updated.Role);
        Assert.Equal(["floor:1:read", "floor:2:write"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_PermissionOnly_KeepsBothRolesExactlyAsStored()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"], ["buildingos_role"] = ["admin"] });

        await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:2:read"]
        });

        Assert.Equal(["viewer"], realm.LastPutAttributes!["role"]);
        Assert.Equal(["admin"], realm.LastPutAttributes!["buildingos_role"]);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_AddsAndRemovesPermissionsWithoutASeparateRead()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new()
        {
            ["role"] = ["viewer"],
            ["permissions"] = ["floor:1:read"],
            ["buildingos_permissions"] = ["floor:9:read"],
        });

        var added = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { PermissionsToAdd = ["floor:2:read"] });
        Assert.Equal(["floor:1:read", "floor:9:read", "floor:2:read"], added.Permissions);

        var removed = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { PermissionsToRemove = ["floor:9:read"] });
        Assert.Equal(["floor:1:read", "floor:2:read"], removed.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_UsesOneToken_ReadPutAndAVerifyingRead()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] });

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Role = "operator",
            Permissions = ["floor:2:read"]
        });

        Assert.Equal(1, realm.TokenRequests);
        Assert.Equal(2, realm.UserGets);
        Assert.Equal(1, realm.Puts);
        Assert.Empty(realm.GroupListGets);
        Assert.Equal("operator", updated.Role);
        Assert.Equal(["floor:2:read"], updated.Permissions);
        Assert.Equal("alice", updated.UserPrincipalName);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_WriteNotPersisted_Throws()
    {
        // Keycloak 24+ without unmanagedAttributePolicy answers 204 to the PUT and stores nothing.
        // Reporting that as success is how #519 went unnoticed.
        var realm = new FakeRealm { DropAttributeWrites = true };
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] });

        var ex = await Assert.ThrowsAsync<UserAttributesNotPersistedException>(() =>
            CreateService(realm).UpdateUserAttributesAsync("id1",
                new UpdateUserAttributesRequest { Permissions = ["floor:2:read"] }));

        // The caller learns what Keycloak actually returns: none of the Building OS attributes.
        Assert.Empty(ex.StoredAttributes);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_ConcurrentOverwrite_IsNotReportedAsDropped()
    {
        // Another admin changing the user between our PUT and the re-read is not a dropped write.
        var realm = new FakeRealm
        {
            AfterPut = attrs =>
            {
                attrs["role"] = ["operator"];
                attrs["permissions"] = ["floor:9:read"];
            },
        };
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] });

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { Permissions = ["floor:2:read"] });

        Assert.Equal("operator", updated.Role);
        Assert.Equal(["floor:9:read"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_ReturnsWhatKeycloakStored()
    {
        var realm = new FakeRealm { ReverseStoredValues = true };
        realm.AddUser("id1", "alice", new());

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { Permissions = ["a:1:read", "b:2:read"] });

        Assert.Equal(["b:2:read", "a:1:read"], updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_NoOwnRoleAfterTheWrite_ReportsTheGroupRole()
    {
        var realm = new FakeRealm();
        realm.AddGroup("g1", "/admins", role: "admin");
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] }, groups: ["g1"]);

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { Role = "" });

        Assert.Equal("admin", updated.Role);
        Assert.Equal(1, realm.TokenRequests);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_UnknownUser_ThrowsUserNotFound()
    {
        var realm = new FakeRealm();

        await Assert.ThrowsAsync<UserNotFoundException>(() => CreateService(realm).UpdateUserAttributesAsync("ghost",
            new UpdateUserAttributesRequest { PermissionsToAdd = ["floor:1:read"] }));
        Assert.Equal(0, realm.Puts);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_SendsTheProfileFields_SoTheySurvive()
    {
        // Keycloak 24+ (user profile) treats a PUT that carries `attributes` as a full profile update:
        // an absent email / firstName / lastName is cleared. Verified against Keycloak 26.7.
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] },
            email: "alice@example.com", firstName: "Alice", lastName: "Liddell");

        await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:2:read"]
        });

        var body = realm.LastPutBody!.RootElement;
        Assert.Equal("alice", body.GetProperty("username").GetString());
        Assert.Equal("alice@example.com", body.GetProperty("email").GetString());
        Assert.Equal("Alice", body.GetProperty("firstName").GetString());
        Assert.Equal("Liddell", body.GetProperty("lastName").GetString());
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("requiredActions")]
    [InlineData("emailVerified")]
    [InlineData("id")]
    [InlineData("createdTimestamp")]
    public async Task UpdateUserAttributesAsync_DoesNotResendAccountState(string field)
    {
        // Resending `enabled` / `requiredActions` / `emailVerified` from the snapshot silently reverts a
        // concurrent SetEnabledAsync(false) (or a required action) by another admin.
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] }, email: "alice@example.com");

        await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest
        {
            Permissions = ["floor:2:read"]
        });

        Assert.False(realm.LastPutBody!.RootElement.TryGetProperty(field, out _),
            $"PUT body must not carry '{field}'");
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_RemovingTheLastPermission_ClearsTheAttribute()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new()
        {
            ["role"] = ["viewer"],
            ["buildingos_permissions"] = ["floor:1:read"],
        });

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { Permissions = [] });

        Assert.False(realm.LastPutAttributes!.ContainsKey("permissions"));
        Assert.False(realm.LastPutAttributes!.ContainsKey("buildingos_permissions"));
        Assert.Empty(updated.Permissions);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_SendsTokenInAuthHeader()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new());

        await CreateService(realm).UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest());

        Assert.All(realm.AdminAuthorizations, a => Assert.Equal("fake-token", a));
        Assert.NotEmpty(realm.AdminAuthorizations);
    }

    [Fact]
    public async Task SetEnabledAsync_PutsEnabledFlagAndReturnsUpdatedUser_WithOneToken()
    {
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] });

        var updated = await CreateService(realm).SetEnabledAsync("id1", enabled: false);

        Assert.False(realm.LastPutBody!.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(updated.Enabled);
        Assert.Equal(1, realm.TokenRequests);
    }

    [Fact]
    public async Task SetEnabledAsync_UnknownUser_ThrowsUserNotFound()
    {
        await Assert.ThrowsAsync<UserNotFoundException>(() =>
            CreateService(new FakeRealm()).SetEnabledAsync("ghost", enabled: false));
    }

    // ── Realm config pins ────────────────────────────────────────────────────

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


    // ── #532: a group lookup that fails leaves the group role unknown ─────────

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetUsersAsync_GroupLookupFails_StillListsEveryone_WithTheGroupRoleUnresolved(HttpStatusCode status)
    {
        // A service account without query-groups (403) or a transient 5xx must not fail the whole list.
        var realm = new FakeRealm { GroupListStatus = status };
        realm.AddGroup("g-admin", "/admins", role: "admin");
        realm.AddUser("id1", "alice", new() { ["role"] = ["viewer"] });
        realm.AddUser("id2", "bob", new() { ["buildingos_role"] = ["operator"], ["permissions"] = ["floor:1:read"] },
            groups: ["g-admin"]);

        var users = await CreateService(realm).GetUsersAsync();

        Assert.Equal(2, users.Count);
        Assert.Equal("viewer", users[0].Role);
        Assert.False(users[0].GroupRoleUnresolved);
        Assert.Null(users[1].Role); // unknown, not guessed
        Assert.True(users[1].GroupRoleUnresolved);
        // The legacy buildingos_role is hidden by any group role, so it is never the fallback (#532 review).
        Assert.Null(users[1].OwnAttributeRole);
        Assert.Equal(["floor:1:read"], users[1].Permissions);
    }

    [Fact]
    public async Task GetUserByIdAsync_ParentGroupLookupFails_ReturnsTheUser_WithTheGroupRoleUnresolved()
    {
        var realm = new FakeRealm { GroupByIdStatus = HttpStatusCode.ServiceUnavailable };
        realm.AddGroup("g-root", "/ops", role: "admin");
        realm.AddGroup("g-child", "/ops/tokyo", parentId: "g-root");
        realm.AddUser("id1", "alice", new(), groups: ["g-child"]);

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.NotNull(user);
        Assert.Null(user!.Role);
        Assert.True(user.GroupRoleUnresolved);
        Assert.Null(user.OwnAttributeRole);
    }

    [Fact]
    public async Task RoleLookup_GroupLookupFails_FlagsTheGroupRoleUnknown_InsteadOfThrowing()
    {
        var realm = new FakeRealm { GroupListStatus = HttpStatusCode.Forbidden };
        realm.AddUser("id1", "alice", new() { ["role"] = ["admin"] });

        var lookup = await CreateService(realm).CreateRoleLookupAsync();
        var state = await lookup.GetUserAsync("id1", includeGroupRole: true);

        Assert.NotNull(state);
        Assert.Equal("admin", state!.Role);
        Assert.Null(state.GroupRole);
        Assert.True(state.GroupRoleUnknown);
    }

    [Fact]
    public async Task SetEnabledAsync_GroupLookupFails_AfterThePut_StillReturnsTheUser()
    {
        var realm = new FakeRealm { GroupListStatus = HttpStatusCode.InternalServerError };
        realm.AddUser("id1", "alice", new());

        var updated = await CreateService(realm).SetEnabledAsync("id1", false);

        Assert.False(updated.Enabled);
        Assert.True(updated.GroupRoleUnresolved);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_GroupLookupFails_AfterThePut_StillReturnsTheStoredUser()
    {
        var realm = new FakeRealm { GroupListStatus = HttpStatusCode.Forbidden };
        realm.AddUser("id1", "alice", new());

        var updated = await CreateService(realm).UpdateUserAttributesAsync("id1",
            new UpdateUserAttributesRequest { Permissions = ["floor:2:read"] });

        Assert.Equal(["floor:2:read"], updated.Permissions);
        Assert.True(updated.GroupRoleUnresolved);
    }

    [Fact]
    public async Task GetUserByIdAsync_GroupLookupFails_LegacyAdminInAViewerGroup_IsNotReportedAsAdmin()
    {
        // No own role, a viewer group (which would hide the legacy value), and a leftover legacy
        // buildingos_role=admin. With the groups unreadable the legacy value must not surface as the
        // fallback role: that would turn a viewer into an admin (#532 review).
        var realm = new FakeRealm { GroupListStatus = HttpStatusCode.Forbidden };
        realm.AddGroup("g-viewer", "/viewers", role: "viewer");
        realm.AddUser("id1", "alice", new() { ["buildingos_role"] = ["admin"] }, groups: ["g-viewer"]);

        var user = await CreateService(realm).GetUserByIdAsync("id1");

        Assert.NotNull(user);
        Assert.True(user!.GroupRoleUnresolved);
        Assert.Null(user.Role);
        Assert.Null(user.OwnAttributeRole);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_CallerCancelledDuringThePostWriteGroupLookup_ThrowsWrittenUnverified()
    {
        // The PUT and the verifying read succeeded, then the request was cancelled while the groups were
        // being read: the write is committed, so it must surface as written, not as a cancellation (#532).
        using var cts = new CancellationTokenSource();
        var realm = new FakeRealm();
        realm.AddUser("id1", "alice", new() { ["permissions"] = ["floor:1:read"] });
        var service = CreateService(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/groups"))
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
            return realm.Handle(req);
        });

        var ex = await Assert.ThrowsAsync<UserAttributesWrittenUnverifiedException>(() =>
            service.UpdateUserAttributesAsync("id1",
                new UpdateUserAttributesRequest { PermissionsToAdd = ["floor:2:read"] }, cts.Token));

        Assert.Equal(1, realm.Puts);
        Assert.Equal(["floor:1:read", "floor:2:read"], ex.Written.Permissions);
        Assert.True(ex.Written.GroupRoleUnresolved);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    // ── #532: a PUT Keycloak accepted is reported as written, even if it cannot be verified ─

    [Fact]
    public async Task UpdateUserAttributesAsync_VerifyingReadFails_ThrowsWrittenUnverified_WithWhatWasWritten()
    {
        var realm = new FakeRealm { UserGetStatusAfterPut = HttpStatusCode.BadGateway };
        realm.AddUser("id1", "alice", new() { ["permissions"] = ["floor:1:read"] });

        var ex = await Assert.ThrowsAsync<UserAttributesWrittenUnverifiedException>(() =>
            CreateService(realm).UpdateUserAttributesAsync("id1",
                new UpdateUserAttributesRequest { Role = "operator", PermissionsToAdd = ["floor:2:read"] }));

        Assert.Equal(1, realm.Puts);
        Assert.Equal("id1", ex.UserId);
        Assert.Equal("operator", ex.Written.Role);
        Assert.Equal(["floor:1:read", "floor:2:read"], ex.Written.Permissions);
        Assert.Equal("alice", ex.Written.UserPrincipalName);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task UpdateUserAttributesAsync_PutFails_IsNotReportedAsWritten()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("openid-connect/token")) return TokenResponse();
            if (req.Method == HttpMethod.Put) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return JsonResponse(new { id = "id1", username = "alice", attributes = new Dictionary<string, string[]>() });
        });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.UpdateUserAttributesAsync("id1", new UpdateUserAttributesRequest { Permissions = ["floor:2:read"] }));
    }

    [Fact]
    public void Realm_LetsTheAdminApiStoreTheBuildingOsAttributes()
    {
        // Keycloak 24+ drops attributes the user profile does not declare unless the realm allows
        // unmanaged attributes — a PUT of `role` / `permissions` would return 204 and store nothing.
        var realmPath = FindRepoFile(Path.Combine("oss-stack", "keycloak", "realm.json"));
        using var realm = JsonDocument.Parse(File.ReadAllText(realmPath));
        var provider = realm.RootElement.GetProperty("components")
            .GetProperty("org.keycloak.userprofile.UserProfileProvider")[0];
        var config = provider.GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!;
        using var profile = JsonDocument.Parse(config);

        Assert.Equal("ADMIN_EDIT", profile.RootElement.GetProperty("unmanagedAttributePolicy").GetString());
    }

    // === Helpers ===

    private static HttpResponseMessage TokenResponse() =>
        JsonResponse(new { access_token = "fake-token", token_type = "Bearer", expires_in = 300 });

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relative} not found above {AppContext.BaseDirectory}");
    }

    private static HttpResponseMessage JsonResponse(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// A minimal Keycloak Admin API: users (whose PUT replaces the whole attribute map, as Keycloak does),
    /// groups with parents, and group membership.
    /// </summary>
    private sealed class FakeRealm
    {
        private const string Prefix = "/admin/realms/building-os/";

        private sealed class User
        {
            public required string Id;
            public required string Username;
            public string? Email, FirstName, LastName;
            public bool? Enabled;
            public Dictionary<string, string[]> Attributes = new();
            public string[] Groups = [];
        }

        private sealed record Group(string Id, string Path, string? ParentId, string? Role);

        private readonly Dictionary<string, User> _users = new();
        private readonly Dictionary<string, Group> _groups = new();

        /// <summary>Keycloak &lt; 23 does not emit <c>parentId</c>.</summary>
        public bool EmitParentId { get; init; } = true;

        /// <summary>
        /// Simulates a realm without <c>unmanagedAttributePolicy</c>: the PUT answers 204 and stores nothing,
        /// and a GET returns none of the undeclared (role / permission) attributes.
        /// </summary>
        public bool DropAttributeWrites { get; init; }

        /// <summary>Stores multi-valued attributes in another order (Keycloak does not promise one).</summary>
        public bool ReverseStoredValues { get; init; }

        public Dictionary<string, string[]>? LastPutAttributes { get; private set; }
        public JsonDocument? LastPutBody { get; private set; }
        public int TokenRequests { get; private set; }
        public int UserGets { get; private set; }
        public int UserListGets { get; private set; }
        public int Puts { get; private set; }
        public List<string> GroupListGets { get; } = new();
        public List<string> GroupByIdGets { get; } = new();
        public List<string> GroupByPathGets { get; } = new();
        public List<string?> AdminAuthorizations { get; } = new();

        public void AddUser(string id, string username, Dictionary<string, string[]> attributes,
            string? email = null, string? firstName = null, string? lastName = null,
            bool? enabled = true, string[]? groups = null) =>
            _users[id] = new User
            {
                Id = id, Username = username, Email = email, FirstName = firstName, LastName = lastName,
                Enabled = enabled, Attributes = attributes, Groups = groups ?? [],
            };

        public void AddGroup(string id, string path, string? role = null, string? parentId = null) =>
            _groups[id] = new Group(id, path, parentId, role);

        /// <summary>Simulates another admin changing the user right after our PUT (before the re-read).</summary>
        public Action<Dictionary<string, string[]>>? AfterPut { get; init; }

        /// <summary>#532: the status <c>users/{id}/groups</c> answers with instead of the groups (e.g. 403, 500).</summary>
        public HttpStatusCode? GroupListStatus { get; init; }

        /// <summary>#532: the status <c>groups/{id}</c> (the parent walk) answers with instead of the group.</summary>
        public HttpStatusCode? GroupByIdStatus { get; init; }

        /// <summary>#532: the status a user GET answers with once a PUT has been accepted (the verifying re-read).</summary>
        public HttpStatusCode? UserGetStatusAfterPut { get; init; }

        /// <summary>Per-request latency, so concurrent requests actually overlap.</summary>
        public TimeSpan Delay { get; init; }

        public int MaxConcurrentRequests { get; private set; }

        private readonly object _gate = new();
        private int _inFlight;

        public HttpResponseMessage Handle(HttpRequestMessage req)
        {
            var now = Interlocked.Increment(ref _inFlight);
            try
            {
                lock (_gate) MaxConcurrentRequests = Math.Max(MaxConcurrentRequests, now);
                if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
                lock (_gate) return HandleCore(req);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private HttpResponseMessage HandleCore(HttpRequestMessage req)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("openid-connect/token"))
            {
                TokenRequests++;
                return TokenResponse();
            }

            AdminAuthorizations.Add(req.Headers.Authorization?.Parameter);
            Assert.StartsWith(Prefix, path);
            var rest = path[Prefix.Length..];
            var parts = rest.Split('/');

            if (rest.StartsWith("group-by-path/"))
            {
                var groupPath = "/" + Uri.UnescapeDataString(rest["group-by-path/".Length..]);
                GroupByPathGets.Add(groupPath);
                var byPath = _groups.Values.FirstOrDefault(g => g.Path == groupPath);
                return byPath is null ? NotFound() : JsonResponse(GroupJson(byPath));
            }

            if (parts[0] == "groups" && parts.Length == 2)
            {
                GroupByIdGets.Add(parts[1]);
                if (GroupByIdStatus is { } groupStatus) return new HttpResponseMessage(groupStatus);
                return _groups.TryGetValue(parts[1], out var g) ? JsonResponse(GroupJson(g)) : NotFound();
            }

            if (parts[0] != "users") return NotFound();

            if (parts.Length == 1)
            {
                UserListGets++;
                return JsonResponse(_users.Values.Select(UserJson).ToArray());
            }

            if (!_users.TryGetValue(parts[1], out var user)) return NotFound();

            if (parts.Length == 3 && parts[2] == "groups")
            {
                GroupListGets.Add(user.Id);
                Assert.Contains("briefRepresentation=false", req.RequestUri.Query);
                if (GroupListStatus is { } listStatus) return new HttpResponseMessage(listStatus);
                return JsonResponse(user.Groups.Select(id => GroupJson(_groups[id])).ToArray());
            }

            if (req.Method == HttpMethod.Put)
            {
                Puts++;
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                LastPutBody = JsonDocument.Parse(body);
                if (LastPutBody.RootElement.TryGetProperty("attributes", out var attrs))
                {
                    LastPutAttributes = JsonSerializer.Deserialize<Dictionary<string, string[]>>(attrs.GetRawText())!;
                    if (!DropAttributeWrites)
                    {
                        user.Attributes = LastPutAttributes.ToDictionary(
                            kv => kv.Key, kv => ReverseStoredValues ? kv.Value.Reverse().ToArray() : kv.Value);
                        AfterPut?.Invoke(user.Attributes);
                    }
                }
                if (LastPutBody.RootElement.TryGetProperty("enabled", out var enabled))
                    user.Enabled = enabled.GetBoolean();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            UserGets++;
            if (Puts > 0 && UserGetStatusAfterPut is { } afterPut) return new HttpResponseMessage(afterPut);
            return JsonResponse(UserJson(DropAttributeWrites ? WithoutUnmanaged(user) : user));
        }

        private object GroupJson(Group g)
        {
            var node = new JsonObject
            {
                ["id"] = g.Id,
                ["name"] = g.Path[(g.Path.LastIndexOf('/') + 1)..],
                ["path"] = g.Path,
                ["attributes"] = g.Role is null
                    ? new JsonObject()
                    : new JsonObject { ["role"] = new JsonArray(g.Role) },
            };
            if (EmitParentId && g.ParentId is not null) node["parentId"] = g.ParentId;
            return node;
        }

        private static object UserJson(User u)
        {
            var node = new JsonObject
            {
                ["id"] = u.Id,
                ["username"] = u.Username,
                ["email"] = u.Email,
                ["firstName"] = u.FirstName,
                ["lastName"] = u.LastName,
                ["emailVerified"] = true,
                ["createdTimestamp"] = 1700000000000,
                ["requiredActions"] = new JsonArray("UPDATE_PASSWORD"),
                ["attributes"] = JsonSerializer.SerializeToNode(u.Attributes),
            };
            if (u.Enabled is not null) node["enabled"] = u.Enabled.Value;
            return node;
        }

        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

        private static User WithoutUnmanaged(User u) => new()
        {
            Id = u.Id, Username = u.Username, Email = u.Email, FirstName = u.FirstName, LastName = u.LastName,
            Enabled = u.Enabled, Groups = u.Groups,
            Attributes = u.Attributes
                .Where(kv => kv.Key is not ("role" or "permissions" or "buildingos_role" or "buildingos_permissions"))
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }
}

internal sealed class MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(handler(request));
}
