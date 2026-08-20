using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AdsB;

public sealed record AdsBSettings(int MaximumAircraft = 500, int RetentionMinutes = 30,
    double? ReceiverLatitude = null, double? ReceiverLongitude = null,
    GeoMapState? MapState = null, int MaximumTrailPoints = 300,
    int MaximumHistory = 10_000,
    bool SaveRawModeS = true,
    string HistoryRecordMode = "both")
{
    public AdsBSettings Normalize() => new(
        Math.Clamp(MaximumAircraft, 50, 5000),
        Math.Clamp(RetentionMinutes, 1, 240),
        ReceiverLatitude is >= -90 and <= 90 ? ReceiverLatitude : null,
        ReceiverLongitude is >= -180 and <= 180 ? ReceiverLongitude : null,
        MapState,
        Math.Clamp(MaximumTrailPoints, 10, 1000),
        Math.Clamp(MaximumHistory, 100, 1_000_000),
        true,
        "both");
}
