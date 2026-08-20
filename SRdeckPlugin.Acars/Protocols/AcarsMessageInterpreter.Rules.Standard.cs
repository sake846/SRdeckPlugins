using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsMessageInterpreter
{
    private static bool TryInterpretArinc620OceanicPosition(string text, out string summary)
    {
        Match match = Arinc620OceanicRegex().Match(text);
        if (match.Success)
        {
            string airlineCode = match.Groups["airline"].Value.ToUpperInvariant();
            string fltNum = match.Groups["flt"].Value.Trim();
            string dep = match.Groups["dep"].Value.ToUpperInvariant();
            string arr = match.Groups["arr"].Value.ToUpperInvariant();

            if (double.TryParse(match.Groups["latDeg"].Value, out double latDeg) &&
                double.TryParse(match.Groups["latMin"].Value, out double rawLatMin) &&
                double.TryParse(match.Groups["lonDeg"].Value, out double lonDeg) &&
                double.TryParse(match.Groups["lonMin"].Value, out double rawLonMin) &&
                int.TryParse(match.Groups["alt"].Value, out int altFt) &&
                int.TryParse(match.Groups["spd"].Value, out int spdKt))
            {
                double latMin = rawLatMin / (match.Groups["latMin"].Value.Length == 3 ? 10.0 : 100.0);
                double lonMin = rawLonMin / (match.Groups["lonMin"].Value.Length == 3 ? 10.0 : 100.0);

                string latHem = match.Groups["latHem"].Value.ToUpperInvariant();
                string lonHem = match.Groups["lonHem"].Value.ToUpperInvariant();

                string airlineName = FormatAirline(airlineCode);
                string depName = FormatAirport(dep);
                string arrName = FormatAirport(arr);

                var sb = new StringBuilder();
                sb.AppendLine("[太平洋航路 位置報告 (ARINC 620)]");
                List<string> meta = [];
                if (!string.IsNullOrEmpty(fltNum)) meta.Add($"便名: {fltNum}");
                if (!string.IsNullOrEmpty(airlineName) && airlineName != airlineCode) meta.Add($"航空会社: {airlineName}");
                meta.Add($"区間: {depName} → {arrName}");
                sb.AppendLine(string.Join(" / ", meta));

                sb.Append($"位置: {latHem}{latDeg:00}°{latMin:00.0}' {lonHem}{lonDeg:000}°{lonMin:00.0}' / 高度: {altFt:N0} ft / 速度: {spdKt} kt");

                summary = sb.ToString();
                return true;
            }
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretTranspacificPosition(string text, out string summary)
    {
        Match tpMatch = TranspacificWeatherRegex().Match(text);
        if (tpMatch.Success)
        {
            Match fltMatch = FlightNumberRegex().Match(text);
            Match regMatch = RegNumberRegex().Match(text);
            Match aptMatch = AirportPairRegex().Match(text);

            double rawLat = double.Parse(tpMatch.Groups["lat"].Value);
            double rawLon = double.Parse(tpMatch.Groups["lon"].Value);

            double latDeg = Math.Floor(rawLat / 100.0);
            double latMin = rawLat - (latDeg * 100.0);
            double lonDeg = Math.Floor(rawLon / 100.0);
            double lonMin = rawLon - (lonDeg * 100.0);

            string posStr = $"北緯{latDeg:00}°{latMin:00.0}' 東経{lonDeg:000}°{lonMin:00.0}'";

            int altVal = int.Parse(tpMatch.Groups["alt"].Value);
            int altFeet = altVal < 1000 ? altVal * 100 : (int)(Math.Round(altVal / 100.0) * 100);

            string timeStr = FormatTime(tpMatch.Groups["time"].Value);
            string temp = tpMatch.Groups["temp"].Value;
            string windDir = tpMatch.Groups["winddir"].Value;
            string windSpd = tpMatch.Groups["windspd"].Value;

            var sb = new StringBuilder();
            sb.AppendLine("[太平洋航路 位置・上空気象報告]");

            List<string> meta = [];
            if (fltMatch.Success) meta.Add($"便名: {fltMatch.Groups["flt"].Value}");
            if (regMatch.Success) meta.Add($"機体: {regMatch.Groups["reg"].Value}");
            if (aptMatch.Success)
            {
                string dep = FormatAirport(aptMatch.Groups["dep"].Value);
                string arr = aptMatch.Groups["arr"].Success ? FormatAirport(aptMatch.Groups["arr"].Value) : string.Empty;
                meta.Add(string.IsNullOrEmpty(arr) ? $"空港: {dep}" : $"区間: {dep} → {arr}");
            }

            if (meta.Count > 0) sb.AppendLine(string.Join(" / ", meta));

            sb.Append($"位置: {posStr} / 高度: {altFeet:N0} ft / 風向風速: {windDir}° {windSpd}kt / 気温: {temp}℃ ({timeStr} UTC)");

            summary = sb.ToString();
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretOooi(string text, out string summary)
    {
        List<string> parts = [];

        Match outMatch = OooiOutRegex().Match(text);
        if (outMatch.Success) parts.Add($"出発(OUT) {FormatTime(outMatch.Groups["time"].Value)}");

        Match offMatch = OooiOffRegex().Match(text);
        if (offMatch.Success) parts.Add($"離陸(OFF) {FormatTime(offMatch.Groups["time"].Value)}");

        Match onMatch = OooiOnRegex().Match(text);
        if (onMatch.Success) parts.Add($"着陸(ON) {FormatTime(onMatch.Groups["time"].Value)}");

        Match inMatch = OooiInRegex().Match(text);
        if (inMatch.Success) parts.Add($"到着(IN) {FormatTime(inMatch.Groups["time"].Value)}");

        if (parts.Count > 0)
        {
            summary = string.Join(" / ", parts);
            Match fltMatch = FlightNumberRegex().Match(text);
            if (fltMatch.Success)
            {
                summary = $"便名 {fltMatch.Groups["flt"].Value} - {summary}";
            }
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretPosition(string text, out string summary)
    {
        if (AcarsPositionParser.TryParse(text, out double lat, out double lon))
        {
            char latHem = lat >= 0 ? 'N' : 'S';
            char lonHem = lon >= 0 ? 'E' : 'W';
            double absLat = Math.Abs(lat);
            double absLon = Math.Abs(lon);
            int latDeg = (int)absLat;
            double latMin = (absLat - latDeg) * 60;
            int lonDeg = (int)absLon;
            double lonMin = (absLon - lonDeg) * 60;

            string posStr = $"{latHem}{latDeg}°{latMin:00.0}' {lonHem}{lonDeg}°{lonMin:00.0}'";

            Match altMatch = AltitudeRegex().Match(text);
            if (altMatch.Success)
            {
                if (int.TryParse(altMatch.Groups["alt"].Value, out int altVal))
                {
                    int feet = altVal < 1000 ? altVal * 100 : altVal;
                    posStr += $" / 高度 {feet:N0} ft";
                }
            }

            Match spdMatch = SpeedRegex().Match(text);
            if (spdMatch.Success)
            {
                posStr += $" / 速度 {spdMatch.Groups["spd"].Value} kt";
            }

            summary = posStr;
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretWeather(string text, out string summary)
    {
        Match metarMatch = MetarRegex().Match(text);
        if (metarMatch.Success)
        {
            string airportCode = metarMatch.Groups["station"].Value.ToUpperInvariant();
            string airportName = FormatAirport(airportCode);
            List<string> info = [$"空港 {airportName}"];

            Match windMatch = WindRegex().Match(text);
            if (windMatch.Success)
            {
                string dir = windMatch.Groups["dir"].Value;
                string spd = windMatch.Groups["spd"].Value;
                info.Add($"風向 {dir}° 風速 {spd}kt");
            }

            Match tempMatch = TempDpRegex().Match(text);
            if (tempMatch.Success)
            {
                string temp = tempMatch.Groups["temp"].Value.Replace("M", "-");
                string dp = tempMatch.Groups["dp"].Success ? tempMatch.Groups["dp"].Value.Replace("M", "-") : string.Empty;
                info.Add(string.IsNullOrEmpty(dp) ? $"気温 {temp}℃" : $"気温 {temp}℃ / 露点 {dp}℃");
            }

            Match qnhMatch = QnhRegex().Match(text);
            if (qnhMatch.Success)
            {
                info.Add($"気圧 QNH {qnhMatch.Groups["qnh"].Value} hPa");
            }

            summary = string.Join(" / ", info);
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretWeatherRequest(string text, out string summary)
    {
        Match header = WeatherRequestHeaderRegex().Match(text);
        if (!header.Success)
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[飛行気象情報要求 (WXRQ)]");
        List<string> meta = [];
        meta.Add($"便識別: {header.Groups["flight"].Value.ToUpperInvariant()}");
        meta.Add($"運航日: {header.Groups["day"].Value}日");
        meta.Add($"区間: {FormatAirport(header.Groups["dep"].Value)} → {FormatAirport(header.Groups["arr"].Value)}");
        meta.Add($"機体: {header.Groups["reg"].Value.ToUpperInvariant()}");
        sb.AppendLine(string.Join(" / ", meta));

        string[] stations = WeatherRequestStationRegex().Matches(text)
            .Select(match => match.Groups["station"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(FormatAirport)
            .ToArray();
        if (stations.Length > 0)
            sb.AppendLine($"対象空港: {string.Join("、", stations)}");

        Match type = WeatherRequestTypeRegex().Match(text);
        if (type.Success)
            sb.AppendLine($"航空会社固有: 要求種別コード TYP {type.Groups["type"].Value}");
        sb.Append(text.Trim());
        summary = sb.ToString().TrimEnd();
        return true;
    }

    private static bool TryInterpretMSeriesMessage(string label, string text, out string summary)
    {
        Match envelope = MSeriesEnvelopeRegex().Match(text);
        if (!envelope.Success)
        {
            summary = string.Empty;
            return false;
        }

        string sequence = envelope.Groups["sequence"].Value;
        string flight = envelope.Groups["flight"].Value.ToUpperInvariant();
        string payload = envelope.Groups["payload"].Value.Trim();
        string series = envelope.Groups["series"].Value.ToUpperInvariant();

        var sb = new StringBuilder();
        sb.AppendLine(label is "1L" or "1M"
            ? "[パイロット認証・ログイン]"
            : $"[{series}形式 AOCメッセージ]");
        sb.AppendLine($"メッセージ連番: {sequence} / 便識別: {flight}");

        Match login = PilotLoginDetailsRegex().Match(payload);
        if (label is "1L" or "1M" && login.Success)
        {
            sb.AppendLine($"対象空港: {FormatAirport(login.Groups["airport"].Value)} / " +
                $"予定時刻: {FormatTime(login.Groups["time"].Value)} UTC");
        }

        Match eventMessage = SSeriesEventRegex().Match(payload);
        if (series == "S" && eventMessage.Success)
        {
            sb.AppendLine($"アプリケーションイベント: EV{eventMessage.Groups["event"].Value}");
            if (eventMessage.Groups["value"].Success)
                sb.AppendLine($"イベント値: {eventMessage.Groups["value"].Value}");
        }

        Match identifier = StandardMessageIdentifierRegex().Match(payload);
        if (identifier.Success)
        {
            string code = identifier.Groups["imi"].Value.ToUpperInvariant();
            string description = code switch
            {
                "AEP" => "ICAO形式の位置・気象報告",
                "FPR" => "飛行計画要求",
                "LIF" => "搭載量・離陸情報要求",
                "PWD" => "予測風データ要求",
                "POS" => "位置報告",
                "WXRQ" => "気象情報要求",
                _ => code
            };
            sb.AppendLine($"標準種別: {description} ({code})");
        }

        foreach (Match element in StandardMessageElementRegex().Matches(payload))
        {
            string key = element.Groups["key"].Value.ToUpperInvariant();
            string value = element.Groups["value"].Value.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            string decoded = key switch
            {
                "DA" => $"出発空港: {FormatElementAirport(value)}",
                "DS" => $"到着空港: {FormatElementAirport(value)}",
                "AN" => $"代替空港: {FormatElementAirport(value)}",
                "STA" => $"対象空港: {FormatElementAirport(value)}",
                "CL" or "AL" => $"巡航高度: {FormatFlightLevel(value)}",
                "CI" => $"コストインデックス: {value}",
                "CR" => $"カンパニールート: {value}",
                "RT" => $"経路: {value}",
                "RW" => $"滑走路: {value}",
                "TO" => $"通過時刻: {FormatElementTime(value)} UTC",
                "TA" => $"外気温: {value}",
                "WV" or "CW" => $"風向風速データ: {value}",
                "ZF" => $"ゼロ燃料重量: {value}",
                "BF" => $"ブロック燃料: {value}",
                "TG" => $"地上走行燃料: {value}",
                "CG" => $"重心位置: {value}",
                "PF" => $"性能補正値: {value}",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(decoded)) sb.AppendLine(decoded);
        }

        string residual = StandardMessageIdentifierRegex().Replace(payload, " ");
        residual = StandardMessageElementRegex().Replace(residual, " ");
        if (eventMessage.Success) residual = SSeriesEventRegex().Replace(residual, " ");
        residual = Regex.Replace(residual, @"[\s/,]+", " ").Trim();
        if (!string.IsNullOrEmpty(residual))
        {
            sb.Append("航空会社固有データ");
        }

        summary = sb.ToString().TrimEnd();
        return true;
    }

    private static string FormatElementAirport(string value)
    {
        Match airport = Regex.Match(value, @"[A-Z]{3,4}", RegexOptions.IgnoreCase);
        return airport.Success ? FormatAirport(airport.Value) : value;
    }

    private static string FormatFlightLevel(string value)
    {
        Match level = Regex.Match(value, @"\d{2,3}");
        return level.Success ? $"FL{level.Value.PadLeft(3, '0')}" : value;
    }

    private static string FormatElementTime(string value)
    {
        Match time = Regex.Match(value, @"\d{4}");
        return time.Success ? FormatTime(time.Value) : value;
    }

    private static bool TryInterpretFlightPlan(string text, out string summary)
    {
        Match etaMatch = EtaRegex().Match(text);
        Match fltMatch = FlightNumberRegex().Match(text);
        if (etaMatch.Success || (fltMatch.Success && text.Contains("ETA", StringComparison.OrdinalIgnoreCase)))
        {
            List<string> parts = [];
            if (fltMatch.Success) parts.Add($"便名 {fltMatch.Groups["flt"].Value}");
            if (etaMatch.Success) parts.Add($"到着予想(ETA) {FormatTime(etaMatch.Groups["time"].Value)}");
            summary = string.Join(" / ", parts);
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretAtisRequest(string label, string text, out string summary)
    {
        if (label == "5D")
        {
            Match compact = CompactAtisRequestRegex().Match(text);
            if (compact.Success)
            {
                string airport = FormatAirport(compact.Groups["airport"].Value);
                summary = $"[D-ATIS空港情報要求]\n対象空港: {airport}\n{text}";
                return true;
            }
        }

        if (label == "B9")
        {
            Match ti2 = Ti2AtisRequestRegex().Match(text);
            if (ti2.Success)
            {
                string stationCode = ti2.Groups["station"].Value.ToUpperInvariant();
                string station = FormatAirport(stationCode);
                string version = ti2.Groups["version"].Value;
                summary = $"[D-ATIS空港情報要求]\n対象空港: {station} / 形式: TI{version}\n{text}";
                return true;
            }
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretPirep(string text, out string summary)
    {
        if (!text.Contains("UA /", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("UUA /", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("PIREP", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("/TB ", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("/IC ", StringComparison.OrdinalIgnoreCase))
        {
            summary = string.Empty;
            return false;
        }

        List<string> parts = [];
        Match turbMatch = TurbRegex().Match(text);
        if (turbMatch.Success) parts.Add($"揺れ(Turbulence): {turbMatch.Groups["val"].Value}");

        Match iceMatch = IceRegex().Match(text);
        if (iceMatch.Success) parts.Add($"着氷(Icing): {iceMatch.Groups["val"].Value}");

        Match fltMatch = FlightNumberRegex().Match(text);
        if (fltMatch.Success) parts.Insert(0, $"便名: {fltMatch.Groups["flt"].Value}");

        if (parts.Count > 0)
        {
            summary = string.Join(" / ", parts);
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static string FormatDir(string dir) => dir.ToUpperInvariant() switch
    {
        "L" => "左",
        "R" => "右",
        _ => dir
    };

    private static string FormatTime(string rawTime)
    {
        if (rawTime.Length == 4 && int.TryParse(rawTime[..2], out int hh) && int.TryParse(rawTime[2..], out int mm))
        {
            return $"{hh:D2}:{mm:D2}";
        }
        return rawTime;
    }

    private static string FormatMonth(string month) => month.ToUpperInvariant() switch
    {
        "JAN" => "1月", "FEB" => "2月", "MAR" => "3月", "APR" => "4月",
        "MAY" => "5月", "JUN" => "6月", "JUL" => "7月", "AUG" => "8月",
        "SEP" => "9月", "OCT" => "10月", "NOV" => "11月", "DEC" => "12月",
        _ => $"{month} "
    };

    private static int MonthNumber(string month) => month.ToUpperInvariant() switch
    {
        "JAN" => 1, "FEB" => 2, "MAR" => 3, "APR" => 4,
        "MAY" => 5, "JUN" => 6, "JUL" => 7, "AUG" => 8,
        "SEP" => 9, "OCT" => 10, "NOV" => 11, "DEC" => 12,
        _ => 0
    };

    private static string CleanRawText(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return string.Empty;
        return lines[0];
    }

    private static string CleanFullRawText(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> clean = [];
        foreach (string line in lines)
        {
            string l = line;
            if (l.StartsWith('#'))
            {
                int slashIndex = l.IndexOf('/');
                if (slashIndex > 0 && slashIndex < 12) l = l[(slashIndex + 1)..].TrimStart();
            }
            clean.Add(l);
        }
        return string.Join("\n", clean);
    }
}
