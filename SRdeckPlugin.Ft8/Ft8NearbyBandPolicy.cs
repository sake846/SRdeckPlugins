using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Dsp;
using SRdeckPlugin.Ft8.Models;

namespace SRdeckPlugin.Ft8;

internal static class Ft8NearbyBandPolicy
{
    // Candidate availability follows the SDR input bandwidth. The main spectrum
    // span may be zoomed independently and must not limit simultaneous reception.
    private const double UsableNyquistRatio = 0.475;

    public static IReadOnlyList<Ft8Band> FindCandidates(
        Ft8Band selectedBand,
        PluginTuningResult tuning,
        IReadOnlyList<Ft8Band> catalog)
    {
        if (tuning.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred ||
            tuning.SampleRateHz <= 0 ||
            tuning.CenterFrequencyHz <= 0 ||
            !IsInsideReceiveBandwidth(selectedBand, tuning))
            return [];

        return catalog
            .Where(item => item.Mode == selectedBand.Mode &&
                           !string.Equals(item.Id, selectedBand.Id, StringComparison.Ordinal) &&
                           IsInsideReceiveBandwidth(item, tuning))
            .OrderBy(item => Math.Abs(item.ChannelCenterFrequencyHz - selectedBand.ChannelCenterFrequencyHz))
            .ThenBy(item => item.ChannelCenterFrequencyHz)
            .ToArray();
    }

    public static bool IsInsideReceiveBandwidth(Ft8Band band, PluginTuningResult tuning)
    {
        long halfBandwidth = Ft8Receiver.OccupiedPassbandHz / 2;
        long receiveHalfWidth = (long)(tuning.SampleRateHz * UsableNyquistRatio);
        long receiveLowerFrequency = tuning.CenterFrequencyHz - receiveHalfWidth;
        long receiveUpperFrequency = tuning.CenterFrequencyHz + receiveHalfWidth;
        return band.ChannelCenterFrequencyHz - halfBandwidth >= receiveLowerFrequency &&
               band.ChannelCenterFrequencyHz + halfBandwidth <= receiveUpperFrequency;
    }
}
