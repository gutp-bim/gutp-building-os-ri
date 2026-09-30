using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class KeycloakUserAttributesTest
{
    // ── Read precedence: mirror what reaches the token / authorization ───────

    [Fact]
    public void ReadRole_OwnRoleWinsOverLegacy()
    {
        // The building-os-role mapper reads `role` only; a buildingos_role next to it never reaches the
        // token, so /admin must not show (or count) it.
        var attrs = new Dictionary<string, string[]>
        {
            ["role"] = ["viewer"],
            ["buildingos_role"] = ["admin"],
        };

        Assert.Equal("viewer", KeycloakUserAttributes.ReadRole(attrs));
    }

    [Fact]
    public void ReadRole_OwnRoleWinsOverTheGroupRole()
    {
        var attrs = new Dictionary<string, string[]> { ["role"] = ["operator"] };

        Assert.Equal("operator", KeycloakUserAttributes.ReadRole(attrs, groupRole: "admin"));
    }

    [Fact]
    public void ReadRole_NoOwnRole_TheGroupRoleWinsOverLegacy()
    {
        // Without an own `role` the mapper emits the group's value; the legacy attribute is only read
        // by the Admin-API fallback, which never runs when the token carries a role claim.
        var attrs = new Dictionary<string, string[]> { ["buildingos_role"] = ["admin"] };

        Assert.Equal("viewer", KeycloakUserAttributes.ReadRole(attrs, groupRole: "viewer"));
    }

    [Fact]
    public void ReadRole_NeitherOwnNorGroupRole_FallsBackToLegacy()
    {
        var attrs = new Dictionary<string, string[]> { ["buildingos_role"] = ["operator"] };

        Assert.Equal("operator", KeycloakUserAttributes.ReadRole(attrs));
    }

    [Fact]
    public void ReadRole_IsTheFirstValue_Untrimmed()
    {
        // Compared exactly as Keycloak emits it: " admin" is not "admin" to AuthorizationContext.IsAdmin.
        var attrs = new Dictionary<string, string[]> { ["role"] = [" admin", "viewer"] };

        Assert.Equal(" admin", KeycloakUserAttributes.ReadRole(attrs));
    }

    [Fact]
    public void ReadRole_WhitespaceOwnRole_StillShadowsGroupAndLegacy()
    {
        // The mapper emits any present own value, so a whitespace role reaches the token and hides the
        // group value — the user is not an admin, whatever their groups say.
        var attrs = new Dictionary<string, string[]>
        {
            ["role"] = ["  "],
            ["buildingos_role"] = ["admin"],
        };

        Assert.Equal("  ", KeycloakUserAttributes.ReadRole(attrs, groupRole: "admin"));
    }

    [Fact]
    public void ReadRole_EmptyOwnRole_IsAbsent()
    {
        var attrs = new Dictionary<string, string[]>
        {
            ["role"] = [""],
            ["buildingos_role"] = ["operator"],
        };

        Assert.Equal("operator", KeycloakUserAttributes.ReadRole(attrs));
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    [Fact]
    public void BuildUpdate_PermissionOnly_LeavesBothRoleAttributesExactlyAsStored()
    {
        // A permission-only write must never change who the user is: not persist the legacy role into
        // `role` (it would override a group role in the token), not drop it, not rewrite `role`.
        var current = new Dictionary<string, string[]>
        {
            ["role"] = [" admin", "x"],
            ["buildingos_role"] = ["viewer"],
        };

        var result = KeycloakUserAttributes.BuildUpdate(current,
            new UpdateUserAttributesRequest { Permissions = ["floor:1:read"] });

        Assert.Equal([" admin", "x"], result["role"]);
        Assert.Equal(["viewer"], result["buildingos_role"]);
    }

    [Fact]
    public void BuildUpdate_PermissionOnly_LegacyRoleOnly_DoesNotCreateARole()
    {
        var current = new Dictionary<string, string[]> { ["buildingos_role"] = ["admin"] };

        var result = KeycloakUserAttributes.BuildUpdate(current,
            new UpdateUserAttributesRequest { Permissions = ["floor:1:read"] });

        Assert.False(result.ContainsKey("role"));
        Assert.Equal(["admin"], result["buildingos_role"]);
    }

    [Fact]
    public void BuildUpdate_ExplicitRole_SetsRoleAndRemovesLegacyRole()
    {
        var current = new Dictionary<string, string[]>
        {
            ["role"] = ["viewer"],
            ["buildingos_role"] = ["admin"],
        };

        var result = KeycloakUserAttributes.BuildUpdate(current, new UpdateUserAttributesRequest { Role = "operator" });

        Assert.Equal(["operator"], result["role"]);
        Assert.False(result.ContainsKey("buildingos_role"));
    }

    [Fact]
    public void BuildUpdate_EmptyRole_ClearsRoleAndLegacyRole()
    {
        var current = new Dictionary<string, string[]>
        {
            ["role"] = ["viewer"],
            ["buildingos_role"] = ["admin"],
        };

        var result = KeycloakUserAttributes.BuildUpdate(current, new UpdateUserAttributesRequest { Role = "" });

        Assert.False(result.ContainsKey("role"));
        Assert.False(result.ContainsKey("buildingos_role"));
    }

    [Fact]
    public void BuildUpdate_AlwaysMigratesPermissions()
    {
        var current = new Dictionary<string, string[]>
        {
            ["permissions"] = ["floor:2:read"],
            ["buildingos_permissions"] = ["floor:1:read", "floor:2:read"],
        };

        var result = KeycloakUserAttributes.BuildUpdate(current, new UpdateUserAttributesRequest { Role = "viewer" });

        Assert.Equal(["floor:2:read", "floor:1:read"], result["permissions"]);
        Assert.False(result.ContainsKey("buildingos_permissions"));
    }

    [Fact]
    public void BuildUpdate_AddsAndRemovesPermissionsOnTheMergedSet()
    {
        var current = new Dictionary<string, string[]>
        {
            ["permissions"] = ["floor:2:read"],
            ["buildingos_permissions"] = ["floor:1:read"],
        };

        var result = KeycloakUserAttributes.BuildUpdate(current, new UpdateUserAttributesRequest
        {
            PermissionsToAdd = ["floor:3:write", "floor:2:read"],
            PermissionsToRemove = ["floor:1:read"],
        });

        Assert.Equal(["floor:2:read", "floor:3:write"], result["permissions"]);
        Assert.False(result.ContainsKey("buildingos_permissions"));
    }

    [Fact]
    public void BuildUpdate_KeepsUnrelatedAttributes()
    {
        var current = new Dictionary<string, string[]> { ["locale"] = ["ja"] };

        var result = KeycloakUserAttributes.BuildUpdate(current, new UpdateUserAttributesRequest { Permissions = [] });

        Assert.Equal(["ja"], result["locale"]);
        Assert.False(result.ContainsKey("permissions"));
    }

    // ── Persist verification ─────────────────────────────────────────────────

    [Fact]
    public void MatchesStored_IgnoresPermissionOrderAndUnrelatedAttributes()
    {
        var written = new Dictionary<string, string[]>
        {
            ["role"] = ["viewer"],
            ["permissions"] = ["a:1:read", "b:2:read"],
        };
        var stored = new Dictionary<string, string[]>
        {
            ["role"] = ["viewer"],
            ["permissions"] = ["b:2:read", "a:1:read"],
            ["locale"] = ["en"],
        };

        Assert.True(KeycloakUserAttributes.MatchesStored(written, stored));
    }

    [Fact]
    public void MatchesStored_DetectsADroppedAttribute()
    {
        // Keycloak 24+ without unmanagedAttributePolicy answers 204 and stores nothing.
        var written = new Dictionary<string, string[]> { ["role"] = ["viewer"] };

        Assert.False(KeycloakUserAttributes.MatchesStored(written, new Dictionary<string, string[]>()));
        Assert.False(KeycloakUserAttributes.MatchesStored(written, null));
    }

    [Fact]
    public void MatchesStored_DetectsALegacyAttributeThatSurvived()
    {
        var written = new Dictionary<string, string[]> { ["role"] = ["viewer"] };
        var stored = new Dictionary<string, string[]>
        {
            ["role"] = ["viewer"],
            ["buildingos_role"] = ["admin"],
        };

        Assert.False(KeycloakUserAttributes.MatchesStored(written, stored));
    }

    // ── Role validation (single source: the controller calls it) ─────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData(" viewer ", "viewer")]
    public void NormalizeRole_TrimsAndMapsBlankToEmpty(string? input, string? expected)
    {
        Assert.Equal(expected, KeycloakUserAttributes.NormalizeRole(input));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("Admin")]
    public void NormalizeRole_UnknownRole_Throws(string role)
    {
        Assert.Throws<ArgumentException>(() => KeycloakUserAttributes.NormalizeRole(role));
    }
}
