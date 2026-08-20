using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Hfdl;

public sealed record HfdlSettings
{
    public string SelectedChannelId { get; init; } = HfdlPluginModule.DefaultChannelId;
    public int MaximumHistory { get; init; } = 10_000;
    public int MaximumAircraft { get; init; } = 500;
    public int RetentionMinutes { get; init; } = 30;
    public int MaximumTrailPoints { get; init; } = 100;
    public bool SaveRawFrames { get; init; } = true;
    public bool SplitHistoryByChannel { get; init; }
    public bool MonitorAudioEnabled { get; init; } = true;
    public int MonitorAudioVolume { get; init; } = 50;
    public GeoMapState? MapState { get; init; }

    public HfdlSettings Normalize() => this with
    {
        SelectedChannelId = HfdlPluginModule.NormalizeChannelId(SelectedChannelId),
        MaximumHistory = Math.Clamp(MaximumHistory, 100, 100_000),
        SaveRawFrames = true,
        SplitHistoryByChannel = false,
        MaximumAircraft = Math.Clamp(MaximumAircraft, 50, 5000),
        RetentionMinutes = Math.Clamp(RetentionMinutes, 1, 1440),
        MaximumTrailPoints = Math.Clamp(MaximumTrailPoints, 10, 1000),
        MonitorAudioVolume = Math.Clamp(MonitorAudioVolume, 0, 100)
    };
}
