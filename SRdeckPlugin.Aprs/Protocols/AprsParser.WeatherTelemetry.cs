using System.Globalization;
using SRdeckPlugin.Aprs.Models;

namespace SRdeckPlugin.Aprs.Protocols;

public static partial class AprsParser
{
    private static double? WeatherNumber(ReadOnlySpan<char> text, bool signed = false)
    {
        ReadOnlySpan<char> digits = signed && text.Length > 0 && text[0] == '-' ? text[1..] : text;
        return digits.Length > 0 && digits.IndexOfAnyExceptInRange('0', '9') < 0 &&
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static AprsWeather ParseWeather(ReadOnlySpan<char> text, bool windPrefix = false)
    {
        var weather = new AprsWeather();
        if (windPrefix && text.Length >= 7 && text[3] == '/')
        {
            double? direction = WeatherNumber(text[..3]);
            weather = weather with
            {
                WindDirectionDegrees = direction is > 0 and <= 360 ? direction : null,
                WindSpeedMetersPerSecond = WeatherNumber(text.Slice(4, 3)) * 0.44704
            };
            text = text[7..];
        }
        // Consume only contiguous weather fields: scanning arbitrary comment text
        // would turn strings such as a callsign ending in t123 into measurements.
        while (text.Length > 0)
        {
            char field = text[0];
            int width = field switch { 'h' => 2, 'b' => 5, 'c' or 's' or 'g' or 't' or 'r' or 'p' or 'P' or 'L' or 'l' => 3, _ => 0 };
            if (width == 0 || text.Length < width + 1) break;
            ReadOnlySpan<char> raw = text.Slice(1, width);
            double? value = WeatherNumber(raw, field == 't');
            if (value is null && raw.IndexOfAnyExcept('.', ' ') >= 0) break;
            weather = field switch
            {
                'c' => weather with { WindDirectionDegrees = value is > 0 and <= 360 ? value : null },
                's' => weather with { WindSpeedMetersPerSecond = value * 0.44704 },
                'g' => weather with { GustMetersPerSecond = value * 0.44704 },
                't' => weather with { TemperatureCelsius = (value - 32) * 5 / 9 },
                'r' => weather with { RainLastHourMillimeters = value * .254 },
                'p' => weather with { RainLast24HoursMillimeters = value * .254 },
                'P' => weather with { RainSinceMidnightMillimeters = value * .254 },
                'h' => weather with { HumidityPercent = value == 0 ? 100 : value },
                'b' => weather with { PressureHectopascals = value / 10 },
                'L' => weather with { LuminosityWattsPerSquareMeter = value },
                'l' => weather with { LuminosityWattsPerSquareMeter = value + 1000 },
                _ => weather
            };
            text = text[(width + 1)..];
        }
        return weather;
    }

    private static AprsTelemetry? ParseTelemetry(ReadOnlySpan<char> text)
    {
        // MIC is the unsequenced telemetry identifier used by Mic-E gateways.
        if (text.Length < 4 || text[3] != ',' && !text.StartsWith("MIC")) return null;
        int? sequence = null;
        if (!text.StartsWith("MIC"))
        {
            if (WeatherNumber(text[..3]) is not { } number) return null;
            sequence = (int)number;
        }
        text = text[(text[3] == ',' ? 4 : 3)..];
        var analog = new double?[5];
        for (int i = 0; i < 5; i++)
        {
            int comma = text.IndexOf(',');
            if (comma < 0) return null;
            ReadOnlySpan<char> raw = text[..comma];
            if (WeatherNumber(raw) is { } value && value <= 255) analog[i] = value;
            else if (raw.Length != 0 && raw.IndexOfAnyExcept('.', ' ') >= 0) return null;
            text = text[(comma + 1)..];
        }
        if (text.Length < 8 || text[..8].IndexOfAnyExcept('0', '1') >= 0) return null;
        return new(sequence, analog, text[..8].ToString());
    }
}
