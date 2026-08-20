namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Selects the coarse decimation used to bring an input stream into the
/// VDL working-rate window. The receiver keeps the compatibility facade,
/// while input preparation remains an independently testable stage.
/// </summary>
internal static class VdlInputPreparationStage
{
    public static int SelectCoarseDecimationFactor(
        int sampleRateHz,
        int workingSampleRateHz,
        int maximumSampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workingSampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSampleRateHz);

        if (sampleRateHz <= maximumSampleRateHz)
            return 1;

        int bestFactor = 1;
        double bestDistance = double.MaxValue;
        int maximumFactor = Math.Max(1, sampleRateHz / workingSampleRateHz);
        for (int factor = 1; factor <= maximumFactor; factor++)
        {
            double rate = sampleRateHz / (double)factor;
            if (rate < workingSampleRateHz || rate > maximumSampleRateHz)
                continue;

            double distance = Math.Abs(rate - maximumSampleRateHz / 2.0);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestFactor = factor;
        }

        return bestFactor;
    }
}
