using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.Acars.Models;

namespace SRdeckPlugin.Acars.Protocols;

/// <summary>
/// Full-specification ACARS message decoder and human-readable plain language interpreter.
/// Decodes ARINC 618 / 620 / 624 labels, telemetry, OOOI, METAR, ADS-B hex squitters, and ACMS engine reports.
/// </summary>
public static partial class AcarsMessageInterpreter
{
    private static readonly AcarsInterpretationPipeline InterpretationPipeline =
        CreateInterpretationPipeline();

    public static string Interpret(string? label, string? text)
    {
        TryInterpret(label, text, out string summary);
        return summary;
    }

    public static AcarsInterpretation InterpretDetailed(string? label, string? text)
    {
        string labelKey = label?.Trim().ToUpperInvariant() ?? string.Empty;
        string rawText = text?.Trim() ?? string.Empty;
        bool decoded = TryInterpret(labelKey, rawText, out string summary);
        string cleanRaw = CleanFullRawText(rawText);

        if (decoded)
        {
            const string uninterpretedMarker = "未解読データ:";
            int uninterpretedStart = summary.IndexOf(uninterpretedMarker, StringComparison.Ordinal);
            if (uninterpretedStart >= 0)
            {
                return new(summary[..uninterpretedStart].TrimEnd(), string.Empty,
                    summary[uninterpretedStart..].Trim());
            }

            string[] proprietaryMarkers =
                ["航空会社・機種固有", "航空会社固有データ", "航空会社固有:"];
            int proprietaryStart = proprietaryMarkers
                .Select(marker => summary.IndexOf(marker, StringComparison.Ordinal))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (proprietaryStart >= 0)
            {
                return new(summary[..proprietaryStart].TrimEnd(),
                    summary[proprietaryStart..].Trim(), string.Empty);
            }

            if (IsAirlineSpecificFormat(labelKey, rawText) &&
                !string.IsNullOrEmpty(cleanRaw) &&
                summary.EndsWith(cleanRaw, StringComparison.Ordinal))
            {
                string decodedPrefix = summary[..^cleanRaw.Length].TrimEnd();
                if (!string.IsNullOrEmpty(decodedPrefix))
                    return new(decodedPrefix, cleanRaw, string.Empty);
            }

            return new(summary, string.Empty, string.Empty);
        }

        return IsAirlineSpecificFormat(labelKey, rawText)
            ? new(string.Empty, summary, string.Empty)
            : new(string.Empty, string.Empty, summary);
    }

    private static bool IsAirlineSpecificFormat(string label, string text) =>
        label is "H1" or "H2" or "H3" or "SA" or "SB" or "S3" or
            "5D" or "5V" or "B9" or "1L" or "1M" ||
        text.Contains("#DFB", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("#DFD", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("#DFR", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("WXRQ", StringComparison.OrdinalIgnoreCase);

    public static bool TryInterpret(string? label, string? text, out string summary)
    {
        string labelKey = label?.Trim().ToUpperInvariant() ?? string.Empty;
        string rawText = text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(rawText))
        {
            summary = GetLabelDescription(labelKey, out string labelName)
                ? $"[{labelName}] 本文なし"
                : $"[Label {labelKey}] 本文なし";
            return true;
        }

        if (InterpretationPipeline.TryInterpret(
            new(labelKey, rawText), out summary, out bool isInterpreted))
            return isInterpreted;

        throw new InvalidOperationException(
            "The ACARS interpretation pipeline must provide a fallback rule.");
    }

    internal static IReadOnlyList<AcarsInterpretationRuleDefinition> InterpretationRules =>
        InterpretationPipeline.Rules;

    private static AcarsInterpretationPipeline CreateInterpretationPipeline() =>
        new(
        [
            // AFN/CPDLC connection confirmation (ARINC 622 CC1).
            new("ARINC 622 CC1 connection confirmation",
                AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretArinc622ConnectionConfirm(input.Text, out summary)),

            // 1. ARINC 622 AT1 CPDLC message.
            new("ARINC 622 AT1 CPDLC",
                AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretArinc622Cpdlc(input.Label, input.Text, out summary)),

            // 1b. ADS-B / FMS / ARINC 622 ADS-C Position & Telemetry report
            new("ADS-B / FMS / ADS-C",
                AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretAdsbAcars(input.Text, out summary)),

            // 2. Transpacific Compact Position & Weather Report
            new("Transpacific position and weather",
                AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretTranspacificPosition(input.Text, out summary)),

            // 3. ARINC 620 Oceanic Position Report (Cathay Pacific / Pacific MI Reports)
            new("ARINC 620 oceanic position",
                AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretArinc620OceanicPosition(input.Text, out summary)),

            // 3. PIREP (Pilot Weather & Turbulence Report)
            new("PIREP", AcarsInterpretationCategory.OooiPositionWeatherFlightPlan,
                TryInterpretPirepRule),

            // 3b. Manufacturer/airline CFB fault reports.
            new("CFB fault report", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretCfbFaultReport(input.Text, out summary)),

            // 4. Compact airline/manufacturer-specific ACMS reports (DFB/DFD/DFR).
            new("Compact ACMS", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretCompactAcmsReport(input.Text, out summary)),

            // 4. ACMS Flight Control / Engine / Telemetry report (Q1/Q2/40/41/H1 ACMS)
            new("Flight control and telemetry", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretFlightControlOrTelemetry(input.Text, out summary)),

            // 4b. Recognizable multipart airline telemetry envelopes (EIB/10B/etc.).
            new("Airline telemetry segment", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretAirlineTelemetrySegment(input.Text, out summary)),

            // 5. Movement Report (OOOI - OUT / OFF / ON / IN)
            new("OOOI movement report", AcarsInterpretationCategory.OooiPositionWeatherFlightPlan,
                TryInterpretOooiRule),

            // 6. Position & Navigation report (5U/5V/20/21/POS)
            new("Position and navigation", AcarsInterpretationCategory.OooiPositionWeatherFlightPlan,
                TryInterpretPositionRule),

            // 7. Flight weather request (WXRQ).
            new("Weather request", AcarsInterpretationCategory.OooiPositionWeatherFlightPlan,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretWeatherRequest(input.Text, out summary)),

            // 7b. METAR / TAF / Airport Weather report
            new("Weather report", AcarsInterpretationCategory.OooiPositionWeatherFlightPlan,
                TryInterpretWeatherRule),

            // 8. Flight Plan / ETA / Gate / Loadsheet report
            new("Flight plan and ETA", AcarsInterpretationCategory.OooiPositionWeatherFlightPlan,
                TryInterpretFlightPlanRule),

            // 8a. D-ATIS requests: standard label 5D and B9/TI2 ATS request.
            new("D-ATIS request", AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretAtisRequest(input.Label, input.Text, out summary)),

            // 8b. CPDLC ATC Data Link Communication (Label B6/21)
            new("CPDLC ATC communication", AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretCpdlc(input.Label, input.Text, out summary)),

            // 8c. D-ATIS / ATIS Terminal Information (Label 51/52/D1)
            new("D-ATIS terminal information", AcarsInterpretationCategory.Arinc620622CpdlcAtis,
                TryInterpretAtisRule),

            // 8d. Gate / Ground Operations (Label 30/31/36/39)
            new("Gate and ground operations", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretGateOps(input.Label, input.Text, out summary)),

            // 8e. Fuel / Performance / Weight Report (Label 16/17/18)
            new("Fuel and performance", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretFuelPerf(input.Label, input.Text, out summary)),

            // 8f. H1/H2/H3 AOC Free Text ARINC 618 structured analysis
            new("AOC free text", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretFreeText(input.Label, input.Text, out summary)),

            // 8g. Common M-series application header and standardized FMC message identifiers.
            // More specific formats above take precedence; unknown airline payload is retained separately.
            new("M-series application message", AcarsInterpretationCategory.AcmsTelemetryAirlineSpecific,
                static (AcarsInterpretationInput input, out string summary) =>
                    TryInterpretMSeriesMessage(input.Label, input.Text, out summary)),

            // 9. Automatic Acknowledgement (ACK / NAK)
            new("Automatic acknowledgement", AcarsInterpretationCategory.CommonFallback,
                TryInterpretAcknowledgementRule),
            new("Automatic OOOI status", AcarsInterpretationCategory.CommonFallback,
                TryInterpretAutomaticOooiRule),

            // 10. Label-based summary for known ARINC labels (Message body not specifically interpreted)
            new("Known-label fallback", AcarsInterpretationCategory.CommonFallback,
                TryInterpretKnownLabelFallbackRule, false),

            // 11. General fallback: full cleaned text (Uninterpreted / Unknown structure)
            new("General fallback", AcarsInterpretationCategory.CommonFallback,
                TryInterpretGeneralFallbackRule, false)
        ]);

    private static bool TryInterpretPirepRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!TryInterpretPirep(input.Text, out string pirepSummary))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[パイロット気象報告 (PIREP)] {pirepSummary}";
        return true;
    }

    private static bool TryInterpretOooiRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!TryInterpretOooi(input.Text, out string oooiSummary))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[動態報告] (OOOI) {oooiSummary}";
        return true;
    }

    private static bool TryInterpretPositionRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!TryInterpretPosition(input.Text, out string positionSummary))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[位置報告] {positionSummary}";
        return true;
    }

    private static bool TryInterpretWeatherRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!TryInterpretWeather(input.Text, out string weatherSummary))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[気象情報] {weatherSummary}";
        return true;
    }

    private static bool TryInterpretFlightPlanRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!TryInterpretFlightPlan(input.Text, out string flightPlanSummary))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[飛行計画・ETA] {flightPlanSummary}";
        return true;
    }

    private static bool TryInterpretAtisRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!TryInterpretAtis(input.Text, out string atisSummary))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[D-ATIS 空港情報] {atisSummary}";
        return true;
    }

    private static bool TryInterpretAcknowledgementRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (input.Label is "_" or "_D" or "SQ")
        {
            summary = "[自動確認応答 (ACK)]";
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretAutomaticOooiRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (input.Label is "Q0" or "QA" or "00")
        {
            summary = $"[動態報告 (OOOI)] 自動ステータス送信 ({CleanRawText(input.Text)})";
            return true;
        }

        summary = string.Empty;
        return false;
    }

    private static bool TryInterpretKnownLabelFallbackRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        if (!GetLabelDescription(input.Label, out string description))
        {
            summary = string.Empty;
            return false;
        }

        summary = $"[{description}]\n{CleanFullRawText(input.Text)}";
        return true;
    }

    private static bool TryInterpretGeneralFallbackRule(
        AcarsInterpretationInput input,
        out string summary)
    {
        summary = CleanFullRawText(input.Text);
        return true;
    }

    private static bool TryInterpretArinc622ConnectionConfirm(string text, out string summary)
    {
        Match match = Arinc622ConnectionConfirmRegex().Match(text);
        if (!match.Success)
        {
            summary = string.Empty;
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[AFN/CPDLC 接続確認 (CC1)]");
        List<string> header = [];
        if (match.Groups["flight"].Success)
            header.Add($"便識別: {match.Groups["flight"].Value.ToUpperInvariant()}");
        if (match.Groups["series"].Success && match.Groups["sequence"].Success)
            header.Add($"メッセージ連番: {match.Groups["series"].Value.ToUpperInvariant()}" +
                match.Groups["sequence"].Value);
        if (header.Count > 0) sb.AppendLine(string.Join(" / ", header));
        sb.AppendLine($"管制施設: {FormatAirport(match.Groups["atsu"].Value)} / " +
            $"機体: {match.Groups["registration"].Value.ToUpperInvariant()}");
        sb.AppendLine("接続状態: Connection Confirm（航空機から地上への接続確認）");
        sb.Append("航空会社固有データ: CC1保証付きバイナリ本文");
        summary = sb.ToString().TrimEnd();
        return true;
    }

    private static bool TryInterpretArinc622Cpdlc(string label, string text, out string summary)
    {
        Match match = Arinc622CpdlcRegex().Match(text);
        if (!match.Success)
        {
            summary = string.Empty;
            return false;
        }

        string series = match.Groups["series"].Value.ToUpperInvariant();
        string sequence = match.Groups["sequence"].Value;
        string flight = match.Groups["flight"].Value.ToUpperInvariant();
        string atsu = match.Groups["atsu"].Value.ToUpperInvariant();
        string registration = match.Groups["registration"].Value.TrimStart('.').ToUpperInvariant();
        string protectedHex = match.Groups["hex"].Value.ToUpperInvariant();

        // The final four hex characters are the ARINC 622 CRC. The preceding
        // bytes contain the ASN.1 PER encoded CPDLC message.
        string payloadHex = protectedHex[..^4];

        var sb = new StringBuilder();
        sb.AppendLine("[CPDLC 管制データリンク (AT1)]");
        List<string> header = [];
        if (!string.IsNullOrEmpty(flight)) header.Add($"便識別: {flight}");
        if (!string.IsNullOrEmpty(series) && !string.IsNullOrEmpty(sequence))
            header.Add($"メッセージ連番: {series}{sequence}");
        if (header.Count > 0) sb.AppendLine(string.Join(" / ", header));
        sb.AppendLine($"管制施設: {FormatAirport(atsu)} / 機体: {registration}");

        bool isDownlink = label.Equals("BA", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(series);
        if (!isDownlink)
        {
            sb.AppendLine("未解読データ: CPDLCアップリンク本文（方向判定済み・未対応）");
            sb.Append($"HEX: {payloadHex}");
            summary = sb.ToString().TrimEnd();
            return true;
        }

        if (AcarsCpdlcDecoder.TryDecodeDownlink(payloadHex, out var cpdlc) && cpdlc != null)
        {
            List<string> cpdlcHeader = [$"メッセージID: {cpdlc.Header.MessageId}"];
            if (cpdlc.Header.ReferenceId.HasValue)
                cpdlcHeader.Add($"参照ID: {cpdlc.Header.ReferenceId.Value}");
            if (cpdlc.Header.Timestamp.HasValue)
                cpdlcHeader.Add($"時刻: {cpdlc.Header.Timestamp.Value:hh\\:mm\\:ss} UTC");
            sb.AppendLine(string.Join(" / ", cpdlcHeader));

            foreach (var element in cpdlc.Elements.Where(element => element.FullyDecoded))
                sb.AppendLine($"応答・要求: {element.JapaneseText}");

            if (cpdlc.FullyDecoded)
            {
                summary = sb.ToString().TrimEnd();
                return true;
            }

            var unresolved = cpdlc.Elements.Where(element => !element.FullyDecoded).ToArray();
            sb.AppendLine("未解読データ: 未対応のCPDLC ASN.1 PER要素");
            if (unresolved.Length > 0)
                sb.AppendLine(string.Join(" / ", unresolved.Select(element => element.JapaneseText)));

            sb.Append($"HEX: {payloadHex}");
            summary = sb.ToString().TrimEnd();
            return true;
        }

        sb.AppendLine("未解読データ: CPDLC ASN.1 PER本文");
        sb.Append($"HEX: {payloadHex}");
        summary = sb.ToString().TrimEnd();
        return true;
    }

    public static bool GetLabelDescription(string label, out string description)
    {
        description = label switch
        {
            "00" or "QA" or "Q0" => "動態報告 (OOOI)",
            "Q1" or "Q2" or "Q3" or "Q4" => "エンジン・機体制御データ (ACMS)",
            "40" or "41" or "42" or "43" or "4A" or "44" or "45" => "エンジン・APUステータス (ECTM/ACMS)",
            "B5" or "B6" => "CPDLC / FMS 航法・データリンク",
            "B9" or "5D" => "D-ATIS空港情報要求",
            "51" or "52" or "80" or "D1" or "S6" or "S7" => "気象情報 (METAR/TAF/ATIS)",
            "5U" => "気象情報要求",
            "5V" => "位置・高度・速度報告",
            "10" or "11" or "12" or "13" or "15" => "飛行計画 / ETA / 荷重報告",
            "16" or "17" or "18" => "燃料ログ / 性能レポート",
            "20" or "21" or "22" or "23" => "Waypoint / 航路進行報告 (CPDLC)",
            "30" or "31" or "36" or "39" => "出発・到着・ゲート情報",
            "H1" or "H2" or "H3" => "AOC フリーテキスト通信",
            "RA" or "RB" or "RC" => "上空気象・揺れ報告 (PIREP)",
            "SA" or "SB" or "S3" => "機体ステータス・テレメトリ",
            "_" or "_D" or "SQ" => "確認応答 (ACK)",
            "1L" or "1M" => "パイロット認証・ログイン",
            "1N" or "1P" => "乗務員情報",
            _ => string.Empty
        };
        return !string.IsNullOrEmpty(description);
    }
}

public sealed record AcarsInterpretation(
    string DecodedText,
    string ProprietaryText,
    string UninterpretedText)
{
    // Airline/manufacturer-specific content remains visible in the UI and raw
    // data panel, but only genuinely uninterpreted standard/unknown content is
    // written to the review log.
    public bool RequiresReviewLog => !string.IsNullOrWhiteSpace(UninterpretedText);
}
