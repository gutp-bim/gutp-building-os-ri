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
/// <item>role — <see cref="LegacyRole"/> when it has a non-blank value, otherwise <see cref="Role"/>.
/// <c>buildingos_role</c> was only ever written by the admin UI, so it is the latest admin decision; a
/// <c>role</c> next to it may be a stale realm-import / kcadm value, and preferring that would make the
/// next permission-only write persist it and drop the legacy value (a silent demotion or revert);</item>
/// <item>permissions — the union of <see cref="Permissions"/> and <see cref="LegacyPermissions"/>
/// (new values first, duplicates dropped).</item>
/// </list>
/// Any write through the admin UI stores the merged values under the new names and removes the legacy
/// attributes for that user. The token path never sees the legacy attributes — migrate them with the kcadm
/// procedure in <c>docs/operations/keycloak-permission-mapping.md</c>.
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

    /// <summary>
    /// Resolves the user's own role: <see cref="LegacyRole"/> first (the admin UI's last write), then
    /// <see cref="Role"/>.
    /// </summary>
    public static string? ReadRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        (FirstNonEmpty(attributes, LegacyRole) ?? FirstNonEmpty(attributes, Role))?.Trim();

    /// <summary>
    /// Resolves a Keycloak group's role. The token mapper reads only the <see cref="Role"/> attribute
    /// (groups never carried the legacy name), so that is the only one consulted.
    /// </summary>
    public static string? ReadGroupRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        ReadMappedRole(attributes);

    /// <summary>
    /// The role the <c>building-os-role</c> token mapper reads from these attributes: <see cref="Role"/>
    /// only, never <see cref="LegacyRole"/>. Unlike <see cref="ReadRole"/> (the admin UI's view, which
    /// prefers the legacy value), this is what actually reaches the <c>building_os_role</c> claim.
    /// </summary>
    public static string? ReadMappedRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        FirstNonEmpty(attributes, Role)?.Trim();

    /// <summary>The legacy <see cref="LegacyRole"/> value alone (the Admin-API fallback's source).</summary>
    public static string? ReadLegacyRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        FirstNonEmpty(attributes, LegacyRole)?.Trim();

    /// <summary>
    /// Normalizes a requested role: <c>null</c> stays <c>null</c> (keep the current role), a blank value
    /// becomes <c>""</c> (clear the role), anything else is trimmed and must be one of
    /// <see cref="RoleCatalog.AssignableRoles"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The role is not assignable.</exception>
    public static string? NormalizeRole(string? role)
    {
        if (role is null) return null;
        var trimmed = role.Trim();
        if (trimmed.Length == 0) return "";
        if (!RoleCatalog.IsAssignable(trimmed))
        {
            throw new ArgumentException(
                $"Unknown role '{trimmed}'. Assignable roles: {string.Join(", ", RoleCatalog.AssignableRoles)}.",
                nameof(role));
        }
        return trimmed;
    }

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
    /// keeps the user's current (merged) value, a blank role clears it, and the legacy attributes are
    /// always removed.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="role"/> is not assignable (<see cref="NormalizeRole"/>).</exception>
    public static Dictionary<string, string[]> BuildUpdate(
        IReadOnlyDictionary<string, string[]>? current,
        string? role,
        IReadOnlyList<string>? permissions)
    {
        var effectiveRole = NormalizeRole(role) ?? ReadRole(current);
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
