using BuildingOS.Shared.Domain.UserManagement;

namespace BuildingOS.Shared.Test.Domain.UserManagement;

public class KeycloakUserAttributesTest
{
    // ── Dual-read precedence ─────────────────────────────────────────────────

    [Fact]
    public void ReadRole_BothPresent_LegacyWins()
    {
        // buildingos_role was only ever written by the /admin UI, so it is the latest admin
        // decision; `role` alongside it may be a stale realm-import / kcadm value. Preferring `role`
        // would make the next permission-only write persist the stale value and drop the legacy one.
        var attrs = new Dictionary<string, string[]>
        {
            ["role"] = ["admin"],
            ["buildingos_role"] = ["viewer"],
        };

        Assert.Equal("viewer", KeycloakUserAttributes.ReadRole(attrs));
    }

    [Fact]
    public void ReadRole_BlankLegacy_FallsBackToNew()
    {
        var attrs = new Dictionary<string, string[]>
        {
            ["role"] = ["operator"],
            ["buildingos_role"] = [" "],
        };

        Assert.Equal("operator", KeycloakUserAttributes.ReadRole(attrs));
    }

    [Fact]
    public void BuildUpdate_PermissionOnly_PersistsTheLegacyRoleWhenBothPresent()
    {
        var current = new Dictionary<string, string[]>
        {
            ["role"] = ["admin"],
            ["buildingos_role"] = ["viewer"],
        };

        var result = KeycloakUserAttributes.BuildUpdate(current, role: null, permissions: ["floor:1:read"]);

        Assert.Equal(["viewer"], result["role"]);
        Assert.False(result.ContainsKey("buildingos_role"));
    }

    // ── Role validation ──────────────────────────────────────────────────────

    [Fact]
    public void BuildUpdate_TrimsTheRole()
    {
        var result = KeycloakUserAttributes.BuildUpdate(null, role: " operator ", permissions: null);

        Assert.Equal(["operator"], result["role"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildUpdate_BlankRole_ClearsTheRole(string role)
    {
        // Whitespace must not become the building_os_role claim; it means "no role", like "".
        var current = new Dictionary<string, string[]> { ["role"] = ["viewer"] };

        var result = KeycloakUserAttributes.BuildUpdate(current, role, permissions: null);

        Assert.False(result.ContainsKey("role"));
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("Admin")]
    public void BuildUpdate_UnknownRole_IsRejected(string role)
    {
        Assert.Throws<ArgumentException>(() => KeycloakUserAttributes.BuildUpdate(null, role, permissions: null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData(" viewer ", "viewer")]
    public void NormalizeRole_TrimsAndMapsBlankToEmpty(string? input, string? expected)
    {
        Assert.Equal(expected, KeycloakUserAttributes.NormalizeRole(input));
    }

    [Fact]
    public void NormalizeRole_UnknownRole_Throws()
    {
        Assert.Throws<ArgumentException>(() => KeycloakUserAttributes.NormalizeRole("root"));
    }
}
