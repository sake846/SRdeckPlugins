using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Hfdl.Dsp;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Hfdl.ViewModels;

public sealed partial class HfdlViewModel : ObservableObject
{
    private static readonly TimeSpan DiagnosticWindow = TimeSpan.FromSeconds(120);
    private readonly Queue<(DateTimeOffset At, long Search, long Candidates, long Sync, long Valid, long Rejected)> diagnosticHistory = new();
    private DateTimeOffset? lastSynchronizationAt;
    private DateTimeOffset? lastValidFrameAt;
    [ObservableProperty] private string _status = "停止中";
    private string selectedChannelId = HfdlPluginModule.DefaultChannelId;
    private int maximumHistory = 10_000;
    private int maximumAircraft = 500;
    private int retentionMinutes = 30;
    private int maximumTrailPoints = 100;
    private bool saveRawFrames = true;
    private bool splitHistoryByChannel;
    private bool monitorAudioEnabled;
    private int monitorAudioVolume = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalFrames))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRate))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRateText))]
    private long _validFrames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalFrames))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRate))]
    [NotifyPropertyChangedFor(nameof(AcceptanceRateText))]
    private long _rejectedFrames;

    private bool synchronizing;
    [ObservableProperty] private string _inputRateText = "IQ入力待機中";
    [ObservableProperty] private string _passbandStatusText = "—";
    [ObservableProperty] private string _selectedFrequencyText = "-";
    [ObservableProperty] private string _rateConversionSummaryText = "—";
    [ObservableProperty] private string _rateConversionText = "—";
    [ObservableProperty] private string _intermediateRateText = "—";
    [ObservableProperty] private string _channelOffsetText = "-";
    [ObservableProperty] private string _inputLevelText = "-";
    [ObservableProperty] private string _channelLevelText = "-";
    [ObservableProperty] private string _channelPeakText = "-";
    [ObservableProperty] private string _searchCorrelationText = "-";
    [ObservableProperty] private string _synchronizationQualityText = "-";
    [ObservableProperty] private string _carrierOffsetText = "-";
    [ObservableProperty] private string _modeText = "同期待機中";
    [ObservableProperty] private string _searchCountsText = "-";
    [ObservableProperty] private string _recentSearchCountsText = "直近120秒: 探索 — / 候補 — / 同期 —";
    [ObservableProperty] private string _recentValidationText = "直近120秒: 合格 — / 不一致 — / 検証合格率 —";
    [ObservableProperty] private string _bufferText = "-";
    [ObservableProperty] private string _processedText = "-";
    [ObservableProperty] private string _diagnosisText = "IQ入力を待っています。";
    [ObservableProperty] private string _captureStatus = "IQ録音: 待機";

    public HfdlViewModel()
    {
    }

    public ObservableCollection<HfdlReception> Messages { get; } = [];
    public IEnumerable<HfdlReception> RecentMessages => Messages.Take(3);
    public IEnumerable<HfdlCategorySummary> RecentFlightGroups => FlightIdGroups.Take(3);
    public ObservableCollection<HfdlReception> FilteredMessages { get; } = [];
    public ObservableCollection<HfdlCategorySummary> FlightIdGroups { get; } = [];
    public ObservableCollection<HfdlCategorySummary> KindGroups { get; } = [];
    public ObservableCollection<HfdlCategorySummary> FilteredFlightIdGroups { get; } = [];
    public ObservableCollection<HfdlCategorySummary> FilteredKindGroups { get; } = [];
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = [];
    public int IdentifiedFlightCount => FlightIdGroups.Count(group => group.Key != "Flight ID不明");

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _historyMode = "Flight ID 別";
    [ObservableProperty] private string _overallStatus = "待機中";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private string _overallPhase = "受信処理";
    [ObservableProperty] private string _overallSummary = "IQ入力を待機しています";
    [ObservableProperty] private string _overallRecommendation = "確認: SDRソースの接続と周波数設定を確認してください";
    [ObservableProperty] private string _overallLastUpdated = "未更新";
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    [ObservableProperty] private HfdlCategorySummary? _selectedListGroup;
    [ObservableProperty] private HfdlReception? _selectedTimelineReception;

    partial void OnSearchTextChanged(string value) => RefreshFilteredHistory();
    partial void OnHistoryModeChanged(string value)
    {
        SelectedListGroup = null;
        OnPropertyChanged(nameof(IsTimeSeriesMode));
        OnPropertyChanged(nameof(IsFlightIdMode));
        OnPropertyChanged(nameof(IsKindMode));
        RefreshFilteredHistory();
    }
    partial void OnValidFramesChanged(long value) => NotifyTotals();
    partial void OnRejectedFramesChanged(long value) => NotifyTotals();

    public bool IsTimeSeriesMode
    {
        get => HistoryMode == "時系列";
        set { if (value) HistoryMode = "時系列"; }
    }
    public bool IsFlightIdMode
    {
        get => HistoryMode == "Flight ID 別";
        set { if (value) HistoryMode = "Flight ID 別"; }
    }
    public bool IsKindMode
    {
        get => HistoryMode == "種別別";
        set { if (value) HistoryMode = "種別別"; }
    }

    public int FilteredCount => HistoryMode switch
    {
        "Flight ID 別" => FilteredFlightIdGroups.Count,
        "種別別" => FilteredKindGroups.Count,
        _ => FilteredMessages.Count
    };

    public IReadOnlyList<HfdlPluginModule.GroundStation> GroundStations => HfdlPluginModule.GroundStations;
    public IReadOnlyList<HfdlPluginModule.Channel> Channels => HfdlPluginModule.Channels;


    [RelayCommand]
    private void StartCapture() => CaptureRequested?.Invoke();

    internal Func<string, bool>? ChannelSelectionRequested { get; set; }
    internal Action<int>? MaximumHistoryChanged { get; set; }
    internal Action<int>? MaximumTrailPointsChanged { get; set; }
    internal Action<bool>? SaveRawFramesChanged { get; set; }
    internal Action<bool>? SplitHistoryByChannelChanged { get; set; }
    internal Action<bool>? MonitorAudioEnabledChanged { get; set; }
    internal Action<int>? MonitorAudioVolumeChanged { get; set; }
    internal Action? ClearRequested { get; set; }
    internal Action? CaptureRequested { get; set; }

    public long TotalFrames => ValidFrames + RejectedFrames;
    public double AcceptanceRate => TotalFrames == 0 ? 0 : ValidFrames * 100.0 / TotalFrames;
    public string AcceptanceRateText => TotalFrames == 0 ? "—" : $"{AcceptanceRate:F1} %";
    public string LastReceptionText => Messages.Count == 0
        ? "—"
        : Messages[0].ReceivedAt.ToLocalTime().ToString("HH:mm:ss");

    public HfdlPluginModule.Channel SelectedChannel => Channels.First(item => item.Id == SelectedChannelId);
    public HfdlPluginModule.GroundStation SelectedGroundStation =>
        GroundStations.First(station => station.Id == SelectedChannel.GroundStationId);

    public int SelectedGroundStationId
    {
        get => SelectedChannel.GroundStationId;
        set
        {
            if (value == SelectedGroundStationId) return;
            HfdlPluginModule.GroundStation? station = GroundStations.FirstOrDefault(item => item.Id == value);
            if (station is null) return;
            SelectedChannelId = HfdlPluginModule.RecommendedChannels(station, DateTimeOffset.UtcNow, 1)[0].Id;
        }
    }

    public IReadOnlyList<HfdlPluginModule.Channel> StationChannels => Channels.Where(channel =>
        channel.GroundStationId == SelectedGroundStationId).OrderBy(channel => channel.FrequencyHz).ToArray();
    public string SettingStatus => $"{SelectedGroundStation.Name} / {SelectedChannel.FrequencyDisplay}";

    public string PropagationGuidance
    {
        get
        {
            DateTimeOffset solarTime = HfdlPluginModule.GetStationSolarTime(SelectedGroundStation, DateTimeOffset.UtcNow);
            string recommended = string.Join("、", HfdlPluginModule.RecommendedChannels(
                SelectedGroundStation, DateTimeOffset.UtcNow).Select(channel => channel.FrequencyDisplay));

            return $"{SelectedGroundStation.Name} 現地太陽時 {solarTime:HH:mm} / " +
                   $"選択中: {HfdlPluginModule.FrequencyTimeGuidance(SelectedChannel.FrequencyHz)} / " +
                   $"現在の推奨候補: {recommended}";
        }
    }

    public string SelectedChannelId
    {
        get => selectedChannelId;
        set
        {
            if (value == selectedChannelId || !Channels.Any(item => item.Id == value)) return;
            string previous = selectedChannelId;
            selectedChannelId = value;
            if (!synchronizing && ChannelSelectionRequested is not null && !ChannelSelectionRequested(value))
                selectedChannelId = previous;
            NotifyChannelSelectionChanged();
        }
    }

    public int MaximumHistory
    {
        get => maximumHistory;
        set
        {
            int normalized = Math.Clamp(value, 100, 100_000);
            if (!SetProperty(ref maximumHistory, normalized)) return;
            while (Messages.Count > maximumHistory) Messages.RemoveAt(Messages.Count - 1);
            OnPropertyChanged(nameof(RecentMessages));
            if (!synchronizing) MaximumHistoryChanged?.Invoke(normalized);
        }
    }

    public int MaximumAircraft
    {
        get => maximumAircraft;
        set
        {
            int n = Math.Clamp(value, 50, 5000);
            if (SetProperty(ref maximumAircraft, n) && !synchronizing)
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
            int n = Math.Clamp(value, 1, 1440);
            if (SetProperty(ref retentionMinutes, n) && !synchronizing)
            {
                RetentionMinutesChanged?.Invoke(n);
                RebuildCategories();
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
            if (!synchronizing) MaximumTrailPointsChanged?.Invoke(normalized);
        }
    }
    public bool SaveRawFrames
    {
        get => saveRawFrames;
        set { if (SetProperty(ref saveRawFrames, value) && !synchronizing) SaveRawFramesChanged?.Invoke(value); }
    }
    public bool SplitHistoryByChannel
    {
        get => splitHistoryByChannel;
        set { if (SetProperty(ref splitHistoryByChannel, value) && !synchronizing) SplitHistoryByChannelChanged?.Invoke(value); }
    }
    public bool MonitorAudioEnabled
    {
        get => monitorAudioEnabled;
        set
        {
            if (SetProperty(ref monitorAudioEnabled, value) && !synchronizing)
                MonitorAudioEnabledChanged?.Invoke(value);
        }
    }

    public int MonitorAudioVolume
    {
        get => monitorAudioVolume;
        set
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (SetProperty(ref monitorAudioVolume, normalized) && !synchronizing)
                MonitorAudioVolumeChanged?.Invoke(normalized);
        }
    }

    internal Action<int>? MaximumAircraftChanged { get; set; }
    internal Action<int>? RetentionMinutesChanged { get; set; }

    internal void SynchronizeSettings(string channelId, int historyLimit, bool audioEnabled, int audioVolume,
        bool saveRaw = true,
        bool splitByChannel = false, int maximumAircraft = 500, int retentionMinutes = 30,
        int maximumTrailPoints = 100)
    {
        synchronizing = true;
        try
        {
            SelectedChannelId = channelId;
            MaximumHistory = historyLimit;
            MaximumAircraft = maximumAircraft;
            RetentionMinutes = retentionMinutes;
            MaximumTrailPoints = maximumTrailPoints;
        SaveRawFrames = saveRaw;
        SplitHistoryByChannel = splitByChannel;
        MonitorAudioEnabled = audioEnabled;
            MonitorAudioVolume = audioVolume;
            UpdateMapMarker();
        }
        finally { synchronizing = false; }
    }
}
