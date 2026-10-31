namespace SRdeckPlugin.SignalWorkbench;

public sealed record WorkbenchSettings(long CenterFrequencyHz = 100_000_000, int BandwidthHz = 25_000)
{
    public static IReadOnlyList<int> Bandwidths { get; } = [6_000, 12_500, 25_000, 50_000, 100_000, 200_000];
    public int OutputSampleRateHz => BandwidthHz * 4;

    public WorkbenchSettings Normalize() => this with
    {
        CenterFrequencyHz = Math.Clamp(CenterFrequencyHz, 1, 10_000_000_000),
        BandwidthHz = Bandwidths.Contains(BandwidthHz) ? BandwidthHz : 25_000
    };
}
