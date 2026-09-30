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
/// wrote before #519. During the migration period:
/// <list type="bullet">
/// <item>role — what /admin shows mirrors what reaches authorization: the user's own <see cref="Role"/>
/// (its first value, exactly as stored — the non-aggregating mapper emits any present value, even a
/// whitespace one, and it shadows the groups), else the role inherited from a group, else
/// <see cref="LegacyRole"/> (read only by the Admin-API fallback, which runs only when the token carries no
/// role claim, i.e. with neither an own nor a group role). A write that does not set the role leaves both
/// role attributes exactly as stored; only an explicit role write sets <see cref="Role"/> and removes
/// <see cref="LegacyRole"/>;</item>
/// <item>permissions — the union of <see cref="Permissions"/> and <see cref="LegacyPermissions"/>
/// (new values first, duplicates dropped). Any write stores the merged set in <see cref="Permissions"/> and
/// removes <see cref="LegacyPermissions"/>.</item>
/// </list>
/// The token path never sees the legacy attributes — migrate them with the kcadm procedure in
/// <c>docs/operations/keycloak-permission-mapping.md</c>.
/// </para>
/// </remarks>
public static class KeycloakUserAttributes
{
    /// <summary>Single-valued role (<c>admin</c> / <c>operator</c> / <c>viewer</c>) → <c>building_os_role</c> claim.</summary>
    public const string Role = "role";

    /// <summary>Multi-valued permission strings → <c>permissions</c> claim (aggregated with groups).</summary>
    public const string Permissions = "permissions";

    /// <summary>Pre-#519 role attribute written by the admin UI. Read by the Admin-API fallback only.</summary>
    public const string LegacyRole = "buildingos_role";

    /// <summary>Pre-#519 permissions attribute written by the admin UI. Merged on read; cleared on write.</summary>
    public const string LegacyPermissions = "buildingos_permissions";

    /// <summary>
    /// The role that reaches authorization: own <see cref="Role"/>, else <paramref name="groupRole"/>, else
    /// <see cref="LegacyRole"/>. Nothing is trimmed — <c>AuthorizationContext.IsAdmin</c> compares the claim
    /// exactly.
    /// </summary>
    public static string? ReadRole(IReadOnlyDictionary<string, string[]>? attributes, string? groupRole = null) =>
        ResolveRole(ReadMappedRole(attributes), groupRole, ReadLegacyRole(attributes));

    /// <summary>Own role, else group role, else legacy role — the order the token + Admin-API fallback apply.</summary>
    public static string? ResolveRole(string? ownRole, string? groupRole, string? legacyRole) =>
        !string.IsNullOrEmpty(ownRole) ? ownRole
        : !string.IsNullOrEmpty(groupRole) ? groupRole
        : string.IsNullOrEmpty(legacyRole) ? null : legacyRole;

    /// <summary>
    /// The role the <c>building-os-role</c> token mapper reads from these attributes: the first value of
    /// <see cref="Role"/>, untrimmed (an empty value counts as absent). Used for a user's own attributes
    /// and for a group's (groups never carried the legacy name).
    /// </summary>
    public static string? ReadMappedRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        FirstValue(attributes, Role);

    /// <summary>The legacy <see cref="LegacyRole"/> value alone (the Admin-API fallback's source), untrimmed.</summary>
    public static string? ReadLegacyRole(IReadOnlyDictionary<string, string[]>? attributes) =>
        FirstValue(attributes, LegacyRole);

    /// <summary>
    /// Validates and normalizes a requested role — the single place a role write is validated (the
    /// controller calls it before the lockout guard and the write). <c>null</c> stays <c>null</c> (keep the
    /// current role), a blank value becomes <c>""</c> (clear the role), anything else is trimmed and must
    /// be one of <see cref="RoleCatalog.AssignableRoles"/>.
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
    /// so every attribute that is not a permission attribute is carried over unchanged — including
    /// <see cref="Role"/> / <see cref="LegacyRole"/> when <see cref="UpdateUserAttributesRequest.Role"/> is
    /// <c>null</c>. A non-null role (already normalized by <see cref="NormalizeRole"/>) replaces
    /// <see cref="Role"/> (<c>""</c> clears it) and removes <see cref="LegacyRole"/>. Permissions are
    /// <see cref="UpdateUserAttributesRequest.Permissions"/> (or the current merged set), then
    /// <see cref="UpdateUserAttributesRequest.PermissionsToAdd"/> /
    /// <see cref="UpdateUserAttributesRequest.PermissionsToRemove"/>; they are always written to
    /// <see cref="Permissions"/> and <see cref="LegacyPermissions"/> is removed.
    /// </summary>
    public static Dictionary<string, string[]> BuildUpdate(
        IReadOnlyDictionary<string, string[]>? current,
        UpdateUserAttributesRequest request)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (current != null)
        {
            foreach (var (key, value) in current)
            {
                if (key is Permissions or LegacyPermissions) continue;
                if (request.Role != null && key is Role or LegacyRole) continue;
                result[key] = value;
            }
        }

        if (!string.IsNullOrEmpty(request.Role))
            result[Role] = [request.Role];

        var remove = new HashSet<string>(request.PermissionsToRemove ?? [], StringComparer.Ordinal);
        var permissions = (request.Permissions ?? ReadPermissions(current))
            .Concat(request.PermissionsToAdd ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && !remove.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (permissions.Length > 0)
            result[Permissions] = permissions;

        return result;
    }

    /// <summary>
    /// Whether the re-read after a PUT shows the write landed at all. Keycloak 24+ without
    /// <c>unmanagedAttributePolicy</c> answers the PUT with 204, stores nothing, and returns none of the
    /// undeclared attributes — so a write whose every non-empty <see cref="Role"/> / <see cref="Permissions"/>
    /// key is absent on re-read was dropped. Values are deliberately <b>not</b> compared: another admin
    /// writing the same user between the PUT and the re-read changes them legitimately, and reporting
    /// that as "not persisted" would be a false failure. Consequently this cannot detect a realm that
    /// keeps showing old values while ignoring writes (e.g. <c>ADMIN_VIEW</c>), nor a write that only
    /// cleared both attributes (nothing to look for).
    /// </summary>
    public static bool LooksPersisted(
        IReadOnlyDictionary<string, string[]> written,
        IReadOnlyDictionary<string, string[]>? stored)
    {
        var writtenKeys = new[] { Role, Permissions }.Where(k => Values(written, k).Any()).ToList();
        return writtenKeys.Count == 0 || writtenKeys.Any(k => Values(stored, k).Any());
    }

    /// <summary>The Building OS attribute values (role / permissions, new and legacy) out of a full map.</summary>
    public static IReadOnlyDictionary<string, string[]> Pick(IReadOnlyDictionary<string, string[]>? attributes) =>
        new[] { Role, Permissions, LegacyRole, LegacyPermissions }
            .Where(k => Values(attributes, k).Any())
            .ToDictionary(k => k, k => Values(attributes, k).ToArray(), StringComparer.Ordinal);

    private static string? FirstValue(IReadOnlyDictionary<string, string[]>? attributes, string key)
    {
        var first = Values(attributes, key).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    private static IEnumerable<string> Values(IReadOnlyDictionary<string, string[]>? attributes, string key) =>
        attributes != null && attributes.TryGetValue(key, out var values) && values != null ? values : [];
}
