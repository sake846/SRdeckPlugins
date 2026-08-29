using System;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Configures the relative-power gate used before Search-state FFT analysis.
/// </summary>
internal sealed record LoRaSearchEnergyGateOptions(
    bool Enabled = true,
    double RelativePowerThreshold = 1.0,
    int WarmupSymbols = 64,
    double TrackingFactor = 1.0 / 64.0,
    double GainStepRatio = 2.0)
{
    public static LoRaSearchEnergyGateOptions Default { get; } = new();
    public static LoRaSearchEnergyGateOptions Disabled { get; } = new(Enabled: false);
}

/// <summary>
/// Tracks Search-state noise power without learning from active acquisition candidates.
/// </summary>
internal sealed class LoRaSearchEnergyGate
{
    private readonly LoRaSearchEnergyGateOptions options;
    private double noisePower;
    private int noiseSamples;

    public LoRaSearchEnergyGate(LoRaSearchEnergyGateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RelativePowerThreshold <= 0 || !double.IsFinite(options.RelativePowerThreshold))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.WarmupSymbols < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.TrackingFactor is <= 0 or > 1 || !double.IsFinite(options.TrackingFactor))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.GainStepRatio <= 1 || !double.IsFinite(options.GainStepRatio))
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
    }

    public bool Enabled => options.Enabled;

    public void Reset()
    {
        noisePower = 0;
        noiseSamples = 0;
    }

    public bool ShouldSkip(double averagePower, bool candidateActive)
    {
        if (!options.Enabled || candidateActive || noiseSamples < options.WarmupSymbols)
            return false;

        double relativePower = averagePower / Math.Max(double.Epsilon, noisePower);
        if (relativePower < 1.0 / options.GainStepRatio || relativePower > options.GainStepRatio)
        {
            // A large step is more likely to be an SDR gain/AGC discontinuity than
            // a marginal packet. Analyze it and require a fresh noise warmup.
            noisePower = averagePower;
            noiseSamples = 1;
            return false;
        }
        return relativePower < options.RelativePowerThreshold;
    }

    public void ObserveNoise(double averagePower)
    {
        if (!options.Enabled) return;

        if (noiseSamples < options.WarmupSymbols)
        {
            noiseSamples++;
            noisePower += (averagePower - noisePower) / noiseSamples;
            return;
        }

        noisePower += (averagePower - noisePower) * options.TrackingFactor;
    }
}
