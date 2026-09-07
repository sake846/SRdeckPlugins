using System.Windows.Media;

namespace SRdeckPlugin.HfFax.Models;

public enum HfFaxReceiverState
{
    Idle,
    StartDetected,
    Phasing,
    Receiving
}

public sealed record HfFaxImageStarted(
    DateTimeOffset StartedAt,
    Guid StreamId,
    long SamplePosition,
    long FrequencyHz,
    int Width,
    int MaximumLines,
    int Ioc,
    int LinesPerMinute,
    bool WasManuallyStarted,
    bool WasAutomaticallyAligned = false,
    double MeasuredLinePeriodSamples = 0,
    int PhasingPulseCount = 0);

public sealed record HfFaxLine(int Index, byte[] Pixels, double SignalQuality);

public sealed record HfFaxImageSnapshot(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    Guid StreamId,
    long SamplePosition,
    long FrequencyHz,
    int Width,
    int Height,
    int Ioc,
    int LinesPerMinute,
    byte[] Pixels,
    double SignalQuality,
    string CompletionReason,
    bool? WasPhasingAligned = null,
    double MeasuredLinePeriodSamples = 0,
    int PhasingPulseCount = 0,
    int AutomaticHorizontalShiftPixels = 0)
{
    public string FrequencyDisplay => $"{FrequencyHz / 1_000.0:N1} kHz";
    public string FormatDisplay => $"IOC {Ioc} / {LinesPerMinute} LPM";
    public string SizeDisplay => $"{Width:N0} × {Height:N0}";
    public string QualityDisplay => $"{SignalQuality:P0}";
    public DateTime LocalStartedAt => StartedAt.ToLocalTime().DateTime;
}

public sealed record HfFaxHistoryRow(HfFaxImageSnapshot Snapshot, ImageSource Preview)
{
    public string Id => Snapshot.Id;
    public DateTime LocalStartedAt => Snapshot.LocalStartedAt;
    public string FrequencyDisplay => Snapshot.FrequencyDisplay;
    public string FormatDisplay => Snapshot.FormatDisplay;
    public string SizeDisplay => Snapshot.SizeDisplay;
    public string QualityDisplay => Snapshot.QualityDisplay;
    public string CompletionReason => Snapshot.CompletionReason;
    public string AlignmentDisplay => Snapshot.WasPhasingAligned switch
    {
        true => $"フェージング適用 / 周期 {Snapshot.MeasuredLinePeriodSamples:F1} samples / " +
                $"検出 {Snapshot.PhasingPulseCount:N0} / 境界補正 {Snapshot.AutomaticHorizontalShiftPixels:+#;-#;0} px",
        false => $"フェージング未適用 / 境界補正 {Snapshot.AutomaticHorizontalShiftPixels:+#;-#;0} px",
        null => "フェージング記録なし（旧履歴）"
    };
}
