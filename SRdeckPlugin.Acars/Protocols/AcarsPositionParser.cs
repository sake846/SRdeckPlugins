using System.Globalization;
using System.Text.RegularExpressions;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsPositionParser
{
    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        return TryParseWithAltitude(text, out latitude, out longitude, out _);
    }

    public static bool TryParseWithAltitude(string? text, out double latitude, out double longitude, out int? altitudeFeet)
    {
        latitude = longitude = 0;
        altitudeFeet = null;

        if (string.IsNullOrWhiteSpace(text)) return false;

        // Extract altitude if present in text (e.g. ALT 35000, FL350, ALT 350)
        Match altMatch = AltitudeRegex().Match(text);
        if (altMatch.Success && int.TryParse(altMatch.Groups["alt"].Value, out int rawAlt))
        {
            altitudeFeet = rawAlt < 1000 ? rawAlt * 100 : rawAlt;
        }

        // 1. Decimal Hemispheres (e.g. N35.3721 E139.7391, 35.3721N 139.7391E)
        Match decimalMatch = DecimalHemispheres().Match(text);
        if (decimalMatch.Success &&
            TryNumber(decimalMatch.Groups["lat"].Value, out latitude) &&
            TryNumber(decimalMatch.Groups["lon"].Value, out longitude))
        {
            if (decimalMatch.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
            if (decimalMatch.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
            return IsValid(latitude, longitude);
        }

        // Explicitly signed decimal pair used by some AOC position reports
        // (e.g. "+35.781,+138.261"). Both signs and the separator are required
        // so unrelated decimal telemetry columns are not mistaken for a position.
        Match signedDecimalMatch = SignedDecimalPair().Match(text);
        if (signedDecimalMatch.Success &&
            TryNumber(signedDecimalMatch.Groups["lat"].Value, out latitude) &&
            TryNumber(signedDecimalMatch.Groups["lon"].Value, out longitude))
        {
            return IsValid(latitude, longitude);
        }

        // Hemisphere-prefixed degrees/minutes with symbols
        // (e.g. N36° 51.8' E138° 36.7').
        Match degreesMinutesMatch = HemisphereDegreesMinutes().Match(text);
        if (degreesMinutesMatch.Success &&
            TryNumber(degreesMinutesMatch.Groups["latDeg"].Value, out double symbolLatDeg) &&
            TryNumber(degreesMinutesMatch.Groups["latMin"].Value, out double symbolLatMin) &&
            TryNumber(degreesMinutesMatch.Groups["lonDeg"].Value, out double symbolLonDeg) &&
            TryNumber(degreesMinutesMatch.Groups["lonMin"].Value, out double symbolLonMin))
        {
            latitude = symbolLatDeg + symbolLatMin / 60.0;
            longitude = symbolLonDeg + symbolLonMin / 60.0;
            if (degreesMinutesMatch.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
            if (degreesMinutesMatch.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
            return symbolLatMin < 60 && symbolLonMin < 60 && IsValid(latitude, longitude);
        }

        // 2. Compact Degrees Minutes (e.g. N35221 E139440, 3522.1N/13944.0E, 3522N13944E)
        Match compactMatch = CompactDegreesMinutes().Match(text);
        if (compactMatch.Success &&
            TryNumber(compactMatch.Groups["latDeg"].Value, out double latDeg) &&
            TryCompactMinutes(compactMatch.Groups["latMin"].Value, out double latMin) &&
            TryNumber(compactMatch.Groups["lonDeg"].Value, out double lonDeg) &&
            TryCompactMinutes(compactMatch.Groups["lonMin"].Value, out double lonMin))
        {
            latitude = latDeg + latMin / 60.0;
            longitude = lonDeg + lonMin / 60.0;
            if (compactMatch.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
            if (compactMatch.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
            return latMin < 60 && lonMin < 60 && IsValid(latitude, longitude);
        }

        // 3. DMS (Degrees Minutes Seconds, e.g. 352230N 1394415E, 35°22'30"N 139°44'15"E)
        Match dmsMatch = DmsRegex().Match(text);
        if (dmsMatch.Success &&
            double.TryParse(dmsMatch.Groups["latD"].Value, out double dLatD) &&
            double.TryParse(dmsMatch.Groups["latM"].Value, out double dLatM) &&
            double.TryParse(dmsMatch.Groups["latS"].Value, out double dLatS) &&
            double.TryParse(dmsMatch.Groups["lonD"].Value, out double dLonD) &&
            double.TryParse(dmsMatch.Groups["lonM"].Value, out double dLonM) &&
            double.TryParse(dmsMatch.Groups["lonS"].Value, out double dLonS))
        {
            latitude = dLatD + (dLatM / 60.0) + (dLatS / 3600.0);
            longitude = dLonD + (dLonM / 60.0) + (dLonS / 3600.0);
            if (dmsMatch.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
            if (dmsMatch.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
            return IsValid(latitude, longitude);
        }

        // ARINC 620 oceanic position report used by airline AOC messages,
        // e.g. "N036,011,E139,000,36002,508". The interpreter already
        // summarizes this format; parsing it here also makes it available to
        // the map and every other position consumer.
        Match arinc620Match = Arinc620PositionRegex().Match(text);
        if (arinc620Match.Success &&
            TryNumber(arinc620Match.Groups["latDeg"].Value, out double arincLatDeg) &&
            TryNumber(arinc620Match.Groups["latMin"].Value, out double rawArincLatMin) &&
            TryNumber(arinc620Match.Groups["lonDeg"].Value, out double arincLonDeg) &&
            TryNumber(arinc620Match.Groups["lonMin"].Value, out double rawArincLonMin))
        {
            double arincLatMin = rawArincLatMin /
                (arinc620Match.Groups["latMin"].Value.Length == 3 ? 10.0 : 100.0);
            double arincLonMin = rawArincLonMin /
                (arinc620Match.Groups["lonMin"].Value.Length == 3 ? 10.0 : 100.0);
            latitude = arincLatDeg + arincLatMin / 60.0;
            longitude = arincLonDeg + arincLonMin / 60.0;
            if (arinc620Match.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
            if (arinc620Match.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
            if (int.TryParse(arinc620Match.Groups["alt"].Value, out int arincAltitude))
                altitudeFeet = arincAltitude;
            return arincLatMin < 60 && arincLonMin < 60 && IsValid(latitude, longitude);
        }

        // 4. ARINC 424 Oceanic Grid / Waypoint (e.g. 54N020W, 5420N -> 54°N 20°W)
        Match gridMatch = ArincGridRegex().Match(text);
        if (gridMatch.Success &&
            double.TryParse(gridMatch.Groups["lat"].Value, out double gLat) &&
            double.TryParse(gridMatch.Groups["lon"].Value, out double gLon))
        {
            latitude = gridMatch.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase) ? -gLat : gLat;
            longitude = gridMatch.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase) ? -gLon : gLon;
            return IsValid(latitude, longitude);
        }

        // 5. Transpacific Compact Position (e.g. 35496 1383541437 30996)
        Match tpMatch = TranspacificPositionRegex().Match(text);
        if (tpMatch.Success &&
            TryNumber(tpMatch.Groups["lat"].Value, out double rawTpLat) &&
            TryNumber(tpMatch.Groups["lon"].Value, out double rawTpLon))
        {
            double tpLatDeg = Math.Floor(rawTpLat / 100.0);
            double tpLatMin = rawTpLat - (tpLatDeg * 100.0);
            double tpLonDeg = Math.Floor(rawTpLon / 100.0);
            double tpLonMin = rawTpLon - (tpLonDeg * 100.0);

            latitude = tpLatDeg + (tpLatMin / 60.0);
            longitude = tpLonDeg + (tpLonMin / 60.0);

            if (tpMatch.Groups["alt"].Success && int.TryParse(tpMatch.Groups["alt"].Value, out int tpAlt))
            {
                altitudeFeet = tpAlt < 1000 ? tpAlt * 100 : tpAlt;
            }

            return tpLatMin < 60 && tpLonMin < 60 && IsValid(latitude, longitude);
        }

        // 6. ARINC 622 ADS-C / Hex Payload Position Decoding
        Match hexMatch = HexPayloadRegex().Match(text);
        if (hexMatch.Success)
        {
            string hex = hexMatch.Groups["hex"].Value;
            if (AcarsAdscDecoder.TryDecodeHex(hex, out var adscData) && adscData.HasPosition)
            {
                latitude = adscData.Latitude!.Value;
                longitude = adscData.Longitude!.Value;
                if (adscData.AltitudeFeet.HasValue && !altitudeFeet.HasValue)
                {
                    altitudeFeet = adscData.AltitudeFeet.Value;
                }
                return IsValid(latitude, longitude);
            }
        }

        return false;
    }

    private static bool TryCompactMinutes(string value, out double minutes)
    {
        string normalized = value.Contains('.') ? value : value.Length == 3 ? $"{value[..2]}.{value[2]}" : value;
        return TryNumber(normalized, out minutes);
    }

    private static bool TryNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out result);

    private static bool IsValid(double latitude, double longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    [GeneratedRegex(@"(?:(?<latHem>[NS])\s*(?<lat>\d{1,2}\.\d+)|(?<lat>\d{1,2}\.\d+)\s*(?<latHem>[NS]))\s*[,/ ]*\s*(?:(?<lonHem>[EW])\s*(?<lon>\d{1,3}\.\d+)|(?<lon>\d{1,3}\.\d+)\s*(?<lonHem>[EW]))", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalHemispheres();

    [GeneratedRegex(@"(?<lat>[+-]\d{1,2}\.\d+)\s*[,/]\s*(?<lon>[+-]\d{1,3}\.\d+)(?![\d.])")]
    private static partial Regex SignedDecimalPair();

    [GeneratedRegex(@"(?<latHem>[NS])\s*(?<latDeg>\d{1,2})\s*°\s*(?<latMin>\d{1,2}(?:\.\d+)?)\s*['′]?\s*[,/]?\s*(?<lonHem>[EW])\s*(?<lonDeg>\d{1,3})\s*°\s*(?<lonMin>\d{1,2}(?:\.\d+)?)\s*['′]?", RegexOptions.IgnoreCase)]
    private static partial Regex HemisphereDegreesMinutes();

    [GeneratedRegex(@"(?:(?<latHem>[NS])\s*(?<latDeg>\d{2})(?<latMin>\d{2}(?:\.\d+)?|\d{3})|(?<latDeg>\d{2})(?<latMin>\d{2}(?:\.\d+)?|\d{3})\s*(?<latHem>[NS]))\s*[,/ ]*\s*(?:(?<lonHem>[EW])\s*(?<lonDeg>\d{3})(?<lonMin>\d{2}(?:\.\d+)?|\d{3})|(?<lonDeg>\d{3})(?<lonMin>\d{2}(?:\.\d+)?|\d{3})\s*(?<lonHem>[EW]))", RegexOptions.IgnoreCase)]
    private static partial Regex CompactDegreesMinutes();

    [GeneratedRegex(@"(?<latD>\d{1,2})[°\s]+(?<latM>\d{2})['\s]+(?<latS>\d{2}(?:\.\d+)?)[&quot;""\s]*(?<latHem>[NS])\s*[,/ ]*\s*(?<lonD>\d{1,3})[°\s]+(?<lonM>\d{2})['\s]+(?<lonS>\d{2}(?:\.\d+)?)[&quot;""\s]*(?<lonHem>[EW])", RegexOptions.IgnoreCase)]
    private static partial Regex DmsRegex();

    [GeneratedRegex(@"(?<latHem>[NS])(?<latDeg>\d{3})[.,\s]+(?<latMin>\d{3,4})[.,\s]+(?<lonHem>[EW])(?<lonDeg>\d{3})[.,\s]+(?<lonMin>\d{3,4})[.,\s]+(?<alt>\d{4,5})(?:[.,\s]+(?<spd>\d{3,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex Arinc620PositionRegex();

    [GeneratedRegex(@"\b(?<lat>\d{2})(?<latHem>[NS])(?<lon>\d{3})(?<lonHem>[EW])\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArincGridRegex();

    [GeneratedRegex(@"(?:ADSB|ADSC|POS)-[A-Z0-9]+(?<hex>[A-F0-9]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex HexPayloadRegex();

    [GeneratedRegex(@"(?:\bALT\b|\bFL\b)\s*(?<alt>\d{3,5})", RegexOptions.IgnoreCase)]
    private static partial Regex AltitudeRegex();

    [GeneratedRegex(@"\b(?<lat>\d{5})\s+(?<lon>\d{6})(?<time>\d{4})\s+(?<alt>\d{5})", RegexOptions.IgnoreCase)]
    private static partial Regex TranspacificPositionRegex();
}
