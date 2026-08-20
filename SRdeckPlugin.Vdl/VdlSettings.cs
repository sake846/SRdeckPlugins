using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Vdl;

public sealed record VdlSettings
{
    public string SelectedChannelId { get; init; } = "136975";
    public int MaximumHistory { get; init; } = 10_000;
    public int MaximumAircraft { get; init; } = 500;
    public int RetentionMinutes { get; init; } = 30;
    public int MaximumTrailPoints { get; init; } = 100;
    public bool SaveDecodedFrames { get; init; } = true;
    public bool SaveUnparsedFrames { get; init; } = true;
    public bool SaveRawFrames { get; init; } = true;
    public bool SaveAcarsText { get; init; } = true;
    public string ProtocolFilter { get; init; } = "all";
    public bool MonitorAudioEnabled { get; init; } = true;
    public int MonitorAudioVolume { get; init; } = 50;
    public bool SquelchEnabled { get; init; } = true;
    public int PreambleVerificationSymbols { get; init; } = 16;
    public bool AdaptiveEqualizerEnabled { get; init; } = true;
    public GeoMapState? MapState { get; init; }

    public VdlSettings Normalize() => this with
    {
        SelectedChannelId = VdlPluginModule.Channels.Any(channel => channel.Id == SelectedChannelId)
            ? SelectedChannelId : "136975",
        MaximumHistory = Math.Clamp(MaximumHistory, 100, 100_000),
        MaximumAircraft = Math.Clamp(MaximumAircraft, 50, 5000),
        RetentionMinutes = Math.Clamp(RetentionMinutes, 1, 240),
        MaximumTrailPoints = Math.Clamp(MaximumTrailPoints, 10, 1000),
        SaveDecodedFrames = true,
        SaveUnparsedFrames = true,
        SaveRawFrames = true,
        SaveAcarsText = true,
        ProtocolFilter = "all",
        MonitorAudioVolume = Math.Clamp(MonitorAudioVolume, 0, 100),
        PreambleVerificationSymbols = 16,
        AdaptiveEqualizerEnabled = true
    };
}
