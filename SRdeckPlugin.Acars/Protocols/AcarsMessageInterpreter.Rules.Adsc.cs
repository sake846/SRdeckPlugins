using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsMessageInterpreter
{
    private static bool TryInterpretAdsbAcars(string text, out string summary)
    {
        Match adsbMatch = AdsbAcarsRegex().Match(text);
        if (adsbMatch.Success)
        {
            string rpt = adsbMatch.Groups["rpt"].Value;
            string airlineCode = adsbMatch.Groups["airline"].Value.ToUpperInvariant();
            string station = adsbMatch.Groups["station"].Value.ToUpperInvariant();
            string reg = adsbMatch.Groups["reg"].Value;
            string date = adsbMatch.Groups["date"].Value;
            string hexData = adsbMatch.Groups["hex"].Value;

            string airlineName = FormatAirline(airlineCode);
            string stationName = FormatAirport(station[..Math.Min(4, station.Length)]);
            string dateStr = date.Length == 4 && int.TryParse(date[..2], out int mm) && int.TryParse(date[2..], out int dd)
                ? $"{mm}月{dd}日"
                : string.Empty;

            var sb = new StringBuilder();
            string rptLabel = string.IsNullOrEmpty(rpt) ? "ADS-C" : $"Report L{rpt}";
            sb.AppendLine($"[ADS-C / FMS位置航法データ ({rptLabel})]");
            string datePart = string.IsNullOrEmpty(dateStr) ? string.Empty : $" / 日時: {dateStr}";
            sb.AppendLine($"航空会社: {airlineName} / 受信局: {stationName} / 機体: {reg}{datePart}");

            if (AcarsAdscDecoder.TryDecodeHex(hexData, out var adsc))
            {
                List<string> adscInfo = [];
                if (adsc.HasPosition)
                {
                    char latH = adsc.Latitude >= 0 ? 'N' : 'S';
                    char lonH = adsc.Longitude >= 0 ? 'E' : 'W';
                    adscInfo.Add($"位置: {latH}{Math.Abs(adsc.Latitude!.Value):00.0000}° {lonH}{Math.Abs(adsc.Longitude!.Value):000.0000}°");
                }
                if (adsc.AltitudeFeet.HasValue) adscInfo.Add($"高度: {adsc.AltitudeFeet:N0} ft");
                if (adsc.GroundSpeedKnots.HasValue) adscInfo.Add($"対地速度: {adsc.GroundSpeedKnots} kt");
                if (adsc.TrueTrackDegrees.HasValue) adscInfo.Add($"方位: {adsc.TrueTrackDegrees}°");
                if (adsc.WindDirectionDegrees.HasValue && adsc.WindSpeedKnots.HasValue)
                    adscInfo.Add($"風向風速: {adsc.WindDirectionDegrees}° {adsc.WindSpeedKnots}kt");
                if (adsc.TemperatureCelsius.HasValue) adscInfo.Add($"外気温: {adsc.TemperatureCelsius}℃");
                if (!string.IsNullOrEmpty(adsc.FlightId)) adscInfo.Add($"便名: {adsc.FlightId}");

                if (adscInfo.Count > 0)
                {
                    sb.AppendLine(string.Join(" / ", adscInfo));
                }
            }

            string hexSnippet = hexData.Length > 24 ? $"{hexData[..24]}..." : hexData;
            sb.Append($"HEXデータ ({hexData.Length / 2} bytes): {hexSnippet}");

            summary = sb.ToString();
            return true;
        }

        // Direct standalone ADS-C Hex check if text starts with hex (e.g. D6031A27...)
        Match standaloneHex = StandaloneHexRegex().Match(text);
        if (standaloneHex.Success && AcarsAdscDecoder.TryDecodeHex(standaloneHex.Value, out var adscStandalone) && adscStandalone.HasPosition)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ADS-C 16進数ペロードデコード]");
            char latH = adscStandalone.Latitude >= 0 ? 'N' : 'S';
            char lonH = adscStandalone.Longitude >= 0 ? 'E' : 'W';
            List<string> parts = [$"位置: {latH}{Math.Abs(adscStandalone.Latitude!.Value):00.0000}° {lonH}{Math.Abs(adscStandalone.Longitude!.Value):000.0000}°"];
            if (adscStandalone.AltitudeFeet.HasValue) parts.Add($"高度: {adscStandalone.AltitudeFeet:N0} ft");
            if (adscStandalone.GroundSpeedKnots.HasValue) parts.Add($"速度: {adscStandalone.GroundSpeedKnots} kt");
            if (adscStandalone.WindDirectionDegrees.HasValue && adscStandalone.WindSpeedKnots.HasValue)
                parts.Add($"風: {adscStandalone.WindDirectionDegrees}° {adscStandalone.WindSpeedKnots}kt");
            if (adscStandalone.TemperatureCelsius.HasValue) parts.Add($"気温: {adscStandalone.TemperatureCelsius}℃");
            sb.Append(string.Join(" / ", parts));
            summary = sb.ToString();
            return true;
        }

        // Extra fallback for ADS-C header formatted messages like "F10ACX0872/FUKJJYA. ADS... KP10719C..."
        Match headerMatch = AdscHeaderRegex().Match(text);
        if (headerMatch.Success)
        {
            string fltRaw = headerMatch.Groups["flt"].Value;
            string station = headerMatch.Groups["station"].Value;
            string hexPayload = headerMatch.Groups["hex"].Value;

            string stationName = FormatAirport(station[..Math.Min(4, station.Length)]);

            var sb = new StringBuilder();
            sb.AppendLine("[ADS-C / FMS位置航法データ]");
            List<string> meta = [];
            if (!string.IsNullOrEmpty(fltRaw)) meta.Add($"便名: {fltRaw}");
            meta.Add($"受信局: {stationName}");
            sb.AppendLine(string.Join(" / ", meta));

            if (AcarsAdscDecoder.TryDecodeHex(hexPayload, out var adscH) && adscH.HasPosition)
            {
                char latH = adscH.Latitude >= 0 ? 'N' : 'S';
                char lonH = adscH.Longitude >= 0 ? 'E' : 'W';
                List<string> adscInfo = [$"位置: {latH}{Math.Abs(adscH.Latitude!.Value):00.0000}° {lonH}{Math.Abs(adscH.Longitude!.Value):000.0000}°"];
                if (adscH.AltitudeFeet.HasValue) adscInfo.Add($"高度: {adscH.AltitudeFeet:N0} ft");
                if (adscH.GroundSpeedKnots.HasValue) adscInfo.Add($"対地速度: {adscH.GroundSpeedKnots} kt");
                if (adscH.WindDirectionDegrees.HasValue && adscH.WindSpeedKnots.HasValue)
                    adscInfo.Add($"風向風速: {adscH.WindDirectionDegrees}° {adscH.WindSpeedKnots}kt");
                if (adscH.TemperatureCelsius.HasValue) adscInfo.Add($"外気温: {adscH.TemperatureCelsius}℃");
                sb.AppendLine(string.Join(" / ", adscInfo));
            }

            string hexSnippet = hexPayload.Length > 24 ? $"{hexPayload[..24]}..." : hexPayload;
            sb.Append($"HEXデータ ({hexPayload.Length / 2} bytes): {hexSnippet}");

            summary = sb.ToString();
            return true;
        }

        // Universal Header & Payload Extractor for L37, L63, AT1, AT1B, ADSB, ADSC, etc.
        // e.g. L37ACX0872/FUKJJYA.AT1..B-KPT62094F2F00DFB4
        Match univMatch = UniversalHeaderPayloadRegex().Match(text);
        if (univMatch.Success)
        {
            string hexData = univMatch.Groups["hex"].Value;
            string rpt = univMatch.Groups["rpt"].Value;
            string fltRaw = univMatch.Groups["flt"].Value;
            string station = univMatch.Groups["station"].Value;
            string reg = univMatch.Groups["reg"].Value;
            string stationName = string.IsNullOrEmpty(station) ? string.Empty : FormatAirport(station[..Math.Min(4, station.Length)]);

            AdscDecodedData? adscUniv = null;
            if (!string.IsNullOrEmpty(hexData) && AcarsAdscDecoder.TryDecodeHex(hexData, out var decoded))
            {
                if (decoded.HasPosition || decoded.AltitudeFeet.HasValue || decoded.GroundSpeedKnots.HasValue)
                    adscUniv = decoded;
            }
            bool decodedOk = adscUniv != null;

            // Return a summary if we decoded telemetry OR if we have meaningful header info (station/reg)
            bool hasHeader = !string.IsNullOrEmpty(stationName) || !string.IsNullOrEmpty(reg) || !string.IsNullOrEmpty(fltRaw);
            if (decodedOk || (hasHeader && !string.IsNullOrEmpty(hexData)))
            {
                var sb = new StringBuilder();
                string rptLabel = string.IsNullOrEmpty(rpt) ? "ADS-C" : $"Report L{rpt}";
                sb.AppendLine($"[ADS-C / FMS位置航法データ ({rptLabel})]");

                List<string> meta = [];
                if (!string.IsNullOrEmpty(fltRaw)) meta.Add($"便名: {fltRaw}");
                if (!string.IsNullOrEmpty(stationName)) meta.Add($"受信局: {stationName}");
                if (!string.IsNullOrEmpty(reg)) meta.Add($"機体: {reg}");
                if (meta.Count > 0) sb.AppendLine(string.Join(" / ", meta));

                if (adscUniv != null)
                {
                    List<string> adscInfo = [];
                    if (adscUniv.HasPosition)
                    {
                        char latH = adscUniv.Latitude >= 0 ? 'N' : 'S';
                        char lonH = adscUniv.Longitude >= 0 ? 'E' : 'W';
                        adscInfo.Add($"位置: {latH}{Math.Abs(adscUniv.Latitude!.Value):00.0000}° {lonH}{Math.Abs(adscUniv.Longitude!.Value):000.0000}°");
                    }
                    if (adscUniv.AltitudeFeet.HasValue) adscInfo.Add($"高度: {adscUniv.AltitudeFeet:N0} ft");
                    if (adscUniv.GroundSpeedKnots.HasValue) adscInfo.Add($"対地速度: {adscUniv.GroundSpeedKnots} kt");
                    if (adscUniv.WindDirectionDegrees.HasValue && adscUniv.WindSpeedKnots.HasValue)
                        adscInfo.Add($"風向風速: {adscUniv.WindDirectionDegrees}° {adscUniv.WindSpeedKnots}kt");
                    if (adscUniv.TemperatureCelsius.HasValue) adscInfo.Add($"外気温: {adscUniv.TemperatureCelsius}℃");
                    if (adscInfo.Count > 0) sb.AppendLine(string.Join(" / ", adscInfo));
                }

                string hexSnippet = hexData.Length > 24 ? $"{hexData[..24]}..." : hexData;
                sb.Append($"HEXデータ ({hexData.Length / 2} bytes): {hexSnippet}");

                summary = sb.ToString().TrimEnd();
                return true;
            }
        }

        summary = string.Empty;
        return false;
    }
}
