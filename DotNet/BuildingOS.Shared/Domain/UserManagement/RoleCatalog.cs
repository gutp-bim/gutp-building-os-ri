namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>
/// One assignable Building OS role and what it grants. This is the backend single source of truth
/// for the fixed roles (admin / operator / viewer, plus the UI-less group-manager of #506); it mirrors the frontend
/// <c>lib/auth/workspaces.ts</c> role→workspace map. Roles are an authorization concept stored as
/// the Keycloak <c>role</c> user attribute (<see cref="KeycloakUserAttributes.Role"/>, #519) — not
/// Keycloak realm roles — and are not dynamic.
/// </summary>
public sealed record RoleCatalogEntry(
    string Role,
    bool IsAdmin,
    IReadOnlyList<string> Workspaces,
    string Description);

/// <summary>Pure read-only catalog of the assignable roles (no I/O, deterministic).</summary>
public static class RoleCatalog
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";
    public const string GroupManager = "group-manager";

    /// <summary>The assignable roles in display order (highest privilege first).</summary>
    public static readonly IReadOnlyList<RoleCatalogEntry> Entries = new[]
    {
        new RoleCatalogEntry(
            Admin, IsAdmin: true,
            Workspaces: new[] { "operator", "admin", "platform" },
            Description: "全権管理者。運用・管理・プラットフォームの全ワークスペースを閲覧でき、ユーザー/ロール/設定を変更できる。"),
        new RoleCatalogEntry(
            Operator, IsAdmin: false,
            Workspaces: new[] { "operator" },
            Description: "運用担当。建物リソースの閲覧と、権限が付与された範囲での制御を行う。管理機能は不可。"),
        new RoleCatalogEntry(
            Viewer, IsAdmin: false,
            Workspaces: new[] { "operator" },
            Description: "閲覧のみ。運用ワークスペースを参照できるが制御・管理はできない。"),
        new RoleCatalogEntry(
            GroupManager, IsAdmin: false,
            Workspaces: Array.Empty<string>(),
            Description: "外部アプリケーションのサービスアカウント用（#506）。Group の作成・変更・削除と、Group に入れるリソースを選ぶためのツイン構造の閲覧だけができる。テレメトリ・制御・ツイン変更・ユーザー管理は不可。画面は持たない。"),
    };

    /// <summary>The set of assignable role names (case-sensitive, lowercase).</summary>
    public static readonly IReadOnlySet<string> AssignableRoles =
        new HashSet<string>(Entries.Select(e => e.Role), StringComparer.Ordinal);

    /// <summary>True if <paramref name="role"/> is one of the assignable roles.</summary>
    public static bool IsAssignable(string? role) =>
        role is not null && AssignableRoles.Contains(role);

    /// <summary>
    /// The least privileged of <paramref name="roles"/> (the first such, in order): an unknown role
    /// (grants nothing) &lt; viewer &lt; operator &lt; group-manager &lt; admin. What authorization falls
    /// back to when a user's groups disagree on the role (#506).
    /// </summary>
    public static string? LeastPrivileged(IEnumerable<string> roles)
        => roles.Select((r, i) => (Role: r, Index: i))
            .OrderBy(x => Rank(x.Role)).ThenBy(x => x.Index)
            .Select(x => x.Role)
            .FirstOrDefault();

    private static int Rank(string role) => role switch
    {
        Viewer => 1,
        Operator => 2,
        GroupManager => 3,
        Admin => 4,
        _ => 0,
    };

    /// <summary>True if the role grants admin privileges (only <c>admin</c> does).</summary>
    public static bool GrantsAdmin(string? role) =>
        string.Equals(role, Admin, StringComparison.Ordinal);
}
