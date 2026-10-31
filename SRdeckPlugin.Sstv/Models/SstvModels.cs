namespace SRdeckPlugin.Sstv.Models;

public sealed record SstvSettings(long FrequencyHz = 14_230_000, bool Fm = false)
{
    public SstvSettings Normalize() => this with
    {
        FrequencyHz = Math.Clamp(FrequencyHz, 100_000, 2_000_000_000)
    };
}

internal enum SstvFamily { Martin, Scottie, Robot, Pd }

internal sealed record SstvMode(
    string Name, int Vis, int Width, int Height, SstvFamily Family, double ScanMs)
{
    public static IReadOnlyList<SstvMode> All { get; } =
    [
        new("Martin M1", 44, 320, 256, SstvFamily.Martin, 146.432),
        new("Martin M2", 40, 160, 256, SstvFamily.Martin, 73.216),
        new("Scottie S1", 60, 320, 256, SstvFamily.Scottie, 138.240),
        new("Scottie S2", 56, 320, 256, SstvFamily.Scottie, 88.064),
        new("Robot 36", 8, 320, 240, SstvFamily.Robot, 88),
        new("PD120", 95, 640, 496, SstvFamily.Pd, 121.600)
    ];

    public double SyncMs => Family switch
    {
        SstvFamily.Martin => 4.862,
        SstvFamily.Pd => 20,
        _ => 9
    };
    public double SyncEndMs => Family == SstvFamily.Scottie ? 3 + 2 * ScanMs + SyncMs : SyncMs;
    public double LineMs => Family switch
    {
        SstvFamily.Martin => SyncMs + 4 * 0.572 + 3 * ScanMs,
        SstvFamily.Scottie => 3 * (ScanMs + 1.5) + SyncMs,
        SstvFamily.Robot => 150,
        _ => 22.080 + 4 * ScanMs
    };
}

public sealed record SstvImage(
    string Id, string Mode, int Width, int Height, int ReceivedLines,
    byte[] Pixels, DateTimeOffset StartedAt, bool Complete);
