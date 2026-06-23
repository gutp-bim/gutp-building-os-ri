using Corvus.Json;

namespace BuildingOS.Shared.Helpers;

public static class ValidationContextExtension
{
    public static string ToLogString(this ValidationContext self) =>
        string.Join(",\n", self.Results.Select(ToLogString));

    public static string ToLogString(this ValidationResult self) =>
        $"{self.Message} at {self.Location?.DocumentLocation ?? "Unknown"}";
}