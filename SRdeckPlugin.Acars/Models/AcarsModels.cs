namespace SRdeckPlugin.Acars.Models;

public sealed record AcarsFrame(
    byte[] Bytes,
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    long SamplePosition,
    long FrequencyHz,
    double SignalQuality);

public sealed record AcarsMessage(
    string Mode,
    string AircraftRegistration,
    string Acknowledgement,
    string Label,
    string BlockId,
    string Text,
    bool IsBlockCheckValid,
    bool HasValidOddParity,
    bool IsContinuationBlock)
{
    public string Summary => string.IsNullOrWhiteSpace(Text)
        ? $"{Label} from {AircraftRegistration}"
        : Text;
}

public sealed record AcarsReception(
    DateTimeOffset ReceivedAt,
    long FrequencyHz,
    string Aircraft,
    string Label,
    string BlockId,
    string Text,
    double SignalQuality,
    string Mode = "",
    string Acknowledgement = "",
    bool IsBlockCheckValid = true,
    bool HasValidOddParity = true,
    bool IsContinuationBlock = false,
    string RawHex = "",
    Guid StreamId = default,
    long SamplePosition = 0)
{
    public string AircraftDisplay => NormalizeRegistration(Aircraft);

    public static string NormalizeRegistration(string? value)
    {
        string trimmed = value?.Trim().TrimStart('.') ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed) ? "機体不明" : trimmed;
    }
    public string FrequencyDisplay => $"{FrequencyHz / 1_000_000.0:F3} MHz";
    public string SignalQualityDisplay => $"Q: {SignalQuality * 100:F0}%";
    public string SummaryText => Protocols.AcarsMessageInterpreter.Interpret(Label, Text);
    public string SingleLineSummary
    {
        get
        {
            string summary = SummaryText;
            return summary.IndexOfAny(['\r', '\n']) >= 0
                ? summary.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim()
                : summary;
        }
    }
    public string DecodedText => Protocols.AcarsMessageInterpreter
        .InterpretDetailed(Label, Text).DecodedText;
    public string ProprietaryText => Protocols.AcarsMessageInterpreter
        .InterpretDetailed(Label, Text).ProprietaryText;
    public string UninterpretedText => Protocols.AcarsMessageInterpreter
        .InterpretDetailed(Label, Text).UninterpretedText;
    public bool HasPosition => Protocols.AcarsPositionParser.TryParse(Text, out _, out _);

    public string CategoryDisplay
    {
        get
        {
            string lbl = Label?.ToUpperInvariant() ?? string.Empty;
            string txt = Text?.ToUpperInvariant() ?? string.Empty;
            if (lbl is "5V" or "20" or "21" or "22" or "23" || txt.Contains("POS") || txt.Contains("LAT") || txt.Contains("LON")) return "位置";
            if (lbl is "00" or "QA" or "Q0" or "10" or "11" or "12" or "13" or "15" || txt.Contains("OUT") || txt.Contains("OFF") || txt.Contains("ON") || txt.Contains("IN") || txt.Contains("GATE")) return "発着";
            if (lbl is "51" or "52" or "80" or "D1" or "S6" or "S7" or "5U" or "RA" or "RB" or "RC" || txt.Contains("METAR") || txt.Contains("TAF") || txt.Contains("PIREP") || txt.Contains("WXRQ")) return "気象";
            if (lbl is "B5" or "B6" or "B9" or "5D" || txt.Contains("CPDLC") || txt.Contains("ATIS") || txt.Contains("ATC")) return "管制";
            if (lbl is "Q1" or "Q2" or "Q3" or "Q4" or "40" or "41" or "42" or "43" or "44" or "45" or "4A" || txt.Contains("#DFB") || txt.Contains("#DFD") || txt.Contains("#DFR") || txt.Contains("ACMS") || txt.Contains("ENG")) return "機体";
            if (lbl is "14" or "16" or "17" or "18" or "30" or "31" or "36" or "39" or "H1" or "H2" or "H3" || txt.Contains("ETA") || txt.Contains("FPL") || txt.Contains("FUEL")) return "運航";
            return "情報";
        }
    }
}
