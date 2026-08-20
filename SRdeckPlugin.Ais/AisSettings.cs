using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais;

public sealed record AisSettings(
    int MaximumTargets = 500,
    int RetentionMinutes = 30,
    bool MonitorAudioEnabled = true,
    int MonitorAudioVolume = 100,
    bool SquelchEnabled = true,
    float SquelchThresholdDbm = -125f,
    GeoMapState? MapState = null,
    int MaximumTrailPoints = 100,
    int MaximumHistory = 10_000,
    bool SaveRawFrames = true,
    string ChannelFilter = "both")
{
    public float SquelchThresholdDbfs
    {
        get => SquelchThresholdDbm;
        init => SquelchThresholdDbm = value <= -50f ? value : (value - 80f);
    }
    public AisSettings Normalize() => new(
        Math.Clamp(MaximumTargets, 50, 10_000),
        Math.Clamp(RetentionMinutes, 1, 1440),
        MonitorAudioEnabled,
        Math.Clamp(MonitorAudioVolume, 0, 100),
        SquelchEnabled,
        Math.Clamp(SquelchThresholdDbm, -160f, 0f),
        MapState,
        Math.Clamp(MaximumTrailPoints, 10, 1000),
        Math.Clamp(MaximumHistory, 100, 1_000_000),
        true,
        "both");
}
