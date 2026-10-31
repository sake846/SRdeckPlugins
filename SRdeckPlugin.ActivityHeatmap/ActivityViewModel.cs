using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.ActivityHeatmap;

public sealed partial class ActivityViewModel(Action<int> changeThreshold, Action<int> changeHistoryHours,
    Action<PluginSpectrumRegion> changeFrequencyRegion, Action clearHistory) : ObservableObject
{
    public static IReadOnlyList<int> Thresholds { get; } = Enumerable.Range(2, 13).Select(n => -n * 10).Reverse().ToArray();
    public static IReadOnlyList<int> HistoryHoursOptions { get; } = [1, 6, 24];
    public static IReadOnlyList<FrequencyRegionChoice> FrequencyRegions { get; } =
        [new(PluginSpectrumRegion.DisplayBand, "スペクトラムに追従"), new(PluginSpectrumRegion.InputBand, "受信帯域全体")];
    [ObservableProperty] private int thresholdDbm = -100;
    [ObservableProperty] private int historyHours = 6;
    [ObservableProperty] private PluginSpectrumRegion frequencyRegion = PluginSpectrumRegion.DisplayBand;
    [ObservableProperty] private string status = "開始待ち";
    [ObservableProperty] private ActivitySnapshot? snapshot;
    partial void OnThresholdDbmChanged(int value) => changeThreshold(value);
    partial void OnFrequencyRegionChanged(PluginSpectrumRegion value) => changeFrequencyRegion(value);
    partial void OnHistoryHoursChanged(int value)
    {
        changeHistoryHours(value);
        OnPropertyChanged(nameof(HistoryDescription));
    }
    public string HistoryDescription => $"周波数ごとの活動率 / 直近{HistoryHours}時間・1行{(HistoryHours == 24 ? 4 : 1)}分・最新は上";
    [RelayCommand] private void Clear() => clearHistory();
}

public sealed record FrequencyRegionChoice(PluginSpectrumRegion Region, string DisplayName);
