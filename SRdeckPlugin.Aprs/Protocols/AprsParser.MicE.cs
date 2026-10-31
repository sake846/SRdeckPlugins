using System.Globalization;
using SRdeckPlugin.Aprs.Models;

namespace SRdeckPlugin.Aprs.Protocols;

public static partial class AprsParser
{
    private static AprsPacket ParseMicE(AprsPacket packet)
    {
        packet = packet with { Kind = "Mic-E位置" };
        string destination = packet.Destination.Split('-')[0];
        ReadOnlySpan<char> data = packet.Information;
        if (destination.Length != 6 || data.Length < 9 || !SymbolTable(data[8]) || data[7] is < '!' or > '~')
            return packet;
        Span<char> latitude = stackalloc char[7];
        latitude[4] = '.';
        int ambiguity = 0;
        for (int i = 0; i < 6; i++)
        {
            char c = destination[i];
            // Custom message bits are legal only in the first three digits.
            char digit = c switch
            {
                >= '0' and <= '9' => c,
                >= 'A' and <= 'J' when i < 3 => (char)(c - 'A' + '0'),
                >= 'P' and <= 'Y' => (char)(c - 'P' + '0'),
                'K' when i < 3 => ' ',
                'L' or 'Z' => ' ',
                _ => '\0'
            };
            if (digit == '\0' || i < 2 && digit == ' ') return packet;
            if (digit == ' ') ambiguity++;
            else if (ambiguity != 0) return packet;
            latitude[i < 4 ? i : i + 1] = digit;
        }
        if (!Coordinate(latitude, 2, 90, out double lat)) return packet;
        if (destination[3] < 'P') lat = -lat;
        if (data[1] is < (char)38 or > (char)127 || data[2] is < (char)38 or > (char)97 ||
            data[3] is < (char)28 or > (char)127 || data[4] is < (char)28 or > (char)127 ||
            data[5] is < (char)28 or > (char)127 || data[6] is < (char)28 or > (char)127)
            return packet;
        int degrees = data[1] - 28 + (destination[4] >= 'P' ? 100 : 0);
        if (degrees is >= 180 and <= 189) degrees -= 80;
        else if (degrees is >= 190 and <= 199) degrees -= 190;
        int minutes = data[2] - 28;
        if (minutes >= 60) minutes -= 60;
        double minuteValue = minutes + (data[3] - 28) / 100.0;
        minuteValue = ambiguity switch
        {
            1 => Math.Floor(minuteValue * 10) / 10 + .05,
            2 => minutes + .5,
            3 => minutes / 10 * 10 + 5,
            4 => 30,
            _ => minuteValue
        };
        double lon = degrees + minuteValue / 60;
        if (degrees > 179 || minuteValue >= 60) return packet;
        if (destination[5] >= 'P') lon = -lon;
        int speed = (data[4] - 28) * 10 + (data[5] - 28) / 10;
        int course = (data[5] - 28) % 10 * 100 + data[6] - 28;
        if (speed >= 800) speed -= 800;
        if (course >= 400) course -= 400;
        packet = packet with
        {
            Latitude = lat, Longitude = lon, Symbol = $"{data[8]}{data[7]}",
            SpeedKnots = speed is >= 0 and <= 799 ? speed : null,
            CourseDegrees = course is > 0 and <= 360 ? course : null
        };
        if (data.Length > 9 && data[9] is '\'' or '`')
        {
            int channels = data[9] == '\'' ? 5 : 2;
            if (data.Length >= 10 + channels * 2)
            {
                var analog = new double?[5];
                bool valid = true;
                for (int i = 0; i < channels; i++)
                {
                    if (!byte.TryParse(data.Slice(10 + i * 2, 2), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out byte value)) { valid = false; break; }
                    analog[channels == 2 ? i * 2 : i] = value;
                }
                if (valid) packet = packet with { Telemetry = new(null, analog, null) };
            }
        }
        return packet;
    }
}
