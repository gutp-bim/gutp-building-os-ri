using System.Text.RegularExpressions;

namespace BuildingOS.Shared.Domain.TwinAdmin;

/// <summary>
/// Pure guard that decides whether a SPARQL string is a safe read-only query for the admin SPARQL
/// console (#322). Only <c>SELECT</c> / <c>ASK</c> are allowed; any update/mutation form
/// (<c>INSERT</c>/<c>DELETE</c>/<c>DROP</c>/<c>CLEAR</c>/<c>LOAD</c>/<c>CREATE</c>/<c>ADD</c>/
/// <c>MOVE</c>/<c>COPY</c>) is rejected. Comments and PREFIX/BASE declarations are stripped first;
/// matching is whole-word and case-insensitive. The guard errs toward rejection (a query body that
/// merely mentions a forbidden keyword as a token is refused) because it protects a destructive
/// boundary.
/// </summary>
public static class SparqlReadOnlyGuard
{
    private static readonly string[] ForbiddenKeywords =
    {
        "INSERT", "DELETE", "DROP", "CLEAR", "LOAD", "CREATE", "ADD", "MOVE", "COPY",
    };

    private static readonly Regex LineComment = new(@"#[^\n\r]*", RegexOptions.Compiled);
    private static readonly Regex PrefixOrBase = new(
        @"(?im)^\s*(PREFIX\s+[^:\s]*:\s*<[^>]*>|BASE\s*<[^>]*>)\s*", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[A-Za-z]+", RegexOptions.Compiled);

    public static bool IsReadOnly(string? sparql) => Validate(sparql).Allowed;

    /// <summary>Validate and return the decision plus a reason when rejected.</summary>
    public static (bool Allowed, string? Reason) Validate(string? sparql)
    {
        if (string.IsNullOrWhiteSpace(sparql))
        {
            return (false, "クエリが空です。");
        }

        // Strip line comments, then leading PREFIX/BASE declarations, so the body starts at the form.
        var body = PrefixOrBase.Replace(LineComment.Replace(sparql, " "), " ").TrimStart();

        var words = Word.Matches(body).Select(m => m.Value).ToList();
        var form = words.FirstOrDefault()?.ToUpperInvariant();
        if (form is not ("SELECT" or "ASK"))
        {
            return (false, "SELECT / ASK のみ実行できます（読み取り専用）。");
        }

        // Defense in depth: reject if any update keyword appears as a whole word in the body.
        var forbidden = words.FirstOrDefault(w => ForbiddenKeywords.Contains(w, StringComparer.OrdinalIgnoreCase));
        if (forbidden is not null)
        {
            return (false, $"更新系キーワード '{forbidden.ToUpperInvariant()}' は使用できません。");
        }

        return (true, null);
    }
}
