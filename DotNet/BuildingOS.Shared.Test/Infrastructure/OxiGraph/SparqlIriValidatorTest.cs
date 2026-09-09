using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// Pure tests for the guard that decides whether a caller-supplied dtId may be interpolated into a
/// SPARQL IRI reference (<c>&lt;...&gt;</c>). The escaping helpers next door only cover string
/// literals; nothing escapes an IRI, so the only safe move is to reject anything that could
/// terminate or reshape the <c>&lt;...&gt;</c> token.
/// </summary>
public class SparqlIriValidatorTest
{
    [Theory]
    // The shape the twin actually stores (sbr: resource IRIs) and the urn: form used in fixtures.
    [InlineData("https://www.sbco.or.jp/ont/resource/room-101")]
    [InlineData("urn:test:room-a")]
    // Non-ASCII must stay usable: OxiGraph stores IRIs, and a Japanese room IRI is legitimate.
    [InlineData("https://www.sbco.or.jp/ont/resource/部屋-101")]
    // Percent-encoding survives; it is a normal IRI character sequence, not an escape hatch.
    [InlineData("https://www.sbco.or.jp/ont/resource/room%20101")]
    public void IsValidAbsoluteIri_AcceptsWellFormedAbsoluteIris(string value)
        => Assert.True(SparqlIriValidator.IsValidAbsoluteIri(value));

    [Theory]
    // The character that actually breaks out of <...>. The controller percent-unescapes the route
    // value before this guard runs, so "%3E" arrives here already decoded as ">".
    [InlineData("https://ex.org/a>b")]
    [InlineData("https://ex.org/a<b")]
    // The rest of the IRI-reference exclusion set (RFC 3987): these cannot appear raw in <...>.
    [InlineData("https://ex.org/a b")]
    [InlineData("https://ex.org/a\"b")]
    [InlineData("https://ex.org/a{b")]
    [InlineData("https://ex.org/a}b")]
    [InlineData("https://ex.org/a|b")]
    [InlineData("https://ex.org/a^b")]
    [InlineData("https://ex.org/a`b")]
    [InlineData("https://ex.org/a\\b")]
    [InlineData("https://ex.org/a\nb")]
    [InlineData("https://ex.org/a\rb")]
    [InlineData("https://ex.org/a\tb")]
    [InlineData("https://ex.org/a\u0001b")]
    [InlineData("https://ex.org/a\u007Fb")]
    // Not absolute: a relative reference in <...> resolves against the base IRI instead of failing,
    // so it would silently query something other than what the caller named.
    [InlineData("room-101")]
    [InlineData("/ont/resource/room-101")]
    [InlineData("//ex.org/room-101")]
    // Empty / blank.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidAbsoluteIri_RejectsAnythingThatCouldReshapeTheIriToken(string? value)
        => Assert.False(SparqlIriValidator.IsValidAbsoluteIri(value));
}
