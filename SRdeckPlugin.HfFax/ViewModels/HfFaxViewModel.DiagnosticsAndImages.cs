using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Dsp;
using SRdeckPlugin.HfFax.Models;

namespace SRdeckPlugin.HfFax.ViewModels;

public sealed partial class HfFaxViewModel
{
    internal void UpdateDiagnostics(HfFaxReceiver.DiagnosticsSnapshot value, float? signalLevelDbm)
    {
        ToneFrequencyText = $"{value.LastToneHz:N0} Hz";
        SignalLevelText = signalLevelDbm is float level && float.IsFinite(level)
            ? $"{level:F1} dBm"
            : value.ChannelRms > 0 ? $"{20 * Math.Log10(value.ChannelRms):F1} dBFS" : "—";
        SignalQualityText = value.ProcessedSamples > 0 ? value.InRangeRatio.ToString("P0") : "—";
        AptDetectorText = $"{ReceiverStateText} / 開始 {value.StartDetectorPower:F3} / 停止 {value.StopDetectorPower:F3}";
        LineSynchronizationText = !value.PerLineSynchronizationEnabled
            ? value.LineSyncLocked ? "フェージング同期済み / ライン毎補正 OFF" : "ライン毎補正 OFF"
            : value.LineSyncLocked
                ? $"ロック / 検出 {value.LineSyncPulseCount:N0} / 欠落補間 {value.RecoveredLineSyncPeriods:N0} lines"
                : "同期パルス待機";
        if (value.AutomaticHorizontalAlignmentEnabled)
            LineSynchronizationText += value.AutomaticHorizontalAlignmentApplied
                ? $" / 横原点 {value.AutomaticHorizontalShiftPixels:+#;-#;0} px"
                : " / 横原点解析中";
        EstimatedSlantText = value.LineSyncLocked && double.IsFinite(value.EstimatedLinePeriodPpm)
            ? $"{value.EstimatedLinePeriodPpm:+0;-0;0} ppm"
            : "—";
        LastDiagnosticsUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    public void ResetProcessingDiagnostics()
    {
        ToneFrequencyText = "—";
        SignalLevelText = "—";
        SignalQualityText = "—";
        AptDetectorText = "待機";
        LineSynchronizationText = "待機";
        EstimatedSlantText = "—";
        LastDiagnosticsUpdated = "未更新";
        SetReceiverState(HfFaxReceiverState.Idle);
    }

    public void ResetCurrentImage()
    {
        displayPixels = [];
        displayWidth = 0;
        displayMaximumLines = 0;
        bitmapCapacityLines = 0;
        activeBitmap = null;
        CurrentImage = null;
        CurrentLines = 0;
        CurrentImageDimensions = "—";
        SaveStatus = "画像保存: 待機";
        SetReceiverState(HfFaxReceiverState.Idle);
    }

    public void ClearImages()
    {
        ResetCurrentImage();
        History.Clear();
        SelectedHistoryItem = null;
    }

    public BitmapSource? CreateCurrentImageForPopup()
    {
        if (displayWidth > 0 && CurrentLines > 0 &&
            displayPixels.Length >= checked(displayWidth * CurrentLines))
        {
            BitmapSource source = BitmapSource.Create(displayWidth, CurrentLines, 96, 96,
                PixelFormats.Gray8, null, displayPixels, displayWidth);
            source.Freeze();
            return source;
        }

        return CurrentImage as BitmapSource;
    }

    public static BitmapSource CreateImageSource(HfFaxImageSnapshot image)
    {
        BitmapSource source = BitmapSource.Create(image.Width, image.Height, 96, 96,
            PixelFormats.Gray8, null, image.Pixels, image.Width);
        source.Freeze();
        return source;
    }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "HF-FAX",
            async () =>
            {
                if (ResetSettingsRequested is not null) await ResetSettingsRequested();
            },
            () => { });
    }

    [RelayCommand]
    private void ResetPluginData() =>
        SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndClearData("HF-FAX",
            () => ClearRequested?.Invoke());

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetAllAsync(
            "HF-FAX",
            async () =>
            {
                if (ResetSettingsRequested is not null) await ResetSettingsRequested();
            },
            () => { },
            () => ClearRequested?.Invoke());
    }

    private void EnsureBitmapCapacity(int requiredLines)
    {
        if (activeBitmap is not null && requiredLines <= bitmapCapacityLines) return;
        int capacity = Math.Min(displayMaximumLines,
            Math.Max(requiredLines, Math.Max(128, bitmapCapacityLines * 2)));
        int previousLength = displayPixels.Length;
        Array.Resize(ref displayPixels, checked(displayWidth * capacity));
        if (displayPixels.Length > previousLength)
            Array.Fill(displayPixels, byte.MaxValue, previousLength,
                displayPixels.Length - previousLength);
        bitmapCapacityLines = capacity;
        activeBitmap = CreateWritableBitmap(capacity);
        CurrentImage = activeBitmap;
    }

    private WriteableBitmap CreateWritableBitmap(int height)
    {
        var bitmap = new WriteableBitmap(displayWidth, height, 96, 96, PixelFormats.Gray8, null);
        bitmap.WritePixels(new Int32Rect(0, 0, displayWidth, height), displayPixels,
            displayWidth, 0);
        return bitmap;
    }

    private void ShowCompletedImage(HfFaxImageSnapshot image)
    {
        // Keep the IOC-derived source bitmap at full resolution. WPF scales
        // only its presentation in the viewport.
        displayWidth = image.Width;
        displayMaximumLines = image.Height;
        displayPixels = image.Pixels;
        bitmapCapacityLines = 0;
        activeBitmap = null;
        BitmapSource source = CreateImageSource(image);
        CurrentImage = source;
        CurrentLines = image.Height;
        CurrentImageDimensions = image.SizeDisplay;
        SignalQualityText = image.QualityDisplay;
    }

    private static BitmapSource CreateHistoryPreview(HfFaxImageSnapshot image)
    {
        double scale = Math.Min(1, Math.Min(640.0 / image.Width, 360.0 / image.Height));
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        byte[] pixels = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(image.Height - 1, y * image.Height / height);
            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min(image.Width - 1, x * image.Width / width);
                pixels[y * width + x] = image.Pixels[sourceY * image.Width + sourceX];
            }
        }
        BitmapSource preview = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Gray8, null, pixels, width);
        preview.Freeze();
        return preview;
    }

    private void TrimHistory()
    {
        while (History.Count > MaximumHistory)
            History.RemoveAt(History.Count - 1);
        if (SelectedHistoryItem is not null && !History.Contains(SelectedHistoryItem))
            SelectedHistoryItem = History.FirstOrDefault();
    }

    private void NotifySettingsChanged()
    {
        if (configuring) return;
        SettingsChanged?.Invoke(new HfFaxSettings
        {
            SelectedStationId = selectedStationId,
            CustomFrequencyHz = centerFrequencyHz,
            Ioc = ioc,
            LinesPerMinute = linesPerMinute,
            MaximumLines = maximumLines,
            MaximumHistory = maximumHistory,
            HorizontalShiftPixels = horizontalShiftPixels,
            SlantCorrectionPpm = slantCorrectionPpm,
            PerLineSynchronizationEnabled = perLineSynchronizationEnabled,
            AutomaticHorizontalAlignmentEnabled = automaticHorizontalAlignmentEnabled,
            VideoBandwidthHz = videoBandwidthHz,
            AutomaticStartStop = automaticStartStop,
            InvertImage = invertImage,
            MonitorAudioEnabled = monitorAudioEnabled,
            MonitorAudioVolume = monitorAudioVolume
        }.Normalize());
    }
}
