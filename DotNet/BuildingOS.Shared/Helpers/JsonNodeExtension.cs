using System.Text.Json.Nodes;

namespace BuildingOS.Shared.Helpers;

public static partial class JsonNodeExtension
{
    public static bool TryGetValue<T>(this JsonNode self, out T? value) => self.AsValue().TryGetValue(out value);
}