namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>
/// The Keycloak user attributes that carry a user's Building OS role and permissions — the one
/// definition of their names (#519).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Role"/> / <see cref="Permissions"/> are the attributes the realm's <c>building-os-api</c>
/// client-scope mappers put into the access token (<c>building_os_role</c> / <c>permissions</c> claims,
/// <c>oss-stack/keycloak/realm.json</c>). The admin UI must write these: a grant stored under any other
/// name never reaches the token, and <see cref="Permissions"/> is also the attribute that is aggregated
/// with every group's <c>permissions</c> (#508).
/// </para>
/// <para>
/// <see cref="LegacyRole"/> / <see cref="LegacyPermissions"/> (<c>buildingos_*</c>) are what the admin UI
/// wrote before #519. They are still <b>read</b> during the migration period so existing users keep their
/// grants on the Admin-API path, with this precedence:
/// <list type="bullet">
/// <item>role — <see cref="Role"/> when it has a non-empty value, otherwise <see cref="LegacyRole"/>;</item>
/// <item>permissions — the union of <see cref="Permissions"/> and <see cref="LegacyPermissions"/>
/// (new values first, duplicates dropped).</item>
/// </list>
/// Any write through the admin UI stores the merged values under the new names and removes the legacy
/// attributes for that user. The token path never sees the legacy attributes — migrate them with the kcadm
/// procedure in <c>docs/guides/keycloak-user-management.md</c>.
/// </para>
/// </remarks>
public static class KeycloakUserAttributes
{
    /// <summary>Single-valued role (<c>admin</c> / <c>operator</c> / <c>viewer</c>) → <c>building_os_role</c> claim.</summary>
    public const string Role = "role";

    /// <summary>Multi-valued permission strings → <c>permissions</c> claim (aggregated with groups).</summary>
    public const string Permissions = "permissions";

    /// <summary>Pre-#519 role attribute written by the admin UI. Read-only fallback; cleared on write.</summary>
    public const string LegacyRole = "buildingos_role";

    /// <summary>Pre-#519 permissions attribute written by the admin UI. Merged on read; cleared on write.</summary>
    public const string LegacyPermissions = "buildingos_permissions";

    /// <summary>Resolves the effective role: <see cref="Role"/> first, then <see cref="LegacyRole"/>.</summary>
    public static string? ReadRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        FirstNonEmpty(attributes, Role) ?? FirstNonEmpty(attributes, LegacyRole);

    /// <summary>Resolves the effective permissions: <see cref="Permissions"/> ∪ <see cref="LegacyPermissions"/>.</summary>
    public static IReadOnlyList<string> ReadPermissions(IReadOnlyDictionary<string, string[]>? attributes) =>
        Values(attributes, Permissions)
            .Concat(Values(attributes, LegacyPermissions))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Builds the full attribute map to PUT back to Keycloak. Keycloak replaces the whole map on update,
    /// so every attribute that is not ours is carried over unchanged. A <c>null</c> role / permissions
    /// keeps the user's current (merged) value; the legacy attributes are always removed.
    /// </summary>
    public static Dictionary<string, string[]> BuildUpdate(
        IReadOnlyDictionary<string, string[]>? current,
        string? role,
        IReadOnlyList<string>? permissions)
    {
        var effectiveRole = role ?? ReadRole(current);
        var effectivePermissions = permissions ?? ReadPermissions(current);

        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (current != null)
        {
            foreach (var (key, value) in current)
            {
                if (key is Role or Permissions or LegacyRole or LegacyPermissions) continue;
                result[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(effectiveRole))
            result[Role] = [effectiveRole];

        var cleanPermissions = effectivePermissions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (cleanPermissions.Length > 0)
            result[Permissions] = cleanPermissions;

        return result;
    }

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string[]>? attributes, string key) =>
        Values(attributes, key).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static IEnumerable<string> Values(IReadOnlyDictionary<string, string[]>? attributes, string key) =>
        attributes != null && attributes.TryGetValue(key, out var values) && values != null ? values : [];
}
