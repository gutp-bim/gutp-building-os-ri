namespace BuildingOS.Shared.Domain;

/// <summary>What a control write does when the point's control schema cannot constrain the value (#481).</summary>
public enum ControlSchemaFailurePolicy
{
    /// <summary>Skip value validation and send the write (fail-open, the historical behaviour).</summary>
    Allow,

    /// <summary>Refuse the write with 400 (fail-closed).</summary>
    Deny,
}

/// <summary>Control-write safety settings of the API server (<c>CONTROL_SCHEMA_FAILURE_POLICY</c>).</summary>
public sealed record ControlSafetyOptions(ControlSchemaFailurePolicy OnSchemaResolutionFailure)
{
    public const string EnvironmentVariable = "CONTROL_SCHEMA_FAILURE_POLICY";

    /// <summary>
    /// <c>deny</c> → <see cref="ControlSchemaFailurePolicy.Deny"/>; unset, empty or <c>allow</c> →
    /// <see cref="ControlSchemaFailurePolicy.Allow"/>. Any other value also yields Allow with
    /// <paramref name="recognized"/> false, so the caller can warn that a typo did not take effect.
    /// </summary>
    public static ControlSchemaFailurePolicy ParsePolicy(string? raw, out bool recognized)
    {
        var value = raw?.Trim() ?? string.Empty;
        recognized = true;
        if (value.Equals("deny", StringComparison.OrdinalIgnoreCase)) return ControlSchemaFailurePolicy.Deny;
        if (value.Length == 0 || value.Equals("allow", StringComparison.OrdinalIgnoreCase)) return ControlSchemaFailurePolicy.Allow;
        recognized = false;
        return ControlSchemaFailurePolicy.Allow;
    }
}
