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

    public long ValidFrameCount { get; private set; }
    public long RejectedFrameCount { get; private set; }
    public long CorrectedFrameCount { get; private set; }
    public long SicRecoveredFrameCount { get; private set; }
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
        long PreambleMatchCount = 0);

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
        PreambleMatchCount);

    public ModeSReceiver() => workingSampleSink = AddWorkingSample;

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
        var result = new List<ModeSFrame>();
        var decodedOffsets = new HashSet<int>();
        var cancellationCandidates = new List<(int Offset, byte[] Bytes, double Quality)>();
        ReadOnlySpan<float> pendingPower = CollectionsMarshal.AsSpan(pending);
        int scan = 0;
        while (scan + LongFrameSamples <= pending.Count)
        {
            if (!HasFastPreambleCandidate(pendingPower, scan, noiseFloorPower))
            {
                scan++;
                continue;
            }
            FastPreambleCandidateCount++;
            if (!IsPreamble(pendingPower, scan, noiseFloorPower, out double quality, out double timingOffset))
            {
                scan++;
                continue;
            }
            if (scan + 1 + LongFrameSamples <= pending.Count &&
                IsPreamble(pendingPower, scan + 1, noiseFloorPower, out double nextQuality, out _) &&
                nextQuality > quality)
            {
                scan++;
                continue;
            }
            PreambleMatchCount++;

            byte[] bytes = DecodeLongFrame(pendingPower, scan + PreambleSamples + timingOffset, noiseFloorPower,
                out float[] bitConfidence, out double timingAdjustment);
            if (ModeSCrc.TryValidateOrCorrectExtendedSquitter(bytes, bitConfidence, out bool corrected))
            {
                long workingPosition = pendingWorkingSampleStart + scan;
                long sourceOffset = (long)Math.Round(workingPosition * (double)inputSampleRate /
                    DemodulationSampleRateHz - groupDelayInputSamples);
                long sourcePosition = streamSampleOrigin + sourceOffset;
                DateTimeOffset receivedAt = metadata.UtcTimestamp.AddSeconds(
                    (sourcePosition - metadata.AbsoluteSampleStart) / (double)inputSampleRate);
                double correctedQuality = corrected ? quality * 0.85 : quality;
                result.Add(new(bytes, receivedAt, metadata.StreamId, sourcePosition, correctedQuality));
                RecordSignalQuality(correctedQuality);
                decodedOffsets.Add(scan);
                cancellationCandidates.Add((scan, bytes, correctedQuality));
                ValidFrameCount++;
                if (corrected) CorrectedFrameCount++;
                if (Math.Abs(timingOffset) > 0.05 || Math.Abs(timingAdjustment) > 0.05)
                    TimingAdjustedFrameCount++;
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

        RecoverBySuccessiveInterferenceCancellation(metadata, groupDelayInputSamples, scan, cancellationCandidates,
            decodedOffsets, result);

        if (scan > 0)
        {
            pending.RemoveRange(0, scan);
            pendingI.RemoveRange(0, scan);
            pendingQ.RemoveRange(0, scan);
            pendingWorkingSampleStart += scan;
        }
        if (pending.Count > LongFrameSamples * 2)
        {
            int remove = pending.Count - LongFrameSamples;
            pending.RemoveRange(0, remove);
            pendingI.RemoveRange(0, remove);
            pendingQ.RemoveRange(0, remove);
            pendingWorkingSampleStart += remove;
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
        pending.Clear();
        pendingI.Clear();
        pendingQ.Clear();
        pendingWorkingSampleStart = 0;
        producedWorkingSamples = 0;
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
        TimingAdjustedFrameCount = 0;
        FastPreambleCandidateCount = 0;
        PreambleMatchCount = 0;
        processedDurationSeconds = 0;
        signalQualitySum = 0;
        lastSignalQuality = 0;
        maximumSignalQuality = 0;
    }

    private void AddWorkingSample(float i, float q)
    {
        float power = i * i + q * q;
        if (pending.Count == 0) pendingWorkingSampleStart = producedWorkingSamples;
        pendingI.Add(i);
        pendingQ.Add(q);
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
}
