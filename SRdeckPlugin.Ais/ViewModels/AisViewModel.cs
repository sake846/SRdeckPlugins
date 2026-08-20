using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Ais.Dsp;
using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais.ViewModels;

public sealed partial class AisViewModel : ObservableObject
{
    private const int MessageDisplayLimit = 2000;
    private const int MinimumDualChannelSampleRateHz = 240_000;
    private static readonly TimeSpan DiagnosticWindow = TimeSpan.FromSeconds(60);
    private readonly Dictionary<uint, List<GeoMapPoint>> trails = [];
    private readonly Queue<(DateTimeOffset At, long AValid, long ARejected, long BValid, long BRejected,
        long AFlags, long ACandidates, long AHypothesisValid,
        long BFlags, long BCandidates, long BHypothesisValid)> diagnosticHistory = new();
    private long recentAValid;
    private long recentARejected;
    private long recentBValid;
    private long recentBRejected;
    private long recentAFlags;
    private long recentACandidates;
    private long recentAHypothesisValid;
    private long recentBFlags;
    private long recentBCandidates;
    private long recentBHypothesisValid;
    private bool bothChannelsInPassband;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _status = "待機中";
    [ObservableProperty] private string _overallStatus = "待機中";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private string _overallPhase = "受信処理";
    [ObservableProperty] private string _overallSummary = "AIS IQ入力を待機しています";
    [ObservableProperty] private string _overallRecommendation = "確認: SDRソースの接続と周波数設定を確認してください";
    [ObservableProperty] private string _overallLastUpdated = "未更新";
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;

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

    private int maximumTargets = 500;
    private int retentionMinutes = 30;
    private int historyMaximum = 10_000;
    private bool saveRawFrames = true;
    private string channelFilter = "both";
    private int maximumTrailPoints = 100;
    private bool monitorAudioEnabled = true;
    private int monitorAudioVolume = 100;
    private bool squelchEnabled = true;
    private float squelchThresholdDbm = -125f;
    [ObservableProperty] private int _inputSampleRateHz;
    [ObservableProperty] private long _centerFrequencyHz;
    [ObservableProperty] private string _monitoredChannelsText = "2 ch (AIS 1 / AIS 2)";
    [ObservableProperty] private string _channelOffsetText = "—";
    [ObservableProperty] private string _rateConversionSummaryText = "—";
    [ObservableProperty] private string _rateConversionText = "—";
    [ObservableProperty] private string _intermediateRateText = "—";
    [ObservableProperty] private double _lastSignalQuality;
    [ObservableProperty] private double _averageSignalQuality;
    [ObservableProperty] private double _maximumSignalQuality;
    [ObservableProperty] private long _coherentFrames;
    [ObservableProperty] private long _fallbackFrames;
    [ObservableProperty] private string _frequencyCorrectionText = "—";
    [ObservableProperty] private string _channelADiagnosticText = "AIS 1: 入力待機中";
    [ObservableProperty] private string _channelBDiagnosticText = "AIS 2: 入力待機中";
    [ObservableProperty] private string _channelADetectionText = "AIS 1: フラグ同期 — / フレーム候補 — / 仮説FCS合格 —";
    [ObservableProperty] private string _channelBDetectionText = "AIS 2: フラグ同期 — / フレーム候補 — / 仮説FCS合格 —";
    [ObservableProperty] private string _recentValidationText = "直近60秒: 有効 — / fallback FCS不一致 — / 検証合格率 —";
    [ObservableProperty] private string _passbandStatusText = "AIS 1/2 / 入力待機中";
    [ObservableProperty] private AisTargetRow? _selectedTarget;
    [ObservableProperty] private AisMessageRow? _selectedTimelineMessage;

    public AisViewModel()
    {
    }

    public ObservableCollection<AisTargetRow> Targets { get; } = [];
    public ObservableCollection<AisTargetRow> FilteredTargets { get; } = [];
    public IEnumerable<AisTargetRow> RecentTargets =>
        Targets.OrderByDescending(item => item.LastSeen).Take(3);
    public ObservableCollection<AisMessageRow> Messages { get; } = [];
    public ObservableCollection<AisMessageRow> FilteredMessages { get; } = [];
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = [];

    internal Action<int, int, int, int, bool, int, bool, float, bool, string>? SettingsChanged { get; set; }
    internal Action? ClearRequested { get; set; }

    partial void OnSearchTextChanged(string value)
    {
        RefreshFilteredHistory();
        RefreshFilteredTargets();
    }

    private void RefreshFilteredTargets()
    {
        FilteredTargets.Clear();
        string filter = SearchText?.Trim() ?? string.Empty;
        foreach (AisTargetRow row in Targets)
        {
            if (filter.Length == 0 || $"{row.MmsiText} {row.DisplayName} {row.CallSign} {row.Destination}".Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredTargets.Add(row);
        }
        if (SelectedTarget is null || !FilteredTargets.Contains(SelectedTarget))
            SelectedTarget = FilteredTargets.FirstOrDefault();
    }
    partial void OnValidFramesChanged(long value) => NotifyTotals();
    partial void OnRejectedFramesChanged(long value) => NotifyTotals();
    partial void OnInputSampleRateHzChanged(int value) => OnPropertyChanged(nameof(InputSampleRateText));
    partial void OnCenterFrequencyHzChanged(long value) => OnPropertyChanged(nameof(CenterFrequencyText));

    public int FilteredCount => FilteredMessages.Count;
    public int PositionedTargetCount => MapMarkers.Count;

    public long TotalFrames => ValidFrames + RejectedFrames;
    public double AcceptanceRate => TotalFrames == 0 ? 0 : ValidFrames * 100.0 / TotalFrames;
    public string AcceptanceRateText => TotalFrames == 0 ? "—" : $"{AcceptanceRate:F1} %";
    public string InputSampleRateText => InputSampleRateHz <= 0 ? "—" :
        InputSampleRateHz >= 1_000_000 ? $"{InputSampleRateHz / 1_000_000.0:F2} MS/s" : $"{InputSampleRateHz / 1_000.0:F1} kS/s";
    public string CenterFrequencyText => CenterFrequencyHz <= 0 ? "—" : $"{CenterFrequencyHz / 1_000_000.0:F3} MHz";
    public string DemodulationMethodText => "複素整合相関 + coherent Viterbi MLSE";
    public string SettingStatus => $"AIS 1 / AIS 2 / SQ {(SquelchEnabled ? $"{SquelchThresholdDbm:F0} dBm" : "OFF")} / {MaximumTargets:N0}局 / {RetentionMinutes}分";

    public int MaximumTargets
    {
        get => maximumTargets;
        set
        {
            int normalized = Math.Clamp(value, 50, 10_000);
            if (SetProperty(ref maximumTargets, normalized)) SettingsUpdated();
        }
    }

    public int RetentionMinutes
    {
        get => retentionMinutes;
        set
        {
            int normalized = Math.Clamp(value, 1, 1440);
            if (SetProperty(ref retentionMinutes, normalized)) SettingsUpdated();
        }
    }

    public int HistoryMaximum
    {
        get => historyMaximum;
        set
        {
            int normalized = Math.Clamp(value, 100, 1_000_000);
            if (SetProperty(ref historyMaximum, normalized)) SettingsUpdated();
        }
    }

    public bool SaveRawFrames { get => saveRawFrames; set { if(SetProperty(ref saveRawFrames,value)) SettingsUpdated(); } }
    public string ChannelFilter { get => channelFilter; set { string n=value is "ais1" or "ais2" or "both" ? value : "both"; if(SetProperty(ref channelFilter,n)) SettingsUpdated(); } }

    public int MaximumTrailPoints
    {
        get => maximumTrailPoints;
        set
        {
            int normalized = Math.Clamp(value, 10, 1000);
            if (!SetProperty(ref maximumTrailPoints, normalized)) return;
            TrimTrails();
            SettingsUpdated();
        }
    }

    public bool MonitorAudioEnabled
    {
        get => monitorAudioEnabled;
        set { if (SetProperty(ref monitorAudioEnabled, value)) SettingsUpdated(); }
    }

    public int MonitorAudioVolume
    {
        get => monitorAudioVolume;
        set
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (SetProperty(ref monitorAudioVolume, normalized)) SettingsUpdated();
        }
    }

    public bool SquelchEnabled
    {
        get => squelchEnabled;
        set { if (SetProperty(ref squelchEnabled, value)) SettingsUpdated(); }
    }

    public float SquelchThresholdDbm
    {
        get => squelchThresholdDbm;
        set
        {
            float normalized = Math.Clamp(value, -160f, 0f);
            if (SetProperty(ref squelchThresholdDbm, normalized))
            {
                OnPropertyChanged(nameof(SquelchThresholdDbfs));
                SettingsUpdated();
            }
        }
    }

    public float SquelchThresholdDbfs
    {
        get => SquelchThresholdDbm;
        set => SquelchThresholdDbm = value;
    }


    internal void SynchronizeSettings(int targetLimit, int retention, int historyLimit, int trailPoints,
        bool audioEnabled, int audioVolume, bool isSquelchEnabled = true,
        float squelchThreshold = -125f,
        bool saveRaw = true,
        string channelFilterValue = "both")
    {
        maximumTargets = targetLimit;
        retentionMinutes = retention;
        historyMaximum = historyLimit;
        saveRawFrames = saveRaw;
        channelFilter = channelFilterValue;
        maximumTrailPoints = trailPoints;
        monitorAudioEnabled = audioEnabled;
        monitorAudioVolume = audioVolume;
        squelchEnabled = isSquelchEnabled;
        squelchThresholdDbm = Math.Clamp(squelchThreshold, -160f, 0f);
        OnPropertyChanged(nameof(MaximumTargets));
        OnPropertyChanged(nameof(RetentionMinutes));
        OnPropertyChanged(nameof(HistoryMaximum));
        OnPropertyChanged(nameof(SaveRawFrames)); OnPropertyChanged(nameof(ChannelFilter));
        OnPropertyChanged(nameof(MaximumTrailPoints));
        OnPropertyChanged(nameof(MonitorAudioEnabled));
        OnPropertyChanged(nameof(MonitorAudioVolume));
        OnPropertyChanged(nameof(SquelchEnabled));
        OnPropertyChanged(nameof(SquelchThresholdDbm));
        OnPropertyChanged(nameof(SquelchThresholdDbfs));
        OnPropertyChanged(nameof(SettingStatus));
    }

    public void AddMessage(AisMessageRow message)
    {
        Messages.Insert(0, message);
        while (Messages.Count > MessageDisplayLimit) Messages.RemoveAt(Messages.Count - 1);
        Targets.FirstOrDefault(item => item.Mmsi == message.Mmsi)?.AddHistory(message);
        if (Matches(message, SearchText))
        {
            FilteredMessages.Insert(0, message);
            while (FilteredMessages.Count > MessageDisplayLimit) FilteredMessages.RemoveAt(FilteredMessages.Count - 1);
            if (SelectedTimelineMessage is null || !FilteredMessages.Contains(SelectedTimelineMessage))
                SelectedTimelineMessage = FilteredMessages.FirstOrDefault();
            OnPropertyChanged(nameof(FilteredCount));
        }
    }
    private void RefreshFilteredHistory()
    {
        FilteredMessages.Clear();
        foreach (AisMessageRow message in Messages)
            if (Matches(message, SearchText)) FilteredMessages.Add(message);
        if (SelectedTimelineMessage is null || !FilteredMessages.Contains(SelectedTimelineMessage))
            SelectedTimelineMessage = FilteredMessages.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredCount));
    }

    private static bool Matches(AisMessageRow item, string? filter)
    {
        filter = filter?.Trim() ?? string.Empty;
        return filter.Length == 0 ||
               item.Mmsi.ToString("000000000").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Channel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               item.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear()
    {
        SelectedTarget = null;
        SelectedTimelineMessage = null;
        Targets.Clear();
        FilteredTargets.Clear();
        Messages.Clear();
        FilteredMessages.Clear();
        MapMarkers.Clear();
        trails.Clear();
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(PositionedTargetCount));
        OnPropertyChanged(nameof(RecentTargets));
        ClearRequested?.Invoke();
    }

    private void SettingsUpdated()
    {
        OnPropertyChanged(nameof(SettingStatus));
        SettingsChanged?.Invoke(MaximumTargets, RetentionMinutes, HistoryMaximum,
            MaximumTrailPoints, MonitorAudioEnabled, MonitorAudioVolume, SquelchEnabled, SquelchThresholdDbm,
            SaveRawFrames, ChannelFilter);
    }

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(TotalFrames));
        OnPropertyChanged(nameof(AcceptanceRate));
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "AIS",
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
            "AIS",
            () =>
            {
                Clear();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await PluginResetHelper.ConfirmAndResetAllAsync(
            "AIS",
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
                Clear();
            });
    }
}

public sealed partial class AisTargetRow(uint mmsi) : ObservableObject
{
    private const int HistoryLimit = 20;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _callSign = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShipTypeText))]
    private int? _shipType;
    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private double? _latitude;
    [ObservableProperty] private double? _longitude;
    [ObservableProperty] private double? _speedKnots;
    [ObservableProperty] private double? _courseDegrees;
    [ObservableProperty] private int? _headingDegrees;
    public int? NavigationStatus { get; private set; }
    [ObservableProperty] private string _navigationStatusText = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAidToNavigation))]
    private string _aidType = string.Empty;
    [ObservableProperty] private string _channel = string.Empty;
    [ObservableProperty] private DateTimeOffset _lastSeen;
    [ObservableProperty] private long _messages;
    [ObservableProperty] private bool _isBaseStation;

    public uint Mmsi { get; } = mmsi;
    public ObservableCollection<AisMessageRow> History { get; } = [];
    public string MmsiText => Mmsi.ToString("000000000");
    public string ShipTypeText => ShipTypeName(ShipType);
    public bool IsAidToNavigation => AidType.Length > 0;

    public void AddHistory(AisMessageRow message)
    {
        History.Insert(0, message);
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
    }

    public void ReplaceHistory(IEnumerable<AisMessageRow> messages)
    {
        History.Clear();
        foreach (AisMessageRow message in messages.Reverse()) AddHistory(message);
    }

    public void Apply(AisTargetState state)
    {
        DisplayName = ResolveDisplayName(state.VesselName, state.CallSign);
        CallSign = state.CallSign;
        ShipType = state.ShipType;
        Destination = state.Destination;
        Latitude = state.Latitude;
        Longitude = state.Longitude;
        SpeedKnots = state.SpeedOverGroundKnots;
        CourseDegrees = state.CourseOverGroundDegrees;
        HeadingDegrees = state.TrueHeadingDegrees;
        NavigationStatus = state.NavigationStatus;
        NavigationStatusText = NavigationStatusName(state.NavigationStatus);
        AidType = state.AidType;
        IsBaseStation = state.IsBaseStation;
        Channel = state.LastChannel;
        LastSeen = state.LastSeen.ToLocalTime();
        Messages = state.MessageCount;
    }

    private string ResolveDisplayName(string vesselName, string callSign) =>
        !string.IsNullOrWhiteSpace(vesselName) ? vesselName.Trim() :
        !string.IsNullOrWhiteSpace(callSign) ? callSign.Trim() : MmsiText;

    private static string NavigationStatusName(int? value) => value switch
    {
        0 => "航行中（機関）",
        1 => "錨泊中",
        2 => "操縦性能制限",
        3 => "操船制限",
        4 => "喫水制限",
        5 => "係留中",
        6 => "座礁",
        7 => "漁労中",
        8 => "帆走中",
        14 => "AIS-SART/MOB",
        15 => "未定義",
        _ => value is null ? string.Empty : $"状態 {value}"
    };

    private static string ShipTypeName(int? value) => value switch
    {
        null or 0 => string.Empty,
        >= 30 and <= 39 => "漁船・作業船",
        >= 40 and <= 49 => "高速船",
        >= 50 and <= 59 => "特殊用途船",
        >= 60 and <= 69 => "旅客船",
        >= 70 and <= 79 => "貨物船",
        >= 80 and <= 89 => "タンカー",
        >= 90 and <= 99 => "その他",
        _ => $"種別 {value}"
    };
}
