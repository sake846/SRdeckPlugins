namespace SRdeckPlugin.Hfdl.Models;

public enum HfdlModulation { Bpsk, Qpsk, EightPsk }

public sealed record HfdlFrame(byte[] Bytes, DateTimeOffset ReceivedAt, Guid StreamId,
    long SamplePosition, long FrequencyHz, HfdlModulation Modulation, double SignalQuality);

public sealed record HfdlMessage(byte Type, string Kind, int? SourceAddress, int? DestinationAddress,
    string FlightId, byte[] Payload, bool IsCrcValid,
    int GroundStationId = 0, int? AircraftId = null, string AircraftIcao = "",
    string Registration = "", bool IsGroundStationMessage = false, bool IsUplink = false)
{
    public string Summary => string.IsNullOrWhiteSpace(FlightId)
        ? $"{Kind}, {Payload.Length} bytes" : $"{Kind} from {FlightId}";
}

public sealed record HfdlReception(DateTimeOffset ReceivedAt, long FrequencyHz, string Kind,
    string FlightId, string SourceAddress, string DestinationAddress, string PayloadHex,
    HfdlModulation Modulation, double SignalQuality,
    byte Type = 0,
    bool IsCrcValid = true,
    Guid StreamId = default,
    long SamplePosition = 0,
    string RawFrameHex = "",
    string ChannelId = "",
    string GroundStationId = "",
    int? AircraftId = null,
    string AircraftIcao = "",
    string Registration = "",
    bool IsGroundStationMessage = false,
    int DecoderVersion = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string AircraftDisplay => IsGroundStationMessage ? $"地上局 {GroundStationId}" :
        !string.IsNullOrWhiteSpace(FlightId) ? FlightId.Trim() :
        !string.IsNullOrWhiteSpace(Registration) ? Registration.Trim() :
        !string.IsNullOrWhiteSpace(AircraftIcao) ? $"ICAO {AircraftIcao}" :
        AircraftId is int id ? $"機体 ID {id} (GS {GroundStationId})" : "機体不明";

    [System.Text.Json.Serialization.JsonIgnore]
    public string FrequencyDisplay => $"{FrequencyHz / 1_000_000.0:F3} MHz";

    public HfdlReception WithMessage(HfdlMessage message) => this with
    {
        Type = message.Type, Kind = message.Kind, FlightId = message.FlightId,
        SourceAddress = Address(message.SourceAddress, message.IsUplink || message.IsGroundStationMessage),
        DestinationAddress = Address(message.DestinationAddress, !message.IsUplink),
        PayloadHex = Convert.ToHexString(message.Payload), IsCrcValid = message.IsCrcValid,
        GroundStationId = message.GroundStationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AircraftId = message.AircraftId, AircraftIcao = message.AircraftIcao,
        Registration = message.Registration, IsGroundStationMessage = message.IsGroundStationMessage,
        DecoderVersion = 1
    };

    private static string Address(int? id, bool station) => id is null ? "" :
        station ? $"GS {id}" : id == 255 ? "Broadcast" : $"AC {id}";
}
