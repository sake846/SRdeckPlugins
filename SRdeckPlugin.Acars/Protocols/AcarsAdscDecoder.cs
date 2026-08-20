using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SRdeckPlugin.Acars.Protocols;

public class AdscDecodedData
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? AltitudeFeet { get; set; }
    public string? FlightId { get; set; }
    public double? GroundSpeedKnots { get; set; }
    public double? TrueTrackDegrees { get; set; }
    public int? VerticalRateFpm { get; set; }
    public double? TrueHeadingDegrees { get; set; }
    public double? MachOrIas { get; set; }
    public double? WindDirectionDegrees { get; set; }
    public double? WindSpeedKnots { get; set; }
    public double? TemperatureCelsius { get; set; }
    public string? NextWaypointInfo { get; set; }
    public string? TimestampUtc { get; set; }

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
}

public static partial class AcarsAdscDecoder
{
    public static bool TryDecodeHex(string hexString, out AdscDecodedData decodedData)
    {
        decodedData = new AdscDecodedData();
        if (string.IsNullOrWhiteSpace(hexString)) return false;

        string cleanHex = Regex.Replace(hexString.Trim(), @"[^A-Fa-f0-9]", string.Empty).ToUpperInvariant();
        if (cleanHex.Length < 10) return false;

        byte[]? bytes = HexToBytes(cleanHex);
        if (bytes == null || bytes.Length < 5) return false;

        int index = 0;
        bool decodedAny = false;

        // Skip potential ARINC 622 header byte if present (e.g. 0xD6, 0x01, etc.)
        if (bytes.Length > 12 && (bytes[0] == 0xD6 || bytes[0] == 0x01 || bytes[0] == 0x03 || bytes[0] == 0x07 || bytes[0] == 0x14 || bytes[0] == 0x15))
        {
            if (!IsRecognizedTag(bytes[0]) && IsRecognizedTag(bytes[1]))
            {
                index = 1;
            }
        }

        while (index < bytes.Length - 1)
        {
            byte tag = bytes[index];
            if (tag == 0)
            {
                index++;
                continue;
            }

            switch (tag)
            {
                case 0x01: // Basic Group (Tag 1)
                case 0x07: // Compact Basic Group (Tag 7)
                    if (TryParseBasicGroup(bytes, ref index, decodedData)) decodedAny = true;
                    else index++;
                    break;

                case 0x02: // Flight ID Group (Tag 2)
                    if (TryParseFlightIdGroup(bytes, ref index, decodedData)) decodedAny = true;
                    else index++;
                    break;

                case 0x03: // Earth Reference Group (Tag 3)
                    if (TryParseEarthRefGroup(bytes, ref index, decodedData)) decodedAny = true;
                    else index++;
                    break;

                case 0x04: // Air Reference Group (Tag 4)
                    if (TryParseAirRefGroup(bytes, ref index, decodedData)) decodedAny = true;
                    else index++;
                    break;

                case 0x05: // Meteorological Group (Tag 5)
                    if (TryParseMetGroup(bytes, ref index, decodedData)) decodedAny = true;
                    else index++;
                    break;

                case 0x06: // Predicted Route Group (Tag 6)
                    if (TryParsePredictedRouteGroup(bytes, ref index, decodedData)) decodedAny = true;
                    else index++;
                    break;

                default:
                    index++;
                    break;
            }
        }

        return decodedAny;
    }

    private static bool IsRecognizedTag(byte tag) => tag is >= 0x01 and <= 0x07;

    private static bool TryParseBasicGroup(byte[] bytes, ref int index, AdscDecodedData data)
    {
        if (index + 9 > bytes.Length) return false;

        index++; // Skip Tag byte

        int latRaw = (bytes[index] << 16) | (bytes[index + 1] << 8) | bytes[index + 2];
        index += 3;

        int lonRaw = (bytes[index] << 16) | (bytes[index + 1] << 8) | bytes[index + 2];
        index += 3;

        double lat = Convert24BitAngle(latRaw, 90.0);
        double lon = Convert24BitAngle(lonRaw, 180.0);

        if (IsValidLatLon(lat, lon))
        {
            data.Latitude = lat;
            data.Longitude = lon;
        }

        if (index + 2 <= bytes.Length)
        {
            ushort altRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
            index += 2;
            int altFt = altRaw * 4;
            if (altFt is > 0 and < 60000)
            {
                data.AltitudeFeet = altFt;
            }
        }

        if (index < bytes.Length && bytes[index] <= 60)
        {
            int sec = bytes[index];
            index++;
            data.TimestampUtc = $"{sec:D2}s";
        }

        return data.HasPosition;
    }

    private static bool TryParseFlightIdGroup(byte[] bytes, ref int index, AdscDecodedData data)
    {
        if (index + 7 > bytes.Length) return false;
        index++; // Skip Tag

        var sb = new StringBuilder();
        ulong packed = 0;
        for (int i = 0; i < 6; i++)
        {
            packed = (packed << 8) | bytes[index + i];
        }
        index += 6;

        for (int i = 7; i >= 0; i--)
        {
            byte charVal = (byte)((packed >> (i * 6)) & 0x3F);
            char c = Decode6BitChar(charVal);
            if (c != ' ' && c != '\0') sb.Append(c);
        }

        string callsign = sb.ToString().Trim();
        if (callsign.Length >= 3)
        {
            data.FlightId = callsign;
            return true;
        }
        return false;
    }

    private static bool TryParseEarthRefGroup(byte[] bytes, ref int index, AdscDecodedData data)
    {
        if (index + 7 > bytes.Length) return false;
        index++; // Skip Tag

        ushort trkRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.TrueTrackDegrees = Math.Round((trkRaw & 0x0FFF) * 360.0 / 4096.0, 1);

        ushort spdRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.GroundSpeedKnots = Math.Round((spdRaw & 0x0FFF) * 0.5, 1);

        short vrRaw = (short)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.VerticalRateFpm = vrRaw * 16;

        return true;
    }

    private static bool TryParseAirRefGroup(byte[] bytes, ref int index, AdscDecodedData data)
    {
        if (index + 7 > bytes.Length) return false;
        index++; // Skip Tag

        ushort hdgRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.TrueHeadingDegrees = Math.Round((hdgRaw & 0x0FFF) * 360.0 / 4096.0, 1);

        ushort machRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.MachOrIas = Math.Round((machRaw & 0x0FFF) * 0.004, 3);

        index += 2; // Skip VR
        return true;
    }

    private static bool TryParseMetGroup(byte[] bytes, ref int index, AdscDecodedData data)
    {
        // Tag + wind direction (2) + wind speed (1) + temperature (2).
        // A truncated five-byte tail used to pass this check and then read
        // bytes[index + 1] past the end while decoding temperature.
        if (index + 6 > bytes.Length) return false;
        index++; // Skip Tag

        ushort wdirRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.WindDirectionDegrees = Math.Round((wdirRaw & 0x01FF) * 360.0 / 512.0, 1);

        byte wspdRaw = bytes[index++];
        data.WindSpeedKnots = wspdRaw;

        short tempRaw = (short)((bytes[index] << 8) | bytes[index + 1]);
        index += 2;
        data.TemperatureCelsius = Math.Round(tempRaw * 0.25, 1);

        return true;
    }

    private static bool TryParsePredictedRouteGroup(byte[] bytes, ref int index, AdscDecodedData data)
    {
        if (index + 9 > bytes.Length) return false;
        index++; // Skip Tag

        int latRaw = (bytes[index] << 16) | (bytes[index + 1] << 8) | bytes[index + 2];
        index += 3;
        int lonRaw = (bytes[index] << 16) | (bytes[index + 1] << 8) | bytes[index + 2];
        index += 3;

        double lat = Convert24BitAngle(latRaw, 90.0);
        double lon = Convert24BitAngle(lonRaw, 180.0);

        if (IsValidLatLon(lat, lon))
        {
            ushort altRaw = (ushort)((bytes[index] << 8) | bytes[index + 1]);
            index += 2;
            int altFt = altRaw * 4;
            data.NextWaypointInfo = $"次経由地: Lat {lat:F3}°, Lon {lon:F3}°, 高度 {altFt} ft";
        }
        else
        {
            index += 2;
        }

        return true;
    }

    private static bool TryFallback24BitPosition(byte[] bytes, AdscDecodedData data)
    {
        for (int offset = 0; offset <= bytes.Length - 6; offset++)
        {
            int latRaw = (bytes[offset] << 16) | (bytes[offset + 1] << 8) | bytes[offset + 2];
            int lonRaw = (bytes[offset + 3] << 16) | (bytes[offset + 4] << 8) | bytes[offset + 5];

            double lat = Convert24BitAngle(latRaw, 90.0);
            double lon = Convert24BitAngle(lonRaw, 180.0);

            if (IsValidLatLon(lat, lon) && Math.Abs(lat) > 0.5 && Math.Abs(lon) > 0.5)
            {
                data.Latitude = lat;
                data.Longitude = lon;

                if (offset + 8 <= bytes.Length)
                {
                    ushort altRaw = (ushort)((bytes[offset + 6] << 8) | bytes[offset + 7]);
                    int altFt = altRaw * 4;
                    if (altFt is > 0 and < 60000) data.AltitudeFeet = altFt;
                }
                return true;
            }
        }
        return false;
    }

    private static double Convert24BitAngle(int raw24, double maxDeg)
    {
        if ((raw24 & 0x800000) != 0)
        {
            int val = raw24 | unchecked((int)0xFF000000);
            return (val / (double)0x7FFFFF) * maxDeg;
        }
        return (raw24 / (double)0x7FFFFF) * maxDeg;
    }

    private static char Decode6BitChar(byte val)
    {
        if (val is >= 1 and <= 26) return (char)('A' + val - 1);
        if (val is >= 48 and <= 57) return (char)('0' + val - 48);
        if (val == 32) return ' ';
        return '\0';
    }

    private static bool IsValidLatLon(double lat, double lon) =>
        lat is >= -90.0 and <= 90.0 && lon is >= -180.0 and <= 180.0;

    private static byte[]? HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }
}
