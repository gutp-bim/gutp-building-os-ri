using System.Text.Json;

namespace ParityHarness.Core;

public static class JsonDiff
{
    public static IReadOnlyList<FieldDiff> Compare(string expected, string actual)
    {
        var diffs = new List<FieldDiff>();
        using var expectedDoc = JsonDocument.Parse(expected);
        using var actualDoc = JsonDocument.Parse(actual);
        CompareElements(expectedDoc.RootElement, actualDoc.RootElement, "", diffs);
        return diffs;
    }

    private static void CompareElements(JsonElement expected, JsonElement actual, string path, List<FieldDiff> diffs)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            diffs.Add(new FieldDiff(path, Stringify(expected), Stringify(actual), DiffType.TypeMismatch));
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in expected.EnumerateObject())
                {
                    var childPath = BuildPath(path, prop.Name);
                    if (actual.TryGetProperty(prop.Name, out var actualProp))
                        CompareElements(prop.Value, actualProp, childPath, diffs);
                    else
                        diffs.Add(new FieldDiff(childPath, Stringify(prop.Value), null, DiffType.Missing));
                }
                foreach (var prop in actual.EnumerateObject())
                {
                    if (!expected.TryGetProperty(prop.Name, out _))
                        diffs.Add(new FieldDiff(BuildPath(path, prop.Name), null, Stringify(prop.Value), DiffType.Extra));
                }
                break;

            case JsonValueKind.Array:
                var expArr = expected.EnumerateArray().ToList();
                var actArr = actual.EnumerateArray().ToList();
                if (expArr.Count != actArr.Count)
                    diffs.Add(new FieldDiff(path, $"length={expArr.Count}", $"length={actArr.Count}", DiffType.LengthMismatch));
                for (var i = 0; i < Math.Min(expArr.Count, actArr.Count); i++)
                    CompareElements(expArr[i], actArr[i], $"{path}[{i}]", diffs);
                break;

            default:
                var expStr = Stringify(expected);
                var actStr = Stringify(actual);
                if (expStr != actStr)
                    diffs.Add(new FieldDiff(path, expStr, actStr, DiffType.ValueMismatch));
                break;
        }
    }

    private static string BuildPath(string parent, string key) =>
        string.IsNullOrEmpty(parent) ? key : $"{parent}.{key}";

    private static string Stringify(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "null",
        JsonValueKind.Null => "null",
        _ => el.GetRawText(),
    };
}
