namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// Guard for caller-supplied dtIds that get interpolated into a SPARQL IRI reference
/// (<c>&lt;{dtId}&gt;</c>) by the twin read paths.
///
/// <para><b>Why a validator and not an escaper.</b> The neighbouring
/// <c>EscapeStringLiteral</c> helpers are for short string literals (<c>"..."</c>), where a
/// backslash escape exists for every dangerous character. An IRI reference has no escape mechanism
/// at all: SPARQL's <c>IRIREF</c> production is literally "<c>&lt;</c>, then any character except
/// the excluded set, then <c>&gt;</c>". So the only way an interpolated dtId can be made safe is to
/// reject anything outside that set — a hostile value ending the token early (<c>&gt;</c>) would
/// otherwise let the rest of the string be parsed as query syntax (SPARQL injection), and a merely
/// malformed one would make OxiGraph answer 400, which surfaces to the client as a 500 through
/// <c>HttpResponseMessage.EnsureSuccessStatusCode()</c>.</para>
///
/// <para><b>Why both checks.</b> The explicit character scan is the hard guarantee: it is the
/// <c>IRIREF</c> exclusion set itself (<c>&lt; &gt; " { } | ^ ` \</c> plus every code point at or
/// below <c>U+0020</c> — space and the C0 controls), widened by <c>U+007F</c>..<c>U+009F</c> (DEL
/// and the C1 controls), which <c>IRIREF</c> happens to tolerate but RFC 3987 excludes from an IRI.
/// So nothing that passes can reshape the <c>&lt;...&gt;</c> token no matter what the rest of the
/// stack does.
/// <see cref="Uri.IsWellFormedUriString(string, UriKind)"/> is layered on top for the *absolute*
/// requirement: a relative reference inside <c>&lt;...&gt;</c> is legal SPARQL and resolves against
/// the query's base IRI, so it would silently query a different resource than the caller named
/// instead of failing. Neither check subsumes the other, and the character scan must not be dropped
/// on the grounds that <c>Uri</c> currently happens to reject the same inputs — that behaviour
/// depends on runtime IRI-parsing settings and on whether a <c>UriParser</c> is registered for the
/// scheme, and it is not the property we need.</para>
///
/// <para>Non-ASCII is deliberately allowed: OxiGraph stores IRIs (not URIs), and a Japanese
/// resource IRI is legitimate. Percent-encoding is likewise just ordinary IRI characters here —
/// callers that percent-unescape a route value must run this guard <em>after</em> unescaping,
/// because that is what turns a harmless <c>%3E</c> into a token-terminating <c>&gt;</c>.</para>
/// </summary>
public static class SparqlIriValidator
{
    // SPARQL 1.1 IRIREF: '<' ([^<>"{}|^`\]-[#x00-#x20])* '>'.
    private const string ExcludedChars = "<>\"{}|^`\\";

    /// <summary>
    /// True when <paramref name="value"/> may be interpolated into <c>&lt;...&gt;</c> as-is.
    /// Callers treat false as "no such resource" rather than "bad request", so a probe cannot tell a
    /// rejected id apart from an id that simply is not in the twin.
    /// </summary>
    public static bool IsValidAbsoluteIri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        foreach (var c in value)
        {
            if (c <= ' ' || c == '\u007F') return false;
            if (c >= '\u0080' && c <= '\u009F') return false;
            if (ExcludedChars.Contains(c)) return false;
        }

        return Uri.IsWellFormedUriString(value, UriKind.Absolute);
    }
}
