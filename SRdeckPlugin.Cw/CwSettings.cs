using SRdeckPlugin.Cw.Models;

namespace SRdeckPlugin.Cw;

public sealed record CwSettings(
    long CenterFrequencyHz = 0,
    int ReceiveBandwidthHz = CwBandwidth.DefaultHz,
    double MinimumSnrDb = 8,
    int MaximumSignals = 24,
    int MaximumRows = 200,
    bool MonitorAudioEnabled = true,
    int MonitorAudioVolume = 50,
    CwLanguage Language = CwLanguage.Western)
{
    public CwSettings Normalize() => this with
    {
        CenterFrequencyHz = Math.Max(0, CenterFrequencyHz),
        ReceiveBandwidthHz = CwBandwidth.Normalize(ReceiveBandwidthHz),
        MinimumSnrDb = Math.Clamp(MinimumSnrDb, 3, 30),
        MaximumSignals = Math.Clamp(MaximumSignals, 1, 32),
        MaximumRows = Math.Clamp(MaximumRows, 25, 2_000),
        MonitorAudioVolume = Math.Clamp(MonitorAudioVolume, 0, 100)
    };
}
