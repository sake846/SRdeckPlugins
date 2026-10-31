using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SRdeckPlugin.AribT98.ViewModels;

public sealed partial class AribT98ViewModel
{
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveRecentIqCommand))]
    private double iqBufferedSeconds;
    [ObservableProperty] private string iqCaptureStatus = "直前20秒の選局済みIQを保持します（48 kHz）";
    [ObservableProperty] private string lastIqCapturePath = "";
    public Func<Task<AribT98IqCaptureResult>>? SaveRecentIqRequested { get; set; }
    private bool CanSaveRecentIq() => IqBufferedSeconds > 0 && SaveRecentIqRequested is not null;

    [RelayCommand(CanExecute = nameof(CanSaveRecentIq))]
    private async Task SaveRecentIqAsync()
    {
        if (!CanSaveRecentIq()) return;
        IqCaptureStatus = "直前のIQを保存中…";
        try
        {
            var result = await SaveRecentIqRequested!();
            LastIqCapturePath = result.WavPath;
            IqCaptureStatus = $"保存済み：{result.DurationSeconds:F2}秒（WAV + 受信情報JSON{(result.GainPath is null ? "" : " + 周波数.gain") }）";
        }
        catch (Exception e) { IqCaptureStatus = $"IQ保存失敗：{e.Message}"; }
    }
}
