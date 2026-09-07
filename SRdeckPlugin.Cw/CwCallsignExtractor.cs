using System.Text.RegularExpressions;

namespace SRdeckPlugin.Cw;

public static partial class CwCallsignExtractor
{
    public static IReadOnlyList<string> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return CandidatePattern().Matches(text.ToUpperInvariant())
            .Select(match => match.Value.Trim('/'))
            .Where(IsPlausible)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsPlausible(string value)
    {
        if (value.Length is < 3 or > 12) return false;
        if (!value.Any(char.IsAsciiLetterUpper) || !value.Any(char.IsAsciiDigit)) return false;
        if (value.Count(character => character == '/') > 1) return false;
        return value.All(character => char.IsAsciiLetterUpper(character) ||
            char.IsAsciiDigit(character) || character == '/');
    }

    [GeneratedRegex(@"(?<![A-Z0-9/])[A-Z0-9]+(?:/[A-Z0-9]+)?(?![A-Z0-9/])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidatePattern();
}
