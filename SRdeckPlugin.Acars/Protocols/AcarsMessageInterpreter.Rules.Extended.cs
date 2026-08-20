using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsMessageInterpreter
{
    private static bool TryInterpretFlightControlOrTelemetry(string text, out string summary)
    {
        if (!Regex.IsMatch(text, @"\b(FLIGHT CONTROL|RPT|ENGINE)\b", RegexOptions.IgnoreCase))
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        Match reportTitleMatch = ReportNameRegex().Match(text);
        string reportTitle = reportTitleMatch.Success ? reportTitleMatch.Groups["name"].Value.Trim() : "FLIGHT CONTROL";
        sb.AppendLine($"[整備・操縦系統データ ({reportTitle})]");

        List<string> metaParts = [];
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] bodyLines = lines.Where(l => !l.Contains('#')).ToArray();
        string bodyText = bodyLines.Length > 0 ? string.Join("\n", bodyLines) : text;
        string? bLine = bodyLines.FirstOrDefault(l => l.StartsWith("B ", StringComparison.OrdinalIgnoreCase) || l.StartsWith("B-", StringComparison.OrdinalIgnoreCase));
        string targetMetaText = bLine ?? bodyText;

        Match fltMatch = FlightNumberRegex().Match(targetMetaText);
        if (!fltMatch.Success && targetMetaText != bodyText) fltMatch = FlightNumberRegex().Match(bodyText);
        if (fltMatch.Success) metaParts.Add($"便名: {fltMatch.Groups["flt"].Value}");

        Match aptMatch = AirportPairRegex().Match(targetMetaText);
        if (!aptMatch.Success && targetMetaText != bodyText) aptMatch = AirportPairRegex().Match(bodyText);
        if (aptMatch.Success)
        {
            string dep = FormatAirport(aptMatch.Groups["dep"].Value);
            string arr = aptMatch.Groups["arr"].Success ? FormatAirport(aptMatch.Groups["arr"].Value) : string.Empty;
            metaParts.Add(string.IsNullOrEmpty(arr) ? $"空港: {dep}" : $"区間: {dep} → {arr}");
        }

        Match regMatch = RegNumberRegex().Match(targetMetaText);
        if (!regMatch.Success && targetMetaText != bodyText) regMatch = RegNumberRegex().Match(bodyText);
        if (regMatch.Success) metaParts.Add($"機体: {regMatch.Groups["reg"].Value}");

        Match dateMatch = DateUtcRegex().Match(targetMetaText);
        if (!dateMatch.Success && targetMetaText != bodyText) dateMatch = DateUtcRegex().Match(bodyText);
        if (dateMatch.Success) metaParts.Add($"日時: {dateMatch.Groups["day"].Value}日 {FormatTime(dateMatch.Groups["time"].Value)} UTC");

        Match engMatch = EngineTypeRegex().Match(targetMetaText);
        if (!engMatch.Success && targetMetaText != bodyText) engMatch = EngineTypeRegex().Match(bodyText);
        if (engMatch.Success) metaParts.Add($"エンジン: {engMatch.Groups["eng"].Value}");

        if (metaParts.Count > 0)
        {
            sb.AppendLine(string.Join(" / ", metaParts));
        }

        List<string> paramParts = [];
        foreach (string line in lines)
        {
            // Line 1: 1 L 0.03 R 0.31
            Match l1Match = AileronParamRegex().Match(line);
            if (l1Match.Success)
            {
                paramParts.Add($"エルロン: 左 {l1Match.Groups["l"].Value}° / 右 {l1Match.Groups["r"].Value}°");
                continue;
            }

            // Line 2: 2 R 0.002
            Match l2Match = RudderParamRegex().Match(line);
            if (l2Match.Success)
            {
                paramParts.Add($"ラダー: {FormatDir(l2Match.Groups["dir"].Value)} {l2Match.Groups["val"].Value}°");
                continue;
            }

            // Line 3: 3 EXT EXT EXT EXT EXT EXT EXT EXT
            if (line.StartsWith("3 ") && line.Contains("EXT"))
            {
                paramParts.Add("舵面ステータス: 全系統展開(EXT)");
                continue;
            }

            // Line 4: 4 0.5 0.5
            Match l4Match = ElevatorParamRegex().Match(line);
            if (l4Match.Success)
            {
                paramParts.Add($"エレベーター: 左右 {l4Match.Groups["val1"].Value}° / {l4Match.Groups["val2"].Value}°");
                continue;
            }

            // Generic Key=Value or Parameter parsing
            Match kvMatch = KeyValueRegex().Match(line);
            if (kvMatch.Success && !line.StartsWith('#'))
            {
                paramParts.Add($"{kvMatch.Groups["key"].Value}: {kvMatch.Groups["val"].Value}");
            }
        }

        if (paramParts.Count > 0)
        {
            sb.Append(string.Join(" / ", paramParts));
        }

        summary = sb.ToString().TrimEnd();
        return true;
    }

    private static bool TryInterpretCfbFaultReport(string text, out string summary)
    {
        Match header = AirlineSegmentHeaderRegex().Match(text);
        if (!header.Success ||
            !header.Groups["format"].Value.Equals("CFB", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("#CFBFLR", StringComparison.OrdinalIgnoreCase))
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[機上整備・故障レポート (CFB/FLR)]");
        sb.AppendLine($"メッセージ連番: {header.Groups["message"].Value[1..]} / " +
            $"便識別: {header.Groups["flight"].Value.ToUpperInvariant()} / " +
            $"セグメント: {header.Groups["part"].Value.ToUpperInvariant()}");

        Match dateTime = CfbFaultDateTimeRegex().Match(text);
        if (dateTime.Success)
        {
            string date = dateTime.Groups["date"].Value;
            string time = dateTime.Groups["time"].Value;
            sb.AppendLine($"発生日時: 20{date[..2]}-{date.Substring(2, 2)}-{date[4..]} " +
                $"{time[..2]}:{time.Substring(2, 2)}:{time[4..]} UTC");
        }

        List<string> systems = [];
        foreach (Match system in CfbFaultSystemRegex().Matches(text))
        {
            string item = system.Groups["system"].Value.ToUpperInvariant();
            if (system.Groups["status"].Success)
                item += $" (状態コード: {system.Groups["status"].Value})";
            systems.Add(item);
        }
        if (systems.Count > 0) sb.AppendLine($"関連システム: {string.Join(" / ", systems)}");

        Match identifier = CfbFaultIdentifierRegex().Match(text);
        if (identifier.Success)
            sb.AppendLine($"故障識別: {identifier.Groups["id"].Value.ToUpperInvariant()}");

        sb.Append("航空会社固有: CFBレポート内の未定義数値フィールドは生データ表示");
        summary = sb.ToString().TrimEnd();
        return true;
    }

    private static bool TryInterpretAirlineTelemetrySegment(string text, out string summary)
    {
        MatchCollection headers = AirlineSegmentHeaderRegex().Matches(text);
        Match header = headers.FirstOrDefault() ?? Match.Empty;
        if (!header.Success)
        {
            summary = string.Empty;
            return false;
        }

        string format = header.Groups["format"].Value.ToUpperInvariant();
        if (format.StartsWith("DF", StringComparison.Ordinal) || format == "CFB")
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[航空会社固有・機上テレメトリ]");
        string message = header.Groups["message"].Value;
        string flight = header.Groups["flight"].Value;
        string[] segments = headers
            .Where(item => item.Groups["message"].Value.Equals(message, StringComparison.OrdinalIgnoreCase) &&
                item.Groups["flight"].Value.Equals(flight, StringComparison.OrdinalIgnoreCase) &&
                item.Groups["format"].Value.Equals(format, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Groups["part"].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        sb.AppendLine($"形式: {format} / メッセージ: {header.Groups["message"].Value} / " +
            $"セグメント: {string.Join('/', segments)} / " +
            $"便識別: {header.Groups["flight"].Value.ToUpperInvariant()}");

        Match report = EmbeddedReportRegex().Match(text);
        if (report.Success) sb.AppendLine($"レポート定義: {report.Groups["report"].Value}");
        Match page = EmbeddedPageRegex().Match(text);
        if (page.Success) sb.AppendLine($"ページ: {page.Groups["page"].Value}");
        Match timestamp = EmbeddedTimestampRegex().Match(text);
        if (timestamp.Success)
        {
            sb.AppendLine($"記録日時: 20{timestamp.Groups["year"].Value}-" +
                $"{MonthNumber(timestamp.Groups["mon"].Value):D2}-{timestamp.Groups["day"].Value} " +
                $"{timestamp.Groups["time"].Value} UTC");
        }

        sb.Append("航空会社固有データ");
        summary = sb.ToString().TrimEnd();
        return true;
    }

    private static bool TryInterpretCompactAcmsReport(string text, out string summary)
    {
        Match formatMatch = CompactAcmsFormatRegex().Match(text);
        Match reportMatch = CompactAcmsReportRegex().Match(text);
        bool hasSegmentHeader = AirlineSegmentHeaderRegex().IsMatch(text);
        bool hasStructuredPayload = text.Count(character => character == ',') >= 3 ||
            text.Count(character => character == '/') >= 2;
        if ((!formatMatch.Success || (!hasStructuredPayload && !hasSegmentHeader)) && !reportMatch.Success)
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[機上整備・性能監視データ (ACMS)]");

        List<string> meta = [];
        if (formatMatch.Success)
            meta.Add($"形式: {formatMatch.Groups["format"].Value.ToUpperInvariant()}");
        if (reportMatch.Success)
            meta.Add($"レポート定義: {reportMatch.Groups["report"].Value}（航空会社独自）");

        Match segmentHeader = AirlineSegmentHeaderRegex().Match(text);
        if (segmentHeader.Success)
        {
            meta.Add($"メッセージ連番: {segmentHeader.Groups["message"].Value[1..]}");
            meta.Add($"セグメント: {segmentHeader.Groups["part"].Value.ToUpperInvariant()}");
            meta.Add($"便識別: {segmentHeader.Groups["flight"].Value.ToUpperInvariant()}");
        }

        Match flightMatch = CompactAcmsFlightRegex().Match(text);
        if (flightMatch.Success)
        {
            string registration = flightMatch.Groups["reg"].Value.ToUpperInvariant();
            if (!registration.Contains('-') && registration.StartsWith('B'))
                registration = $"B-{registration[1..]}";
            meta.Add($"機体: {registration}");

            string month = FormatMonth(flightMatch.Groups["mon"].Value);
            string day = flightMatch.Groups["day"].Value;
            string time = flightMatch.Groups["time"].Value;
            if (time.Length == 6)
                meta.Add($"日時: {month}{day}日 {time[..2]}:{time.Substring(2, 2)}:{time[4..]} UTC");

            string departure = FormatAirport(flightMatch.Groups["dep"].Value);
            string arrival = FormatAirport(flightMatch.Groups["arr"].Value);
            meta.Add($"区間: {departure} → {arrival}");
            meta.Add($"便識別: {flightMatch.Groups["flight"].Value}");
        }

        if (meta.Count > 0) sb.AppendLine(string.Join(" / ", meta));

        sb.Append("航空会社・機種固有の整備／性能パラメータ");

        summary = sb.ToString().TrimEnd();
        return true;
    }

}
