using System.Globalization;

namespace BuildingOS.Shared.Domain.ControlFeedback;

/// <summary>What the feedback point should read once a control took effect (#637).</summary>
public sealed record ControlExpectedValue(double Value, double Tolerance)
{
    /// <summary>Whether <paramref name="actual"/> counts as the expected value.</summary>
    public bool Matches(double actual) => double.IsFinite(actual) && Math.Abs(actual - Value) <= Tolerance + Epsilon(Value);

    // Absorbs binary rounding (0.1 + 0.2) without loosening a deliberate tolerance in any meaningful way.
    private static double Epsilon(double v) => Math.Max(1e-9, Math.Abs(v) * 1e-12);
}

/// <summary>
/// Derives the expected feedback value from the control itself (#637, pure). The ControlSchema decides how the
/// comparison works: a <c>number</c> is compared within the configured tolerance, a <c>boolean</c> (0/1) or an
/// <c>enum</c> code exactly. A release point (#600) has no expected value — after release the target goes back to
/// whatever the lower priorities say, which is not something the request names.
/// </summary>
public static class ControlExpectedValueResolver
{
    public static ControlExpectedValue? Resolve(
        ControlSchema? schema, double requestedValue, double tolerance, bool isReleasePoint)
    {
        if (isReleasePoint || !double.IsFinite(requestedValue)) return null;
        var type = (schema?.DataType ?? string.Empty).Trim().ToLowerInvariant();
        return type switch
        {
            "number" => new ControlExpectedValue(requestedValue, Math.Max(0, double.IsFinite(tolerance) ? tolerance : 0)),
            "boolean" when requestedValue is 0 or 1 => new ControlExpectedValue(requestedValue, 0),
            "enum" => new ControlExpectedValue(requestedValue, 0),
            // No (usable) schema: the request is all there is; compare exactly rather than guess a tolerance.
            "" => new ControlExpectedValue(requestedValue, 0),
            _ => null,
        };
    }

    /// <summary>The feedback value as text for the audit and the client (invariant culture, round-trippable).</summary>
    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
