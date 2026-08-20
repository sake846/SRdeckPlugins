using System.Globalization;

namespace SRdeckPlugin.Analog.Views;

internal enum FrequencyInputUnit
{
    Hz,
    KiloHertz,
    MegaHertz
}

internal static class FrequencyInputParser
{
    public static bool TryParse(string input, FrequencyInputUnit defaultUnit, out long frequencyHz)
    {
        SRdeckPlugin.Wpf.FrequencyInputUnit sharedUnit = defaultUnit switch
        {
            FrequencyInputUnit.MegaHertz => SRdeckPlugin.Wpf.FrequencyInputUnit.MegaHertz,
            FrequencyInputUnit.KiloHertz => SRdeckPlugin.Wpf.FrequencyInputUnit.KiloHertz,
            _ => SRdeckPlugin.Wpf.FrequencyInputUnit.Hz
        };
        return SRdeckPlugin.Wpf.FrequencyInputParser.TryParse(
            input, sharedUnit, out frequencyHz);
    }

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
            .Replace('－', '-')
            .Replace('ー', '-')
            .Replace('–', '-')
            .Replace('—', '-');

        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= '０' && chars[i] <= '９')
            {
                chars[i] = (char)('0' + (chars[i] - '０'));
            }
        }
        text = new string(chars);

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
