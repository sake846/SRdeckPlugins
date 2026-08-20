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
    public string SummaryText => Protocols.AcarsMessageInterpreter.Interpret(Label, Text);
    public string DecodedText => Protocols.AcarsMessageInterpreter
        .InterpretDetailed(Label, Text).DecodedText;
    public string ProprietaryText => Protocols.AcarsMessageInterpreter
        .InterpretDetailed(Label, Text).ProprietaryText;
    public string UninterpretedText => Protocols.AcarsMessageInterpreter
        .InterpretDetailed(Label, Text).UninterpretedText;
    public bool HasPosition => Protocols.AcarsPositionParser.TryParse(Text, out _, out _);
}
