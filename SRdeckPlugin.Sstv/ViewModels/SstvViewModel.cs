using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Sstv.Models;

namespace SRdeckPlugin.Sstv.ViewModels;

public sealed partial class SstvViewModel : ObservableObject
{
    [ObservableProperty] private long frequencyHz = 14_230_000;
    [ObservableProperty] private bool fm;
    [ObservableProperty] private string status = "未初期化";
    [ObservableProperty] private string imageDescription = "VIS信号を待機しています";
    [ObservableProperty] private string toneText = "—";
    [ObservableProperty] private ImageSource? image;
    [ObservableProperty] private string saveStatus = string.Empty;

    public Func<SstvSettings, Task>? ApplySettingsRequested { get; set; }
    public Func<Task>? SaveRequested { get; set; }
    public Action? ResetRequested { get; set; }

    [RelayCommand]
    private Task ApplySettingsAsync() => ApplySettingsRequested?.Invoke(new(FrequencyHz, Fm)) ?? Task.CompletedTask;

    [RelayCommand]
    private Task SaveAsync() => SaveRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private void Reset() => ResetRequested?.Invoke();

    public void Configure(SstvSettings settings)
    {
        FrequencyHz = settings.FrequencyHz;
        Fm = settings.Fm;
    }

    public void ShowImage(SstvImage value)
    {
        BitmapSource bitmap = BitmapSource.Create(value.Width, value.Height, 96, 96,
            PixelFormats.Rgb24, null, value.Pixels, value.Width * 3);
        bitmap.Freeze();
        Image = bitmap;
        ImageDescription = $"{value.Mode} / {value.Width} × {value.Height} / " +
            $"{value.ReceivedLines}ライン{(value.Complete ? " / 完了" : string.Empty)}";
    }
}
