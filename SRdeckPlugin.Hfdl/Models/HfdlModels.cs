namespace SRdeckPlugin.Hfdl.Models;

public enum HfdlModulation { Bpsk, Qpsk, EightPsk }

public sealed record HfdlFrame(byte[] Bytes, DateTimeOffset ReceivedAt, Guid StreamId,
    long SamplePosition, long FrequencyHz, HfdlModulation Modulation, double SignalQuality);

public sealed record HfdlMessage(byte Type, string Kind, int? SourceAddress, int? DestinationAddress,
    string FlightId, byte[] Payload, bool IsCrcValid)
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
    string GroundStationId = "");
