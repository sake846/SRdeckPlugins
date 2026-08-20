using System.Text;
using SRdeckPlugin.AdsB.Models;

namespace SRdeckPlugin.AdsB.Protocols;

public static class AdsBMessageParser
{
    private const string Charset = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ#####_###############0123456789######";

    public static bool TryParse(ModeSFrame frame, out AdsBMessage? message)
    {
        message = null;
        ReadOnlySpan<byte> bytes = frame.Bytes;
        if (!ModeSCrc.IsValidExtendedSquitter(bytes)) return false;

        string icao = frame.Icao;
        int typeCode = bytes[4] >> 3;
        if (typeCode is >= 1 and <= 4)
        {
            var callsign = new StringBuilder(8);
            for (int index = 0; index < 8; index++)
            {
                int code = GetBits(bytes, 40 + index * 6, 6);
                char character = code < Charset.Length ? Charset[code] : '#';
                callsign.Append(character is '#' or '_' ? ' ' : character);
            }
            string value = callsign.ToString().Trim();
            message = new(icao, typeCode, "identification", $"{icao} {value}".Trim(), Callsign: value);
            return true;
        }

        if (typeCode is >= 5 and <= 8)
        {
            int movement = ((bytes[4] & 0x07) << 4) | (bytes[5] >> 4);
            double? speed = DecodeSurfaceMovement(movement);
            bool trackValid = (bytes[5] & 0x08) != 0;
            int trackRaw = ((bytes[5] & 0x07) << 4) | (bytes[6] >> 4);
            double? track = trackValid ? trackRaw * 360.0 / 128 : null;
            bool odd = (bytes[6] & 0x04) != 0;
            int cprLatitude = ((bytes[6] & 0x03) << 15) | (bytes[7] << 7) | (bytes[8] >> 1);
            int cprLongitude = ((bytes[8] & 0x01) << 16) | (bytes[9] << 8) | bytes[10];
            message = new(icao, typeCode, "surface-position",
                speed is null ? $"{icao} surface position" : $"{icao} ground {speed:F1} kt",
                GroundSpeedKnots: speed, TrackDegrees: track, IsOnGround: true, IsSurfacePosition: true,
                IsOddCpr: odd, CprLatitude: cprLatitude, CprLongitude: cprLongitude);
            return true;
        }

        if (typeCode is >= 9 and <= 18 or >= 20 and <= 22)
        {
            int encodedAltitude = GetBits(bytes, 40, 12);
            int? altitude = DecodeAltitude(encodedAltitude);
            bool geometricAltitude = typeCode is >= 20 and <= 22;
            bool odd = (bytes[6] & 0x04) != 0;
            int cprLatitude = ((bytes[6] & 0x03) << 15) | (bytes[7] << 7) | (bytes[8] >> 1);
            int cprLongitude = ((bytes[8] & 0x01) << 16) | (bytes[9] << 8) | bytes[10];
            message = new(icao, typeCode, "airborne-position",
                altitude is null ? $"{icao} airborne position" : $"{icao} {altitude:N0} ft",
                AltitudeFeet: altitude, IsGeometricAltitude: geometricAltitude, IsOnGround: false,
                IsOddCpr: odd, CprLatitude: cprLatitude, CprLongitude: cprLongitude);
            return true;
        }

        if (typeCode == 19)
        {
            int subtype = bytes[4] & 0x07;
            if (subtype is 1 or 2)
            {
                int scale = subtype == 2 ? 4 : 1;
                int ewRaw = ((bytes[5] & 0x03) << 8) | bytes[6];
                int nsRaw = ((bytes[7] & 0x7F) << 3) | (bytes[8] >> 5);
                double? ew = ewRaw == 0 ? null : (ewRaw - 1) * scale * ((bytes[5] & 0x04) != 0 ? -1 : 1);
                double? ns = nsRaw == 0 ? null : (nsRaw - 1) * scale * ((bytes[7] & 0x80) != 0 ? -1 : 1);
                double? speed = ew is null || ns is null ? null : Math.Sqrt(ew.Value * ew.Value + ns.Value * ns.Value);
                double? track = ew is null || ns is null ? null : (Math.Atan2(ew.Value, ns.Value) * 180 / Math.PI + 360) % 360;
                int verticalRaw = ((bytes[8] & 0x07) << 6) | (bytes[9] >> 2);
                int? verticalRate = verticalRaw == 0 ? null : (verticalRaw - 1) * 64 * ((bytes[8] & 0x08) != 0 ? -1 : 1);
                message = new(icao, typeCode, "airborne-velocity",
                    speed is null ? $"{icao} velocity" : $"{icao} {speed:F0} kt / {track:F0}°",
                    GroundSpeedKnots: speed, TrackDegrees: track, VerticalRateFeetPerMinute: verticalRate);
                return true;
            }
            if (subtype is 3 or 4)
            {
                bool headingValid = (bytes[5] & 0x04) != 0;
                int headingRaw = ((bytes[5] & 0x03) << 8) | bytes[6];
                double? heading = headingValid ? headingRaw * 360.0 / 1024 : null;
                bool trueAirspeed = (bytes[7] & 0x80) != 0;
                int airspeedRaw = ((bytes[7] & 0x7F) << 3) | (bytes[8] >> 5);
                int scale = subtype == 4 ? 4 : 1;
                double? airspeed = airspeedRaw == 0 ? null : (airspeedRaw - 1) * scale;
                int verticalRaw = ((bytes[8] & 0x07) << 6) | (bytes[9] >> 2);
                int? verticalRate = verticalRaw == 0 ? null :
                    (verticalRaw - 1) * 64 * ((bytes[8] & 0x08) != 0 ? -1 : 1);
                message = new(icao, typeCode, "airborne-velocity",
                    airspeed is null ? $"{icao} airspeed" :
                    $"{icao} {airspeed:F0} kt / {(heading is null ? "---" : $"{heading:F0}°")}",
                    AirspeedKnots: airspeed, HeadingDegrees: heading,
                    IsTrueAirspeed: trueAirspeed, VerticalRateFeetPerMinute: verticalRate);
                return true;
            }
        }

        if (typeCode == 28)
        {
            int subtype = GetBits(bytes, 37, 3);
            if (subtype == 1)
            {
                int emergencyCode = GetBits(bytes, 40, 3);
                string emergency = DecodeEmergency(emergencyCode);
                string squawk = DecodeIdentity(GetBits(bytes, 43, 13));
                message = new(icao, typeCode, "aircraft-status",
                    $"{icao} {emergency} / squawk {squawk}",
                    EmergencyState: emergency, Squawk: squawk);
                return true;
            }
        }

        if (typeCode == 29)
        {
            int subtype = GetBits(bytes, 37, 2);
            if (subtype == 0 && GetBits(bytes, 42, 1) == 0)
            {
                int altitudeSource = GetBits(bytes, 39, 2);
                int altitudeRaw = GetBits(bytes, 47, 10);
                int? selectedAltitude = altitudeSource is 1 or 3 ? -1000 + altitudeRaw * 100 : null;
                int horizontalSource = GetBits(bytes, 57, 2);
                int headingRaw = GetBits(bytes, 59, 9);
                double? selectedHeading = horizontalSource != 0 && headingRaw <= 359 ? headingRaw : null;
                bool? isTrack = horizontalSource == 0 ? null : GetBits(bytes, 68, 1) != 0;
                message = new(icao, typeCode, "target-state",
                    selectedAltitude is null ? $"{icao} target state" : $"{icao} selected {selectedAltitude:N0} ft",
                    SelectedAltitudeFeet: selectedAltitude, SelectedHeadingDegrees: selectedHeading,
                    SelectedHeadingIsTrack: isTrack, NacP: GetBits(bytes, 71, 4),
                    NicBaro: GetBits(bytes, 75, 1) != 0, Sil: GetBits(bytes, 76, 2),
                    EmergencyState: DecodeEmergency(GetBits(bytes, 85, 3)));
                return true;
            }
            if (subtype == 1)
            {
                int altitudeRaw = GetBits(bytes, 41, 11);
                int? selectedAltitude = altitudeRaw == 0 ? null : (altitudeRaw - 1) * 32;
                bool headingValid = GetBits(bytes, 61, 1) != 0;
                double? selectedHeading = headingValid ? GetBits(bytes, 62, 9) * 180.0 / 256 : null;
                message = new(icao, typeCode, "target-state",
                    selectedAltitude is null ? $"{icao} target state" : $"{icao} selected {selectedAltitude:N0} ft",
                    SelectedAltitudeFeet: selectedAltitude, SelectedHeadingDegrees: selectedHeading,
                    SelectedHeadingIsTrack: headingValid ? false : null,
                    NacP: GetBits(bytes, 71, 4), NicBaro: GetBits(bytes, 75, 1) != 0,
                    Sil: GetBits(bytes, 76, 2));
                return true;
            }
        }

        if (typeCode == 31)
        {
            int subtype = GetBits(bytes, 37, 3);
            if (subtype is 0 or 1)
            {
                int version = GetBits(bytes, 72, 3);
                message = new(icao, typeCode, "operational-status",
                    $"{icao} ADS-B version {version}", AdsBVersion: version,
                    NacP: version == 0 ? null : GetBits(bytes, 76, 4),
                    Sil: version == 0 ? null : GetBits(bytes, 82, 2),
                    NicA: version == 0 ? null : GetBits(bytes, 75, 1) != 0,
                    NicBaro: version == 0 || subtype == 1 ? null : GetBits(bytes, 84, 1) != 0,
                    IsOnGround: subtype == 1);
                return true;
            }
        }

        message = new(icao, typeCode, "extended-squitter", $"{icao} type code {typeCode}");
        return true;
    }

    private static int? DecodeAltitude(int encoded)
    {
        if ((encoded & 0x10) != 0)
        {
            int n = ((encoded & 0xFE0) >> 1) | (encoded & 0x0F);
            return n * 25 - 1000;
        }

        // Insert the omitted M bit, convert the Gillham bit layout to Mode A,
        // then decode the reflected Gray code in 500/100 ft groups.
        int ac13 = ((encoded & 0x0FC0) << 1) | (encoded & 0x003F);
        int modeC = ModeAToModeC(DecodeIdentityCode(ac13));
        return modeC < -12 ? null : modeC * 100;
    }

    private static int ModeAToModeC(int modeA)
    {
        if ((modeA & unchecked((int)0xFFFF8889)) != 0 || (modeA & 0xF0) == 0) return int.MinValue;
        int oneHundreds = 0;
        if ((modeA & 0x10) != 0) oneHundreds ^= 7;
        if ((modeA & 0x20) != 0) oneHundreds ^= 3;
        if ((modeA & 0x40) != 0) oneHundreds ^= 1;
        if ((oneHundreds & 5) == 5) oneHundreds ^= 2;
        if (oneHundreds > 5) return int.MinValue;

        int fiveHundreds = 0;
        if ((modeA & 0x0002) != 0) fiveHundreds ^= 0x0FF;
        if ((modeA & 0x0004) != 0) fiveHundreds ^= 0x07F;
        if ((modeA & 0x1000) != 0) fiveHundreds ^= 0x03F;
        if ((modeA & 0x2000) != 0) fiveHundreds ^= 0x01F;
        if ((modeA & 0x4000) != 0) fiveHundreds ^= 0x00F;
        if ((modeA & 0x0100) != 0) fiveHundreds ^= 0x007;
        if ((modeA & 0x0200) != 0) fiveHundreds ^= 0x003;
        if ((modeA & 0x0400) != 0) fiveHundreds ^= 0x001;
        if ((fiveHundreds & 1) != 0) oneHundreds = 6 - oneHundreds;
        return fiveHundreds * 5 + oneHundreds - 13;
    }

    private static double? DecodeSurfaceMovement(int movement) => movement switch
    {
        0 or 125 or 126 or 127 => null,
        1 => 0,
        2 => 0.0625,
        <= 8 => 0.125 + (movement - 3 + 0.5) * 0.875 / 6,
        <= 12 => 1 + (movement - 9 + 0.5) * 0.25,
        <= 38 => 2 + (movement - 13 + 0.5) * 0.5,
        <= 93 => 15 + (movement - 39 + 0.5),
        <= 108 => 70 + (movement - 94 + 0.5) * 2,
        <= 123 => 100 + (movement - 109 + 0.5) * 5,
        124 => 180,
        _ => null
    };

    private static string DecodeEmergency(int code) => code switch
    {
        0 => "none",
        1 => "general emergency",
        2 => "medical",
        3 => "minimum fuel",
        4 => "no communications",
        5 => "unlawful interference",
        6 => "downed aircraft",
        _ => "reserved"
    };

    private static string DecodeIdentity(int field) => DecodeIdentityCode(field).ToString("X4");

    private static int DecodeIdentityCode(int field)
    {
        int identity = 0;
        if ((field & 0x1000) != 0) identity |= 0x0010;
        if ((field & 0x0800) != 0) identity |= 0x1000;
        if ((field & 0x0400) != 0) identity |= 0x0020;
        if ((field & 0x0200) != 0) identity |= 0x2000;
        if ((field & 0x0100) != 0) identity |= 0x0040;
        if ((field & 0x0080) != 0) identity |= 0x4000;
        if ((field & 0x0020) != 0) identity |= 0x0100;
        if ((field & 0x0010) != 0) identity |= 0x0001;
        if ((field & 0x0008) != 0) identity |= 0x0200;
        if ((field & 0x0004) != 0) identity |= 0x0002;
        if ((field & 0x0002) != 0) identity |= 0x0400;
        if ((field & 0x0001) != 0) identity |= 0x0004;
        return identity;
    }

    private static int GetBits(ReadOnlySpan<byte> bytes, int start, int length)
    {
        int value = 0;
        for (int bit = 0; bit < length; bit++)
            value = (value << 1) | ((bytes[(start + bit) / 8] >> (7 - ((start + bit) % 8))) & 1);
        return value;
    }
}
