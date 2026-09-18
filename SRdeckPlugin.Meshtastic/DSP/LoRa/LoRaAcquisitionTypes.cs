
// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

public sealed record LoRaPreambleDetection(
    DateTimeOffset DetectedAt,
    float PeakToAverageDb,
    float DechirpedPeakHz,
    int ConsecutiveSymbols)
{
    public float? EstimatedSnrDb { get; init; }
    public float? EstimatedRssiDbfs { get; init; }
}

public sealed record LoRaFrameSynchronization(
    DateTimeOffset SynchronizedAt,
    byte SyncWord,
    float UpChirpPeakHz,
    float DownChirpPeakHz,
    int PayloadDelaySamples,
    float CarrierFrequencyOffsetHz = 0,
    int TimingCorrectionSamples = 0,
    bool CompensationApplied = false,
    bool CompensationRequired = false)
{
    /// <summary>Selected-band SNR estimated from repeated preamble chirps, in dB.</summary>
    public float? EstimatedSnrDb { get; init; }
    public float? EstimatedRssiDbfs { get; init; }
}

public sealed record LoRaAcquisitionDiagnostic(
    DateTimeOffset Timestamp,
    string Stage,
    string Message,
    float PeakToAverageDb,
    float PeakFrequencyHz,
    int? ObservedSymbol,
    int? ExpectedSymbol,
    bool IsFailure);
