namespace SRdeckPlugin.Ft8.Models;

using SRdeckPlugin.Ft8.Dsp;

public enum WeakSignalMode
{
    FT8,
    FT4,
    JT65
}

public sealed record Ft8Band(
    string Id,
    string Band,
    long DialFrequencyHz,
    string Region = "世界共通",
    WeakSignalMode Mode = WeakSignalMode.FT8)
{
    public long ChannelCenterFrequencyHz => DialFrequencyHz + Ft8Receiver.AudioCenterHz;
    public string DisplayName => $"{Mode} / {Band}  {DialFrequencyHz / 1_000_000.0:F6} MHz";
    public string BandDisplayName => $"{Band}  {DialFrequencyHz / 1_000_000.0:F6} MHz";
}

public sealed record Ft8Reception(
    DateTimeOffset SlotStart,
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    long FrequencyHz,
    int AudioFrequencyHz,
    double TimeOffsetSeconds,
    int SnrDb,
    int SyncScore,
    string Message,
    string MessageType,
    string FromCall,
    string ToCall,
    string Extra,
    byte[] Payload,
    WeakSignalMode Mode = WeakSignalMode.FT8)
{
    public DateTime LocalReceivedAt => ReceivedAt.LocalDateTime;
    public string BandFrequencyText => $"{FrequencyHz / 1_000_000.0:F6} MHz";
    public string PayloadHex => Convert.ToHexString(Payload);
}

public readonly record struct Ft8DecoderDiagnostics(
    int InputSampleRateHz,
    int BufferedSamples,
    long SlotsProcessed,
    long CandidatesExamined,
    long ValidMessages,
    long LdpcRejected,
    long CrcRejected,
    TimeSpan LastDecodeDuration,
    DateTimeOffset? LastSlotStart,
    int LastSlotValidMessages = 0,
    DateTimeOffset? LastDecodedSlotStart = null,
    double IntermediateSampleRateHz = 0,
    bool UsesHostChannelRateConversion = false,
    double ChannelLevelDbfs = double.NegativeInfinity);
