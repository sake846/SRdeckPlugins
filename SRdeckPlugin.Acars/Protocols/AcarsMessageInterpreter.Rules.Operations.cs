using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

public static partial class AcarsMessageInterpreter
{
    /// <summary>
    /// CPDLC (Controller-Pilot Data Link Communications) messages. ARINC 618 label B6 / 21.
    /// Detects FANS-1/A and ATN B1 style messages by label and/or keyword presence.
    /// </summary>
    private static bool TryInterpretCpdlc(string label, string text, out string summary)
    {
        bool isCpdlcLabel = label is "B6" or "21";
        bool hasCpdlcContent =
            text.Contains("/FANS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CPDLC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LOGON", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LOGOFF", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/WILCO", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/UNABLE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/ROGER", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/STANDBY", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DATALINK", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ATC COMM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/CLX/", StringComparison.OrdinalIgnoreCase);

        if (!isCpdlcLabel && !hasCpdlcContent)
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[CPDLC 管制-パイロットデータリンク]");

        List<string> parts = [];

        Match fltMatch = FlightNumberRegex().Match(text);
        if (fltMatch.Success) parts.Add($"便名: {fltMatch.Groups["flt"].Value}");

        // Message type classification
        string msgType = string.Empty;
        if (text.Contains("/WILCO", StringComparison.OrdinalIgnoreCase)) msgType = "了解・実行 (WILCO)";
        else if (text.Contains("/UNABLE", StringComparison.OrdinalIgnoreCase)) msgType = "実行不可 (UNABLE)";
        else if (text.Contains("/ROGER", StringComparison.OrdinalIgnoreCase)) msgType = "了解 (ROGER)";
        else if (text.Contains("/STANDBY", StringComparison.OrdinalIgnoreCase)) msgType = "待機指示 (STANDBY)";
        else if (text.Contains("LOGOFF", StringComparison.OrdinalIgnoreCase)) msgType = "データリンク切断 (LOGOFF)";
        else if (text.Contains("LOGON", StringComparison.OrdinalIgnoreCase)) msgType = "データリンク接続要求 (LOGON)";
        else if (text.Contains("DATALINK STATUS", StringComparison.OrdinalIgnoreCase)) msgType = "データリンク状態通知";
        else if (text.Contains("/CLX/", StringComparison.OrdinalIgnoreCase)) msgType = "クリアランス";
        else if (text.Contains("CONTACT", StringComparison.OrdinalIgnoreCase)) msgType = "コンタクト要求";
        else if (text.Contains("MONITOR", StringComparison.OrdinalIgnoreCase)) msgType = "周波数モニター要求";
        else if (isCpdlcLabel) msgType = "データリンクメッセージ";

        if (!string.IsNullOrEmpty(msgType)) parts.Add($"種別: {msgType}");

        Match freqMatch = VhfFrequencyRegex().Match(text);
        if (freqMatch.Success) parts.Add($"周波数: {freqMatch.Groups["freq"].Value} MHz");

        Match altMatch = AltitudeRegex().Match(text);
        if (altMatch.Success && int.TryParse(altMatch.Groups["alt"].Value, out int altNum))
            parts.Add(altNum < 1000 ? $"高度: FL{altNum:D3}" : $"高度: {altNum:N0} ft");

        if (parts.Count > 0) sb.AppendLine(string.Join(" / ", parts));

        string cleanBody = CleanFullRawText(text);
        if (!string.IsNullOrWhiteSpace(cleanBody)) sb.Append(cleanBody);

        summary = sb.ToString().TrimEnd();
        return true;
    }

    /// <summary>
    /// D-ATIS / ATIS (Automated Terminal Information Service) messages. ARINC 618 label 51/52/D1.
    /// </summary>
    private static bool TryInterpretAtis(string text, out string summary)
    {
        Match atisHeaderMatch = AtisHeaderRegex().Match(text);
        if (!atisHeaderMatch.Success)
        {
            summary = string.Empty;
            return false;
        }

        List<string> parts = [];

        string icao = atisHeaderMatch.Groups["icao"].Success
            ? atisHeaderMatch.Groups["icao"].Value.ToUpperInvariant()
            : string.Empty;
        string infoLetter = atisHeaderMatch.Groups["info"].Success
            ? atisHeaderMatch.Groups["info"].Value.ToUpperInvariant()
            : string.Empty;

        if (!string.IsNullOrEmpty(icao)) parts.Add($"空港: {FormatAirport(icao)}");
        if (!string.IsNullOrEmpty(infoLetter)) parts.Add($"情報: {infoLetter}");

        Match rwyMatch = RunwayRegex().Match(text);
        if (rwyMatch.Success) parts.Add($"滑走路: {rwyMatch.Groups["rwy"].Value}");

        Match windMatch = WindRegex().Match(text);
        if (windMatch.Success)
        {
            string gust = windMatch.Groups["gst"].Success ? $"G{windMatch.Groups["gst"].Value}kt" : string.Empty;
            parts.Add(string.IsNullOrEmpty(gust)
                ? $"風: {windMatch.Groups["dir"].Value}°/{windMatch.Groups["spd"].Value}kt"
                : $"風: {windMatch.Groups["dir"].Value}°/{windMatch.Groups["spd"].Value}kt {gust}");
        }

        Match visMatch = VisibilityRegex().Match(text);
        if (visMatch.Success) parts.Add($"視程: {visMatch.Groups["vis"].Value}km");

        Match tempMatch = TempDpRegex().Match(text);
        if (tempMatch.Success)
        {
            string temp = tempMatch.Groups["temp"].Value.Replace("M", "-");
            string dp = tempMatch.Groups["dp"].Success
                ? tempMatch.Groups["dp"].Value.Replace("M", "-")
                : string.Empty;
            parts.Add(string.IsNullOrEmpty(dp) ? $"気温: {temp}℃" : $"気温: {temp}℃/露点: {dp}℃");
        }

        Match qnhMatch = QnhRegex().Match(text);
        if (qnhMatch.Success) parts.Add($"QNH: {qnhMatch.Groups["qnh"].Value}hPa");

        summary = parts.Count > 0 ? string.Join(" / ", parts) : CleanFullRawText(text);
        return true;
    }

    /// <summary>
    /// Gate / Ground Operations messages. ARINC 618 labels 30/31/36/39.
    /// Also detects gate/stand assignments by keyword even without a specific label.
    /// </summary>
    private static bool TryInterpretGateOps(string label, string text, out string summary)
    {
        bool isGateLabel = label is "30" or "31" or "36" or "39";
        bool hasGateKeyword = GateKeywordRegex().IsMatch(text);

        if (!isGateLabel && !hasGateKeyword)
        {
            summary = string.Empty;
            return false;
        }

        string opType = label switch
        {
            "30" => "[出発前クリアランス (PDC)]",
            "31" => "[D-ATIS 出発情報]",
            "36" => "[出発ゲート情報]",
            "39" => "[到着ゲート情報]",
            _ => "[地上運航情報]"
        };

        var sb = new StringBuilder();
        sb.AppendLine(opType);

        List<string> parts = [];

        Match fltMatch = FlightNumberRegex().Match(text);
        if (fltMatch.Success) parts.Add($"便名: {fltMatch.Groups["flt"].Value}");

        Match aptMatch = AirportPairRegex().Match(text);
        if (aptMatch.Success)
        {
            string dep = FormatAirport(aptMatch.Groups["dep"].Value);
            string arr = aptMatch.Groups["arr"].Success
                ? FormatAirport(aptMatch.Groups["arr"].Value)
                : string.Empty;
            parts.Add(string.IsNullOrEmpty(arr) ? $"空港: {dep}" : $"区間: {dep} → {arr}");
        }

        Match gateMatch = GateNumberRegex().Match(text);
        if (gateMatch.Success) parts.Add($"ゲート: {gateMatch.Groups["gate"].Value}");

        Match standMatch = ParkingStandRegex().Match(text);
        if (standMatch.Success) parts.Add($"スタンド: {standMatch.Groups["stand"].Value}");

        Match rwyMatch = RunwayRegex().Match(text);
        if (rwyMatch.Success) parts.Add($"使用滑走路: {rwyMatch.Groups["rwy"].Value}");

        Match etaMatch = EtaRegex().Match(text);
        if (etaMatch.Success) parts.Add($"ETA: {FormatTime(etaMatch.Groups["time"].Value)} UTC");

        if (parts.Count > 0) sb.AppendLine(string.Join(" / ", parts));

        string cleanBody = CleanFullRawText(text);
        if (!string.IsNullOrWhiteSpace(cleanBody)) sb.Append(cleanBody);

        summary = sb.ToString().TrimEnd();
        // Only trigger on keyword match if we found at least one structured field
        return isGateLabel || parts.Count > 1;
    }

    /// <summary>
    /// Fuel log, performance, and weight-and-balance reports. ARINC 618 labels 16/17/18.
    /// </summary>
    private static bool TryInterpretFuelPerf(string label, string text, out string summary)
    {
        bool isFuelLabel = label is "16" or "17" or "18";
        bool hasFuelKeyword = FuelKeywordRegex().IsMatch(text);

        if (!isFuelLabel && !hasFuelKeyword)
        {
            summary = string.Empty;
            return false;
        }

        string reportType = label switch
        {
            "16" => "[燃料ログ]",
            "17" => "[性能レポート]",
            "18" => "[重量・バランス]",
            _ => "[燃料・性能データ]"
        };

        var sb = new StringBuilder();
        sb.AppendLine(reportType);

        List<string> parts = [];

        Match fltMatch = FlightNumberRegex().Match(text);
        if (fltMatch.Success) parts.Add($"便名: {fltMatch.Groups["flt"].Value}");

        Match aptMatch = AirportPairRegex().Match(text);
        if (aptMatch.Success)
        {
            string dep = FormatAirport(aptMatch.Groups["dep"].Value);
            string arr = aptMatch.Groups["arr"].Success
                ? FormatAirport(aptMatch.Groups["arr"].Value)
                : string.Empty;
            if (!string.IsNullOrEmpty(arr)) parts.Add($"区間: {dep} → {arr}");
        }

        Match fobMatch = FuelValueRegex().Match(text);
        if (fobMatch.Success)
        {
            string unit = fobMatch.Groups["unit"].Success ? " " + fobMatch.Groups["unit"].Value : string.Empty;
            parts.Add($"搭載燃料(FOB): {fobMatch.Groups["val"].Value}{unit}");
        }

        Match zfwMatch = ZfwRegex().Match(text);
        if (zfwMatch.Success) parts.Add($"ZFW: {zfwMatch.Groups["val"].Value}t");

        Match towMatch = TowRegex().Match(text);
        if (towMatch.Success) parts.Add($"TOW: {towMatch.Groups["val"].Value}t");

        Match ldwMatch = LdwRegex().Match(text);
        if (ldwMatch.Success) parts.Add($"LDW: {ldwMatch.Groups["val"].Value}t");

        if (parts.Count > 0) sb.AppendLine(string.Join(" / ", parts));
        else sb.Append(CleanFullRawText(text));

        summary = sb.ToString().TrimEnd();
        // Avoid false positives from keyword-only matches without structural fields
        return isFuelLabel || parts.Count > 1;
    }

    /// <summary>
    /// ARINC 618 H1/H2/H3 AOC Free Text — Airline Operational Control communications.
    /// H1 = downlink (aircraft to ground), H2 = uplink (ground to aircraft).
    /// Extracts flight info and classifies message content by keywords.
    /// </summary>
    private static bool TryInterpretFreeText(string label, string text, out string summary)
    {
        if (label is not ("H1" or "H2" or "H3"))
        {
            summary = string.Empty;
            return false;
        }

        string direction = label switch
        {
            "H1" => "[AOC フリーテキスト 下り (機→地) H1]",
            "H2" => "[AOC フリーテキスト 上り (地→機) H2]",
            _ => "[AOC フリーテキスト通信 H3]"
        };

        var sb = new StringBuilder();
        sb.AppendLine(direction);

        List<string> meta = [];

        Match fltMatch = FlightNumberRegex().Match(text);
        if (fltMatch.Success) meta.Add($"便名: {fltMatch.Groups["flt"].Value}");

        Match aptMatch = AirportPairRegex().Match(text);
        if (aptMatch.Success)
        {
            string dep = FormatAirport(aptMatch.Groups["dep"].Value);
            string arr = aptMatch.Groups["arr"].Success
                ? FormatAirport(aptMatch.Groups["arr"].Value)
                : string.Empty;
            if (!string.IsNullOrEmpty(arr)) meta.Add($"区間: {dep} → {arr}");
        }

        // Content keyword classification for common AOC message categories
        string? contentType = null;
        if (text.Contains("EMERGENCY", StringComparison.OrdinalIgnoreCase)) contentType = "緊急事態";
        else if (text.Contains("MEDICAL", StringComparison.OrdinalIgnoreCase)) contentType = "医療状態";
        else if (text.Contains("DIVERT", StringComparison.OrdinalIgnoreCase)) contentType = "代替空港への転航";
        else if (text.Contains("RETURN", StringComparison.OrdinalIgnoreCase)) contentType = "出発空港への引き返し";
        else if (text.Contains("TECHNICAL", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("MECH", StringComparison.OrdinalIgnoreCase)) contentType = "技術的問題";
        else if (text.Contains("DELAY", StringComparison.OrdinalIgnoreCase)) contentType = "遅延通知";
        else if (text.Contains("FUEL", StringComparison.OrdinalIgnoreCase)) contentType = "燃料関連";
        else if (text.Contains("WX") ||
                 text.Contains("WEATHER", StringComparison.OrdinalIgnoreCase)) contentType = "気象関連";
        else if (text.Contains("LANDING", StringComparison.OrdinalIgnoreCase)) contentType = "着陸情報";
        else if (text.Contains("TAKEOFF", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("DEPARTURE", StringComparison.OrdinalIgnoreCase)) contentType = "離陸・出発情報";
        else if (text.Contains("CONFIRM", StringComparison.OrdinalIgnoreCase)) contentType = "確認";
        else if (text.Contains("REQUEST", StringComparison.OrdinalIgnoreCase)) contentType = "要求";

        if (contentType != null) meta.Add($"種別: {contentType}");

        Match etaMatch = EtaRegex().Match(text);
        if (etaMatch.Success) meta.Add($"ETA: {FormatTime(etaMatch.Groups["time"].Value)} UTC");

        if (meta.Count > 0) sb.AppendLine(string.Join(" / ", meta));
        sb.Append(CleanFullRawText(text));

        summary = sb.ToString().TrimEnd();
        return meta.Count > 0;
    }
}

