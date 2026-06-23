using System.Text.Json.Nodes;

namespace BuildingOS.Shared.Helpers;

public static partial class JsonObjectExtension
{
    public static bool TryAdd(this JsonObject self, string propertyName, JsonNode? value)
    {
        if (value == null) return false;
        self.Add(propertyName, value);
        return true;
    }
}