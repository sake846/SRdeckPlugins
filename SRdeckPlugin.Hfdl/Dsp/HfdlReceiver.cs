using SRdeckPlugin.Contracts;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Hfdl.Protocols;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Hfdl.Dsp;

/// <summary>Streaming ARINC 635 HFDL burst synchronizer, PSK demodulator and FEC decoder.</summary>
public sealed partial class HfdlReceiver
{
    public const int MinimumSampleRateHz = 48_000;
    public const int SymbolRate = HfdlPhysicalLayer.SymbolRate;
    public const int MonitorAudioSampleRateHz = 14_400;
    private const int WorkingSampleRate = MonitorAudioSampleRateHz;
    private const int SamplesPerSymbol = WorkingSampleRate / SymbolRate;
    private const double ChannelCutoffHz = 2_400;
    // The raw path already applies this edge in its rate-conversion FIR. The
    // standard channel path needs the equivalent demodulation filter after
    // host rate conversion and before differential PSK correlation.
    internal const double DemodulationIqCutoffHz = 2_400;
    private const double MinimumPreambleCorrelation = 0.72;
    private readonly SlidingSampleBuffer<Complex32> working = new(WorkingSampleRate * 6);
    private Complex32[] workingSampleScratch = new Complex32[WorkingSampleRate * 6];
    private Complex32[] integerSymbolScratch = new Complex32[WorkingSampleRate * 6];
    private int integerSymbolStride;
    private double[] symbolMagnitudeScratch = new double[WorkingSampleRate * 6 / SamplesPerSymbol];
    private double[] differentialEnergyPrefixScratch = new double[WorkingSampleRate * 6 / SamplesPerSymbol];
    private readonly List<bool> postFecBits = new(16_384);
    // Reduce multi-MS/s SDR streams in inexpensive bounded stages before the
    // selective polyphase FIR runs at roughly 60 kS/s.
    private readonly BoundedCicDecimator coarseDecimator = new();
    private readonly BoundedCicDecimator fineDecimator = new();
    private readonly PolyphaseRationalResampler finalResampler = new(64, allowUpsampling: false);
    private readonly ComplexIqLowPassFilter demodulationIqLowPass = new();
    private readonly AudioMonitor audioMonitor = new();
    private int inputSampleRate;
    private long workingSampleStart;
    private RateConversionPlan currentRatePlan;
    private BurstCandidate? pendingCandidate;
    private float inputRms;
    private float channelRms;
    private float channelPeak;
    private double lastSearchBestCorrelation;
    private double lastPreambleCorrelation;
    private double lastDataQuality;
    private double lastCarrierOffsetHz;
    private int lastDataRate;
    private long processedInputSamples;
    private long processedWorkingSamples;
    private long preambleSearchPassCount;
    private long preambleCorrelationEvaluationCount;
    private long preambleSparseCorrelationPointCount;
    private long preambleSparseGateCandidateCount;
    private long preambleCoarsePeakCandidateCount;
    private long preambleFullCorrelationEvaluationCount;
    private long preambleFullCorrelationPointCount;
    private long preambleReusedCorrelationPointCount;
    private long preambleMagnitudeCalculationCount;
    private long preambleSearchCpuTicks;
    private long preambleCandidateCount;
    private long preambleRefinementCount;
    private long synchronizationCount;
    private int preambleSearchSampleOffset;

    internal long PreambleCorrelationEvaluationCount => preambleCorrelationEvaluationCount;
    internal int PreambleSearchSampleOffset => preambleSearchSampleOffset;

    public long ValidFrameCount { get; private set; }
    public long RejectedFrameCount { get; private set; }

    public readonly record struct DiagnosticsSnapshot(
        int InputSampleRateHz,
        int CoarseDecimationFactor,
        int FineDecimationFactor,
        double IntermediateSampleRateHz,
        int ResamplerInterpolationFactor,
        int ResamplerDecimationFactor,
        float InputRms,
        float ChannelRms,
        float ChannelPeak,
        double LastSearchBestCorrelation,
        double LastPreambleCorrelation,
        double LastDataQuality,
        double LastCarrierOffsetHz,
        int LastDataRate,
        bool HasPendingBurst,
        int BufferedWorkingSamples,
        long ProcessedInputSamples,
        long ProcessedWorkingSamples,
        long PreambleSearchPassCount,
        long PreambleCandidateCount,
        long SynchronizationCount,
        long ValidFrameCount,
        long RejectedFrameCount,
        long PreambleSparseCorrelationPointCount = 0,
        long PreambleFullCorrelationPointCount = 0,
        long PreambleReusedCorrelationPointCount = 0,
        long PreambleMagnitudeCalculationCount = 0,
        double PreambleSearchCpuMilliseconds = 0,
        long PreambleSparseGateCandidateCount = 0,
        long PreambleCoarsePeakCandidateCount = 0,
        long PreambleFullCorrelationEvaluationCount = 0,
        long PreambleRefinementCount = 0);

    public DiagnosticsSnapshot GetDiagnostics() => new(
        inputSampleRate,
        currentRatePlan.CoarseFactor,
        currentRatePlan.FineFactor,
        currentRatePlan.IntermediateSampleRateHz,
        currentRatePlan.InterpolationFactor,
        currentRatePlan.ResamplerDecimationFactor,
        inputRms,
        channelRms,
        channelPeak,
        lastSearchBestCorrelation,
        lastPreambleCorrelation,
        lastDataQuality,
        lastCarrierOffsetHz,
        lastDataRate,
        pendingCandidate is not null,
        working.Count,
        processedInputSamples,
        processedWorkingSamples,
        preambleSearchPassCount,
        preambleCandidateCount,
        synchronizationCount,
        ValidFrameCount,
        RejectedFrameCount,
        preambleSparseCorrelationPointCount,
        preambleFullCorrelationPointCount,
        preambleReusedCorrelationPointCount,
        preambleMagnitudeCalculationCount,
        preambleSearchCpuTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
        preambleSparseGateCandidateCount,
        preambleCoarsePeakCandidateCount,
        preambleFullCorrelationEvaluationCount,
        preambleRefinementCount);

    public IReadOnlyList<HfdlFrame> Process(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata)
        => ProcessCore(samples, metadata, Span<float>.Empty, captureAudio: false, out _);

    public IReadOnlyList<HfdlFrame> Process(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata,
        Span<float> monitorAudio, out int monitorAudioSampleCount)
        => ProcessCore(samples, metadata, monitorAudio, captureAudio: true, out monitorAudioSampleCount);

    private IReadOnlyList<HfdlFrame> ProcessCore(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata,
        Span<float> monitorAudio, bool captureAudio, out int monitorAudioSampleCount)
    {
        if (metadata.SampleRateHz < MinimumSampleRateHz)
            throw new InvalidOperationException("HFDL reception requires an IQ sample rate of at least 48 kHz.");
        if (inputSampleRate != metadata.SampleRateHz || metadata.Discontinuity != IqDiscontinuity.None)
            Reset(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
        if (working.Count == 0) workingSampleStart = metadata.AbsoluteSampleStart;
        monitorAudioSampleCount = 0;
        double inputEnergy = 0;
        double outputEnergy = 0;
        double outputPeak = 0;
        int outputCount = 0;

        // HFDL occupies less than 2.8 kHz. The staged CIC response performs the
        // bulk anti-alias reduction, and the final 2.4 kHz FIR rejects adjacent
        // HF-channel energy before conversion to eight samples per symbol.
        foreach (Complex32 sample in samples)
        {
            inputEnergy += sample.I * sample.I + sample.Q * sample.Q;
            if (!coarseDecimator.TryProcess(sample.I, sample.Q, out float coarseI, out float coarseQ) ||
                !fineDecimator.TryProcess(coarseI, coarseQ, out float fineI, out float fineQ) ||
                !finalResampler.TryProcess(fineI, fineQ, out float outputI, out float outputQ)) continue;
            working.Add(new(outputI, outputQ));
            double outputPower = outputI * outputI + outputQ * outputQ;
            outputEnergy += outputPower;
            outputPeak = Math.Max(outputPeak, Math.Sqrt(outputPower));
            outputCount++;
            if (captureAudio)
            {
                if (monitorAudioSampleCount >= monitorAudio.Length)
                    throw new ArgumentException("The HFDL monitor audio buffer is too small.", nameof(monitorAudio));
                monitorAudio[monitorAudioSampleCount++] = audioMonitor.Process(outputI, outputQ);
            }
        }
        inputRms = samples.IsEmpty ? 0 : (float)Math.Sqrt(inputEnergy / samples.Length);
        if (outputCount > 0)
        {
            channelRms = (float)Math.Sqrt(outputEnergy / outputCount);
            channelPeak = (float)outputPeak;
        }
        processedInputSamples += samples.Length;
        processedWorkingSamples += outputCount;

        List<HfdlFrame> result = DecodeAvailable(metadata);
        int maximum = WorkingSampleRate * 6;
        if (working.Count > maximum)
            RemoveWorking(working.Count - maximum);
        return result;
    }

    public IReadOnlyList<HfdlFrame> ProcessChannel(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata channelMetadata,
        Span<float> monitorAudio,
        bool captureAudio,
        out int monitorAudioSampleCount)
    {
        AppliedChannelConfiguration applied = channelMetadata.Configuration;
        if (applied.OutputSampleRateHz != WorkingSampleRate)
            throw new InvalidOperationException($"HFDL requires a {WorkingSampleRate} Hz channel stream.");
        IqBlockMetadata source = channelMetadata.Source;
        if (channelMetadata.OutputSampleStart == 0 ||
            source.Discontinuity != IqDiscontinuity.None)
            ResetChannel(channelMetadata);

        monitorAudioSampleCount = 0;
        double energy = 0;
        double peak = 0;
        foreach (Complex32 sample in samples)
        {
            demodulationIqLowPass.Process(sample.I, sample.Q,
                out float filteredI, out float filteredQ);
            Complex32 filtered = new(filteredI, filteredQ);
            working.Add(filtered);
            double power = filteredI * filteredI + filteredQ * filteredQ;
            energy += power;
            peak = Math.Max(peak, Math.Sqrt(power));
            if (!captureAudio) continue;
            if (monitorAudioSampleCount >= monitorAudio.Length)
                throw new ArgumentException("The HFDL monitor audio buffer is too small.", nameof(monitorAudio));
            monitorAudio[monitorAudioSampleCount++] = audioMonitor.Process(sample.I, sample.Q);
        }
        inputRms = samples.IsEmpty ? 0 : (float)Math.Sqrt(energy / samples.Length);
        if (!samples.IsEmpty)
        {
            channelRms = inputRms;
            channelPeak = (float)peak;
        }
        processedInputSamples += source.SampleCount;
        processedWorkingSamples += samples.Length;
        IqBlockMetadata decodeMetadata = source with
        {
            CenterFrequencyHz = applied.ChannelCenterFrequencyHz,
            SampleCount = samples.Length
        };
        List<HfdlFrame> result = DecodeAvailable(decodeMetadata);
        int maximum = WorkingSampleRate * 6;
        if (working.Count > maximum) RemoveWorking(working.Count - maximum);
        return result;
    }

    private void ResetChannel(ChannelIqBlockMetadata metadata)
    {
        AppliedChannelConfiguration applied = metadata.Configuration;
        working.Clear();
        postFecBits.Clear();
        inputSampleRate = applied.InputSampleRateHz;
        workingSampleStart = metadata.MapOutputToSource(0);
        currentRatePlan = new(
            applied.CoarseDecimationFactor,
            applied.FineDecimationFactor,
            applied.InputSampleRateHz / (double)(applied.CoarseDecimationFactor * applied.FineDecimationFactor),
            applied.InterpolationFactor,
            applied.ResamplerDecimationFactor);
        demodulationIqLowPass.Configure(WorkingSampleRate, DemodulationIqCutoffHz);
        audioMonitor.Reset();
        pendingCandidate = null;
        preambleSearchSampleOffset = 0;
        inputRms = channelRms = channelPeak = 0;
        lastSearchBestCorrelation = 0;
        lastPreambleCorrelation = 0;
        lastDataQuality = 0;
        lastCarrierOffsetHz = 0;
        lastDataRate = 0;
        processedInputSamples = 0;
        processedWorkingSamples = 0;
        preambleSearchPassCount = 0;
        preambleCorrelationEvaluationCount = 0;
        preambleSparseCorrelationPointCount = 0;
        preambleSparseGateCandidateCount = 0;
        preambleCoarsePeakCandidateCount = 0;
        preambleFullCorrelationEvaluationCount = 0;
        preambleFullCorrelationPointCount = 0;
        preambleReusedCorrelationPointCount = 0;
        preambleMagnitudeCalculationCount = 0;
        preambleSearchCpuTicks = 0;
        preambleCandidateCount = 0;
        preambleRefinementCount = 0;
        synchronizationCount = 0;
    }

    /// <summary>Feeds an already FEC-decoded serial bit stream to the link deframer.</summary>
    public IReadOnlyList<byte[]> ProcessHardBits(ReadOnlySpan<bool> recoveredBits)
    {
        foreach (bool bit in recoveredBits) postFecBits.Add(bit);
        return ExtractFlagDelimitedFrames().Select(item => item.Bytes).ToArray();
    }

    public void Reset(long absoluteSampleStart = 0, int sampleRateHz = MinimumSampleRateHz)
    {
        working.Clear(); postFecBits.Clear(); inputSampleRate = sampleRateHz;
        workingSampleStart = absoluteSampleStart;
        currentRatePlan = SelectRateConversionPlan(sampleRateHz);
        int sourceDecimationFactor = checked(currentRatePlan.CoarseFactor * currentRatePlan.FineFactor);
        coarseDecimator.Configure(currentRatePlan.CoarseFactor, 3);
        fineDecimator.Configure(currentRatePlan.FineFactor, 3);
        finalResampler.Configure(sampleRateHz, sourceDecimationFactor,
            WorkingSampleRate, ChannelCutoffHz);
        audioMonitor.Reset();
        pendingCandidate = null;
        preambleSearchSampleOffset = 0;
        inputRms = channelRms = channelPeak = 0;
        lastSearchBestCorrelation = 0;
        lastPreambleCorrelation = 0;
        lastDataQuality = 0;
        lastCarrierOffsetHz = 0;
        lastDataRate = 0;
        processedInputSamples = 0;
        processedWorkingSamples = 0;
        preambleSearchPassCount = 0;
        preambleCorrelationEvaluationCount = 0;
        preambleSparseCorrelationPointCount = 0;
        preambleSparseGateCandidateCount = 0;
        preambleCoarsePeakCandidateCount = 0;
        preambleFullCorrelationEvaluationCount = 0;
        preambleFullCorrelationPointCount = 0;
        preambleReusedCorrelationPointCount = 0;
        preambleMagnitudeCalculationCount = 0;
        preambleSearchCpuTicks = 0;
        preambleCandidateCount = 0;
        preambleRefinementCount = 0;
        synchronizationCount = 0;
    }

    internal readonly record struct RateConversionPlan(
        int CoarseFactor,
        int FineFactor,
        double IntermediateSampleRateHz,
        int InterpolationFactor,
        int ResamplerDecimationFactor);

    internal static RateConversionPlan SelectRateConversionPlan(int sampleRateHz)
    {
        if (sampleRateHz < MinimumSampleRateHz)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        RateConversionPlan best = default;
        bool found = false;
        double bestIntermediateDistance = double.MaxValue;
        double bestCoarseDistance = double.MaxValue;
        int maximumCoarseFactor = Math.Max(1, sampleRateHz / 240_000 + 1);
        for (int coarseFactor = 1; coarseFactor <= maximumCoarseFactor; coarseFactor++)
        {
            double coarseRate = sampleRateHz / (double)coarseFactor;
            if (coarseRate is < 240_000 or > 400_000) continue;
            for (int fineFactor = 1; fineFactor <= 8; fineFactor++)
            {
                int totalFactor = coarseFactor * fineFactor;
                double intermediateRate = sampleRateHz / (double)totalFactor;
                if (intermediateRate is < 56_000 or > 72_000) continue;
                long numerator = (long)WorkingSampleRate * totalFactor;
                long divisor = GreatestCommonDivisor(numerator, sampleRateHz);
                int interpolation = checked((int)(numerator / divisor));
                int decimation = checked((int)(sampleRateHz / divisor));
                double intermediateDistance = Math.Abs(intermediateRate - 64_000);
                double coarseDistance = Math.Abs(coarseRate - 320_000);
                if (found && (interpolation > best.InterpolationFactor ||
                    interpolation == best.InterpolationFactor && intermediateDistance > bestIntermediateDistance ||
                    interpolation == best.InterpolationFactor && intermediateDistance == bestIntermediateDistance &&
                    coarseDistance >= bestCoarseDistance)) continue;
                best = new(coarseFactor, fineFactor, intermediateRate, interpolation, decimation);
                bestIntermediateDistance = intermediateDistance;
                bestCoarseDistance = coarseDistance;
                found = true;
            }
        }
        if (found) return best;

        int fallbackCoarse = Math.Max(1, sampleRateHz / 300_000);
        int fallbackCoarseRate = sampleRateHz / fallbackCoarse;
        int fallbackFine = Math.Max(1, fallbackCoarseRate / 60_000);
        int fallbackTotal = fallbackCoarse * fallbackFine;
        long fallbackNumerator = (long)WorkingSampleRate * fallbackTotal;
        long fallbackDivisor = GreatestCommonDivisor(fallbackNumerator, sampleRateHz);
        return new(fallbackCoarse, fallbackFine, sampleRateHz / (double)fallbackTotal,
            checked((int)(fallbackNumerator / fallbackDivisor)),
            checked((int)(sampleRateHz / fallbackDivisor)));
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }
}
