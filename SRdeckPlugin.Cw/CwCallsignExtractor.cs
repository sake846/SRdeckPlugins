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

    private static bool IsPlausible(string value) =>
        value.Length is >= 3 and <= 12 && value.Any(char.IsAsciiLetterUpper) && value.Any(char.IsAsciiDigit);

    [GeneratedRegex(@"(?<![A-Z0-9/])[A-Z0-9]+(?:/[A-Z0-9]+)?(?![A-Z0-9/])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CandidatePattern();
}
