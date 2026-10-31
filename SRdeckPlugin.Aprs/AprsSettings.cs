namespace SRdeckPlugin.Aprs;

public sealed record AprsSettings(long FrequencyHz = 144_660_000, int MaximumHistory = 2000)
{
    public AprsSettings Normalize() => this with
    {
        FrequencyHz = FrequencyHz is >= 1_000_000 and <= 2_147_483_647 ? FrequencyHz : 144_660_000,
        MaximumHistory = Math.Clamp(MaximumHistory, 100, 10_000)
    };
}
