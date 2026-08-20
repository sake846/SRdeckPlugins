using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Ais.Dsp;

public sealed class AisReceiver(string channel, long frequencyHz)
{
    public const int SymbolRate = 9_600;
    public const int DemodulationSampleRateHz = 96_000;
    public const int MonitorAudioSampleRateHz = 48_000;
    public const int ChannelBandwidthHz = 25_000;
    public const float DefaultSquelchThresholdDbfs = -85f;
    internal const int SamplesPerSymbol = DemodulationSampleRateHz / SymbolRate;
    private const int SquelchHoldSamples = DemodulationSampleRateHz * 150 / 1_000;
    private const float SquelchHysteresisDb = 2f;
    private static readonly int[] TimingHypotheses = [0, 2, 4, 6, 8];
    private static readonly int[] FrequencyHypothesesHz = [-1_500, 0, 1_500];
    private readonly PhaseDecoder[] phases = Enumerable.Range(0, SamplesPerSymbol)
        .Select(offset => new PhaseDecoder(offset)).ToArray();
    private readonly CoherentGmskSequenceDecoder[] coherentDecoders =
        (from offset in TimingHypotheses
         from frequencyOffset in FrequencyHypothesesHz
         select new CoherentGmskSequenceDecoder(offset, frequencyOffset)).ToArray();
    private readonly Dictionary<string, RecentFrame> recentFrames = new(StringComparer.Ordinal);
    private bool hasPreviousSample;
    private Complex32 previousSample;
    private long discriminatorIndex;
    private long validFrames;
    private long rejectedFrames;
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
        set => squelchThresholdDbfs = Math.Clamp(value, -120f, 0f);
    }
    public bool IsSquelchOpen => !isSquelchEnabled || isSquelchOpen;

    public void Reset()
    {
        foreach (PhaseDecoder phase in phases) phase.Reset();
        foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders) decoder.Reset();
        recentFrames.Clear();
        hasPreviousSample = false;
        previousSample = default;
        discriminatorIndex = 0;
        validFrames = 0;
        rejectedFrames = 0;
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
        isSquelchOpen = !isSquelchEnabled;
        squelchHoldSamples = 0;
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
        if (metadata.Source.Discontinuity != IqDiscontinuity.None) ResetSignalState();
        MeasureChannelLevel(samples);
        UpdateSquelch(samples.Length);
        var results = new List<AisFrame>();
        monitorAudioSampleCount = 0;
        bool writeMonitorAudio = !monitorAudio.IsEmpty && IsSquelchOpen;

        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 current = samples[index];
            foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders)
            {
                byte[]? payload = decoder.Feed(current, discriminatorIndex, out double quality);
                if (payload is not null)
                    TryAddFrame(payload, quality, decoder.FrequencyOffsetHz, coherent: true,
                        index, metadata, results);
            }

            if (!hasPreviousSample)
            {
                previousSample = current;
                hasPreviousSample = true;
                discriminatorIndex++;
                continue;
            }
            double cross = previousSample.I * current.Q - previousSample.Q * current.I;
            double dot = previousSample.I * current.I + previousSample.Q * current.Q;
            float discriminator = (float)Math.Atan2(cross, dot);
            previousSample = current;
            if (!monitorAudio.IsEmpty)
            {
                monitorAccumulator += discriminator;
                if (++monitorDecimationPhase == 2)
                {
                    monitorDecimationPhase = 0;
                    float raw = monitorAccumulator * 0.5f / MathF.PI;
                    monitorAccumulator = 0;
                    monitorPower += MonitorPowerCoefficient * (raw * raw - monitorPower);
                    float desiredGain = Math.Clamp(
                        0.2f / MathF.Sqrt(MathF.Max(monitorPower, 1e-10f)), 0.1f, 20f);
                    monitorGain += 0.01f * (desiredGain - monitorGain);
                    if (writeMonitorAudio)
                    {
                        if (monitorAudioSampleCount >= monitorAudio.Length)
                            throw new ArgumentException("The AIS monitor audio buffer is too small.",
                                nameof(monitorAudio));
                        monitorAudio[monitorAudioSampleCount++] = MathF.Tanh(raw * monitorGain);
                    }
                }
            }

            foreach (PhaseDecoder phase in phases)
            {
                byte[]? payload = phase.Feed(discriminator, discriminatorIndex, out double quality);
                if (payload is null) continue;
                TryAddFrame(payload, quality, 0, coherent: false, index, metadata, results);
            }
            discriminatorIndex++;
        }
        rejectedFrames = phases.Sum(item => item.RejectedFrames);
        return results;
    }

    private void TryAddFrame(
        byte[] payload,
        double quality,
        int frequencyCorrectionHz,
        bool coherent,
        int index,
        ChannelIqBlockMetadata metadata,
        List<AisFrame> results)
    {
        long outputPosition = metadata.OutputSampleStart + index;
        string key = Convert.ToHexString(payload);
        if (recentFrames.TryGetValue(key, out RecentFrame previous) &&
            outputPosition - previous.Position < DemodulationSampleRateHz / 20)
        {
            if (coherent && !previous.Coherent)
            {
                recentFrames[key] = previous with { Coherent = true };
                if (fallbackFrames > 0) fallbackFrames--;
                coherentFrames++;
                lastFrequencyCorrectionHz = frequencyCorrectionHz;
            }
            return;
        }
        recentFrames[key] = new(outputPosition, coherent);
        foreach (string stale in recentFrames.Where(item =>
                     outputPosition - item.Value.Position > DemodulationSampleRateHz * 10L)
                 .Select(item => item.Key).ToArray())
            recentFrames.Remove(stale);

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
        SquelchThresholdDbfs);

    private void ResetSignalState()
    {
        foreach (PhaseDecoder phase in phases) phase.Reset();
        foreach (CoherentGmskSequenceDecoder decoder in coherentDecoders) decoder.Reset();
        recentFrames.Clear();
        hasPreviousSample = false;
        discriminatorIndex = 0;
        monitorDecimationPhase = 0;
        monitorAccumulator = 0;
        monitorPower = 0;
        monitorGain = 1;
        channelLevelDbfs = double.NaN;
        isSquelchOpen = !isSquelchEnabled;
        squelchHoldSamples = 0;
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
        float SquelchThresholdDbfs = DefaultSquelchThresholdDbfs);

    private readonly record struct RecentFrame(long Position, bool Coherent);

    private sealed class PhaseDecoder(int offset)
    {
        private readonly AisHdlcDecoder decoder = new();
        private float sum;
        private int count;
        private bool initializedRange;
        private double low;
        private double high;
        public long RejectedFrames => decoder.RejectedFrames;
        public long FlagCount => decoder.FlagCount;
        public long FrameCandidateCount => decoder.FrameCandidateCount;
        public long HypothesisValidFrames => decoder.ValidFrames;

        public void Reset()
        {
            decoder.Reset();
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
