using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Vdl.Dsp;
using SRdeckPlugin.Vdl;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Vdl.ViewModels;

public sealed partial class VdlViewModel : ObservableObject
{

    private readonly ObservableCollection<VdlDecodedFrame> frames = [];
    private readonly ObservableCollection<VdlCategorySummary> callsignGroups = [];
    private readonly ObservableCollection<VdlDecodedFrame> filteredFrames = [];
    private readonly ObservableCollection<VdlCategorySummary> filteredCallsignGroups = [];
    private readonly ObservableCollection<GeoMapMarker> mapMarkers = [];
    private readonly Dictionary<string, (GeoMapMarker Marker, DateTimeOffset LastReceivedAt)> mapMarkersByAircraft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DiagnosticWindowSample> diagnosticWindow = new();
    private DateTimeOffset? lastValidFrameObservedAt;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _overallStatus = "待機中";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private string _overallPhase = "受信処理";
    [ObservableProperty] private string _overallSummary = "IQ入力を待機しています";
    [ObservableProperty] private string _overallRecommendation = "確認: SDRソースの接続と周波数設定を確認してください";
    [ObservableProperty] private string _overallLastUpdated = "未更新";
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    [ObservableProperty] private VdlCategorySummary? _selectedListGroup;
    [ObservableProperty] private VdlDecodedFrame? _selectedTimelineFrame;

    partial void OnSearchTextChanged(string value) => RefreshFilteredHistory();

    public ObservableCollection<VdlDecodedFrame> FilteredFrames => filteredFrames;
    public ObservableCollection<VdlCategorySummary> FilteredCallsignGroups => filteredCallsignGroups;

    private string selectedProfileId = "136975";
    private bool isAudioMonitorEnabled = false;
    private int audioMonitorVolume = 50;
    private bool isSquelchEnabled = true;
    private bool isAdaptiveEqualizerEnabled;
    private int maximumHistory = 10_000;
    private int maximumAircraft = 500;
    private int retentionMinutes = 30;
    private int maximumTrailPoints = 100;
    private bool saveRawFrames = true;
    private int preambleVerificationSymbols = 16;

    [ObservableProperty] private string _captureStatus = "IQ録音: 待機";
    [ObservableProperty] private string _status = "停止中";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalFrameCount))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRate))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRateText))]
    private long _validFrameCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalFrameCount))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRate))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRateText))]
    private long _rejectedFrameCount;

    [ObservableProperty] private long _synchronizationCount;
    [ObservableProperty] private long _frequencyOffsetHz;
    [ObservableProperty] private string _inputRateText = "--";
    [ObservableProperty] private string _channelText = "--";
    [ObservableProperty] private string _channelOffsetText = "--";
    [ObservableProperty] private string _passbandStatusText = "—";
    [ObservableProperty] private string _rateConversionSummaryText = "—";
    [ObservableProperty] private string _rateConversionText = "—";
    [ObservableProperty] private string _intermediateRateText = "—";
    [ObservableProperty] private string _inputLevelText = "--";
    [ObservableProperty] private string _channelLevelText = "--";
    [ObservableProperty] private string _channelPeakText = "--";
    [ObservableProperty] private string _noiseFloorText = "--";
    [ObservableProperty] private string _preambleLevelText = "--";
    [ObservableProperty] private string _synchronizationMetricText = "--";
    [ObservableProperty] private string _detectorText = "--";
    [ObservableProperty] private string _preambleQualityText = "--";
    [ObservableProperty] private string _timingRecoveryText = "--";
    [ObservableProperty] private string _carrierRecoveryText = "--";
    [ObservableProperty] private string _processingText = "--";
    [ObservableProperty] private string _pipelineTimingText = "--";
    [ObservableProperty] private string _pipelineInputText = "--";
    [ObservableProperty] private string _pipelineAudioText = "--";
    [ObservableProperty] private string _headerStatusText = "--";
    [ObservableProperty] private string _fecStatusText = "--";
    [ObservableProperty] private string _rsFecStatusText = "--";
    [ObservableProperty] private string _avlcStatusText = "--";
    [ObservableProperty] private string _rescueStatusText = "--";
    [ObservableProperty] private string _diagnosisText = "--";

    [RelayCommand]
    private void ClearMessages()
    {
        SelectedTimelineFrame = null;
        frames.Clear();
        callsignGroups.Clear();
        mapMarkers.Clear();
        mapMarkersByAircraft.Clear();
        OnPropertyChanged(nameof(IdentifiedCallsignCount));
        OnPropertyChanged(nameof(RecentCallsignGroups));
        RefreshOverview();
        OnPropertyChanged(nameof(LastReceptionText));
        RefreshFilteredHistory();
        ClearRequested?.Invoke();
    }

    [RelayCommand]
    private void StartCapture()
    {
        CaptureRequested?.Invoke();
    }

    public ObservableCollection<VdlDecodedFrame> Frames => frames;
    public IEnumerable<VdlDecodedFrame> RecentFrames => frames.Take(3);
    public IEnumerable<VdlCategorySummary> RecentCallsignGroups => callsignGroups.Take(3);
    public ObservableCollection<GeoMapMarker> MapMarkers => mapMarkers;

    public IReadOnlyList<VdlPluginModule.Channel> Channels => VdlPluginModule.Channels;

    public string SelectedProfileId
    {
        get => selectedProfileId;
        set
        {
            if (selectedProfileId == value) return;
            if (ChannelSelectionRequested?.Invoke(value) == true)
            {
                selectedProfileId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedChannelName));
            }
        }
    }

    public string SelectedChannelName => Channels.FirstOrDefault(c => c.Id == selectedProfileId)?.Name ?? "VDL Mode 2";
    internal Func<string, bool>? ChannelSelectionRequested { get; set; }
    internal Action<int>? MaximumHistoryChanged { get; set; }
    internal Action<int>? MaximumTrailPointsChanged { get; set; }
    internal Action<int>? MaximumAircraftChanged { get; set; }
    internal Action<int>? RetentionMinutesChanged { get; set; }
    internal Action<bool>? SaveRawFramesChanged { get; set; }
    internal Action<bool>? MonitorAudioEnabledChanged { get; set; }
    internal Action<int>? MonitorAudioVolumeChanged { get; set; }
    internal Action<bool>? SquelchEnabledChanged { get; set; }
    internal Action<int>? PreambleVerificationSymbolsChanged { get; set; }
    internal Action<bool>? AdaptiveEqualizerEnabledChanged { get; set; }
    internal Action? ClearRequested { get; set; }
    internal Action? CaptureRequested { get; set; }

    public bool IsAudioMonitorEnabled
    {
        get => isAudioMonitorEnabled;
        set
        {
            if (isAudioMonitorEnabled == value) return;
            isAudioMonitorEnabled = value;
            MonitorAudioEnabledChanged?.Invoke(value);
            OnPropertyChanged();
        }
    }

    public int AudioMonitorVolume
    {
        get => audioMonitorVolume;
        set
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (audioMonitorVolume == normalized) return;
            audioMonitorVolume = normalized;
            MonitorAudioVolumeChanged?.Invoke(normalized);
            OnPropertyChanged();
        }
    }

    public bool IsSquelchEnabled
    {
        get => isSquelchEnabled;
        set
        {
            if (isSquelchEnabled == value) return;
            isSquelchEnabled = value;
            SquelchEnabledChanged?.Invoke(value);
            OnPropertyChanged();
        }
    }

    public int MaximumHistory
    {
        get => maximumHistory;
        set
        {
            int normalized = Math.Clamp(value, 100, 100_000);
            if (maximumHistory == normalized) return;
            maximumHistory = normalized;
            while (frames.Count > maximumHistory) frames.RemoveAt(frames.Count - 1);
            OnPropertyChanged(nameof(RecentFrames));
            RebuildCategories();
            RefreshFilteredHistory();
            MaximumHistoryChanged?.Invoke(normalized);
            OnPropertyChanged();
        }
    }

    public int PreambleVerificationSymbols
    {
        get => preambleVerificationSymbols;
        set
        {
            int normalized = value switch { 16 => 16, 12 => 12, 8 => 8, 4 => 4, _ => 16 };
            if (preambleVerificationSymbols == normalized) return;
            preambleVerificationSymbols = normalized;
            PreambleVerificationSymbolsChanged?.Invoke(normalized);
            OnPropertyChanged();
        }
    }

    public int MaximumAircraft
    {
        get => maximumAircraft;
        set
        {
            int n = Math.Clamp(value, 50, 5000);
            if (SetProperty(ref maximumAircraft, n))
            {
                MaximumAircraftChanged?.Invoke(n);
                RebuildCategories();
            }
        }
    }
    public int RetentionMinutes
    {
        get => retentionMinutes;
        set
        {
            int n = Math.Clamp(value, 1, 240);
            if (SetProperty(ref retentionMinutes, n))
            {
                RetentionMinutesChanged?.Invoke(n);
                RefreshMapMarkers();
            }
        }
    }

    public int MaximumTrailPoints
    {
        get => maximumTrailPoints;
        set
        {
            int normalized = Math.Clamp(value, 10, 1000);
            if (!SetProperty(ref maximumTrailPoints, normalized)) return;
            TrimTrails();
            MaximumTrailPointsChanged?.Invoke(normalized);
        }
    }
    public bool SaveRawFrames { get => saveRawFrames; set { if(SetProperty(ref saveRawFrames,value)) SaveRawFramesChanged?.Invoke(value); } }
    public bool IsAdaptiveEqualizerEnabled
    {
        get => isAdaptiveEqualizerEnabled;
        set
        {
            if (isAdaptiveEqualizerEnabled == value) return;
            isAdaptiveEqualizerEnabled = value;
            AdaptiveEqualizerEnabledChanged?.Invoke(value);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<int> AvailablePreambleVerificationSymbols { get; } = [16, 12, 8, 4];

    public long TotalFrameCount => ValidFrameCount + RejectedFrameCount;
    public double AcceptanceRate => TotalFrameCount == 0 ? 0 : ValidFrameCount * 100.0 / TotalFrameCount;
    public string AcceptanceRateText => TotalFrameCount == 0 ? "—" : $"{AcceptanceRate:F1} %";
    public int IdentifiedCallsignCount => callsignGroups.Count(group => group.Key != "機体不明");
    public string LastReceptionText => frames.Count == 0
        ? "—"
        : frames[0].ReceivedAt.ToLocalTime().ToString("HH:mm:ss");

    public void SynchronizeSettings(string profileId, bool audioEnabled, int audioVolume, int historyLimit,
        int verificationSymbols = 16, bool squelchEnabled = true, bool adaptiveEqualizerEnabled = false,
        bool saveRaw = true,
        int maxAircraft = 500, int retentionMins = 30, int maximumTrailPoints = 100)
    {
        selectedProfileId = profileId;
        isAudioMonitorEnabled = audioEnabled;
        audioMonitorVolume = audioVolume;
        isSquelchEnabled = squelchEnabled;
        isAdaptiveEqualizerEnabled = adaptiveEqualizerEnabled;
        maximumHistory = historyLimit;
        maximumAircraft = maxAircraft;
        retentionMinutes = retentionMins;
        this.maximumTrailPoints = Math.Clamp(maximumTrailPoints, 10, 1000);
        saveRawFrames = saveRaw;
        preambleVerificationSymbols = verificationSymbols switch { 16 => 16, 12 => 12, 8 => 8, 4 => 4, _ => 16 };
        OnPropertyChanged(nameof(SelectedProfileId));
        OnPropertyChanged(nameof(SelectedChannelName));
        OnPropertyChanged(nameof(IsAudioMonitorEnabled));
        OnPropertyChanged(nameof(AudioMonitorVolume));
        OnPropertyChanged(nameof(IsSquelchEnabled));
        OnPropertyChanged(nameof(IsAdaptiveEqualizerEnabled));
        OnPropertyChanged(nameof(MaximumHistory));
        OnPropertyChanged(nameof(MaximumAircraft)); OnPropertyChanged(nameof(RetentionMinutes));
        OnPropertyChanged(nameof(MaximumTrailPoints));
        OnPropertyChanged(nameof(SaveRawFrames));
        OnPropertyChanged(nameof(PreambleVerificationSymbols));
        RefreshMapMarkers();
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "VDL",
            async () =>
            {
                if (ResetSettingsRequested is not null)
                {
                    await ResetSettingsRequested();
                }
            },
            () => { });
    }

    [RelayCommand]
    private void ResetPluginData()
    {
        PluginResetHelper.ConfirmAndClearData(
            "VDL",
            () =>
            {
                ClearMessages();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await PluginResetHelper.ConfirmAndResetAllAsync(
            "VDL",
            async () =>
            {
                if (ResetSettingsRequested is not null)
                {
                    await ResetSettingsRequested();
                }
            },
            () => { },
            () =>
            {
                ClearMessages();
            });
    }
}
