namespace SRdeckPlugin.HfFax;

public sealed record HfFaxSettings
{
    public string SelectedStationId { get; init; } = HfFaxPluginModule.DefaultStationId;
    public long CustomFrequencyHz { get; init; } = 7_795_000;
    public int Ioc { get; init; }
    public int LinesPerMinute { get; init; }
    public int MaximumLines { get; init; } = 2_400;
    public int MaximumHistory { get; init; } = 8;
    public int HorizontalShiftPixels { get; init; }
    public int SlantCorrectionPpm { get; init; }
    public bool PerLineSynchronizationEnabled { get; init; } = true;
    public bool AutomaticHorizontalAlignmentEnabled { get; init; } = true;
    public int VideoBandwidthHz { get; init; } = Dsp.HfFaxReceiver.DefaultVideoBandwidthHz;
    public bool AutomaticStartStop { get; init; } = true;
    public bool InvertImage { get; init; }
    public bool MonitorAudioEnabled { get; init; } = true;
    public int MonitorAudioVolume { get; init; } = 40;

    public HfFaxSettings Normalize() => this with
    {
        SelectedStationId = HfFaxPluginModule.NormalizeStationId(SelectedStationId),
        CustomFrequencyHz = Math.Clamp(CustomFrequencyHz, 100_000, 2_000_000_000),
        Ioc = Ioc is 0 or 288 or 576 ? Ioc : 0,
        LinesPerMinute = LinesPerMinute is 0 or 60 or 90 or 120 or 240 ? LinesPerMinute : 0,
        MaximumLines = Math.Clamp(MaximumLines, 200, 4_000),
        MaximumHistory = Math.Clamp(MaximumHistory, 1, 50),
        HorizontalShiftPixels = Math.Clamp(HorizontalShiftPixels, -2_000, 2_000),
        SlantCorrectionPpm = Math.Clamp(SlantCorrectionPpm, -2_000, 2_000),
        VideoBandwidthHz = VideoBandwidthHz is 0 or 600 or 1_200 or 1_800
            ? VideoBandwidthHz
            : Dsp.HfFaxReceiver.DefaultVideoBandwidthHz,
        MonitorAudioVolume = Math.Clamp(MonitorAudioVolume, 0, 100)
    };
}
