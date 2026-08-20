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
    public const int BitRate = 2_400;
    private const int SamplesPerBit = DemodulationSampleRateHz / BitRate;
    private const int DecodeIntervalSamples = DemodulationSampleRateHz / 10;
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
    private byte[] predecessorsBuffer = new byte[4800];
    private bool[] tonesBuffer = new bool[2400];
    private bool[] bitsBuffer = new bool[2400];
    // Do the expensive rate reduction first. The polyphase FIR then runs only
    // at about 60 kS/s, not at an SDR's multi-MS/s input rate.
    private readonly BoundedCicDecimator coarseDecimator = new();
    private readonly BoundedCicDecimator fineDecimator = new();
    private readonly PolyphaseRationalResampler finalResampler = new(32, allowUpsampling: false);
    private readonly ComplexFrequencyTranslator downconverter = new();
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
        long RejectedFrameCount);

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
        RejectedFrameCount);


}
