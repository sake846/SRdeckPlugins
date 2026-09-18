using SRdeck.DSP;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Contracts;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Services;

public readonly record struct MeshtasticReceiveSnapshot(
    bool IsTargetInPassband,
    long SubmittedBlocks,
    long ProcessedBlocks,
    long DroppedBlocks,
    int QueueDepth,
    int MaximumQueueDepth,
    double CurrentQueueDelayMs,
    double AverageQueueDelayMs,
    double MaximumQueueDelayMs,
    double CurrentProcessingTimeMs,
    double AverageProcessingTimeMs,
    double MaximumProcessingTimeMs,
    double CurrentInputBlockTimeMs,
    double CurrentProcessingLoadPercent,
    double AverageProcessingLoadPercent,
    double MaximumProcessingLoadPercent,
    long DetectedPreambles,
    long SynchronizedFrames,
    long DecodedHeaders,
    long DecodedPayloads,
    long ParsedMeshtasticPackets,
    long DuplicateMeshtasticPackets,
    long DecodedMeshtasticData,
    LoRaPreambleDetection? LastDetection,
    LoRaFrameSynchronization? LastSynchronization,
    LoRaExplicitHeader? LastHeader,
    LoRaPayloadFrame? LastPayload,
    MeshtasticRadioPacket? LastMeshtasticPacket,
    MeshtasticData? LastMeshtasticData,
    double OldestDeferredIqMs,
    double DeferredRetentionRemainingMs,
    long DeferredRecoveredBlocks,
    long ExpiredHistoryBlocks,
    double AverageChannelizationCpuMs = 0,
    double AverageDetectionCpuMs = 0,
    int InputSampleRateHz = 0)
{
    public MeshtasticWorkerFailure? WorkerFailure { get; init; }
    public long RetainedIqBytes { get; init; }
    public double RetainedIqMilliseconds { get; init; }
    public long QueueLimitDroppedBlocks { get; init; }
}

public sealed record MeshtasticWorkerFailure(
    DateTimeOffset Timestamp, long Sequence, long AbsoluteSampleStart, Exception Exception);

public sealed record MeshtasticPacketReception(
    MeshtasticRadioPacket Packet,
    bool IsDuplicate,
    int SeenCount,
    bool IsDataDecoded,
    MeshtasticLoRaReceptionQuality Quality,
    MeshtasticRadioReception Radio);

public sealed record MeshtasticBandwidthSlots(int BandwidthHz, IReadOnlyList<int> RadioChannels);

public sealed record MeshtasticDataReception(
    MeshtasticRadioPacket Packet,
    MeshtasticData Data,
    bool IsDuplicate,
    int SeenCount,
    MeshtasticLoRaReceptionQuality Quality)
{
    public MeshtasticRadioReception Radio { get; init; } = MeshtasticRadioReception.Unknown;
}

public sealed record MeshtasticRadioReception(
    MeshtasticRegion Region,
    int RadioChannel,
    int FrequencyHz,
    int BandwidthHz,
    int SpreadingFactor,
    int CodingRateDenominator)
{
    public static MeshtasticRadioReception Unknown { get; } = new(default, 0, 0, 0, 0, 0);

    public string Summary => RadioChannel <= 0
        ? "受信モード: -"
        : $"{Region} slot {RadioChannel}  {FrequencyHz / 1_000_000.0:F4} MHz  /  BW {BandwidthHz / 1000.0:0.###} kHz  /  SF{SpreadingFactor}  CR 4/{CodingRateDenominator}";
}

public sealed record MeshtasticLoRaReceptionQuality(
    float? EstimatedSnrDb,
    float? PreamblePeakHz,
    float? SyncUpPeakHz,
    float? SyncDownPeakHz,
    bool? PayloadCrcValid,
    int CorrectedCodewords)
{
    public float? EstimatedRssiDbfs { get; init; }
    public float? EstimatedRssiDbm { get; init; }

    public string Summary =>
        $"{FormatSignal(EstimatedSnrDb, EstimatedRssiDbfs, EstimatedRssiDbm)} / " +
        $"CRC {(PayloadCrcValid switch { true => "OK", false => "NG", _ => "-" })} / " +
        $"Corr {CorrectedCodewords}";

    public string Details =>
        $"estimatedSnrDb={(EstimatedSnrDb is float snr && float.IsFinite(snr) ? snr.ToString("F1") : "-")} " +
        $"estimatedRssiDbfs={(EstimatedRssiDbfs is float dbfs && float.IsFinite(dbfs) ? dbfs.ToString("F1") : "-")} " +
        $"estimatedRssiDbm={(EstimatedRssiDbm is float dbm && float.IsFinite(dbm) ? dbm.ToString("F1") : "-")} " +
        $"preamblePeakHz={PreamblePeakHz?.ToString("F1") ?? "-"} " +
        $"syncUpPeakHz={SyncUpPeakHz?.ToString("F1") ?? "-"} " +
        $"syncDownPeakHz={SyncDownPeakHz?.ToString("F1") ?? "-"} " +
        $"payloadCrc={PayloadCrcValid?.ToString() ?? "-"} correctedCodewords={CorrectedCodewords}";

    public const string SnrDescription = "SNR は受信帯域基準の推定SNR（繰り返しを確認したプリアンブルから算出）。LoRaチップの報告値とは推定方法が異なります。";
    public const string RssiDescription = "RSSI は同じプリアンブルの帯域内電力（信号＋雑音）の推定値。dBm はSRdeckの受信レベル校正に依存します。校正情報がない場合はdBFSで表示します。";
    public static string SignalDescription => SnrDescription + Environment.NewLine + RssiDescription;

    public static string FormatSnr(float? value) => value is float snr && float.IsFinite(snr)
        ? $"SNR {snr:+0.0;-0.0;0.0} dB"
        : "SNR —";

    public static string FormatRssi(float? dbfs, float? dbm = null) =>
        dbm is float calibrated && float.IsFinite(calibrated) ? $"RSSI {calibrated:F1} dBm" :
        dbfs is float relative && float.IsFinite(relative) ? $"RSSI {relative:F1} dBFS" : "RSSI —";

    public static string FormatSignal(float? snr, float? dbfs, float? dbm = null) =>
        $"{FormatSnr(snr)} / {FormatRssi(dbfs, dbm)}";

    internal static float? CalibrateRssi(float? dbfs, IPluginReceiverTelemetry? telemetry)
    {
        if (dbfs is not float value || !float.IsFinite(value) || telemetry is null) return null;
        float calibrated = telemetry.DbfsToDbm(value);
        return float.IsFinite(calibrated) ? calibrated : null;
    }

    internal MeshtasticLoRaReceptionQuality WithReceiverCalibration(IPluginReceiverTelemetry? telemetry) =>
        this with { EstimatedRssiDbm = CalibrateRssi(EstimatedRssiDbfs, telemetry) };

    internal static string HideLegacyQuality(string value)
    {
        if (value.StartsWith("Q ", StringComparison.OrdinalIgnoreCase))
        {
            int separator = value.IndexOf(" / ", StringComparison.Ordinal);
            value = "SNR —" + (separator >= 0 ? value[separator..] : "");
        }
        else if (value.StartsWith("S/N ", StringComparison.Ordinal)) value = "SNR " + value[4..];
        if (value.StartsWith("SNR ", StringComparison.Ordinal) && !value.Contains(" / RSSI ", StringComparison.Ordinal))
        {
            int separator = value.IndexOf(" / ", StringComparison.Ordinal);
            value = separator >= 0 ? value.Insert(separator, " / RSSI —") : value + " / RSSI —";
        }
        return value;
    }
}
