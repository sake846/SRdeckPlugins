using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.AdsB.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using System.Buffers;
using System.Runtime.InteropServices;

namespace SRdeckPlugin.AdsB.Dsp;

/// <summary>Streaming 1090 MHz Mode S pulse-position demodulator.</summary>
public sealed partial class ModeSReceiver
{
    public const int MinimumInputSampleRateHz = 2_000_000;
    public const int DemodulationSampleRateHz = 4_000_000;
    private const long TargetFrequencyHz = 1_090_000_000;
    internal const double ChannelCutoffHz = 925_000;
    private const int SamplesPerHalfBit = DemodulationSampleRateHz / 2_000_000;
    private const int PreambleSamples = 16 * SamplesPerHalfBit;
    private const int LongFrameSamples = PreambleSamples + 112 * 2 * SamplesPerHalfBit;
    private readonly List<float> pending = new(LongFrameSamples * 2);
    private readonly List<float> pendingI = new(LongFrameSamples * 2);
    private readonly List<float> pendingQ = new(LongFrameSamples * 2);
    private readonly BoundedCicDecimator coarseDecimator = new();
    private readonly PolyphaseRationalResampler finalResampler = new(33);
    private readonly ComplexFrequencyTranslator downconverter = new();
    private readonly Action<float, float> workingSampleSink;
    private long pendingWorkingSampleStart;
    private long producedWorkingSamples;
    private long streamSampleOrigin;
    private int inputSampleRate;
    private int rawPipelineSampleRate;
    private long centerFrequencyHz;
    private int coarseDecimationFactor;
    private float noiseFloorPower;
    private double processedDurationSeconds;
    private double signalQualitySum;
    private double lastSignalQuality;
    private double maximumSignalQuality;
    private bool enableSuccessiveInterferenceCancellation = true;
    private int timingGradientInterval = 1;
    private long timingGradientEvaluationCount;
    private long timingTrackedFrameCount;
    private double timingAdjustmentSum;
    private double maximumTimingAdjustment;

    public long ValidFrameCount { get; private set; }
    public long RejectedFrameCount { get; private set; }
    public long CorrectedFrameCount { get; private set; }
    public long SicRecoveredFrameCount { get; private set; }
    public long SicCancellationRunCount { get; private set; }
    public long TimingAdjustedFrameCount { get; private set; }
    public long FastPreambleCandidateCount { get; private set; }
    public long PreambleMatchCount { get; private set; }
    internal int RawPipelineConfigurationCount { get; private set; }

    public sealed record DiagnosticsSnapshot(
        int InputSampleRateHz,
        long CenterFrequencyHz,
        long ChannelOffsetHz,
        double NoiseFloorDbfs,
        double LastSignalQuality,
        double AverageSignalQuality,
        double MaximumSignalQuality,
        double ValidFramesPerSecond,
        long CorrectedFrameCount,
        long FastPreambleCandidateCount = 0,
        long PreambleMatchCount = 0,
        bool SicEnabled = true,
        long SicCancellationRunCount = 0,
        long SicRecoveredFrameCount = 0,
        int TimingGradientInterval = 1,
        long TimingGradientEvaluationCount = 0,
        double AverageTimingAdjustment = 0,
        double MaximumTimingAdjustment = 0);

    public DiagnosticsSnapshot GetDiagnostics() => new(
        inputSampleRate,
        centerFrequencyHz,
        centerFrequencyHz - TargetFrequencyHz,
        noiseFloorPower > 0 ? 10 * Math.Log10(noiseFloorPower) : double.NegativeInfinity,
        lastSignalQuality,
        ValidFrameCount == 0 ? 0 : signalQualitySum / ValidFrameCount,
        maximumSignalQuality,
        processedDurationSeconds <= 0 ? 0 : ValidFrameCount / processedDurationSeconds,
        CorrectedFrameCount,
        FastPreambleCandidateCount,
        PreambleMatchCount,
        enableSuccessiveInterferenceCancellation,
        SicCancellationRunCount,
        SicRecoveredFrameCount,
        timingGradientInterval,
        timingGradientEvaluationCount,
        timingTrackedFrameCount == 0 ? 0 : timingAdjustmentSum / timingTrackedFrameCount,
        maximumTimingAdjustment);

    public ModeSReceiver() => workingSampleSink = AddWorkingSample;

    public bool EnableSuccessiveInterferenceCancellation => enableSuccessiveInterferenceCancellation;
    internal int PendingIqSampleCount => pendingI.Count + pendingQ.Count;
    internal int TimingGradientInterval => timingGradientInterval;

    public void ConfigureSuccessiveInterferenceCancellation(bool enabled)
    {
        if (enableSuccessiveInterferenceCancellation == enabled) return;
        enableSuccessiveInterferenceCancellation = enabled;
        ClearPendingSamples();
    }

    internal void ConfigureTimingGradientInterval(int interval)
    {
        if (interval is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(interval));
        timingGradientInterval = interval;
    }

    public IReadOnlyList<ModeSFrame> Process(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata)
    {
        if (metadata.SampleRateHz < MinimumInputSampleRateHz)
            throw new InvalidOperationException("ADS-B reception requires an IQ sample rate of at least 2 MHz.");

        long offsetHz = TargetFrequencyHz - metadata.CenterFrequencyHz;
        if (Math.Abs(offsetHz) > metadata.SampleRateHz * 0.5)
            return Array.Empty<ModeSFrame>();

        if (rawPipelineSampleRate != metadata.SampleRateHz ||
            metadata.Discontinuity != IqDiscontinuity.None)
            Reset(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
        centerFrequencyHz = metadata.CenterFrequencyHz;
        processedDurationSeconds += samples.Length / (double)metadata.SampleRateHz;
        downconverter.Configure(offsetHz, metadata.SampleRateHz);
        foreach (Complex32 sample in samples)
        {
            downconverter.Mix(sample.I, sample.Q, out float mixedI, out float mixedQ);

            if (!coarseDecimator.TryProcess(mixedI, mixedQ, out float coarseI, out float coarseQ))
                continue;
            finalResampler.Process(coarseI, coarseQ, workingSampleSink);
        }
        double groupDelay = finalResampler.GroupDelaySamples * coarseDecimationFactor +
            coarseDecimator.GroupDelayInputSamples;
        return DecodePending(metadata, groupDelay);
    }

    public IReadOnlyList<ModeSFrame> ProcessChannel(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata)
    {
        if (metadata.Configuration.OutputSampleRateHz != DemodulationSampleRateHz)
            throw new InvalidOperationException($"ADS-B requires a {DemodulationSampleRateHz} Hz channel stream.");
        IqBlockMetadata source = metadata.Source;
        if (inputSampleRate != source.SampleRateHz || metadata.OutputSampleStart == 0 ||
            source.Discontinuity != IqDiscontinuity.None)
            ResetChannel(metadata.SourceSampleOrigin, source.SampleRateHz);
        centerFrequencyHz = source.CenterFrequencyHz;
        processedDurationSeconds += samples.Length / (double)metadata.Configuration.OutputSampleRateHz;
        foreach (Complex32 sample in samples) AddWorkingSample(sample.I, sample.Q);
        return DecodePending(source, metadata.Configuration.GroupDelayInputSamples);
    }

    private IReadOnlyList<ModeSFrame> DecodePending(
        IqBlockMetadata metadata,
        double groupDelayInputSamples)
    {
        if (pending.Count < LongFrameSamples) return Array.Empty<ModeSFrame>();
        var result = new List<ModeSFrame>();
        var decodedOffsets = new HashSet<int>();
        var cancellationCandidates = new List<(int Offset, byte[] Bytes, double Quality)>();
        ReadOnlySpan<float> pendingPower = CollectionsMarshal.AsSpan(pending);
        double[] prefixStorage = ArrayPool<double>.Shared.Rent(pending.Count + 1);
        try
        {
            Span<double> powerPrefix = prefixStorage.AsSpan(0, pending.Count + 1);
            powerPrefix[0] = 0;
            for (int index = 0; index < pendingPower.Length; index++)
                powerPrefix[index + 1] = powerPrefix[index] + pendingPower[index];

            Span<float> integerSlots = stackalloc float[16];
            Span<byte> decodedBytes = stackalloc byte[14];
            Span<float> bitConfidence = stackalloc float[112];
            int cachedPreambleScan = -1;
            double cachedQuality = 0;
            double cachedTimingOffset = 0;
            int scan = 0;
            while (scan + LongFrameSamples <= pending.Count)
            {
                double quality;
                double timingOffset;
                if (cachedPreambleScan == scan)
                {
                    quality = cachedQuality;
                    timingOffset = cachedTimingOffset;
                    cachedPreambleScan = -1;
                    FastPreambleCandidateCount++;
                }
                else
                {
                    FillIntegerPreambleSlots(powerPrefix, scan, integerSlots);
                    if (!HasFastPreambleCandidate(integerSlots, noiseFloorPower))
                    {
                        scan++;
                        continue;
                    }
                    FastPreambleCandidateCount++;
                    if (!IsPreamble(pendingPower, scan, noiseFloorPower, integerSlots,
                            out quality, out timingOffset))
                    {
                        scan++;
                        continue;
                    }
                }
                if (scan + 1 + LongFrameSamples <= pending.Count)
                {
                    FillIntegerPreambleSlots(powerPrefix, scan + 1, integerSlots);
                    if (IsPreamble(pendingPower, scan + 1, noiseFloorPower, integerSlots,
                            out double nextQuality, out double nextTimingOffset) && nextQuality > quality)
                    {
                        cachedPreambleScan = scan + 1;
                        cachedQuality = nextQuality;
                        cachedTimingOffset = nextTimingOffset;
                        scan++;
                        continue;
                    }
                }
                PreambleMatchCount++;

                DecodeLongFrame(pendingPower, scan + PreambleSamples + timingOffset, noiseFloorPower,
                    decodedBytes, bitConfidence, out double timingAdjustment);
                if (ModeSCrc.TryValidateOrCorrectExtendedSquitter(
                        decodedBytes, bitConfidence, out bool corrected))
                {
                    byte[] acceptedBytes = decodedBytes.ToArray();
                    long workingPosition = pendingWorkingSampleStart + scan;
                    long sourceOffset = (long)Math.Round(workingPosition * (double)inputSampleRate /
                        DemodulationSampleRateHz - groupDelayInputSamples);
                    long sourcePosition = streamSampleOrigin + sourceOffset;
                    DateTimeOffset receivedAt = metadata.UtcTimestamp.AddSeconds(
                        (sourcePosition - metadata.AbsoluteSampleStart) / (double)inputSampleRate);
                    double correctedQuality = corrected ? quality * 0.85 : quality;
                    result.Add(new(acceptedBytes, receivedAt, metadata.StreamId, sourcePosition, correctedQuality));
                    RecordSignalQuality(correctedQuality);
                    decodedOffsets.Add(scan);
                    cancellationCandidates.Add((scan, acceptedBytes, correctedQuality));
                    ValidFrameCount++;
                    if (corrected) CorrectedFrameCount++;
                    if (Math.Abs(timingOffset) > 0.05 || Math.Abs(timingAdjustment) > 0.05)
                        TimingAdjustedFrameCount++;
                    double absoluteTimingAdjustment = Math.Abs(timingAdjustment);
                    timingTrackedFrameCount++;
                    timingAdjustmentSum += absoluteTimingAdjustment;
                    maximumTimingAdjustment = Math.Max(
                        maximumTimingAdjustment, absoluteTimingAdjustment);
                    // Continue inside the decoded frame. A second transmitter can start
                    // before this frame ends; its independently valid CRC must still be considered.
                    scan += SamplesPerHalfBit;
                }
                else
                {
                    RejectedFrameCount++;
                    scan++;
                }
            }

            RecoverBySuccessiveInterferenceCancellation(metadata, groupDelayInputSamples, scan,
                cancellationCandidates, decodedOffsets, result);

            RemovePendingPrefix(scan);
            if (pending.Count > LongFrameSamples * 2)
                RemovePendingPrefix(pending.Count - LongFrameSamples);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(prefixStorage);
        }
        return result;
    }

    public void Reset(long absoluteSampleStart = 0, int sampleRateHz = MinimumInputSampleRateHz)
    {
        ResetWorkingState(absoluteSampleStart, sampleRateHz);
        if (rawPipelineSampleRate != sampleRateHz)
        {
            coarseDecimationFactor = SelectCoarseDecimationFactor(sampleRateHz);
            coarseDecimator.Configure(coarseDecimationFactor, 2);
            finalResampler.Configure(sampleRateHz, coarseDecimationFactor,
                DemodulationSampleRateHz, ChannelCutoffHz);
            rawPipelineSampleRate = sampleRateHz;
            RawPipelineConfigurationCount++;
        }
        else
        {
            coarseDecimator.Reset();
            finalResampler.Reset();
        }
        downconverter.ResetPhase();
    }

    public void ResetChannel(
        long absoluteSampleStart = 0,
        int sampleRateHz = MinimumInputSampleRateHz)
    {
        ResetWorkingState(absoluteSampleStart, sampleRateHz);
    }

    private void ResetWorkingState(long absoluteSampleStart, int sampleRateHz)
    {
        if (sampleRateHz < MinimumInputSampleRateHz)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        producedWorkingSamples = 0;
        ClearPendingSamples();
        streamSampleOrigin = absoluteSampleStart;
        inputSampleRate = sampleRateHz;
        noiseFloorPower = 0;
    }

    public void ResetStatistics()
    {
        ValidFrameCount = 0;
        RejectedFrameCount = 0;
        CorrectedFrameCount = 0;
        SicRecoveredFrameCount = 0;
        SicCancellationRunCount = 0;
        TimingAdjustedFrameCount = 0;
        FastPreambleCandidateCount = 0;
        PreambleMatchCount = 0;
        timingGradientEvaluationCount = 0;
        timingTrackedFrameCount = 0;
        timingAdjustmentSum = 0;
        maximumTimingAdjustment = 0;
        processedDurationSeconds = 0;
        signalQualitySum = 0;
        lastSignalQuality = 0;
        maximumSignalQuality = 0;
    }

    private void AddWorkingSample(float i, float q)
    {
        float power = i * i + q * q;
        if (pending.Count == 0) pendingWorkingSampleStart = producedWorkingSamples;
        if (enableSuccessiveInterferenceCancellation)
        {
            pendingI.Add(i);
            pendingQ.Add(q);
        }
        pending.Add(power);
        producedWorkingSamples++;

        if (!float.IsFinite(power)) return;
        if (noiseFloorPower <= 0) noiseFloorPower = Math.Max(power, 1e-20f);
        else
        {
            float coefficient = power <= noiseFloorPower * 4 ? 0.002f : 0.000002f;
            noiseFloorPower += coefficient * (power - noiseFloorPower);
            noiseFloorPower = Math.Max(noiseFloorPower, 1e-20f);
        }
    }

    private void ClearPendingSamples()
    {
        pending.Clear();
        pendingI.Clear();
        pendingQ.Clear();
        pendingWorkingSampleStart = producedWorkingSamples;
    }

    private void RemovePendingPrefix(int count)
    {
        if (count <= 0) return;
        pending.RemoveRange(0, count);
        if (enableSuccessiveInterferenceCancellation)
        {
            pendingI.RemoveRange(0, count);
            pendingQ.RemoveRange(0, count);
        }
        pendingWorkingSampleStart += count;
    }
}
