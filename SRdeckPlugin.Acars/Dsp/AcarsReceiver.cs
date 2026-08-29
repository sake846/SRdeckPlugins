using System.Numerics;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Acars.Dsp;

/// <summary>Streaming VHF ACARS AM-MSK receiver.</summary>
public sealed partial class AcarsReceiver
{
    public const int DemodulationSampleRateHz = 48_000;
    public const int ChannelBandwidthHz = 10_000;
    public const int ChannelStopbandBandwidthHz = 20_000;
    internal const float DemodulationIqCutoffHz = 5_000f;
    private const double RateConversionCutoffHz = 14_000;
    public const int BitRate = 2_400;
    private const int SamplesPerBit = DemodulationSampleRateHz / BitRate;
    private const int DecodeIntervalSamples = DemodulationSampleRateHz / 10;
    private const int MaximumAcquisitionCandidatePhases = 3;
    private static readonly (float[] I, float[] Q) LowTone = CreateCorrelator(1_200);
    private static readonly (float[] I, float[] Q) HighTone = CreateCorrelator(2_400);
    private const float MskSquelchOpenThreshold = 0.58f;
    internal const int MskSquelchHoldSamples = DemodulationSampleRateHz * 150 / 1_000;
    internal const int MonitorAudioDelaySamples = DemodulationSampleRateHz * 12 / 1_000;
    private const int FastMskMetricWindowHops = 40;
    private const float FastMskAverageOpenThreshold = 0.48f;
    private float[] audioBuffer = new float[DemodulationSampleRateHz * 2];
    private int audioCount;
    private ToneCorrelation[] lowCorrelations = new ToneCorrelation[2400];
    private ToneCorrelation[] highCorrelations = new ToneCorrelation[2400];
    private float[] confidenceBuffer = new float[2400];
    private float[] bitConfidenceBuffer = new float[2400];
    // The twenty timing phases collectively visit every possible sample
    // offset. Cache each window once, then gather the strided phase sequences.
    private ToneCorrelation[] lowCorrelationCache = new ToneCorrelation[2400];
    private ToneCorrelation[] highCorrelationCache = new ToneCorrelation[2400];
    private float[] confidenceCache = new float[2400];
    private int correlationCacheCount;
    private byte[] predecessorsBuffer = new byte[4800];
    private bool[] tonesBuffer = new bool[2400];
    private bool[] bitsBuffer = new bool[2400];
    private byte[] packedBytesBuffer = new byte[300];
    // Do the expensive rate reduction first. The polyphase FIR then runs only
    // at about 60 kS/s, not at an SDR's multi-MS/s input rate.
    private readonly BoundedCicDecimator coarseDecimator = new();
    private readonly BoundedCicDecimator fineDecimator = new();
    private readonly PolyphaseRationalResampler finalResampler = new(32, allowUpsampling: false);
    private readonly ComplexFrequencyTranslator downconverter = new();
    private readonly IqChannelLowPass iqChannelLowPass = new();
    private readonly ChannelAgc channelAgc = new();
    private readonly DemodulatedAudioLowPass demodulatedAudioLowPass = new();
    private int inputSampleRate;
    private int intermediateDecimationFactor;
    private float dc;
    private int samplesSinceDecode;
    private long audioSampleStart;
    private long lastFramePosition = long.MinValue;
    private long lastCandidatePosition = long.MinValue;
    private long targetFrequencyHz;
    private RateConversionPlan currentRatePlan;
    private float demodulatedPower;
    private float demodulatedPeak;
    private float lastToneConfidence;
    private float lastMskSquelchMetric;
    private bool isMskSquelchOpen;
    private int mskSquelchHoldSamples;
    private readonly MonitorAudioSquelchGate monitorAudioSquelchGate = new();
    private readonly float[] fastMskWindow = new float[SamplesPerBit];
    private int fastMskWindowPosition;
    private int fastMskWindowCount;
    private int fastMskHopCount;
    private int fastMskLowToneAge = 10_000;
    private int fastMskHighToneAge = 10_000;
    private int fastMskCrossingInterval;
    private int fastMskCrossingSign;
    private double fastMskWindowPower;
    private readonly float[] fastMskMetrics = new float[FastMskMetricWindowHops];
    private int fastMskMetricPosition;
    private int fastMskMetricCount;
    private float fastMskMetricSum;
    private long decodePassCount;
    private long processedAudioSampleCount;
    private long hypothesisEvaluationCount;
    private long syncCandidateCount;
    private long hypothesisScanCount;
    private long hypothesisPrunedCount;
    private long hypothesisEarlyExitCount;
    private long hypothesisFallbackCount;
    private long uncorrectedValidFrameCount;
    private long singleBitCorrectionCount;
    private long doubleBitCorrectionCount;
    private long ambiguousCorrectionRejectionCount;
    private long candidateLimitRejectionCount;
    private CorrectionStatus lastRejectedCorrectionStatus;
    private int lastCorrectedBitIndex1 = -1;
    private int lastCorrectedBitIndex2 = -1;
    private float lastCorrectedBitConfidence1;
    private float lastCorrectedBitConfidence2;
    private int lastCorrectionTrialCount;
    private long lastDecodeScanStartPosition;
    private long lastDecodeScanEndPosition;
    private int lastDecodeScannedAudioSampleCount;
    private long lastDecodeToneCorrelationCount;
    private long lastDecodeReusedToneCorrelationCount;
    private long totalToneCorrelationCount;
    private double lastDecodeCpuMilliseconds;
    private int lastAcquisitionCandidatePhaseCount;
    private int lastFullDecodePhaseCount;
    private long acquisitionFallbackCount;
    private long totalFullDecodePhaseCount;

    private static readonly float AudioPowerCoefficient =
        (float)(1 - Math.Exp(-1 / (0.050 * DemodulationSampleRateHz)));
    private static readonly float AudioPeakDecay =
        (float)Math.Exp(-1 / (0.250 * DemodulationSampleRateHz));

    public long ValidFrameCount { get; private set; }
    public long RejectedFrameCount { get; private set; }
    public bool IsSquelchEnabled { get; set; } = true;
    public bool IsMskSquelchOpen => isMskSquelchOpen;

    public readonly record struct DiagnosticsSnapshot(
        int InputSampleRateHz,
        int CoarseDecimationFactor,
        int FineDecimationFactor,
        double IntermediateSampleRateHz,
        int ResamplerInterpolationFactor,
        int ResamplerDecimationFactor,
        float ChannelInputRms,
        float ChannelAgcGain,
        float DemodulatedAudioRms,
        float DemodulatedAudioPeak,
        float ToneConfidence,
        float MskSquelchMetric,
        bool IsMskSquelchOpen,
        long DecodePassCount,
        long ProcessedAudioSampleCount,
        long ValidFrameCount,
        long RejectedFrameCount,
        long HypothesisEvaluationCount = 0,
        long SyncCandidateCount = 0,
        long HypothesisScanCount = 0,
        long HypothesisPrunedCount = 0,
        long HypothesisEarlyExitCount = 0,
        long HypothesisFallbackCount = 0,
        long UncorrectedValidFrameCount = 0,
        long SingleBitCorrectionCount = 0,
        long DoubleBitCorrectionCount = 0,
        long AmbiguousCorrectionRejectionCount = 0,
        long CandidateLimitRejectionCount = 0,
        int LastCorrectedBitIndex1 = -1,
        int LastCorrectedBitIndex2 = -1,
        float LastCorrectedBitConfidence1 = 0,
        float LastCorrectedBitConfidence2 = 0,
        int LastCorrectionTrialCount = 0,
        long LastDecodeScanStartPosition = 0,
        long LastDecodeScanEndPosition = 0,
        int LastDecodeScannedAudioSampleCount = 0,
        long LastDecodeToneCorrelationCount = 0,
        long LastDecodeReusedToneCorrelationCount = 0,
        long TotalToneCorrelationCount = 0,
        double LastDecodeCpuMilliseconds = 0,
        int LastAcquisitionCandidatePhaseCount = 0,
        int LastFullDecodePhaseCount = 0,
        long AcquisitionFallbackCount = 0,
        long TotalFullDecodePhaseCount = 0);

    public DiagnosticsSnapshot GetDiagnostics() => new(
        inputSampleRate,
        currentRatePlan.CoarseFactor,
        currentRatePlan.FineFactor,
        currentRatePlan.IntermediateSampleRateHz,
        currentRatePlan.InterpolationFactor,
        currentRatePlan.ResamplerDecimationFactor,
        channelAgc.EstimatedRms,
        channelAgc.CurrentGain,
        MathF.Sqrt(MathF.Max(demodulatedPower, 0)),
        demodulatedPeak,
        lastToneConfidence,
        lastMskSquelchMetric,
        isMskSquelchOpen,
        decodePassCount,
        processedAudioSampleCount,
        ValidFrameCount,
        RejectedFrameCount,
        hypothesisEvaluationCount,
        syncCandidateCount,
        hypothesisScanCount,
        hypothesisPrunedCount,
        hypothesisEarlyExitCount,
        hypothesisFallbackCount,
        uncorrectedValidFrameCount,
        singleBitCorrectionCount,
        doubleBitCorrectionCount,
        ambiguousCorrectionRejectionCount,
        candidateLimitRejectionCount,
        lastCorrectedBitIndex1,
        lastCorrectedBitIndex2,
        lastCorrectedBitConfidence1,
        lastCorrectedBitConfidence2,
        lastCorrectionTrialCount,
        lastDecodeScanStartPosition,
        lastDecodeScanEndPosition,
        lastDecodeScannedAudioSampleCount,
        lastDecodeToneCorrelationCount,
        lastDecodeReusedToneCorrelationCount,
        totalToneCorrelationCount,
        lastDecodeCpuMilliseconds,
        lastAcquisitionCandidatePhaseCount,
        lastFullDecodePhaseCount,
        acquisitionFallbackCount,
        totalFullDecodePhaseCount);
}
