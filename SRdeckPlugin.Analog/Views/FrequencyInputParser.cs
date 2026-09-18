using System.Globalization;

using System.Text;

namespace SRdeckPlugin.Analog.Views;

internal static class FrequencyInputParser
{
    public static bool TryParseSquelchThreshold(string input, out float threshold)
    {
        threshold = 0f;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string text = input.Trim()
            .Replace("dBm", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("dBFS", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("　", string.Empty, StringComparison.Ordinal)
            .Normalize(NormalizationForm.FormKC)
            .Replace('ー', '-')
            .Replace('–', '-')
            .Replace('—', '-');

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
            !float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
        {
            return false;
        }

        if (!float.IsFinite(parsed)) return false;

        if (parsed > 0f && parsed <= 150f)
        {
            parsed = -parsed;
        }

        if (parsed is < -150f or > 0f) return false;

        threshold = parsed;
        return true;
    }
}
