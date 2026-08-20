namespace SRdeckPlugin.Vdl.Models;

public enum AvlcAddressType { Reserved = 0, Aircraft = 1, GroundStationAdministrative = 4, GroundStationDelivery = 5, AllStations = 7 }
public enum AvlcFrameKind { Information, Supervisory, Unnumbered }

public sealed record AvlcAddress(uint Value, AvlcAddressType Type, bool Status)
{
    public string Address => $"{Value:X6}";
    public string TypeName => Type switch
    {
        AvlcAddressType.Aircraft => "Aircraft",
        AvlcAddressType.GroundStationAdministrative => "GS-ADM",
        AvlcAddressType.GroundStationDelivery => "GS-DEL",
        AvlcAddressType.AllStations => "All",
        _ => "Reserved"
    };
}

public sealed record AvlcPacket(AvlcAddress Destination, AvlcAddress Source, byte Control,
    AvlcFrameKind Kind, string FrameType, int? SendSequence, int? ReceiveSequence,
    bool PollFinal, byte[] Information);

public sealed record VdlAcarsMessage(string Mode, string Registration, string Acknowledgement,
    string Label, char BlockId, string MessageNumber, char MessageSequence, string FlightId,
    string Text, bool FinalBlock, bool CrcValid, string ReassemblyStatus);

public sealed record VdlX25Packet(int LogicalChannel, string PacketType, int? SendSequence,
    int? ReceiveSequence, bool MoreData, string UpperProtocol, byte[] UserData);

public sealed record VdlFrame(
    byte[] Payload,
    DateTimeOffset ReceivedAt,
    long FrequencyHz,
    Guid StreamId = default,
    long SamplePosition = 0,
    double SignalQuality = double.NaN,
    double PreambleSnrDb = double.NaN,
    double PreambleCoherence = double.NaN)
{
    public string Hex => Convert.ToHexString(Payload);
    public string Summary => Payload.Length >= 2
        ? $"AVLC {Payload[0]:X2} {Payload[1]:X2} / {Payload.Length} bytes"
        : $"AVLC / {Payload.Length} bytes";
}

public sealed record VdlDecodedFrame(VdlFrame Raw, AvlcPacket? Avlc, VdlAcarsMessage? Acars,
    VdlX25Packet? X25, string Protocol, string ParseStatus)
{
    public DateTimeOffset ReceivedAt => Raw.ReceivedAt.ToLocalTime();
    public long FrequencyHz => Raw.FrequencyHz;
    public string Hex => Raw.Hex;
    public string Source => Avlc is null ? "-" : $"{Avlc.Source.Address} ({Avlc.Source.TypeName})";
    public string Destination => Avlc is null ? "-" : $"{Avlc.Destination.Address} ({Avlc.Destination.TypeName})";
    public string FrameType => Avlc?.FrameType ?? "不明";
    public string Callsign => !string.IsNullOrWhiteSpace(Acars?.FlightId) ? Acars.FlightId.Trim() :
        !string.IsNullOrWhiteSpace(Acars?.Registration) ? Acars.Registration.Trim() :
        Avlc?.Source.Type == AvlcAddressType.Aircraft ? Avlc.Source.Address :
        Avlc?.Destination.Type == AvlcAddressType.Aircraft ? Avlc.Destination.Address : "-";
    public string Text => Acars?.Text ?? ParseStatus;
    public string Summary => $"{Protocol} {Source} → {Destination} {FrameType}" +
        (Callsign == "-" ? string.Empty : $" / {Callsign}") +
        (string.IsNullOrWhiteSpace(Acars?.Text) ? string.Empty : $" / {Acars.Text}");
}
