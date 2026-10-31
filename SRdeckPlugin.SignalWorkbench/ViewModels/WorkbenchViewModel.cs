using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SRdeckPlugin.SignalWorkbench.ViewModels;

public sealed partial class WorkbenchViewModel(Func<int, Task> applyBandwidth) : ObservableObject
{
    [ObservableProperty] private string status = "解析開始待ち";
    [ObservableProperty] private string frequency = "—";
    [ObservableProperty] private int selectedBandwidthHz = 25_000;
    [ObservableProperty] private bool isFrozen;
    [ObservableProperty] private SignalSnapshot? snapshot;
    [ObservableProperty] private string level = "—";
    [ObservableProperty] private string frequencyStatistics = "—";
    [ObservableProperty] private string histogramRange = "—";
    [ObservableProperty] private string window = "—";
    [ObservableProperty] private string candidateStatus = "解析窓の蓄積後に候補を表示します。";
    [ObservableProperty] private string fskSummary = "解析データ待ち";
    [ObservableProperty] private string fskDetails = "—";

    public IReadOnlyList<int> Bandwidths => WorkbenchSettings.Bandwidths;

    public void Configure(WorkbenchSettings settings)
    {
        Frequency = $"{settings.CenterFrequencyHz / 1_000_000.0:F6} MHz";
        SelectedBandwidthHz = settings.BandwidthHz;
        IsFrozen = false;
        Snapshot = null;
        Level = FrequencyStatistics = HistogramRange = Window = "—";
        CandidateStatus = "解析窓の蓄積後に候補を表示します。";
        FskSummary = "解析データ待ち";
        FskDetails = "—";
    }

    public void ApplySnapshot(SignalSnapshot value)
    {
        if (IsFrozen) return;
        Snapshot = value;
        Level = $"{value.LevelDbfs:F1} dBFS";
        FrequencyStatistics = value.FrequencySampleCount == 0 ? "有効な位相差なし" :
            $"平均 {value.MeanFrequencyHz:+0.0;-0.0;0.0} Hz / 5–95%幅 {value.FrequencySpreadHz:N1} Hz";
        HistogramRange = $"横軸 −{value.HistogramHalfRangeHz:N0} ～ +{value.HistogramHalfRangeHz:N0} Hz / {value.FrequencySampleCount:N0} 組";
        Window = $"{value.SampleRateHz:N0} sample/s / {value.DurationSeconds * 1_000:F1} ms / 分解能 {value.ResolutionHz:F1} Hz";
        CandidateStatus = value.Candidates.Length == 0 ? "明瞭な周期ピークなし。帯域・信号強度・解析窓を確認してください。" :
            $"{value.Candidates.Length} 件 / 探索範囲 {value.ResolutionHz * 4:N1} ～ {value.SampleRateHz / 4:N0} sym/s";
        FskSummary = value.Fsk is { } fsk ? $"{fsk.Tones.Length}FSK様のトーン候補" :
            "明瞭な等間隔のトーンを検出できませんでした。";
        FskDetails = value.Fsk is { } measurements
            ? $"中心ずれ {measurements.CenterOffsetHz:+0.0;-0.0;0.0} Hz / 隣接間隔 {measurements.ToneSpacingHz:N1} Hz / 外側偏移 ±{measurements.OuterDeviationHz:N1} Hz"
            : "トーンが重なる信号、短いバースト、弱い信号では検出できない場合があります。";
        Status = "受信中 / 解析更新中";
    }

    [RelayCommand]
    private async Task ApplyBandwidthAsync()
    {
        try { await applyBandwidth(SelectedBandwidthHz); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = $"帯域変更失敗 / {exception.Message}";
        }
    }
}
