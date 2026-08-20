using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    int InputSampleRateHz = 0);

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
    float? PreambleMarginDb,
    float? PreamblePeakHz,
    float? SyncUpPeakHz,
    float? SyncDownPeakHz,
    bool? PayloadCrcValid,
    int CorrectedCodewords)
{
    public string Summary =>
        $"Q {(PreambleMarginDb.HasValue ? $"{PreambleMarginDb.Value:F1} dB" : "-")} / " +
        $"CRC {(PayloadCrcValid switch { true => "OK", false => "NG", _ => "-" })} / " +
        $"Corr {CorrectedCodewords}";

    public string Details =>
        $"marginDb={PreambleMarginDb?.ToString("F1") ?? "-"} " +
        $"preamblePeakHz={PreamblePeakHz?.ToString("F1") ?? "-"} " +
        $"syncUpPeakHz={SyncUpPeakHz?.ToString("F1") ?? "-"} " +
        $"syncDownPeakHz={SyncDownPeakHz?.ToString("F1") ?? "-"} " +
        $"payloadCrc={PayloadCrcValid?.ToString() ?? "-"} correctedCodewords={CorrectedCodewords}";
}

public interface IMeshtasticReceiveService : IDisposable
{
    MeshtasticReceiveSnapshot Snapshot { get; }
    void ResetStatistics();
    void StartStream();
    ValueTask StopStreamAsync(CancellationToken cancellationToken = default);
    void UpdateRingProgress(IqSampleRingBuffer buffer, long absoluteSampleEnd);
    event Action<LoRaPreambleDetection>? PreambleDetected;
    event Action<LoRaFrameSynchronization>? FrameSynchronized;
    event Action<LoRaExplicitHeader>? ExplicitHeaderDecoded;
    event Action<LoRaPayloadFrame>? PayloadDecoded;
    event Action<LoRaAcquisitionDiagnostic>? AcquisitionDiagnostic;
    event Action<MeshtasticPacketReception>? MeshtasticPacketReceived;
    event Action<MeshtasticDataReception>? MeshtasticDataReceived;

    bool TryConfigureChannels(MeshtasticRegion region, MeshtasticModemPreset preset, IReadOnlyList<MeshtasticBandwidthSlots> bandwidthSlots, out string error);

    bool TrySubmit(
        IqSampleRingBuffer buffer,
        int blockStartPointer,
        int sampleCount,
        int sampleRateHz,
        int inputCenterFrequencyHz,
        long absoluteSampleEnd);

    bool TrySubmitNormalized(
        ReadOnlySpan<Complex32> samples,
        int sampleRateHz,
        int inputCenterFrequencyHz,
        long sequence,
        long absoluteSampleStart);
    ValueTask WarmUpProcessingAsync(
        int sampleRateHz,
        int inputCenterFrequencyHz,
        int blockCount,
        CancellationToken cancellationToken);
}
