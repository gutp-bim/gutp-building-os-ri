namespace BuildingOS.Shared.Domain.Authorization;

/// <summary>How a client-credentials token (<c>idtyp=app</c>) is authorized (#667).</summary>
public enum ClientCredentialsRolePolicy
{
    /// <summary>
    /// The token gets the <c>building_os_role</c> / <c>permissions</c> it carries, exactly like a user
    /// token; without a role it gets nothing. Admin only with an explicit <c>building_os_role=admin</c>.
    /// </summary>
    Explicit,

    /// <summary>
    /// The pre-#667 behaviour: every client-credentials token is admin unless it carries exactly
    /// <c>building_os_role=group-manager</c>. A migration aid for deployments whose clients have no role yet.
    /// </summary>
    LegacyAdmin,
}

/// <summary>Client-credentials authorization setting of the API server (<c>CLIENT_CREDENTIALS_ROLE_POLICY</c>).</summary>
public sealed record ClientCredentialsRoleOptions(ClientCredentialsRolePolicy Policy)
{
    public const string EnvironmentVariable = "CLIENT_CREDENTIALS_ROLE_POLICY";

    /// <summary>The fail-closed default.</summary>
    public static readonly ClientCredentialsRoleOptions Default = new(ClientCredentialsRolePolicy.Explicit);

    /// <summary>
    /// <c>legacy-admin</c> → <see cref="ClientCredentialsRolePolicy.LegacyAdmin"/>; unset, empty or
    /// <c>explicit</c> → <see cref="ClientCredentialsRolePolicy.Explicit"/>. Any other value also yields
    /// Explicit — a typo must never re-open admin-by-default — with <paramref name="recognized"/> false so
    /// the caller can say the value did not take effect.
    /// </summary>
    public static ClientCredentialsRolePolicy ParsePolicy(string? raw, out bool recognized)
    {
        var value = raw?.Trim() ?? string.Empty;
        recognized = true;
        if (value.Equals("legacy-admin", StringComparison.OrdinalIgnoreCase)) return ClientCredentialsRolePolicy.LegacyAdmin;
        if (value.Length == 0 || value.Equals("explicit", StringComparison.OrdinalIgnoreCase)) return ClientCredentialsRolePolicy.Explicit;
        recognized = false;
        return ClientCredentialsRolePolicy.Explicit;
    }
}
