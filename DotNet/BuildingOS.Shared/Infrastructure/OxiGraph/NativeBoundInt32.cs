using System.Globalization;
using System.Text.RegularExpressions;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>Exact int32 projection of an RDF numeric lexical form; never rounds or underflows.</summary>
internal static class NativeBoundInt32
{
    public static int? Parse(string? raw)
    {
        if (raw is null) return null;
        var match = Regex.Match(raw.Trim(),
            @"\A(?<sign>[+-]?)(?<whole>[0-9]*)(?:\.(?<fraction>[0-9]*))?(?:[eE](?<exponent>[+-]?[0-9]+))?\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success) return null;
        var fraction = match.Groups["fraction"].Value;
        var digits = match.Groups["whole"].Value + fraction;
        if (digits.Length == 0) return null;
        digits = digits.TrimStart('0');
        if (digits.Length == 0) return 0;
        var exponent = 0;
        if (match.Groups["exponent"].Success && !int.TryParse(match.Groups["exponent"].Value,
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return null;
        var scale = (long)fraction.Length - exponent;
        if (scale > 0)
        {
            if (scale > digits.Length || digits.AsSpan(digits.Length - (int)scale).IndexOfAnyExcept('0') >= 0)
                return null;
            digits = digits[..(digits.Length - (int)scale)];
        }
        else
        {
            // At most ten decimal digits can fit int32; avoid expanding huge exponents.
            if (digits.Length - scale > 10) return null;
            digits += new string('0', (int)-scale);
        }
        return int.TryParse(match.Groups["sign"].Value + digits, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
