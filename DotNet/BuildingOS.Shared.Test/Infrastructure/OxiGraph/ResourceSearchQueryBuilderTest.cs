using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Pure tests for the SPARQL builder behind the cross-resource search endpoint (/resources/search).
/// Asserts on query fragments rather than executing against OxiGraph.
/// </summary>
public class ResourceSearchQueryBuilderTest
{
    private const string Building = "https://www.sbco.or.jp/ont/Building";
    private const string Level = "https://www.sbco.or.jp/ont/Level";
    private const string Room = "https://www.sbco.or.jp/ont/Room";
    private const string Equipment = "https://www.sbco.or.jp/ont/EquipmentExt";
    private const string Point = "https://www.sbco.or.jp/ont/PointExt";
    private const string HasPart = "https://www.sbco.or.jp/ont/hasPart";
    private const string CustomTags = "https://www.sbco.or.jp/ont/customTags";
    private const string KeyBoolMapEntry = "https://www.sbco.or.jp/ont/KeyBoolMapEntry";
    private const string Key = "https://www.sbco.or.jp/ont/key";
    private const string Value = "https://www.sbco.or.jp/ont/value";

    private static readonly string[] NoTags = [];

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void Build_NoTypeNoBuilding_EmitsAllFiveTypeBranches()
    {
        var sparql = ResourceSearchQueryBuilder.Build("vav", null, null, NoTags, 50, 0);

        Assert.Contains($"<{Building}>", sparql);
        Assert.Contains($"<{Level}>", sparql);
        Assert.Contains($"<{Room}>", sparql);
        Assert.Contains($"<{Equipment}>", sparql);
        Assert.Contains($"<{Point}>", sparql);
        Assert.Contains("BIND(\"building\" AS ?type)", sparql);
        Assert.Contains("BIND(\"floor\" AS ?type)", sparql);
        Assert.Contains("BIND(\"space\" AS ?type)", sparql);
        Assert.Contains("BIND(\"device\" AS ?type)", sparql);
        Assert.Contains("BIND(\"point\" AS ?type)", sparql);
    }

    [Fact]
    public void Build_WithQuery_EmitsCaseInsensitiveContainsFilterOnNameAndId()
    {
        var sparql = ResourceSearchQueryBuilder.Build("vav", null, null, NoTags, 50, 0);

        Assert.Contains(
            "FILTER(CONTAINS(LCASE(?name), LCASE(\"vav\")) || CONTAINS(LCASE(?id), LCASE(\"vav\")))",
            sparql);
    }

    [Fact]
    public void Build_EmptyQuery_OmitsFilter()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, "floor", "urn:b1", NoTags, 50, 0);

        Assert.DoesNotContain("FILTER(CONTAINS", sparql);
    }

    [Fact]
    public void Build_TypeFilter_EmitsOnlyThatBranch()
    {
        var sparql = ResourceSearchQueryBuilder.Build("x", "point", null, NoTags, 50, 0);

        Assert.Contains($"<{Point}>", sparql);
        Assert.Contains("BIND(\"point\" AS ?type)", sparql);
        Assert.DoesNotContain($"<{Building}>", sparql);
        Assert.DoesNotContain($"<{Equipment}>", sparql);
    }

    [Fact]
    public void Build_BuildingScope_ScopesFloorViaHasPart()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, "floor", "urn:b1", NoTags, 50, 0);

        Assert.Contains($"<urn:b1> <{HasPart}> ?dt", sparql);
    }

    [Fact]
    public void Build_BuildingScope_OmitsDeviceAndPointBranches()
    {
        // Device/point cannot be reliably building-scoped in SBCO (they join via the sbco:floor
        // string literal, not hasPart), so a building-scoped search returns structural resources only.
        var sparql = ResourceSearchQueryBuilder.Build("x", null, "urn:b1", NoTags, 50, 0);

        Assert.Contains($"<{Building}>", sparql);
        Assert.Contains($"<{Level}>", sparql);
        Assert.Contains($"<{Room}>", sparql);
        Assert.DoesNotContain($"<{Equipment}>", sparql);
        Assert.DoesNotContain($"<{Point}>", sparql);
    }

    [Fact]
    public void Build_AppliesLimitAndOffsetVerbatim()
    {
        var sparql = ResourceSearchQueryBuilder.Build("x", null, null, NoTags, 50, 20);

        Assert.Contains("LIMIT 50", sparql);
        Assert.Contains("OFFSET 20", sparql);
    }

    [Fact]
    public void Build_OrdersByTypeThenName()
    {
        var sparql = ResourceSearchQueryBuilder.Build("x", null, null, NoTags, 50, 0);

        Assert.Contains("ORDER BY ?type ?name", sparql);
    }

    [Fact]
    public void Build_EscapesQuoteAndBackslashInQuery()
    {
        var sparql = ResourceSearchQueryBuilder.Build("a\"b\\c", null, null, NoTags, 50, 0);

        // " → \" and \ → \\ so the SPARQL string literal stays well-formed
        Assert.Contains("a\\\"b\\\\c", sparql);
    }

    // ── customTags tag search (#332) ───────────────────────────────────────────

    [Fact]
    public void Build_WithSingleTag_EmitsKeyBoolMapEntryFilter()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, "point", null, ["hvac"], 50, 0);

        Assert.Contains("FILTER EXISTS", sparql);
        Assert.Contains($"?dt <{CustomTags}> ?tagEntry0", sparql);
        Assert.Contains($"a <{KeyBoolMapEntry}>", sparql);
        Assert.Contains($"<{Key}> \"hvac\"", sparql);
        Assert.Contains($"<{Value}> \"true\"^^xsd:boolean", sparql);
    }

    [Fact]
    public void Build_WithMultipleTags_EmitsAndFilterExists()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, "point", null, ["hvac", "temperature"], 50, 0);

        // AND = one FILTER EXISTS block per tag, with distinct entry variables.
        Assert.Equal(2, Count(sparql, "FILTER EXISTS"));
        Assert.Contains("?tagEntry0", sparql);
        Assert.Contains("?tagEntry1", sparql);
        Assert.Contains($"<{Key}> \"hvac\"", sparql);
        Assert.Contains($"<{Key}> \"temperature\"", sparql);
    }

    [Fact]
    public void Build_WithTag_EscapesStringLiteral()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, null, null, ["a\"b\\c"], 50, 0);

        Assert.Contains($"<{Key}> \"a\\\"b\\\\c\"", sparql);
    }

    [Fact]
    public void Build_WithTag_EscapesNewlineCrTab_NoRawControlChars()
    {
        // A raw newline/CR would break out of the SPARQL short string literal (injection). They must
        // be emitted as \n / \r / \t escapes, never as raw control characters.
        var sparql = ResourceSearchQueryBuilder.Build(null, null, null, ["a\nb\rc\td"], 50, 0);

        Assert.Contains("a\\nb\\rc\\td", sparql);
        Assert.DoesNotContain("a\nb", sparql);   // no raw newline inside the literal
        Assert.DoesNotContain("b\rc", sparql);   // no raw carriage return
    }

    [Fact]
    public void Build_WithQuery_EscapesNewline()
    {
        // The same escaping protects the q (CONTAINS) filter input.
        var sparql = ResourceSearchQueryBuilder.Build("a\nb", null, null, NoTags, 50, 0);

        Assert.Contains("a\\nb", sparql);
        Assert.DoesNotContain("LCASE(\"a\nb\")", sparql);
    }

    [Fact]
    public void Build_WithEmptyTags_OmitsTagFilter()
    {
        var sparql = ResourceSearchQueryBuilder.Build("x", null, null, NoTags, 50, 0);

        Assert.DoesNotContain("FILTER EXISTS", sparql);
        Assert.DoesNotContain($"<{CustomTags}>", sparql);
    }

    [Fact]
    public void Build_WithBlankTag_IsSkipped()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, null, null, ["  "], 50, 0);

        Assert.DoesNotContain("FILTER EXISTS", sparql);
    }

    [Fact]
    public void Build_WithQueryAndTag_CombinesNameIdAndTagFilters()
    {
        var sparql = ResourceSearchQueryBuilder.Build("temp", "point", null, ["hvac"], 50, 0);

        Assert.Contains("FILTER(CONTAINS(LCASE(?name), LCASE(\"temp\"))", sparql);
        Assert.Contains("FILTER EXISTS", sparql);
        Assert.Contains($"<{Key}> \"hvac\"", sparql);
    }

    [Fact]
    public void Build_WithTypeAndTag_EmitsOnlyTypeBranchAndTagFilter()
    {
        var sparql = ResourceSearchQueryBuilder.Build(null, "point", null, ["hvac"], 50, 0);

        Assert.Contains($"<{Point}>", sparql);
        Assert.DoesNotContain($"<{Building}>", sparql);
        Assert.Contains("FILTER EXISTS", sparql);
    }
}
