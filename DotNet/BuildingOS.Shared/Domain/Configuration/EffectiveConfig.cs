namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>
/// One effective-configuration key for the read-only config view (#147). Secret values are never
/// carried over the wire — <see cref="Value"/> is always <c>null</c> for secrets and only
/// <see cref="IsSet"/> reports whether a value is present.
/// </summary>
public sealed record ConfigEntry(string Key, bool IsSecret, bool IsSet, string? Value);

/// <summary>The API server's effective configuration as a flat, masked list.</summary>
public sealed record EffectiveConfig
{
    public IReadOnlyList<ConfigEntry> Entries { get; init; } = [];
}
