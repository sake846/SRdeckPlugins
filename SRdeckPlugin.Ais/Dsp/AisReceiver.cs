using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Contracts;

using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Ais.Dsp;

public sealed class AisReceiver(string channel, long frequencyHz)
{
    public const int SymbolRate = 9_600;
    public const int DemodulationSampleRateHz = 96_000;
    public const int MonitorAudioSampleRateHz = 48_000;
    public const int ChannelBandwidthHz = 25_000;
    // Keep the full occupied passband in the host channel request. This
    // narrower filter is for residual channel noise immediately before the
    // coherent correlator and phase discriminator.
    internal const float DemodulationIqCutoffHz = 14_000f;
    public const float DefaultSquelchThresholdDbfs = -85f;
    internal const int SamplesPerSymbol = DemodulationSampleRateHz / SymbolRate;
    private const int SquelchHoldSamples = DemodulationSampleRateHz * 150 / 1_000;
    private const int DuplicateWindowSamples = DemodulationSampleRateHz / 20;
    private const int RecentFrameRetentionSamples = DemodulationSampleRateHz * 10;
    private const int RecentFramePruneIntervalSamples = DemodulationSampleRateHz;
    private const float SquelchHysteresisDb = 2f;
    internal const int DemodulationGateDetectionWindowSamples = 64;
    internal const int DemodulationGatePreRollSamples = 768;
    internal const ushort AllNoncoherentPhaseMask =
        (1 << SamplesPerSymbol) - 1;
    private const int DemodulationGateHoldSamples = DemodulationSampleRateHz / 100;
    private static readonly int[] TimingHypotheses = [0, 2, 4, 6, 8];
    private static readonly int[] FrequencyHypothesesHz = [-1_500, 0, 1_500];
    private readonly PhaseDecoder[] phases = Enumerable.Range(0, SamplesPerSymbol)
        .Select(offset => new PhaseDecoder(offset)).ToArray();
    private readonly CoherentGmskSequenceDecoder[] coherentDecoders =
        (from offset in TimingHypotheses
         from frequencyHypothesisIndex in Enumerable.Range(0, FrequencyHypothesesHz.Length)
         select new CoherentGmskSequenceDecoder(offset,
             FrequencyHypothesesHz[frequencyHypothesisIndex], frequencyHypothesisIndex)).ToArray();
    private readonly CoherentGmskFrequencyMixer[] coherentMixers = FrequencyHypothesesHz
        .Select(frequencyOffset => new CoherentGmskFrequencyMixer(frequencyOffset)).ToArray();
    private readonly ComplexIqLowPassFilter iqChannelLowPass = new();
    private readonly RecentAisFrameCache recentFrames = new(
        DuplicateWindowSamples,
        RecentFrameRetentionSamples,
        RecentFramePruneIntervalSamples);
    private readonly RecentAisPhaseContributionCache recentPhaseContributions = new(
        DuplicateWindowSamples,
        RecentFrameRetentionSamples,
        RecentFramePruneIntervalSamples);
    private readonly long[] uniqueNoncoherentFramesByPhase =
        new long[SamplesPerSymbol];
    private bool hasPreviousSample;
    private Complex32 previousSample;
    private long discriminatorIndex;
    private long validFrames;
    private long rejectedFrames;
    private long fallbackRejectedFrames;
    private long coherentRejectedFrames;
    private double signalQualitySum;
    private double maximumSignalQuality;
    private double lastSignalQuality;
    private int inputSampleRateHz;
    private long inputCenterFrequencyHz;
    private int outputSampleRateHz;
    private int coarseDecimationFactor = 1;
    private int fineDecimationFactor = 1;
    private int resamplerInterpolationFactor = 1;
    private int resamplerDecimationFactor = 1;
    private DateTimeOffset lastFrameAt;
    private long coherentFrames;
    private long fallbackFrames;
    private int lastFrequencyCorrectionHz;
    private double channelLevelDbfs = double.NaN;
    private int monitorDecimationPhase;
    private float monitorAccumulator;
    private float monitorPower;
    private float monitorGain = 1f;
    private bool isSquelchEnabled = true;
    private float squelchThresholdDbfs = DefaultSquelchThresholdDbfs;
    private bool isSquelchOpen;
    private int squelchHoldSamples;
    private bool iqChannelLowPassConfigured;
    private bool hasPreviousMonitorSample;
    private Complex32 previousMonitorSample;
    private bool isDemodulationGateEnabled;
    private bool isDemodulationGateOpen = true;
    private readonly Complex32[] demodulationGateRing =
        new Complex32[DemodulationGatePreRollSamples];
    private readonly long[] demodulationGatePositions =
        new long[DemodulationGatePreRollSamples];
    private int demodulationGateRingPosition;
    private int demodulationGateRingCount;
    private readonly float[] demodulationGatePowerWindow =
        new float[DemodulationGateDetectionWindowSamples];
    private int demodulationGatePowerPosition;
    private int demodulationGatePowerCount;
    private double demodulationGatePowerSum;
    private double demodulationGateThresholdPower =
        Math.Pow(10, DefaultSquelchThresholdDbfs / 10d);
    private double demodulationGateCloseThresholdPower =
        Math.Pow(10, (DefaultSquelchThresholdDbfs - SquelchHysteresisDb) / 10d);
    private int demodulationGateHoldSamples;
    private long demodulationGateBypassedSamples;
    private long demodulationGateReplaySamples;
    private long demodulationGateOpenCount;
    private int lastDemodulationReplaySampleCount;
    private int lastDemodulationDetectionDelaySamples;
    internal bool IsCoherentDemodulationEnabled { get; set; } = true;
    internal ushort NoncoherentPhaseMask { get; set; } = AllNoncoherentPhaseMask;
    private static readonly float MonitorPowerCoefficient =
        (float)(1 - Math.Exp(-1 / (0.050 * MonitorAudioSampleRateHz)));

    public string Channel { get; } = channel;
    public long FrequencyHz { get; } = frequencyHz;
    public long ValidFrameCount => validFrames;
    public long RejectedFrameCount => rejectedFrames;
    public bool IsSquelchEnabled
    {
        get => isSquelchEnabled;
        set
        {
            isSquelchEnabled = value;
            if (!value)
            {
                isSquelchOpen = true;
                squelchHoldSamples = 0;
            }
        }
    }
    public float SquelchThresholdDbfs
    {
        get => squelchThresholdDbfs;
        set
        {
            squelchThresholdDbfs = Math.Clamp(value, -120f, 0f);
            demodulationGateThresholdPower = Math.Pow(10, squelchThresholdDbfs / 10d);
            demodulationGateCloseThresholdPower = Math.Pow(10,
                (squelchThresholdDbfs - SquelchHysteresisDb) / 10d);
        }
    }
    public bool IsSquelchOpen => !isSquelchEnabled || isSquelchOpen;
    public bool IsDemodulationGateEnabled
    {
        get => isDemodulationGateEnabled;
        set
        {
            if (isDemodulationGateEnabled == value) return;
            isDemodulationGateEnabled = value;
            ResetDemodulatorState();
            ResetDemodulationGateState();
        }
    }
    public bool IsDemodulationGateOpen =>
        !isDemodulationGateEnabled || isDemodulationGateOpen;

    public void Reset()
    {
        foreach (PhaseDecoder phase in phases) phase.Reset();
        foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders) decoder.Reset();
        recentFrames.Clear();
        recentPhaseContributions.Clear();
        Array.Clear(uniqueNoncoherentFramesByPhase);
        hasPreviousSample = false;
        previousSample = default;
        discriminatorIndex = 0;
        validFrames = 0;
        rejectedFrames = 0;
        fallbackRejectedFrames = 0;
        coherentRejectedFrames = 0;
        signalQualitySum = 0;
        maximumSignalQuality = 0;
        lastSignalQuality = 0;
        inputSampleRateHz = 0;
        inputCenterFrequencyHz = 0;
        outputSampleRateHz = 0;
        coarseDecimationFactor = 1;
        fineDecimationFactor = 1;
        resamplerInterpolationFactor = 1;
        resamplerDecimationFactor = 1;
        lastFrameAt = default;
        coherentFrames = 0;
        fallbackFrames = 0;
        lastFrequencyCorrectionHz = 0;
        channelLevelDbfs = double.NaN;
        monitorDecimationPhase = 0;
        monitorAccumulator = 0;
        monitorPower = 0;
        monitorGain = 1;
        hasPreviousMonitorSample = false;
        previousMonitorSample = default;
        isSquelchOpen = !isSquelchEnabled;
        squelchHoldSamples = 0;
        demodulationGateBypassedSamples = 0;
        demodulationGateReplaySamples = 0;
        demodulationGateOpenCount = 0;
        lastDemodulationReplaySampleCount = 0;
        lastDemodulationDetectionDelaySamples = 0;
        ResetDemodulationGateState();
        iqChannelLowPass.Reset();
        iqChannelLowPassConfigured = false;
    }

    public IReadOnlyList<AisFrame> ProcessChannel(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata)
        => ProcessChannel(samples, metadata, Span<float>.Empty, out _);

    public IReadOnlyList<AisFrame> ProcessChannel(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata,
        Span<float> monitorAudio,
        out int monitorAudioSampleCount)
    {
        if (metadata.Configuration.OutputSampleRateHz != DemodulationSampleRateHz)
            throw new ArgumentException($"AIS requires {DemodulationSampleRateHz} S/s channel IQ.", nameof(metadata));
        inputSampleRateHz = metadata.Source.SampleRateHz;
        inputCenterFrequencyHz = metadata.Source.CenterFrequencyHz;
        outputSampleRateHz = metadata.Configuration.OutputSampleRateHz;
        coarseDecimationFactor = metadata.Configuration.CoarseDecimationFactor;
        fineDecimationFactor = metadata.Configuration.FineDecimationFactor;
        resamplerInterpolationFactor = metadata.Configuration.InterpolationFactor;
        resamplerDecimationFactor = metadata.Configuration.ResamplerDecimationFactor;
        if (!iqChannelLowPassConfigured)
        {
            iqChannelLowPass.Configure(outputSampleRateHz, DemodulationIqCutoffHz);
            iqChannelLowPassConfigured = true;
        }
        if (metadata.Source.Discontinuity != IqDiscontinuity.None) ResetSignalState();
        MeasureChannelLevel(samples);
        UpdateSquelch(samples.Length);
        var results = new List<AisFrame>();
        monitorAudioSampleCount = 0;
        bool writeMonitorAudio = !monitorAudio.IsEmpty && IsSquelchOpen;
        Span<CoherentGmskMixedSample> mixed = stackalloc CoherentGmskMixedSample[3];

        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 input = samples[index];
            iqChannelLowPass.Process(input.I, input.Q, out float filteredI, out float filteredQ);
            Complex32 current = new(filteredI, filteredQ);
            bool hasMonitorDiscriminator = false;
            float monitorDiscriminator = 0;
            if (!monitorAudio.IsEmpty)
                hasMonitorDiscriminator = ProcessMonitorSample(current, monitorAudio,
                    writeMonitorAudio, ref monitorAudioSampleCount,
                    out monitorDiscriminator);

            long outputPosition = metadata.OutputSampleStart + index;
            if (!isDemodulationGateEnabled)
                FeedDemodulators(current, outputPosition, metadata, results, mixed,
                    hasMonitorDiscriminator, monitorDiscriminator);
            else
                ProcessGatedSample(current, outputPosition, metadata, results, mixed,
                    hasMonitorDiscriminator, monitorDiscriminator);
        }
        // RejectedFrames is a hypothesis-level FCS rejection count. Keep the
        // fallback and coherent contributions separate so diagnostics can
        // explain the total without mixing decoder populations.
        fallbackRejectedFrames = phases.Sum(item => item.RejectedFrames);
        coherentRejectedFrames = coherentDecoders.Sum(item => item.RejectedFrames);
        rejectedFrames = fallbackRejectedFrames + coherentRejectedFrames;
        return results;
    }

    private bool ProcessMonitorSample(
        Complex32 current,
        Span<float> monitorAudio,
        bool writeMonitorAudio,
        ref int monitorAudioSampleCount,
        out float discriminator)
    {
        discriminator = 0;
        if (!hasPreviousMonitorSample)
        {
            previousMonitorSample = current;
            hasPreviousMonitorSample = true;
            return false;
        }
        double cross = previousMonitorSample.I * current.Q -
                       previousMonitorSample.Q * current.I;
        double dot = previousMonitorSample.I * current.I +
                     previousMonitorSample.Q * current.Q;
        previousMonitorSample = current;
        discriminator = (float)Math.Atan2(cross, dot);
        monitorAccumulator += discriminator;
        if (++monitorDecimationPhase != 2) return true;

        monitorDecimationPhase = 0;
        float raw = monitorAccumulator * 0.5f / MathF.PI;
        monitorAccumulator = 0;
        monitorPower += MonitorPowerCoefficient * (raw * raw - monitorPower);
        float desiredGain = Math.Clamp(
            0.2f / MathF.Sqrt(MathF.Max(monitorPower, 1e-10f)), 0.1f, 20f);
        monitorGain += 0.01f * (desiredGain - monitorGain);
        if (!writeMonitorAudio) return true;
        if (monitorAudioSampleCount >= monitorAudio.Length)
            throw new ArgumentException("The AIS monitor audio buffer is too small.",
                nameof(monitorAudio));
        monitorAudio[monitorAudioSampleCount++] = MathF.Tanh(raw * monitorGain);
        return true;
    }

    private void FeedDemodulators(
        Complex32 current,
        long outputPosition,
        ChannelIqBlockMetadata metadata,
        List<AisFrame> results,
        Span<CoherentGmskMixedSample> mixed,
        bool hasPrecomputedDiscriminator,
        float precomputedDiscriminator)
    {
        if (IsCoherentDemodulationEnabled)
        {
            for (int hypothesis = 0; hypothesis < coherentMixers.Length; hypothesis++)
                mixed[hypothesis] = coherentMixers[hypothesis].Mix(current, discriminatorIndex);
            foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders)
            {
                byte[]? payload = decoder.Feed(current,
                    mixed[decoder.FrequencyHypothesisIndex], discriminatorIndex, out double quality);
                if (payload is not null)
                    TryAddFrame(payload, quality, decoder.FrequencyOffsetHz, coherent: true,
                        outputPosition, metadata, results);
            }
        }

        if (NoncoherentPhaseMask == 0)
        {
            discriminatorIndex++;
            return;
        }
        if (!hasPreviousSample)
        {
            previousSample = current;
            hasPreviousSample = true;
            discriminatorIndex++;
            return;
        }
        float discriminator;
        if (hasPrecomputedDiscriminator)
        {
            discriminator = precomputedDiscriminator;
        }
        else
        {
            double cross = previousSample.I * current.Q - previousSample.Q * current.I;
            double dot = previousSample.I * current.I + previousSample.Q * current.Q;
            discriminator = (float)Math.Atan2(cross, dot);
        }
        previousSample = current;
        for (int phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            if ((NoncoherentPhaseMask & (1 << phaseIndex)) == 0) continue;
            PhaseDecoder phase = phases[phaseIndex];
            byte[]? payload = phase.Feed(discriminator, discriminatorIndex, out double quality);
            if (payload is null) continue;
            AisPhaseContributionChange contribution = recentPhaseContributions.Track(
                payload, outputPosition, phaseIndex);
            if (contribution.RemovedUniquePhase >= 0)
                uniqueNoncoherentFramesByPhase[contribution.RemovedUniquePhase]--;
            if (contribution.AddedUniquePhase >= 0)
                uniqueNoncoherentFramesByPhase[contribution.AddedUniquePhase]++;
            TryAddFrame(payload, quality, 0, coherent: false,
                outputPosition, metadata, results);
        }
        discriminatorIndex++;
    }

    private void ProcessGatedSample(
        Complex32 current,
        long outputPosition,
        ChannelIqBlockMetadata metadata,
        List<AisFrame> results,
        Span<CoherentGmskMixedSample> mixed,
        bool hasPrecomputedDiscriminator,
        float precomputedDiscriminator)
    {
        AddDemodulationGateSample(current, outputPosition);
        double meanPower = UpdateDemodulationGatePower(current);
        bool detectorReady = demodulationGatePowerCount ==
            DemodulationGateDetectionWindowSamples;
        bool detected = detectorReady && meanPower >= demodulationGateThresholdPower;
        if (isDemodulationGateOpen)
        {
            FeedDemodulators(current, outputPosition, metadata, results, mixed,
                hasPrecomputedDiscriminator, precomputedDiscriminator);
            if (detectorReady && meanPower >= demodulationGateCloseThresholdPower)
            {
                demodulationGateHoldSamples = DemodulationGateHoldSamples;
                return;
            }
            if (demodulationGateHoldSamples > 0) demodulationGateHoldSamples--;
            if (demodulationGateHoldSamples != 0) return;
            isDemodulationGateOpen = false;
            ResetDemodulatorState();
            return;
        }

        demodulationGateBypassedSamples++;
        if (!detected) return;

        ResetDemodulatorState();
        isDemodulationGateOpen = true;
        demodulationGateHoldSamples = DemodulationGateHoldSamples;
        demodulationGateOpenCount++;
        lastDemodulationReplaySampleCount = demodulationGateRingCount;
        lastDemodulationDetectionDelaySamples = CalculateDemodulationDetectionDelay();
        demodulationGateReplaySamples += demodulationGateRingCount;
        int replayStart = demodulationGateRingPosition - demodulationGateRingCount;
        if (replayStart < 0) replayStart += demodulationGateRing.Length;
        for (int replay = 0; replay < demodulationGateRingCount; replay++)
        {
            int ringIndex = replayStart + replay;
            if (ringIndex >= demodulationGateRing.Length)
                ringIndex -= demodulationGateRing.Length;
            FeedDemodulators(demodulationGateRing[ringIndex],
                demodulationGatePositions[ringIndex], metadata, results, mixed,
                hasPrecomputedDiscriminator: false, precomputedDiscriminator: 0);
        }
    }

    private void AddDemodulationGateSample(Complex32 sample, long outputPosition)
    {
        demodulationGateRing[demodulationGateRingPosition] = sample;
        demodulationGatePositions[demodulationGateRingPosition] = outputPosition;
        if (++demodulationGateRingPosition == demodulationGateRing.Length)
            demodulationGateRingPosition = 0;
        if (demodulationGateRingCount < demodulationGateRing.Length)
            demodulationGateRingCount++;
    }

    private double UpdateDemodulationGatePower(Complex32 sample)
    {
        float power = sample.I * sample.I + sample.Q * sample.Q;
        demodulationGatePowerSum -=
            demodulationGatePowerWindow[demodulationGatePowerPosition];
        demodulationGatePowerWindow[demodulationGatePowerPosition] = power;
        demodulationGatePowerSum += power;
        if (++demodulationGatePowerPosition == demodulationGatePowerWindow.Length)
            demodulationGatePowerPosition = 0;
        if (demodulationGatePowerCount < demodulationGatePowerWindow.Length)
            demodulationGatePowerCount++;
        return demodulationGatePowerSum / Math.Max(1, demodulationGatePowerCount);
    }

    private int CalculateDemodulationDetectionDelay()
    {
        int delay = 0;
        int index = demodulationGatePowerPosition;
        for (int sample = 0; sample < demodulationGatePowerCount; sample++)
        {
            if (--index < 0) index = demodulationGatePowerWindow.Length - 1;
            if (demodulationGatePowerWindow[index] < demodulationGateThresholdPower)
                break;
            delay++;
        }
        return Math.Max(1, delay);
    }

    private void ResetDemodulatorState()
    {
        foreach (PhaseDecoder phase in phases) phase.ResetState();
        foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders)
            decoder.ResetState();
        hasPreviousSample = false;
        previousSample = default;
        discriminatorIndex = 0;
    }

    private void ResetDemodulationGateState()
    {
        isDemodulationGateOpen = !isDemodulationGateEnabled;
        demodulationGateRingPosition = 0;
        demodulationGateRingCount = 0;
        demodulationGatePowerPosition = 0;
        demodulationGatePowerCount = 0;
        demodulationGatePowerSum = 0;
        demodulationGateHoldSamples = 0;
        lastDemodulationReplaySampleCount = 0;
        lastDemodulationDetectionDelaySamples = 0;
        Array.Clear(demodulationGatePowerWindow);
    }

    private void TryAddFrame(
        byte[] payload,
        double quality,
        int frequencyCorrectionHz,
        bool coherent,
        long outputPosition,
        ChannelIqBlockMetadata metadata,
        List<AisFrame> results)
    {
        RecentAisFrameResult recent = recentFrames.TryAdd(
            payload, outputPosition, coherent);
        if (recent != RecentAisFrameResult.Added)
        {
            if (recent == RecentAisFrameResult.Promoted)
            {
                if (fallbackFrames > 0) fallbackFrames--;
                coherentFrames++;
                lastFrequencyCorrectionHz = frequencyCorrectionHz;
            }
            return;
        }

        long sourcePosition = metadata.MapOutputToSource(outputPosition);
        DateTimeOffset receivedAt = metadata.Source.UtcTimestamp.AddSeconds(
            (sourcePosition - metadata.Source.AbsoluteSampleStart) /
            (double)metadata.Source.SampleRateHz);
        lastSignalQuality = quality;
        signalQualitySum += quality;
        maximumSignalQuality = Math.Max(maximumSignalQuality, quality);
        validFrames++;
        if (coherent)
        {
            coherentFrames++;
            lastFrequencyCorrectionHz = frequencyCorrectionHz;
        }
        else fallbackFrames++;
        lastFrameAt = receivedAt;
        results.Add(new(payload, receivedAt, metadata.Source.StreamId, sourcePosition,
            Channel, FrequencyHz, quality));
    }

    public DiagnosticsSnapshot GetDiagnostics() => new(
        DateTimeOffset.UtcNow,
        Channel,
        FrequencyHz,
        inputSampleRateHz,
        inputCenterFrequencyHz,
        outputSampleRateHz,
        coarseDecimationFactor,
        fineDecimationFactor,
        resamplerInterpolationFactor,
        resamplerDecimationFactor,
        validFrames,
        rejectedFrames,
        lastSignalQuality,
        validFrames == 0 ? 0 : signalQualitySum / validFrames,
        maximumSignalQuality,
        lastFrameAt,
        coherentFrames,
        fallbackFrames,
        lastFrequencyCorrectionHz,
        phases.Sum(item => item.FlagCount) + coherentDecoders.Sum(item => item.FlagCount),
        phases.Sum(item => item.FrameCandidateCount) + coherentDecoders.Sum(item => item.FrameCandidateCount),
        phases.Sum(item => item.HypothesisValidFrames) + coherentDecoders.Sum(item => item.ValidFrames),
        channelLevelDbfs,
        IsSquelchEnabled,
        IsSquelchOpen,
        SquelchThresholdDbfs,
        fallbackRejectedFrames,
        coherentRejectedFrames,
        IsDemodulationGateEnabled,
        IsDemodulationGateOpen,
        DemodulationGatePreRollSamples,
        DemodulationGateDetectionWindowSamples,
        lastDemodulationReplaySampleCount,
        lastDemodulationDetectionDelaySamples,
        demodulationGateBypassedSamples,
        demodulationGateReplaySamples,
        demodulationGateOpenCount,
        phases.Select(item => new NoncoherentPhaseDiagnosticsSnapshot(
            item.Offset,
            item.FlagCount,
            item.FrameCandidateCount,
            item.HypothesisValidFrames,
            item.RejectedFrames,
            uniqueNoncoherentFramesByPhase[item.Offset])).ToArray());

    private void ResetSignalState()
    {
        foreach (PhaseDecoder phase in phases) phase.Reset();
        foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders) decoder.Reset();
        recentFrames.Clear();
        recentPhaseContributions.Clear();
        Array.Clear(uniqueNoncoherentFramesByPhase);
        hasPreviousSample = false;
        discriminatorIndex = 0;
        monitorDecimationPhase = 0;
        monitorAccumulator = 0;
        monitorPower = 0;
        monitorGain = 1;
        hasPreviousMonitorSample = false;
        previousMonitorSample = default;
        channelLevelDbfs = double.NaN;
        isSquelchOpen = !isSquelchEnabled;
        squelchHoldSamples = 0;
        ResetDemodulationGateState();
        if (iqChannelLowPassConfigured) iqChannelLowPass.Reset();
    }

    private void MeasureChannelLevel(ReadOnlySpan<Complex32> samples)
    {
        double powerSum = 0;
        int finiteCount = 0;
        foreach (Complex32 sample in samples)
        {
            double power = sample.I * sample.I + sample.Q * sample.Q;
            if (!double.IsFinite(power)) continue;
            powerSum += power;
            finiteCount++;
        }
        channelLevelDbfs = finiteCount == 0
            ? double.NaN
            : 10 * Math.Log10(Math.Max(powerSum / finiteCount, 1e-12));
    }

    private void UpdateSquelch(int sampleCount)
    {
        if (!isSquelchEnabled)
        {
            isSquelchOpen = true;
            squelchHoldSamples = 0;
            return;
        }

        double threshold = squelchThresholdDbfs +
            (isSquelchOpen ? -SquelchHysteresisDb : SquelchHysteresisDb);
        if (double.IsFinite(channelLevelDbfs) && channelLevelDbfs >= threshold)
        {
            isSquelchOpen = true;
            squelchHoldSamples = SquelchHoldSamples;
            return;
        }

        squelchHoldSamples = Math.Max(0, squelchHoldSamples - sampleCount);
        if (squelchHoldSamples == 0) isSquelchOpen = false;
    }

    // ValidFrames is the de-duplicated frame count emitted to the plugin.
    // RejectedFrames and HypothesisRejectedFrameCount are per-decoder
    // hypothesis counts, so they must be interpreted with the other
    // Hypothesis* fields rather than added to ValidFrames.
    public sealed record DiagnosticsSnapshot(
        DateTimeOffset MeasuredAt,
        string Channel,
        long ChannelFrequencyHz,
        int InputSampleRateHz,
        long InputCenterFrequencyHz,
        int OutputSampleRateHz,
        int CoarseDecimationFactor,
        int FineDecimationFactor,
        int ResamplerInterpolationFactor,
        int ResamplerDecimationFactor,
        long ValidFrames,
        long RejectedFrames,
        double LastSignalQuality,
        double AverageSignalQuality,
        double MaximumSignalQuality,
        DateTimeOffset LastFrameAt,
        long CoherentFrames,
        long FallbackFrames,
        int LastFrequencyCorrectionHz,
        long HypothesisFlagCount = 0,
        long HypothesisFrameCandidateCount = 0,
        long HypothesisValidFrameCount = 0,
        double ChannelLevelDbfs = double.NaN,
        bool IsSquelchEnabled = true,
        bool IsSquelchOpen = false,
        float SquelchThresholdDbfs = DefaultSquelchThresholdDbfs,
        long FallbackRejectedFrameCount = 0,
        long CoherentRejectedFrameCount = 0,
        bool IsDemodulationGateEnabled = false,
        bool IsDemodulationGateOpen = true,
        int DemodulationGatePreRollSamples = 0,
        int DemodulationGateDetectionWindowSamples = 0,
        int LastDemodulationReplaySampleCount = 0,
        int LastDemodulationDetectionDelaySamples = 0,
        long DemodulationGateBypassedSamples = 0,
        long DemodulationGateReplaySamples = 0,
        long DemodulationGateOpenCount = 0,
        IReadOnlyList<NoncoherentPhaseDiagnosticsSnapshot>? NoncoherentPhases = null)
    {
        public long HypothesisRejectedFrameCount => RejectedFrames;
    }

    public sealed record NoncoherentPhaseDiagnosticsSnapshot(
        int Offset,
        long FlagCount,
        long FrameCandidateCount,
        long ValidFrameCount,
        long RejectedFrameCount,
        long UniqueValidFrameCount);

    private sealed class PhaseDecoder(int offset)
    {
        private readonly AisHdlcDecoder decoder = new();
        private float sum;
        private int count;
        private bool initializedRange;
        private double low;
        private double high;
        public long RejectedFrames => decoder.RejectedFrames;
        public int Offset => offset;
        public long FlagCount => decoder.FlagCount;
        public long FrameCandidateCount => decoder.FrameCandidateCount;
        public long HypothesisValidFrames => decoder.ValidFrames;

        public void Reset()
        {
            decoder.Reset();
            ResetIntegratorState();
        }

        public void ResetState()
        {
            decoder.ResetState();
            ResetIntegratorState();
        }

        private void ResetIntegratorState()
        {
            sum = 0;
            count = 0;
            initializedRange = false;
            low = 0;
            high = 0;
        }

        public byte[]? Feed(float discriminator, long index, out double quality)
        {
            quality = 0;
            if (index < offset) return null;
            sum += discriminator;
            if (++count < SamplesPerSymbol) return null;

            double symbol = sum / count;
            sum = 0;
            count = 0;
            if (!initializedRange)
            {
                low = high = symbol;
                initializedRange = true;
            }
            else
            {
                const double decay = 0.002;
                low = symbol < low ? symbol : low + (symbol - low) * decay;
                high = symbol > high ? symbol : high + (symbol - high) * decay;
            }
            double threshold = (low + high) * 0.5;
            quality = Math.Clamp(Math.Abs(symbol - threshold) / Math.Max((high - low) * 0.5, 1e-6), 0, 1);
            return decoder.FeedLevel(symbol >= threshold);
        }
    }
}
