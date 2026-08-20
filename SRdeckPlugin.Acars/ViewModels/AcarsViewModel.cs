using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Acars.Dsp;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars.ViewModels;

public sealed partial class AcarsViewModel : ObservableObject
{
    private const string AircraftColor = "#63a4ff";
    private const float MinimumInputRms = 0.00001f;
    private const float MaximumAgcGainWarning = 0.95f;
    private const float MinimumToneConfidence = 0.30f;
    private static readonly TimeSpan DiagnosticWindow = TimeSpan.FromSeconds(60);
    private readonly Queue<(DateTimeOffset At, long Valid, long Rejected, long Candidates)> diagnosticHistory = new();
    private DateTimeOffset? lastValidFrameAt;

    [ObservableProperty] private string _status = "停止中";
    private string selectedRegion = "日本";
    private string selectedChannelId = "jp-primary";
    [ObservableProperty] private int _maximumHistory = 10_000;
    private int maximumTrailPoints = 100;

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

    [ObservableProperty] private string _captureStatus = "IQ録音: 待機";
    [ObservableProperty] private string _inputRateText = "--";
    [ObservableProperty] private string _rateConversionSummaryText = "—";
    [ObservableProperty] private string _rateConversionText = "—";
    [ObservableProperty] private string _intermediateRateText = "—";
    [ObservableProperty] private string _channelOffsetText = "--";
    [ObservableProperty] private string _passbandStatusText = "—";
    [ObservableProperty] private string _channelLevelText = "--";
    [ObservableProperty] private string _agcGainText = "--";
    [ObservableProperty] private string _audioLevelText = "--";
    [ObservableProperty] private string _audioPeakText = "--";
    [ObservableProperty] private string _toneConfidenceText = "--";
    [ObservableProperty] private string _detectorText = "待機中";
    [ObservableProperty] private string _decodePassText = "0";
    [ObservableProperty] private string _recentValidationText = "候補 — / BCS合格 — / 不一致 — / 検証合格率 —";
    [ObservableProperty] private string _monitoredChannelsText = "1 ch";
    [ObservableProperty] private bool _isAudioMonitorEnabled = true;
    [ObservableProperty] private bool _isSquelchEnabled = true;
    [ObservableProperty] private int _audioMonitorVolume = 100;
    [ObservableProperty] private bool _saveUninterpretedMessages;
    [ObservableProperty] private bool _saveRawFrames = true;
    [ObservableProperty] private string _uninterpretedLogFilePath = "acars_uninterpreted_messages.log";
    [ObservableProperty] private int _maximumAircraft = 500;
    [ObservableProperty] private int _retentionMinutes = 30;
    [ObservableProperty] private bool _buzzerEnabled = true;
    private bool synchronizingSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRawData))]
    [NotifyPropertyChangedFor(nameof(SelectedRawHeader))]
    [NotifyPropertyChangedFor(nameof(SelectedRawData))]
    private AcarsReception? _selectedReception;

    [ObservableProperty] private AcarsReception? _selectedTimelineReception;

    [ObservableProperty] private AcarsCategorySummary? _selectedAircraftGroup;
    private readonly HashSet<string> monitoredChannelIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GeoMapMarker> mapMarkersByAircraft = new(StringComparer.OrdinalIgnoreCase);

    public AcarsViewModel()
    {
        monitoredChannelIds.Add(selectedChannelId);
        RebuildChannelSelections();
    }

    public ObservableCollection<AcarsReception> Messages { get; } = [];
    public IEnumerable<AcarsReception> RecentMessages => Messages.Take(3);
    public IEnumerable<AcarsCategorySummary> RecentAircraftGroups => AircraftGroups.Take(3);
    public ObservableCollection<AcarsCategorySummary> AircraftGroups { get; } = [];
    public ObservableCollection<AcarsReception> FilteredMessages { get; } = [];
    public ObservableCollection<AcarsCategorySummary> FilteredAircraftGroups { get; } = [];
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = [];
    public ObservableCollection<AcarsChannelSelection> RegionalChannelSelections { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _overallStatus = "待機中";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private string _overallPhase = "受信処理";
    [ObservableProperty] private string _overallSummary = "IQ入力を待機しています";
    [ObservableProperty] private string _overallRecommendation = "確認: SDRソースの接続と周波数設定を確認してください";
    [ObservableProperty] private string _overallLastUpdated = "未更新";
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;

    partial void OnSearchTextChanged(string value) => RefreshFilteredHistory();
    partial void OnSelectedAircraftGroupChanged(AcarsCategorySummary? value)
    {
        if (value is not null) SelectedReception = value.LatestReception;
    }
    partial void OnIsAudioMonitorEnabledChanged(bool value)
    {
        if (!synchronizingSettings) MonitorAudioEnabledChanged?.Invoke(value);
    }
    partial void OnIsSquelchEnabledChanged(bool value)
    {
        if (!synchronizingSettings) SquelchEnabledChanged?.Invoke(value);
    }
    partial void OnAudioMonitorVolumeChanged(int value)
    {
        int normalized = Math.Clamp(value, 0, 100);
        if (value != normalized)
        {
            synchronizingSettings = true;
            AudioMonitorVolume = normalized;
            synchronizingSettings = false;
        }
        if (!synchronizingSettings) MonitorAudioVolumeChanged?.Invoke(normalized);
    }
    partial void OnSaveUninterpretedMessagesChanged(bool value)
    {
        if (!synchronizingSettings) SaveUninterpretedMessagesChanged?.Invoke(value);
    }
    partial void OnBuzzerEnabledChanged(bool value)
    {
        if (!synchronizingSettings) BuzzerEnabledChanged?.Invoke(value);
    }
    partial void OnMaximumHistoryChanged(int value)
    {
        int normalized = Math.Clamp(value, 100, 100_000);
        if (!synchronizingSettings) MaximumHistoryChanged?.Invoke(normalized);
    }
    partial void OnMaximumAircraftChanged(int value)
    {
        int normalized = Math.Clamp(value, 50, 5000);
        if (value != normalized)
        {
            synchronizingSettings = true;
            MaximumAircraft = normalized;
            synchronizingSettings = false;
        }
        if (!synchronizingSettings)
        {
            MaximumAircraftChanged?.Invoke(normalized);
            RebuildCategories();
        }
    }
    partial void OnRetentionMinutesChanged(int value)
    {
        int normalized = Math.Clamp(value, 1, 240);
        if (value != normalized)
        {
            synchronizingSettings = true;
            RetentionMinutes = normalized;
            synchronizingSettings = false;
        }
        if (!synchronizingSettings)
        {
            RetentionMinutesChanged?.Invoke(normalized);
            RebuildCategories();
        }
    }
    partial void OnSaveRawFramesChanged(bool value)
    {
        if (!synchronizingSettings) SaveRawFramesChanged?.Invoke(value);
    }
    partial void OnUninterpretedLogFilePathChanged(string value)
    {
        if (!synchronizingSettings) UninterpretedLogFilePathChanged?.Invoke(value);
    }

    internal Action<int>? MaximumTrailPointsChanged { get; set; }

    public int FilteredCount => FilteredAircraftGroups.Count;
    public int ReceivedAircraftCount => AircraftGroups.Count(group => group.Key != "機体不明");

    public bool HasSelectedRawData => SelectedReception is not null;
    public string SelectedRawData => SelectedReception?.Text ?? string.Empty;
    public string SelectedRawHeader => SelectedReception is null
        ? string.Empty
        : $"{SelectedReception.ReceivedAt:yyyy-MM-dd HH:mm:ss.fff zzz}  |  " +
          $"{SelectedReception.FrequencyHz / 1_000_000.0:F3} MHz  |  " +
          $"{SelectedReception.Aircraft}  |  Label {SelectedReception.Label}  |  " +
          $"Block {SelectedReception.BlockId}  |  Quality {SelectedReception.SignalQuality * 100:F1} %";

    public int PositionedAircraftCount => MapMarkers.Count;
    public IReadOnlyList<AcarsPluginModule.Channel> Channels => AcarsPluginModule.Channels;
    public IReadOnlyList<string> Regions { get; } =
        AcarsPluginModule.Channels.Select(item => item.Region).Distinct().ToArray();
    public IReadOnlyList<AcarsPluginModule.Channel> RegionalChannels =>
        Channels.Where(item => AcarsPluginModule.IsChannelAvailableInRegion(
            item, SelectedRegion)).OrderBy(item => item.FrequencyHz).ToArray();

    internal Func<string, bool>? ChannelSelectionRequested { get; set; }
    internal Func<IReadOnlyList<string>, bool>? MonitoredChannelsChanged { get; set; }
    internal Action<int>? MaximumHistoryChanged { get; set; }
    internal Action<int>? MaximumAircraftChanged { get; set; }
    internal Action<int>? RetentionMinutesChanged { get; set; }
    internal Action<bool>? MonitorAudioEnabledChanged { get; set; }
    internal Action<bool>? SquelchEnabledChanged { get; set; }
    internal Action<int>? MonitorAudioVolumeChanged { get; set; }
    internal Action<bool>? SaveUninterpretedMessagesChanged { get; set; }
    internal Action<bool>? SaveRawFramesChanged { get; set; }
    internal Action<string>? UninterpretedLogFilePathChanged { get; set; }
    internal Action<bool>? BuzzerEnabledChanged { get; set; }
    internal Action? ClearRequested { get; set; }
    internal Action? CaptureRequested { get; set; }

    public long TotalFrames => ValidFrames + RejectedFrames;
    public double AcceptanceRate => TotalFrames == 0 ? 0 : ValidFrames * 100.0 / TotalFrames;
    public string AcceptanceRateText => TotalFrames == 0 ? "—" : $"{AcceptanceRate:F1} %";
    public string LastReceptionText => Messages.Count == 0
        ? "—"
        : Messages[0].ReceivedAt.ToLocalTime().ToString("HH:mm:ss");
    public string SettingStatus => $"{monitoredChannelIds.Count} ch 監視中 / 航跡{MaximumTrailPoints}点";
    public int MaximumTrailPoints
    {
        get => maximumTrailPoints;
        set
        {
            int normalized = Math.Clamp(value, 10, 1000);
            if (!SetProperty(ref maximumTrailPoints, normalized)) return;
            TrimTrails();
            OnPropertyChanged(nameof(SettingStatus));
            if (!synchronizingSettings) MaximumTrailPointsChanged?.Invoke(normalized);
        }
    }
    public AcarsPluginModule.Channel SelectedChannel =>
        Channels.FirstOrDefault(item => item.Id == SelectedChannelId) ?? Channels[0];

    public string SelectedRegion
    {
        get => selectedRegion;
        set
        {
            if (value == selectedRegion || !Regions.Contains(value)) return;
            string previousRegion = selectedRegion;
            string previousChannelId = selectedChannelId;
            AcarsPluginModule.Channel channel = Channels.First(item => item.Region == value);
            selectedRegion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RegionalChannels));
            RebuildChannelSelections();
            SelectedChannelId = channel.Id;
            if (selectedChannelId == previousChannelId)
            {
                selectedRegion = previousRegion;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RegionalChannels));
                RebuildChannelSelections();
            }
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
            if (!synchronizingSettings && ChannelSelectionRequested is not null &&
                !ChannelSelectionRequested(value))
            {
                selectedChannelId = previous;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedChannel));
                OnPropertyChanged(nameof(SettingStatus));
                return;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedChannel));
            OnPropertyChanged(nameof(SettingStatus));
            monitoredChannelIds.RemoveWhere(id => Channels.Any(channel =>
                channel.Id == id && channel.Region != SelectedChannel.Region));
            if (monitoredChannelIds.Add(value))
            {
                OnPropertyChanged(nameof(SettingStatus));
                RebuildChannelSelections();
            }
            if (selectedRegion != SelectedChannel.Region)
            {
                selectedRegion = SelectedChannel.Region;
                OnPropertyChanged(nameof(SelectedRegion));
                OnPropertyChanged(nameof(RegionalChannels));
                RebuildChannelSelections();
            }
        }
    }



    internal void SynchronizeSettings(string channelId, IReadOnlyList<string> monitoredIds, int historyLimit,
        bool audioEnabled = true, int audioVolume = 100, bool saveUninterpreted = false,
        bool buzzerEnabled = true, int trailPoints = 100,
        bool saveRawFrames = true,
        string uninterpretedLogFilePath = "acars_uninterpreted_messages.log",
        bool squelchEnabled = true, int maximumAircraft = 500, int retentionMinutes = 30)
    {
        synchronizingSettings = true;
        try
        {
            monitoredChannelIds.Clear();
            foreach (string id in monitoredIds.Where(id => Channels.Any(channel => channel.Id == id)))
                monitoredChannelIds.Add(id);
            monitoredChannelIds.Add(channelId);
            SelectedChannelId = channelId;
            MaximumHistory = historyLimit;
            MaximumAircraft = maximumAircraft;
            RetentionMinutes = retentionMinutes;
            IsAudioMonitorEnabled = audioEnabled;
            IsSquelchEnabled = squelchEnabled;
            AudioMonitorVolume = audioVolume;
            SaveUninterpretedMessages = saveUninterpreted;
            SaveRawFrames = saveRawFrames;
            UninterpretedLogFilePath = uninterpretedLogFilePath;
            BuzzerEnabled = buzzerEnabled;
            MaximumTrailPoints = trailPoints;
            RebuildChannelSelections();
            OnPropertyChanged(nameof(SettingStatus));
        }
        finally { synchronizingSettings = false; }
    }
    private void RebuildCategories()
    {
        string? selectedKey = SelectedAircraftGroup?.Key;
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-RetentionMinutes);
        var activeGroups = Messages
            .GroupBy(item => DisplayKey(item.Aircraft, "機体不明"))
            .Where(group => group.Any(item => item.ReceivedAt >= cutoff))
            .OrderByDescending(group => group.Max(item => item.ReceivedAt))
            .Take(MaximumAircraft);
        ReplaceCategories(AircraftGroups, activeGroups);
        SelectedAircraftGroup = selectedKey is null
            ? null
            : AircraftGroups.FirstOrDefault(item =>
                string.Equals(item.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(ReceivedAircraftCount));
        OnPropertyChanged(nameof(RecentAircraftGroups));
    }

    private void RefreshFilteredHistory()
    {
        FilteredMessages.Clear();
        string filter = SearchText?.Trim() ?? string.Empty;
        foreach (AcarsReception msg in Messages)
        {
            if (string.IsNullOrEmpty(filter) ||
                (msg.Aircraft?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.Label?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.SummaryText?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
            {
                FilteredMessages.Add(msg);
            }
        }

        FilteredAircraftGroups.Clear();
        foreach (AcarsCategorySummary grp in AircraftGroups)
        {
            if (string.IsNullOrEmpty(filter) ||
                grp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                grp.LatestText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAircraftGroups.Add(grp);
            }
        }

        if (SelectedAircraftGroup is null || !FilteredAircraftGroups.Contains(SelectedAircraftGroup))
            SelectedAircraftGroup = FilteredAircraftGroups.FirstOrDefault();

        if (SelectedTimelineReception is null || !FilteredMessages.Contains(SelectedTimelineReception))
            SelectedTimelineReception = FilteredMessages.FirstOrDefault();

        OnPropertyChanged(nameof(FilteredCount));
    }

    private static void ReplaceCategories(ObservableCollection<AcarsCategorySummary> target,
        IEnumerable<IGrouping<string, AcarsReception>> groups)
    {
        AcarsCategorySummary[] summaries = groups.Select(group =>
        {
            AcarsReception latest = group.OrderByDescending(item => item.ReceivedAt).First();
            AcarsInterpretation interpretation = AcarsMessageInterpreter.InterpretDetailed(
                latest.Label, latest.Text);
            return new AcarsCategorySummary(group.Key, group.Count(), latest.ReceivedAt.ToLocalTime(),
                latest.Aircraft, latest.Label, interpretation.DecodedText,
                interpretation.ProprietaryText, interpretation.UninterpretedText,
                group.OrderByDescending(item => item.ReceivedAt).Take(20).ToArray(), latest);
        }).ToArray();
        StableRecencyOrder.Replace(target, summaries, item => item.Key, item => item.LastReceivedAt);
    }

    private static string DisplayKey(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();


    private static string GetAltitudeBandColor(int? altitudeFeet) => altitudeFeet switch
    {
        null => "#FFFFFF",
        >= 30000 => "#40c8ff",
        >= 10000 => "#67d391",
        _ => "#ffb454"
    };

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "ACARS",
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
            "ACARS",
            () =>
            {
                ClearMessages();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await PluginResetHelper.ConfirmAndResetAllAsync(
            "ACARS",
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

public sealed record AcarsCategorySummary(
    string Key,
    int Count,
    DateTimeOffset LastReceivedAt,
    string Aircraft,
    string Label,
    string DecodedText,
    string ProprietaryText,
    string UninterpretedText,
    IReadOnlyList<AcarsReception> History,
    AcarsReception LatestReception)
{
    public string LatestText => string.Join('\n',
        new[] { DecodedText, ProprietaryText, UninterpretedText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    public string FrequencyDisplay => $"{LatestReception.FrequencyHz / 1_000_000.0:F3} MHz";
    public string SignalQualityDisplay => $"{LatestReception.SignalQuality * 100:F1} %";
    public string ValidationDisplay =>
        $"BCS {(LatestReception.IsBlockCheckValid ? "OK" : "NG")} / " +
        $"Parity {(LatestReception.HasValidOddParity ? "OK" : "NG")}";
}

public sealed class AcarsChannelSelection : ObservableObject
{
    private bool isMonitored;

    public AcarsChannelSelection(AcarsPluginModule.Channel channel, bool isMonitored, bool isPrimary)
    {
        Channel = channel;
        this.isMonitored = isMonitored;
        IsPrimary = isPrimary;
    }

    public AcarsPluginModule.Channel Channel { get; }
    public bool IsPrimary { get; }
    public string DisplayName => Channel.Name;
    internal Func<AcarsChannelSelection, bool, bool>? Changed { get; set; }

    public bool IsMonitored
    {
        get => isMonitored;
        set
        {
            if (value == isMonitored) return;
            if (Changed is not null && !Changed(this, value))
            {
                OnPropertyChanged(nameof(IsMonitored));
                return;
            }
            SetProperty(ref isMonitored, value);
        }
    }
}
