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

public sealed record HfFaxSettingOption(int Value, string DisplayName);

public sealed partial class HfFaxViewModel : ObservableObject
{
    private bool configuring;
    private string selectedStationId = HfFaxPluginModule.DefaultStationId;
    private long centerFrequencyHz = 7_795_000;
    private int ioc;
    private int linesPerMinute;
    private int maximumLines = 2_400;
    private int maximumHistory = 8;
    private int horizontalShiftPixels;
    private int slantCorrectionPpm;
    private bool perLineSynchronizationEnabled = true;
    private bool automaticHorizontalAlignmentEnabled = true;
    private int videoBandwidthHz = HfFaxReceiver.DefaultVideoBandwidthHz;
    private bool automaticStartStop = true;
    private bool invertImage;
    private bool monitorAudioEnabled = true;
    private int monitorAudioVolume = 40;
    private byte[] displayPixels = [];
    private int displayWidth;
    private int displayMaximumLines;
    private int bitmapCapacityLines;
    private WriteableBitmap? activeBitmap;
    private HfFaxReceiverState receiverState;
    private IPluginRuntimeDiagnostics runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    private OverallStatusKind overallStatusKind = OverallStatusKind.Idle;

    [ObservableProperty] private string status = "未初期化";
    [ObservableProperty] private ImageSource? currentImage;
    [ObservableProperty] private int currentLines;
    [ObservableProperty] private string currentImageDimensions = "—";
    [ObservableProperty] private string toneFrequencyText = "—";
    [ObservableProperty] private string signalLevelText = "—";
    [ObservableProperty] private string signalQualityText = "—";
    [ObservableProperty] private string aptDetectorText = "待機";
    [ObservableProperty] private string lineSynchronizationText = "待機";
    [ObservableProperty] private string estimatedSlantText = "—";
    [ObservableProperty] private string lastDiagnosticsUpdated = "未更新";
    [ObservableProperty] private string saveStatus = "画像保存: 待機";
    [ObservableProperty] private HfFaxHistoryRow? selectedHistoryItem;

    public HfFaxViewModel()
    {
        ManualStartCommand = new RelayCommand(() => ManualStartRequested?.Invoke());
        ManualStopCommand = new RelayCommand(() => ManualStopRequested?.Invoke());
        SaveImageCommand = new RelayCommand(() => SaveImageRequested?.Invoke());
        ClearCommand = new RelayCommand(() => ClearRequested?.Invoke());
    }

    public IReadOnlyList<HfFaxPluginModule.Station> Stations => HfFaxPluginModule.Stations;
    public IReadOnlyList<HfFaxSettingOption> IocOptions { get; } =
        [new(0, "自動"), new(576, "576"), new(288, "288")];
    public IReadOnlyList<HfFaxSettingOption> LinesPerMinuteOptions { get; } =
        [new(0, "自動"), new(120, "120"), new(90, "90"), new(60, "60"), new(240, "240")];
    public IReadOnlyList<HfFaxSettingOption> VideoBandwidthOptions { get; } =
        [new(0, "OFF"), new(600, "600 Hz"), new(1_200, "1,200 Hz"), new(1_800, "1,800 Hz")];
    public ObservableCollection<HfFaxHistoryRow> History { get; } = [];
    public RelayCommand ManualStartCommand { get; }
    public RelayCommand ManualStopCommand { get; }
    public RelayCommand SaveImageCommand { get; }
    public RelayCommand ClearCommand { get; }
    public Action<HfFaxSettings>? SettingsChanged { get; set; }
    internal Action? ManualStartRequested { get; set; }
    internal Action? ManualStopRequested { get; set; }
    internal Action? SaveImageRequested { get; set; }
    internal Action? ClearRequested { get; set; }
    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    public OverallStatusKind OverallStatusKind
    {
        get => overallStatusKind;
        private set => SetProperty(ref overallStatusKind, value);
    }

    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => runtimeDiagnostics;
        set => SetProperty(ref runtimeDiagnostics, value ?? NullPluginRuntimeDiagnostics.Instance);
    }

    public string SettingStatus =>
        $"{CenterFrequencyDisplay} / IOC {IocDisplay} / {LinesPerMinuteDisplay} LPM / " +
        $"APT {(AutomaticStartStop ? "自動" : "手動")} / " +
        $"ライン同期 {(PerLineSynchronizationEnabled ? "自動" : "OFF")} / " +
        $"横原点 {(AutomaticHorizontalAlignmentEnabled ? "自動" : "OFF")} / " +
        $"音声 {(MonitorAudioEnabled ? $"{MonitorAudioVolume}%" : "OFF")} / " +
        $"履歴 {MaximumHistory}件";

    public string CenterFrequencyDisplay => $"{CenterFrequencyHz / 1_000.0:N1} kHz";
    public string IocDisplay => Ioc == 0 ? "自動" : Ioc.ToString();
    public string LinesPerMinuteDisplay => LinesPerMinute == 0 ? "自動" : LinesPerMinute.ToString();
    public string ReceiverStateText => receiverState switch
    {
        HfFaxReceiverState.StartDetected => "APT開始信号を検出",
        HfFaxReceiverState.Phasing => "APT位相信号を受信中",
        HfFaxReceiverState.Receiving => "画像受信中",
        _ => AutomaticStartStop ? "APT開始待機" : "手動開始待機"
    };
    public bool IsCustomStation => SelectedStationId == "custom";
    public bool IsReceivingImage => receiverState == HfFaxReceiverState.Receiving;

    public string SelectedStationId
    {
        get => selectedStationId;
        set
        {
            string normalized = HfFaxPluginModule.NormalizeStationId(value);
            if (!SetProperty(ref selectedStationId, normalized)) return;
            HfFaxPluginModule.Station station = Stations.First(item => item.Id == normalized);
            if (station.FrequencyHz > 0 && centerFrequencyHz != station.FrequencyHz)
            {
                centerFrequencyHz = station.FrequencyHz;
                OnPropertyChanged(nameof(CenterFrequencyHz));
                OnPropertyChanged(nameof(CenterFrequencyDisplay));
            }
            OnPropertyChanged(nameof(IsCustomStation));
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public long CenterFrequencyHz
    {
        get => centerFrequencyHz;
        set
        {
            if (!SetProperty(ref centerFrequencyHz, Math.Clamp(value, 100_000, 2_000_000_000))) return;
            OnPropertyChanged(nameof(CenterFrequencyDisplay));
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int Ioc
    {
        get => ioc;
        set
        {
            if (!SetProperty(ref ioc, value is 0 or 288 or 576 ? value : 0)) return;
            OnPropertyChanged(nameof(IocDisplay));
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int LinesPerMinute
    {
        get => linesPerMinute;
        set
        {
            int normalized = value is 0 or 60 or 90 or 120 or 240 ? value : 0;
            if (!SetProperty(ref linesPerMinute, normalized)) return;
            OnPropertyChanged(nameof(LinesPerMinuteDisplay));
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int MaximumLines
    {
        get => maximumLines;
        set
        {
            if (!SetProperty(ref maximumLines, Math.Clamp(value, 200, 4_000))) return;
            NotifySettingsChanged();
        }
    }

    public int MaximumHistory
    {
        get => maximumHistory;
        set
        {
            if (!SetProperty(ref maximumHistory, Math.Clamp(value, 1, 50))) return;
            TrimHistory();
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int HorizontalShiftPixels
    {
        get => horizontalShiftPixels;
        set
        {
            if (!SetProperty(ref horizontalShiftPixels, Math.Clamp(value, -2_000, 2_000))) return;
            NotifySettingsChanged();
        }
    }

    public int SlantCorrectionPpm
    {
        get => slantCorrectionPpm;
        set
        {
            if (!SetProperty(ref slantCorrectionPpm, Math.Clamp(value, -2_000, 2_000))) return;
            NotifySettingsChanged();
        }
    }

    public bool PerLineSynchronizationEnabled
    {
        get => perLineSynchronizationEnabled;
        set
        {
            if (!SetProperty(ref perLineSynchronizationEnabled, value)) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public bool AutomaticHorizontalAlignmentEnabled
    {
        get => automaticHorizontalAlignmentEnabled;
        set
        {
            if (!SetProperty(ref automaticHorizontalAlignmentEnabled, value)) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int VideoBandwidthHz
    {
        get => videoBandwidthHz;
        set
        {
            int normalized = value is 0 or 600 or 1_200 or 1_800
                ? value
                : HfFaxReceiver.DefaultVideoBandwidthHz;
            if (!SetProperty(ref videoBandwidthHz, normalized)) return;
            NotifySettingsChanged();
        }
    }

    public bool AutomaticStartStop
    {
        get => automaticStartStop;
        set
        {
            if (!SetProperty(ref automaticStartStop, value)) return;
            OnPropertyChanged(nameof(ReceiverStateText));
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public bool InvertImage
    {
        get => invertImage;
        set
        {
            if (!SetProperty(ref invertImage, value)) return;
            NotifySettingsChanged();
        }
    }

    public bool MonitorAudioEnabled
    {
        get => monitorAudioEnabled;
        set
        {
            if (!SetProperty(ref monitorAudioEnabled, value)) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    public int MonitorAudioVolume
    {
        get => monitorAudioVolume;
        set
        {
            if (!SetProperty(ref monitorAudioVolume, Math.Clamp(value, 0, 100))) return;
            OnPropertyChanged(nameof(SettingStatus));
            NotifySettingsChanged();
        }
    }

    partial void OnStatusChanged(string value)
    {
        OverallStatusKind = value.StartsWith("受信中", StringComparison.Ordinal) ||
            receiverState == HfFaxReceiverState.Receiving
            ? OverallStatusKind.Running
            : value.Contains("失敗", StringComparison.Ordinal) ||
              value.Contains("帯域外", StringComparison.Ordinal)
                ? OverallStatusKind.Warning
                : value.StartsWith("設定済み", StringComparison.Ordinal) ||
                  value.StartsWith("待機中", StringComparison.Ordinal)
                    ? OverallStatusKind.Info
                    : OverallStatusKind.Idle;
    }

    public void Configure(HfFaxSettings value, long activeFrequencyHz)
    {
        configuring = true;
        try
        {
            SelectedStationId = value.SelectedStationId;
            CenterFrequencyHz = activeFrequencyHz;
            Ioc = value.Ioc;
            LinesPerMinute = value.LinesPerMinute;
            MaximumLines = value.MaximumLines;
            MaximumHistory = value.MaximumHistory;
            HorizontalShiftPixels = value.HorizontalShiftPixels;
            SlantCorrectionPpm = value.SlantCorrectionPpm;
            PerLineSynchronizationEnabled = value.PerLineSynchronizationEnabled;
            AutomaticHorizontalAlignmentEnabled = value.AutomaticHorizontalAlignmentEnabled;
            VideoBandwidthHz = value.VideoBandwidthHz;
            AutomaticStartStop = value.AutomaticStartStop;
            InvertImage = value.InvertImage;
            MonitorAudioEnabled = value.MonitorAudioEnabled;
            MonitorAudioVolume = value.MonitorAudioVolume;
        }
        finally { configuring = false; }
        OnPropertyChanged(nameof(SettingStatus));
    }

    public void SetCenterFrequency(long value)
    {
        configuring = true;
        try { CenterFrequencyHz = value; }
        finally { configuring = false; }
    }

    public void BeginImage(HfFaxImageStarted image)
    {
        displayWidth = image.Width;
        displayMaximumLines = image.MaximumLines;
        bitmapCapacityLines = Math.Min(128, image.MaximumLines);
        displayPixels = new byte[checked(image.Width * bitmapCapacityLines)];
        Array.Fill(displayPixels, byte.MaxValue);
        activeBitmap = CreateWritableBitmap(bitmapCapacityLines);
        CurrentImage = activeBitmap;
        CurrentLines = 0;
        CurrentImageDimensions = $"{image.Width:N0} × 0";
        SaveStatus = image.WasAutomaticallyAligned
            ? "画像保存: 受信中 / 位相基準で横位置を自動補正"
            : "画像保存: 受信中";
    }

    public void AppendLine(HfFaxLine line)
    {
        if (displayWidth <= 0 || line.Pixels.Length != displayWidth ||
            line.Index < 0 || line.Index >= displayMaximumLines) return;
        EnsureBitmapCapacity(line.Index + 1);
        line.Pixels.CopyTo(displayPixels, line.Index * displayWidth);
        activeBitmap?.WritePixels(new Int32Rect(0, line.Index, displayWidth, 1),
            line.Pixels, displayWidth, 0);
        CurrentLines = line.Index + 1;
        CurrentImageDimensions = $"{displayWidth:N0} × {CurrentLines:N0}";
        SignalQualityText = line.SignalQuality.ToString("P0");
    }

    public void RealignCurrentImage(
        HfFaxImageSnapshot image,
        int horizontalShiftPixels,
        bool automaticallyAligned = false)
    {
        if (activeBitmap is null || displayWidth != image.Width || image.Height <= 0 ||
            image.Height > displayMaximumLines ||
            image.Pixels.Length != checked(image.Width * image.Height)) return;

        EnsureBitmapCapacity(image.Height);
        image.Pixels.CopyTo(displayPixels, 0);
        activeBitmap?.WritePixels(new Int32Rect(0, 0, displayWidth, image.Height),
            image.Pixels, displayWidth, 0);
        CurrentImage = activeBitmap;
        CurrentLines = image.Height;
        CurrentImageDimensions = $"{displayWidth:N0} × {CurrentLines:N0}";
        SaveStatus = automaticallyAligned
            ? $"画像保存: 受信中 / 横原点を自動整列 / 手動 {horizontalShiftPixels:+#;-#;0} px"
            : $"画像保存: 受信中 / 横位置 {horizontalShiftPixels:+#;-#;0} px";
    }

    public void CompleteImage(HfFaxImageSnapshot image)
    {
        ShowCompletedImage(image);
        var row = new HfFaxHistoryRow(image, CreateHistoryPreview(image));
        History.Insert(0, row);
        TrimHistory();
        SelectedHistoryItem = row;
        SaveStatus = $"画像完成 / {image.CompletionReason}";
    }

    public void LoadHistory(IEnumerable<HfFaxImageSnapshot> images)
    {
        History.Clear();
        foreach (HfFaxImageSnapshot image in images.Take(MaximumHistory))
            History.Add(new HfFaxHistoryRow(image, CreateHistoryPreview(image)));
        SelectedHistoryItem = History.FirstOrDefault();
        if (SelectedHistoryItem is { Snapshot: { } latest })
        {
            ShowCompletedImage(latest);
            SaveStatus = "前回の受信画像を復元";
        }
    }

    public void SetReceiverState(HfFaxReceiverState state)
    {
        receiverState = state;
        OnPropertyChanged(nameof(ReceiverStateText));
        OnPropertyChanged(nameof(IsReceivingImage));
        AptDetectorText = ReceiverStateText;
        if (state is HfFaxReceiverState.Phasing or HfFaxReceiverState.Receiving)
            OverallStatusKind = OverallStatusKind.Running;
    }

}
