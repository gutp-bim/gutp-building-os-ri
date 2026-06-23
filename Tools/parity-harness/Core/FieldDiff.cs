namespace ParityHarness.Core;

public enum DiffType
{
    ValueMismatch,
    TypeMismatch,
    Missing,
    Extra,
    LengthMismatch,
}

public record FieldDiff(string Path, string? Expected, string? Actual, DiffType Type);
