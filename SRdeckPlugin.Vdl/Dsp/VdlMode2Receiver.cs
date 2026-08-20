using System.Diagnostics;
using System.Numerics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Streaming VDL Mode 2 receiver. The physical layer is D8PSK at 10.5 ksym/s
/// with a 16-symbol preamble, a scrambled 25-bit length header and an
/// interleaved Reed-Solomon protected data field containing HDLC/AVLC frames.
/// </summary>
public sealed partial class VdlMode2Receiver
{
    public const int SymbolRate = 10_500;
    public const int BitRate = 31_500;
    internal const int SamplesPerSymbol = 10;
    internal const int WorkingSampleRate = SymbolRate * SamplesPerSymbol;
    public const int MonitorAudioSampleRate = WorkingSampleRate / 5;

    private const int PreambleSymbols = 16;
    private int preambleVerificationSymbols = 16;
    public int PreambleVerificationSymbols
    {
        get => preambleVerificationSymbols;
        set => preambleVerificationSymbols = value switch
        {
            16 => 16,
            12 => 12,
            4 => 4,
            _ => 16
        };
    }
    private const int HeaderLength = 25;
    private const int TransmissionLengthBits = 17;
    private const int HeaderFecBits = 5;
    private const int MaxTransmissionBits = 0x3fff;
    private const ushort ScramblerInitialValue = 0x6959;
    private const int ReedSolomonDataBytes = 249;
    private const int ReedSolomonCodewordBytes = 255;
    private const double SyncThreshold = 4.0;
    private const double CandidateSyncThreshold = 12.0;
    private const double PreambleCoherenceThreshold = 0.78;
    private const double PreambleSnrThresholdDb = 2.0;
    private const double TimingProportionalGain = 0.18;
    private const double TimingIntegralGain = 0.0002;
    private const double MaximumTimingRateCorrection = 0.003;
    private const double DefaultCarrierTrackingGain = 0.005;
    private const double MaximumCarrierCorrection = Math.PI / 2;
    private const int TimingBufferLength = 64;
    private const int DefaultRecoveryBudgetMilliseconds = 25;

    private static readonly double[] PreamblePhases =
    [
        0, 3, -3, 1, 1, 2, 0, 4,
        -3, 4, -2, 3, 1, -2, -3, 0
    ];

    private static readonly int[] GrayCode = [0, 1, 3, 2, 6, 7, 5, 4];
    private static readonly uint[] HeaderParityChecks =
    [
        0b0000000011111111111110000,
        0b0011111100001111111101000,
        0b1100011100110000111100100,
        0b1101101101010011001100010,
        0b0110100111100101010100001
    ];

    private static readonly uint[] HeaderCorrections =
    [
        0b0000000000000000000000000, 0b0000000000000000000000001,
        0b0000000000000000000000010, 0b0000000000000000000000000,
        0b0000000000000000000000100, 0b0000000000000000000000000,
        0b1000000000000000000000000, 0b0100000000000000000000000,
        0b0000000000000000000001000, 0b0010000000000000000000000,
        0b0001000000000000000000000, 0b0000100000000000000000000,
        0b0000010000000000000000000, 0b0000000000000000000000000,
        0b0000001000000000000000000, 0b0000000100000000000000000,
        0b0000000000000000000010000, 0b0000000010000000000000000,
        0b0000000000000000000000000, 0b0000000001000000000000000,
        0b0000000000000000000000000, 0b0000000000100000000000000,
        0b0000000000010000000000000, 0b0000000000000000000000000,
        0b0000000000001000000000000, 0b0000000000000100000000000,
        0b0000000000000010000000000, 0b0000000000000001000000000,
        0b0000000000000000100000000, 0b0000000000000000010000000,
        0b0000000000000000001000000, 0b0000000000000000000100000
    ];

    private readonly Complex32[] syncBuffer = new Complex32[PreambleSymbols * SamplesPerSymbol];
    private readonly Complex32[] timingBuffer = new Complex32[TimingBufferLength];
    private readonly List<bool> burstBits = [];
    private readonly List<double> burstReliabilities = [];
    private readonly List<Complex32> burstSymbols = [];
    private readonly List<Complex32> burstSymbolsEarly = [];
    private readonly List<Complex32> burstSymbolsLate = [];
    private double burstInitialPreviousPhase;
    private double burstInitialPhaseDrift;
    private int syncWriteIndex;
    private int syncSampleCount;
    private int inputSampleRate;
    private int coarseDecimationFactor = 1;
    private double intermediateSampleRate = WorkingSampleRate;
    private readonly BoundedCicDecimator coarseDecimator = new();
    private readonly PolyphaseRationalResampler resampler = new(32);
    private readonly ComplexFrequencyTranslator downconverter = new();
    private readonly RootRaisedCosineFilter matchedFilter = new();
    private readonly AudioMonitor audioMonitor = new();
    public bool IsSquelchEnabled { get; set; } = true;
    private ReceiverState state;
    private double previousPhase;
    private double phaseDriftPerSymbol;
    private double burstCarrierErrorPower;
    private long burstCarrierUpdateCount;
    private double burstTimingErrorPower;
    private long burstTimingUpdateCount;
    private bool adaptiveEqualizerConfigured;
    private Complex adaptiveEqualizerTap0;
    private Complex adaptiveEqualizerTap1;
    private Complex adaptiveEqualizerTap2;
    private Complex adaptiveEqualizerPrevious1;
    private Complex adaptiveEqualizerPrevious2;
    private double adaptiveEqualizerCarrierPhase;
    private double previousSyncError = double.PositiveInfinity;
    private double previousSyncPhase;
    private double previousSyncDrift;
    private double previousSyncCoherence;
    private double previousSyncPower;
    private double previousSyncPhaseResidualRms;
    private double previousSyncAmplitudeCoefficientOfVariation;
    private double noiseFloorPower;
    private long noiseEstimateCount;
    private double lastPreambleCoherence;
    private double lastPreamblePower;
    private double lastPreambleSnrDb = double.NaN;
    private double lastPreamblePhaseResidualRms;
    private double lastPreambleAmplitudeCoefficientOfVariation;
    private int requestedBurstBits;
    private int transmissionLength;
    private long targetFrequencyHz;
    private long centerFrequencyHz;
    private double inputRms;
    private double channelRms;
    private double channelPeak;
    private double currentBlockMinSyncError = double.PositiveInfinity;
    private double currentBlockMaxCoherence;
    private double displaySyncError = double.PositiveInfinity;
    private double displayCoherence;
    private DateTime displayHoldUntil = DateTime.MinValue;
    private long processedInputSamples;
    private long processedWorkingSamples;
    private long workingSampleIndex = -1;
    private double nextSymbolTime;
    private double timingCenterTime;
    private Complex32 timingCenterSample;
    private bool timingErrorPending;
    private double timingRateCorrection;
    private double lastTimingError;
    private double lastTimingOffsetSamples;
    private long timingUpdateCount;
    private double lastCarrierError;
    private double carrierErrorPower;
    private long carrierUpdateCount;
    private bool phaseHypothesesEnabled = true;
    private bool rescueDecodingEnabled = true;
    private double lastPhaseHypothesisTimingOffset;
    private double lastPhaseHypothesisFrequencyOffsetHz;

    public long ValidFrameCount { get; private set; }
    public long RejectedFrameCount { get; private set; }
    public long SynchronizationCount { get; private set; }
    public long FrequencyOffsetHz { get; private set; }
    public long PreambleCandidateCount { get; private set; }
    public long HeaderAcceptedCount { get; private set; }
    public long HeaderRejectedCount { get; private set; }
    public long HeaderCleanCount { get; private set; }
    public long HeaderCorrectedCount { get; private set; }
    public long HeaderFecRejectedCount { get; private set; }
    public long HeaderLengthRejectedCount { get; private set; }
    public long BurstTimeoutCount { get; private set; }
    public long QualityRejectedCount { get; private set; }
    public long FecCleanBlockCount { get; private set; }
    public long FecUnprotectedBlockCount { get; private set; }
    public long FecCorrectedBlockCount { get; private set; }
    public long FecCorrectedOctetCount { get; private set; }
    public long FecUncorrectableBlockCount { get; private set; }
    public long FecSoftAttemptBlockCount { get; private set; }
    public long FecSoftCorrectedBlockCount { get; private set; }
    public long FecSoftCorrectedOctetCount { get; private set; }
    public long FecSoftRejectedBlockCount { get; private set; }
    public long AvlcFlagPairCount { get; private set; }
    public long AvlcUnstuffedFrameCount { get; private set; }
    public long AvlcFcsRejectedFrameCount { get; private set; }
    public long PhaseHypothesisAttemptCount { get; private set; }
    public long PhaseHypothesisSuccessCount { get; private set; }
    public long PhaseHypothesisRecoveredFrameCount { get; private set; }
    public long ChaseAttemptCount { get; private set; }
    public long ChaseSuccessCount { get; private set; }
    public long ChaseRecoveredFrameCount { get; private set; }
    public long RecoveryBudgetExceededCount { get; private set; }
    public long AdaptiveEqualizerAppliedCount { get; private set; }
    internal int RecoveryBudgetMilliseconds { get; set; } = DefaultRecoveryBudgetMilliseconds;
    internal double CarrierTrackingLoopGain { get; set; } = DefaultCarrierTrackingGain;
    internal bool TimingRecoveryEnabled { get; set; } = true;
    internal double TimingRecoveryLoopGainScale { get; set; } = 1;
    internal double InitialTimingOffsetSamples { get; set; }
    internal bool AdaptiveEqualizerEnabled { get; set; }
    internal Action<byte[], bool>? DecodedBurstObserver { get; set; }
    internal Action<BurstQualitySnapshot>? BurstQualityObserver { get; set; }
    internal Action<PreambleQualitySnapshot>? PreambleQualityObserver { get; set; }

    internal readonly record struct BurstQualitySnapshot(long SymbolCount, double AverageReliability,
        double CarrierErrorRmsRadians, double TimingErrorRms, double FinalTimingOffsetSamples,
        double FinalTimingRateCorrection);
    internal readonly record struct PreambleQualitySnapshot(int SymbolCount, double Coherence,
        double SnrDb, double PhaseResidualRmsRadians, double AmplitudeCoefficientOfVariation,
        double CarrierOffsetHz, double FlatChannelNmse, double ThreeTapChannelNmse);

    public readonly record struct DiagnosticsSnapshot(
        int InputSampleRateHz, int WorkingSampleRateHz, int CoarseDecimationFactor,
        double IntermediateSampleRateHz, int ResamplerInterpolationFactor,
        int ResamplerDecimationFactor, long CenterFrequencyHz,
        long TargetFrequencyHz, long FrequencyOffsetHz, double InputRms,
        double ChannelRms, double ChannelPeak, double BestSynchronizationError,
        double SynchronizationThreshold, double CandidateThreshold,
        double PreambleCoherence, double PreambleCoherenceThreshold,
        double NoiseFloorRms, double PreambleRms, double PreambleSnrDb,
        double PreambleSnrThresholdDb, long QualityRejectedCount,
        long ProcessedInputSamples, long ProcessedWorkingSamples,
        long PreambleCandidateCount, long SynchronizationCount,
        long HeaderAcceptedCount, long HeaderRejectedCount, long HeaderCleanCount,
        long HeaderCorrectedCount, long HeaderFecRejectedCount,
        long HeaderLengthRejectedCount, long BurstTimeoutCount,
        long ValidFrameCount, long RejectedFrameCount, double TimingError,
        double TimingOffsetSamples, double TimingRateCorrection,
        long TimingUpdateCount, double CarrierErrorRadians,
        double CarrierErrorRmsRadians, double CarrierOffsetHz,
        long CarrierUpdateCount, long FecCleanBlockCount,
        long FecUnprotectedBlockCount, long FecCorrectedBlockCount, long FecCorrectedOctetCount,
        long FecUncorrectableBlockCount, long FecSoftAttemptBlockCount,
        long FecSoftCorrectedBlockCount, long FecSoftCorrectedOctetCount,
        long FecSoftRejectedBlockCount, long AvlcFlagPairCount,
        long AvlcUnstuffedFrameCount, long AvlcFcsRejectedFrameCount,
        long PhaseHypothesisAttemptCount, long PhaseHypothesisSuccessCount,
        long PhaseHypothesisRecoveredFrameCount, long ChaseAttemptCount,
        long ChaseSuccessCount, long ChaseRecoveredFrameCount,
        long RecoveryBudgetExceededCount,
        double LastPhaseHypothesisTimingOffset,
        double LastPhaseHypothesisFrequencyOffsetHz);

    public DiagnosticsSnapshot GetDiagnostics() => new(
        inputSampleRate, WorkingSampleRate, coarseDecimationFactor,
        intermediateSampleRate, resampler.InterpolationFactor,
        resampler.DecimationFactor, centerFrequencyHz, targetFrequencyHz,
        FrequencyOffsetHz, inputRms, channelRms, channelPeak,
        displaySyncError, SyncThreshold, CandidateSyncThreshold,
        lastPreambleCoherence, PreambleCoherenceThreshold,
        Math.Sqrt(Math.Max(0, noiseFloorPower)), Math.Sqrt(Math.Max(0, lastPreamblePower)),
        lastPreambleSnrDb, PreambleSnrThresholdDb, QualityRejectedCount,
        processedInputSamples, processedWorkingSamples, PreambleCandidateCount,
        SynchronizationCount, HeaderAcceptedCount, HeaderRejectedCount,
        HeaderCleanCount, HeaderCorrectedCount, HeaderFecRejectedCount,
        HeaderLengthRejectedCount, BurstTimeoutCount, ValidFrameCount, RejectedFrameCount,
        lastTimingError, lastTimingOffsetSamples, timingRateCorrection,
        timingUpdateCount, lastCarrierError,
        carrierUpdateCount == 0 ? 0 : Math.Sqrt(carrierErrorPower / carrierUpdateCount),
        phaseDriftPerSymbol * SymbolRate / (2 * Math.PI), carrierUpdateCount,
        FecCleanBlockCount, FecUnprotectedBlockCount, FecCorrectedBlockCount,
        FecCorrectedOctetCount, FecUncorrectableBlockCount,
        FecSoftAttemptBlockCount, FecSoftCorrectedBlockCount,
        FecSoftCorrectedOctetCount, FecSoftRejectedBlockCount,
        AvlcFlagPairCount, AvlcUnstuffedFrameCount, AvlcFcsRejectedFrameCount,
        PhaseHypothesisAttemptCount, PhaseHypothesisSuccessCount,
        PhaseHypothesisRecoveredFrameCount, ChaseAttemptCount,
        ChaseSuccessCount, ChaseRecoveredFrameCount,
        RecoveryBudgetExceededCount,
        lastPhaseHypothesisTimingOffset, lastPhaseHypothesisFrequencyOffsetHz);

}
