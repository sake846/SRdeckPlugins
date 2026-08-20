using System.Text;
using SRdeckPlugin.Ais.Models;

namespace SRdeckPlugin.Ais.Protocols;

public static class AisMessageParser
{
    private const string SixBitText = "@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^- !\"#$%&'()*+,-./0123456789:;<=>?";

    public static bool TryParse(AisFrame frame, out AisMessage? message)
    {
        message = null;
        ReadOnlySpan<byte> data = frame.Payload;
        if (data.Length < 5) return false;
        int bitLength = data.Length * 8;
        int type = (int)GetUnsigned(data, 0, 6);
        int repeat = (int)GetUnsigned(data, 6, 2);
        uint mmsi = (uint)GetUnsigned(data, 8, 30);
        if (type is < 1 or > 27 || mmsi == 0) return false;

        if (type is 1 or 2 or 3 && bitLength >= 168)
        {
            int navStatus = (int)GetUnsigned(data, 38, 4);
            int rotRaw = (int)GetSigned(data, 42, 8);
            int sogRaw = (int)GetUnsigned(data, 50, 10);
            double? longitude = DecodeLongitude(GetSigned(data, 61, 28));
            double? latitude = DecodeLatitude(GetSigned(data, 89, 27));
            int cogRaw = (int)GetUnsigned(data, 116, 12);
            int headingRaw = (int)GetUnsigned(data, 128, 9);
            double? rot = rotRaw == -128 ? null :
                Math.CopySign(Math.Pow(Math.Abs(rotRaw) / 4.733, 2), rotRaw);
            double? speed = sogRaw == 1023 ? null : sogRaw / 10.0;
            double? course = cogRaw >= 3600 ? null : cogRaw / 10.0;
            message = new(type, repeat, mmsi, "class-a-position",
                $"{mmsi:000000000} {FormatMotion(speed, course)}",
                latitude, longitude, speed, course, headingRaw == 511 ? null : headingRaw,
                rot, navStatus, PositionAccurate: GetUnsigned(data, 60, 1) != 0,
                UtcSecond: DecodeUtcSecond(data, 137));
            return true;
        }

        if (type is 4 or 11 && bitLength >= 168)
        {
            double? longitude = DecodeLongitude(GetSigned(data, 79, 28));
            double? latitude = DecodeLatitude(GetSigned(data, 107, 27));
            message = new(type, repeat, mmsi, "base-station",
                $"{mmsi:000000000} base station", latitude, longitude,
                PositionAccurate: GetUnsigned(data, 78, 1) != 0,
                UtcSecond: (int)GetUnsigned(data, 72, 6));
            return true;
        }

        if (type == 5 && bitLength >= 424)
        {
            int imo = (int)GetUnsigned(data, 40, 30);
            string callSign = DecodeText(data, 70, 42);
            string name = DecodeText(data, 112, 120);
            int shipType = (int)GetUnsigned(data, 232, 8);
            string destination = DecodeText(data, 302, 120);
            int draught = (int)GetUnsigned(data, 294, 8);
            message = new(type, repeat, mmsi, "static-voyage",
                $"{mmsi:000000000} {DisplayName(name, callSign)}",
                VesselName: name, CallSign: callSign, ImoNumber: imo == 0 ? null : imo,
                ShipType: shipType == 0 ? null : shipType, Destination: destination,
                DraughtMetres: draught == 0 ? null : draught / 10.0,
                DimensionToBowMetres: NullIfZero(data, 240, 9),
                DimensionToSternMetres: NullIfZero(data, 249, 9),
                DimensionToPortMetres: NullIfZero(data, 258, 6),
                DimensionToStarboardMetres: NullIfZero(data, 264, 6));
            return true;
        }

        if (type == 18 && bitLength >= 168)
        {
            int sogRaw = (int)GetUnsigned(data, 46, 10);
            int cogRaw = (int)GetUnsigned(data, 112, 12);
            int headingRaw = (int)GetUnsigned(data, 124, 9);
            double? speed = sogRaw == 1023 ? null : sogRaw / 10.0;
            double? course = cogRaw >= 3600 ? null : cogRaw / 10.0;
            message = new(type, repeat, mmsi, "class-b-position",
                $"{mmsi:000000000} {FormatMotion(speed, course)}",
                DecodeLatitude(GetSigned(data, 85, 27)), DecodeLongitude(GetSigned(data, 57, 28)),
                speed, course, headingRaw == 511 ? null : headingRaw,
                PositionAccurate: GetUnsigned(data, 56, 1) != 0,
                UtcSecond: DecodeUtcSecond(data, 133));
            return true;
        }

        if (type == 19 && bitLength >= 312)
        {
            int sogRaw = (int)GetUnsigned(data, 46, 10);
            int cogRaw = (int)GetUnsigned(data, 112, 12);
            int headingRaw = (int)GetUnsigned(data, 124, 9);
            string name = DecodeText(data, 143, 120);
            int shipType = (int)GetUnsigned(data, 263, 8);
            double? speed = sogRaw == 1023 ? null : sogRaw / 10.0;
            double? course = cogRaw >= 3600 ? null : cogRaw / 10.0;
            message = new(type, repeat, mmsi, "class-b-extended",
                $"{mmsi:000000000} {DisplayName(name, string.Empty)}",
                DecodeLatitude(GetSigned(data, 85, 27)), DecodeLongitude(GetSigned(data, 57, 28)),
                speed, course, headingRaw == 511 ? null : headingRaw,
                VesselName: name, ShipType: shipType == 0 ? null : shipType,
                DimensionToBowMetres: NullIfZero(data, 271, 9),
                DimensionToSternMetres: NullIfZero(data, 280, 9),
                DimensionToPortMetres: NullIfZero(data, 289, 6),
                DimensionToStarboardMetres: NullIfZero(data, 295, 6),
                PositionAccurate: GetUnsigned(data, 56, 1) != 0,
                UtcSecond: DecodeUtcSecond(data, 133));
            return true;
        }

        if (type == 21 && bitLength >= 272)
        {
            int aidType = (int)GetUnsigned(data, 38, 5);
            string name = DecodeText(data, 43, 120);
            message = new(type, repeat, mmsi, "aid-to-navigation",
                $"{mmsi:000000000} {DisplayName(name, string.Empty)}",
                DecodeLatitude(GetSigned(data, 192, 27)), DecodeLongitude(GetSigned(data, 164, 28)),
                VesselName: name, PositionAccurate: GetUnsigned(data, 163, 1) != 0,
                AidType: AidTypeName(aidType),
                DimensionToBowMetres: NullIfZero(data, 219, 9),
                DimensionToSternMetres: NullIfZero(data, 228, 9),
                DimensionToPortMetres: NullIfZero(data, 237, 6),
                DimensionToStarboardMetres: NullIfZero(data, 243, 6));
            return true;
        }

        if (type == 24 && bitLength >= 160)
        {
            int part = (int)GetUnsigned(data, 38, 2);
            if (part == 0)
            {
                string name = DecodeText(data, 40, Math.Min(120, bitLength - 40));
                message = new(type, repeat, mmsi, "static-data-a",
                    $"{mmsi:000000000} {DisplayName(name, string.Empty)}", VesselName: name);
                return true;
            }
            if (part == 1)
            {
                string callSign = bitLength >= 132 ? DecodeText(data, 90, 42) : string.Empty;
                message = new(type, repeat, mmsi, "static-data-b",
                    $"{mmsi:000000000} {DisplayName(string.Empty, callSign)}",
                    CallSign: callSign, ShipType: (int)GetUnsigned(data, 40, 8),
                    DimensionToBowMetres: bitLength >= 168 ? NullIfZero(data, 132, 9) : null,
                    DimensionToSternMetres: bitLength >= 168 ? NullIfZero(data, 141, 9) : null,
                    DimensionToPortMetres: bitLength >= 168 ? NullIfZero(data, 150, 6) : null,
                    DimensionToStarboardMetres: bitLength >= 168 ? NullIfZero(data, 156, 6) : null);
                return true;
            }
        }

        if (type == 27 && bitLength >= 96)
        {
            int sogRaw = (int)GetUnsigned(data, 79, 6);
            int cogRaw = (int)GetUnsigned(data, 85, 9);
            message = new(type, repeat, mmsi, "long-range-position",
                $"{mmsi:000000000} long-range position",
                DecodeLatitude(GetSigned(data, 62, 17), 600),
                DecodeLongitude(GetSigned(data, 44, 18), 600),
                sogRaw == 63 ? null : sogRaw,
                cogRaw == 511 ? null : cogRaw,
                NavigationStatus: (int)GetUnsigned(data, 40, 4),
                PositionAccurate: GetUnsigned(data, 38, 1) != 0);
            return true;
        }

        message = new(type, repeat, mmsi, $"message-{type}", $"{mmsi:000000000} AIS message {type}");
        return true;
    }

    internal static ulong GetUnsigned(ReadOnlySpan<byte> data, int start, int length)
    {
        if (length is < 1 or > 64 || start < 0 || start + length > data.Length * 8)
            throw new ArgumentOutOfRangeException(nameof(length));
        ulong value = 0;
        for (int bit = 0; bit < length; bit++)
            value = (value << 1) | (uint)((data[(start + bit) / 8] >> (7 - ((start + bit) % 8))) & 1);
        return value;
    }

    internal static long GetSigned(ReadOnlySpan<byte> data, int start, int length)
    {
        ulong value = GetUnsigned(data, start, length);
        if ((value & (1UL << (length - 1))) == 0) return (long)value;
        return (long)(value | (ulong.MaxValue << length));
    }

    private static string DecodeText(ReadOnlySpan<byte> data, int start, int length)
    {
        var text = new StringBuilder(length / 6);
        for (int offset = 0; offset + 6 <= length; offset += 6)
        {
            int value = (int)GetUnsigned(data, start + offset, 6);
            text.Append(value < SixBitText.Length ? SixBitText[value] : ' ');
        }
        return text.ToString().TrimEnd('@', ' ');
    }

    private static double? DecodeLongitude(long raw, double divisor = 600_000) =>
        Math.Abs(raw) >= 181 * divisor ? null : raw / divisor;

    private static double? DecodeLatitude(long raw, double divisor = 600_000) =>
        Math.Abs(raw) >= 91 * divisor ? null : raw / divisor;

    private static int? DecodeUtcSecond(ReadOnlySpan<byte> data, int start)
    {
        int second = (int)GetUnsigned(data, start, 6);
        return second <= 59 ? second : null;
    }

    private static int? NullIfZero(ReadOnlySpan<byte> data, int start, int length)
    {
        int value = (int)GetUnsigned(data, start, length);
        return value == 0 ? null : value;
    }

    private static string DisplayName(string name, string callSign) =>
        !string.IsNullOrWhiteSpace(name) ? name :
        !string.IsNullOrWhiteSpace(callSign) ? callSign : "名称未受信";

    private static string FormatMotion(double? speed, double? course) =>
        $"{(speed is null ? "--.- kt" : $"{speed:F1} kt")} / {(course is null ? "---.-°" : $"{course:F1}°")}";

    private static string AidTypeName(int value) => value switch
    {
        1 => "Reference point",
        2 => "RACON",
        3 => "Fixed offshore structure",
        5 => "Light",
        6 => "Light with sectors",
        9 => "Beacon, cardinal N",
        10 => "Beacon, cardinal E",
        11 => "Beacon, cardinal S",
        12 => "Beacon, cardinal W",
        13 => "Beacon, port hand",
        14 => "Beacon, starboard hand",
        17 => "Buoy, cardinal N",
        18 => "Buoy, cardinal E",
        19 => "Buoy, cardinal S",
        20 => "Buoy, cardinal W",
        21 => "Buoy, port hand",
        22 => "Buoy, starboard hand",
        31 => "Light vessel / LANBY",
        _ => $"AtoN type {value}"
    };
}
