using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;

namespace BuildingOS.Shared.Test.Domain;

/// <summary>
/// Guards the shipped twin fixture's control schema against the failure mode in #331: a
/// <c>bos:enumLabels</c> literal that is not a JSON object silently disables enum validation
/// (<see cref="ControlValueValidator"/> catches the parse failure and falls back to permissive),
/// so a malformed fixture produces no error anywhere — it just stops enforcing.
///
/// This matters beyond the fixture itself: <c>fixtures/e2e/twin.ttl</c> is the default value of
/// <c>OXIGRAPH_SEED_TTL_PATH</c> in docker-compose.oss.yaml, so it seeds every default stack, and
/// it is the only .ttl in the repository carrying <c>bos:enumLabels</c> — i.e. the one worked
/// example a newcomer copies from.
/// </summary>
public class ShippedTwinFixtureControlSchemaTest
{
    // bos:enumLabels "<turtle string literal>" — captures the escaped literal body.
    private static readonly Regex EnumLabelsLiteral = new(
        "bos:enumLabels\\s+\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    [Fact]
    public void ShippedTwinFixture_EnumLabels_AreParseableByTheValidator()
    {
        var labels = EnumLabelsFromTwinFixture();

        Assert.True(
            labels.Count > 0,
            "Expected fixtures/e2e/twin.ttl to declare at least one bos:enumLabels literal; "
            + "if the fixture legitimately dropped its enum point, delete this test with it.");

        foreach (var value in labels)
        {
            var schema = new ControlSchema { DataType = "enum", EnumLabels = value };

            // A code outside the declared set must be rejected. With a non-JSON literal the
            // validator cannot derive the allowed set and silently accepts everything (#331).
            Assert.False(
                ControlValueValidator.Validate(schema, 99_999).IsValid,
                $"bos:enumLabels literal {value} does not constrain the control value. It must be a "
                + "JSON object keyed by the allowed numeric codes, e.g. {\"1\":\"Off\",\"2\":\"Low\"} — "
                + "a \"&&\"-delimited string parses as nothing and disables enum validation entirely.");
        }
    }

    [Fact]
    public void ShippedPointListSnapshot_MatchesTheTwinFixtureEnumLabels()
    {
        // fixtures/e2e/pointlist.json is the expected GET /gateways/{id}/pointlist response for the
        // same dataset, and the API passes the literal through verbatim — so the two must agree, or
        // the snapshot stops being a usable expectation.
        var fromTwin = EnumLabelsFromTwinFixture();
        var fromSnapshot = EnumLabelsFromPointListSnapshot();

        Assert.Equal(fromTwin.OrderBy(v => v, StringComparer.Ordinal), fromSnapshot.OrderBy(v => v, StringComparer.Ordinal));
    }

    private static List<string> EnumLabelsFromTwinFixture()
    {
        var ttl = File.ReadAllText(FixturePath("twin.ttl"));
        return EnumLabelsLiteral.Matches(ttl)
            .Select(m => UnescapeTurtleLiteral(m.Groups[1].Value))
            .ToList();
    }

    private static List<string> EnumLabelsFromPointListSnapshot()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath("pointlist.json")));
        return doc.RootElement.GetProperty("points").EnumerateArray()
            .Select(p => p.TryGetProperty("controlSchema", out var schema) ? schema : default)
            .Where(schema => schema.ValueKind == JsonValueKind.Object)
            .Select(schema => schema.TryGetProperty("enumLabels", out var labels) ? labels.GetString() : null)
            .Where(labels => !string.IsNullOrEmpty(labels))
            .Select(labels => labels!)
            .ToList();
    }

    /// <summary>Turtle escapes embedded quotes and backslashes; recover the literal the store holds.</summary>
    private static string UnescapeTurtleLiteral(string value)
        => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>
    /// Walks up from the test binary to the repository root (identified by the fixtures directory),
    /// so the test does not depend on the build output layout.
    /// </summary>
    private static string FixturePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", "e2e", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate fixtures/e2e/{fileName} above {AppContext.BaseDirectory}");
    }
}
