using System;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

internal enum LoRaSearchThresholdMode
{
    Fixed,
    AdaptiveTemporalQuantile
}

internal sealed record LoRaSearchThresholdOptions(
    LoRaSearchThresholdMode Mode,
    float FixedThresholdDb = 11.0f,
    float MinimumThresholdDb = 11.0f,
    float MaximumThresholdDb = 18.0f,
    double TargetWindowFalseAlarmProbability = 0.01,
    float MarginDb = 0.5f,
    int WindowLength = 256,
    int MinimumSamples = 64)
{
    public static LoRaSearchThresholdOptions Fixed { get; } = new(LoRaSearchThresholdMode.Fixed);
}

internal readonly record struct LoRaSearchThresholdDecision(float ThresholdDb, bool Accepted);

internal readonly record struct LoRaSearchPeakObservation(
    float PeakToAverageDb,
    float ThresholdDb,
    bool Accepted,
    bool NoiseEstimatorUpdated);

/// <summary>
/// Search-only temporal threshold estimator. Peak statistics are quantized into
/// a bounded histogram so the update path remains allocation-free. Once a
/// preamble candidate is active, callers freeze the estimator until the
/// candidate is accepted or rejected.
/// </summary>
internal sealed class LoRaSearchThresholdPolicy
{
    private const float HistogramMinimumDb = -10;
    private const float HistogramStepDb = 0.25f;
    private const int HistogramBinCount = 161;

    private readonly LoRaSearchThresholdOptions options;
    private readonly byte[] ring;
    private readonly int[] histogram = new int[HistogramBinCount];
    private int ringIndex;
    private int count;

    public LoRaSearchThresholdPolicy(LoRaSearchThresholdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != LoRaSearchThresholdMode.AdaptiveTemporalQuantile)
            throw new ArgumentException("The adaptive policy requires an adaptive threshold mode.", nameof(options));
        if (!(options.MinimumThresholdDb <= options.FixedThresholdDb &&
              options.FixedThresholdDb <= options.MaximumThresholdDb))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.TargetWindowFalseAlarmProbability is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.WindowLength is < 16 or > 4096 ||
            options.MinimumSamples is < 8 || options.MinimumSamples > options.WindowLength)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        ring = new byte[options.WindowLength];
        CurrentThresholdDb = options.FixedThresholdDb;
    }

    public float CurrentThresholdDb { get; private set; }

    public LoRaSearchThresholdDecision Evaluate(float peakToAverageDb, bool candidateActive)
    {
        float threshold = CurrentThresholdDb;
        bool accepted = peakToAverageDb >= threshold;
        if (!candidateActive && float.IsFinite(peakToAverageDb))
        {
            Add(peakToAverageDb);
            CurrentThresholdDb = CalculateThreshold();
        }
        return new LoRaSearchThresholdDecision(threshold, accepted);
    }

    public void Reset()
    {
        Array.Clear(ring);
        Array.Clear(histogram);
        ringIndex = 0;
        count = 0;
        CurrentThresholdDb = options.FixedThresholdDb;
    }

    private void Add(float value)
    {
        int bin = Math.Clamp(
            (int)MathF.Round((value - HistogramMinimumDb) / HistogramStepDb),
            0,
            HistogramBinCount - 1);
        if (count == ring.Length)
            histogram[ring[ringIndex]]--;
        else
            count++;
        ring[ringIndex] = (byte)bin;
        histogram[bin]++;
        if (++ringIndex == ring.Length) ringIndex = 0;
    }

    private float CalculateThreshold()
    {
        if (count < options.MinimumSamples) return options.FixedThresholdDb;
        int target = Math.Clamp(
            (int)Math.Ceiling(count * (1.0 - options.TargetWindowFalseAlarmProbability)),
            1,
            count);
        int cumulative = 0;
        int quantileBin = 0;
        for (; quantileBin < histogram.Length; quantileBin++)
        {
            cumulative += histogram[quantileBin];
            if (cumulative >= target) break;
        }
        float quantile = HistogramMinimumDb + quantileBin * HistogramStepDb;
        return Math.Clamp(
            quantile + options.MarginDb,
            options.MinimumThresholdDb,
            options.MaximumThresholdDb);
    }
}
