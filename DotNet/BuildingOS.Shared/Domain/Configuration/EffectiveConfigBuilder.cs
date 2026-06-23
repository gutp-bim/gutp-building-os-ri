namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// Pure builder for the effective-config view (#147). Masks secrets to presence-only so a secret's
/// value never reaches the response, regardless of caller.
/// </summary>
public static class EffectiveConfigBuilder
{
    /// <summary>
    /// Builds one entry. <paramref name="rawValue"/> presence drives <c>IsSet</c>; a secret key never
    /// carries its value (the value is dropped even when set).
    /// </summary>
    public static ConfigEntry ToEntry(string key, bool isSecret, string? rawValue)
    {
        var isSet = !string.IsNullOrEmpty(rawValue);
        var value = isSecret ? null : (isSet ? rawValue : null);
        return new ConfigEntry(key, isSecret, isSet, value);
    }

    /// <summary>
    /// Builds the masked config from an allowlist and a lookup over the live configuration. Only the
    /// allowlisted keys are read.
    /// </summary>
    public static EffectiveConfig Build(
        IEnumerable<(string Key, bool IsSecret)> allowlist,
        Func<string, string?> lookup)
    {
        var entries = allowlist
            .Select(item => ToEntry(item.Key, item.IsSecret, lookup(item.Key)))
            .ToList();
        return new EffectiveConfig { Entries = entries };
    }
}
