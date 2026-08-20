using SRdeckPlugin.Wpf;
using SRdeckPlugin.Ft8.Models;

namespace SRdeckPlugin.Ft8;

public sealed record Ft8Settings(
    string SelectedBandId = Ft8PluginModule.DefaultBandId,
    int MaximumHistory = 10_000,
    int MinimumSyncScore = 4,
    int MaximumCandidates = 240,
    int LdpcIterations = 35,
    bool MonitorAudioEnabled = true,
    int MonitorAudioVolume = 100,
    GeoMapState? MapState = null,
    int MaximumStations = 500,
    int RetentionMinutes = 30,
    bool SavePayload = true,
    bool SplitHistoryByBand = false,
    int MapMarkerLimit = 100,
    WeakSignalMode Mode = WeakSignalMode.FT8,
    IReadOnlyList<string>? AdditionalBandIds = null)
{
    public Ft8Settings Normalize()
    {
        string normalizedBandId = SelectedBandId.StartsWith("band-", StringComparison.Ordinal)
            ? $"ft8-{SelectedBandId}"
            : SelectedBandId;
        Ft8Band selectedBand = Ft8PluginModule.Bands.FirstOrDefault(item => item.Id == normalizedBandId)
            ?? Ft8PluginModule.Bands.First(item => item.Id == Ft8PluginModule.DefaultBandId);
        string[] additionalBandIds = (AdditionalBandIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.StartsWith("band-", StringComparison.Ordinal) ? $"ft8-{id}" : id)
            .Where(id => !string.Equals(id, selectedBand.Id, StringComparison.Ordinal))
            .Where(id => Ft8PluginModule.Bands.Any(item =>
                item.Id == id && item.Mode == selectedBand.Mode))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new(
        selectedBand.Id,
        Math.Clamp(MaximumHistory, 100, 20_000),
        Math.Clamp(MinimumSyncScore, 0, 40),
        Math.Clamp(MaximumCandidates, 50, 1000),
        Math.Clamp(LdpcIterations, 10, 100),
        MonitorAudioEnabled,
        Math.Clamp(MonitorAudioVolume, 0, 100),
        MapState,
        Math.Clamp(MaximumStations, 50, 5000),
        Math.Clamp(RetentionMinutes, 1, 240),
        true,
        false,
        Math.Clamp(MapMarkerLimit, 50, 10_000),
        selectedBand.Mode,
        additionalBandIds);
    }
}
