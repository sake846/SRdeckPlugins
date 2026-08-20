using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars;

public sealed record AcarsSettings
{
    public const string DefaultUninterpretedLogFilePath = "acars_uninterpreted_messages.log";

    public string SelectedChannelId { get; init; } = "jp-primary";
    public string[] MonitoredChannelIds { get; init; } = [];
    public int MaximumHistory { get; init; } = 10_000;
    public int MaximumAircraft { get; init; } = 500;
    public int RetentionMinutes { get; init; } = 30;
    public bool SaveRawFrames { get; init; } = true;
    public int MaximumTrailPoints { get; init; } = 100;
    public bool MonitorAudioEnabled { get; init; } = true;
    public bool SquelchEnabled { get; init; } = true;
    public int MonitorAudioVolume { get; init; } = 100;
    public bool SaveUninterpretedMessages { get; init; } = false;
    public string UninterpretedLogFilePath { get; init; } = DefaultUninterpretedLogFilePath;
    public bool BuzzerEnabled { get; init; } = true;
    public GeoMapState? MapState { get; init; }

    public AcarsSettings Normalize()
    {
        AcarsPluginModule.Channel selectedChannel = AcarsPluginModule.Channels
            .FirstOrDefault(item => item.Id == SelectedChannelId) ??
            AcarsPluginModule.Channels.First(item => item.Id == "jp-primary");
        string selected = selectedChannel.Id;
        string[] monitored = (MonitoredChannelIds ?? [])
            .Where(id => AcarsPluginModule.Channels.Any(channel =>
                channel.Id == id && AcarsPluginModule.IsChannelAvailableInRegion(
                    channel, selectedChannel.Region)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (monitored.Length == 0) monitored = [selected];
        if (!monitored.Contains(selected, StringComparer.Ordinal)) monitored = [.. monitored, selected];
        return this with
        {
            SelectedChannelId = selected,
            MonitoredChannelIds = monitored,
            MaximumHistory = Math.Clamp(MaximumHistory, 100, 100_000),
            MaximumAircraft = Math.Clamp(MaximumAircraft, 50, 5000),
            RetentionMinutes = Math.Clamp(RetentionMinutes, 1, 240),
            MaximumTrailPoints = Math.Clamp(MaximumTrailPoints, 10, 1000),
            MonitorAudioVolume = Math.Clamp(MonitorAudioVolume, 0, 100),
            SaveRawFrames = true,
            UninterpretedLogFilePath = DefaultUninterpretedLogFilePath
        };
    }
}
