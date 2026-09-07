namespace SRdeckPlugin.Cw;

public sealed record CwBandwidthOption(int Value, string Display);

public static class CwBandwidth
{
    public const int DefaultHz = 10_000;

    public static IReadOnlyList<CwBandwidthOption> Options { get; } =
    [
        new(10_000, "10 kHz"),
        new(20_000, "20 kHz"),
        new(50_000, "50 kHz")
    ];

    public static int Normalize(int bandwidthHz)
    {
        for (int index = 0; index < Options.Count; index++)
            if (Options[index].Value == bandwidthHz) return bandwidthHz;
        return DefaultHz;
    }

    public static int GetOutputSampleRateHz(int bandwidthHz) =>
        checked(Normalize(bandwidthHz) * 6 / 5);

    public static int GetMinimumIntermediateSampleRateHz(int bandwidthHz) =>
        checked(GetOutputSampleRateHz(bandwidthHz) * 2);

    public static int GetMaximumIntermediateSampleRateHz(int bandwidthHz) =>
        checked(GetOutputSampleRateHz(bandwidthHz) * 16);

    public static string Format(int bandwidthHz) =>
        Options.FirstOrDefault(option => option.Value == bandwidthHz)?.Display ??
        Options.First(option => option.Value == DefaultHz).Display;
}
