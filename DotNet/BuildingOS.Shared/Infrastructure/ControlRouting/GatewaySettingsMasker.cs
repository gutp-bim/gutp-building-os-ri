namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Pure masking of a gateway's connection <see cref="GatewayConnection.Settings"/> for display on the
/// admin surface (#323). Secret-ish keys (password / secret / credential / token / key) report
/// presence only ("***"); non-secret keys (host / port / tenant / user) pass through. Mirrors the
/// "secrets report presence, not value" rule of the effective-config view (#147).
/// </summary>
public static class GatewaySettingsMasker
{
    private static readonly string[] SecretMarkers =
        { "password", "secret", "credential", "token", "key" };

    public static bool IsSecretKey(string key) =>
        SecretMarkers.Any(m => key.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns a copy of <paramref name="settings"/> with secret values replaced by "***".</summary>
    public static IReadOnlyDictionary<string, string> Mask(IReadOnlyDictionary<string, string> settings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            result[key] = IsSecretKey(key)
                ? (string.IsNullOrEmpty(value) ? "" : "***")
                : value;
        }
        return result;
    }
}
