using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.AdsB.Dsp;
using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AdsB.ViewModels;

public sealed partial class AdsBViewModel : ObservableObject
{
    private const int MinimumInputSampleRateHz = 2_000_000;
    private const double MaximumRecentCrcRejectRate = 0.50;
    private const double MaximumRecentCorrectionRate = 0.25;
    private static readonly TimeSpan DiagnosticWindow = TimeSpan.FromSeconds(10);
    private const int MediumAltitudeFeet = 10_000;
    private const int HighAltitudeFeet = 24_000;
    private readonly Dictionary<string, List<GeoMapPoint>> trails = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset At, long Valid, long Rejected, long Corrected,
        long FastCandidates, long PreambleMatches)> diagnosticHistory = new();
    private DateTimeOffset? lastValidFrameAt;
    private long recentValidFrames;
    private long recentRejectedFrames;
    private long recentCorrectedFrames;
    private long recentFastPreambleCandidates;
    private long recentPreambleMatches;
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

    [ObservableProperty] private long _sicRecoveredFrames;
    [ObservableProperty] private long _timingAdjustedFrames;
    [ObservableProperty] private long _correctedFrames;
    [ObservableProperty] private long _fastPreambleCandidates;
    [ObservableProperty] private long _preambleMatches;
    [ObservableProperty] private double _validFramesPerSecond;
    [ObservableProperty] private int _inputSampleRateHz;
    [ObservableProperty] private long _centerFrequencyHz;
    [ObservableProperty] private long _channelOffsetHz;
    [ObservableProperty] private double _noiseFloorDbfs = double.NegativeInfinity;
    [ObservableProperty] private double _noiseFloorDbm = double.NegativeInfinity;
    [ObservableProperty] private double _lastSignalQuality;
    [ObservableProperty] private double _averageSignalQuality;
    [ObservableProperty] private double _maximumSignalQuality;
    [ObservableProperty] private string _recentValidationText = "直近10秒: CRC合格 — / 訂正 — / 不一致 — / 検証合格率 —";
    [ObservableProperty] private string _recentDetectionText = "直近10秒: 高速候補 — / プリアンブル同期 —";
    [ObservableProperty] private string _passbandStatusText = "1090 MHz / 入力待機中";
    [ObservableProperty] private string _status = "停止中";
    private int maximumAircraft = 500;
    private int retentionMinutes = 30;
    private int maximumHistory = 10_000;
    private bool saveRawModeS = true;
    private string historyRecordMode = "both";
    private int maximumTrailPoints = 300;
    private double? receiverLatitude;
    private double? receiverLongitude;

    [ObservableProperty] private string _captureStatus = "IQ録音: 待機";

    public AdsBViewModel()
    {
    }

    public ObservableCollection<AircraftRow> Aircraft { get; } = [];
    public ObservableCollection<AircraftRow> FilteredAircraft { get; } = [];
    public IEnumerable<AircraftRow> RecentAircraft =>
        Aircraft.OrderByDescending(item => item.LastSeen).Take(3);
    public ObservableCollection<AdsBMessageRow> Messages { get; } = [];
    public ObservableCollection<AdsBMessageRow> FilteredMessages { get; } = [];
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _overallStatus = "待機中";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty] private string _overallPhase = "受信処理";
    [ObservableProperty] private string _overallSummary = "1090MHz IQ入力を待機しています";
    [ObservableProperty] private string _overallRecommendation = "確認: SDRソースの接続と周波数設定を確認してください";
    [ObservableProperty] private string _overallLastUpdated = "未更新";
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    [ObservableProperty] private AdsBMessageRow? _selectedTimelineMessage;

    partial void OnSearchTextChanged(string value)
    {
        RefreshFilteredHistory();
        RefreshFilteredAircraft();
    }
    partial void OnValidFramesChanged(long value) => NotifyTotals();
    partial void OnRejectedFramesChanged(long value) => NotifyTotals();
    partial void OnInputSampleRateHzChanged(int value)
    {
        OnPropertyChanged(nameof(InputSampleRateText));
        OnPropertyChanged(nameof(RateConversionText));
        OnPropertyChanged(nameof(RateConversionSummaryText));
        OnPropertyChanged(nameof(IntermediateRateText));
    }
    partial void OnCenterFrequencyHzChanged(long value) => OnPropertyChanged(nameof(CenterFrequencyText));
    partial void OnChannelOffsetHzChanged(long value) => OnPropertyChanged(nameof(ChannelOffsetText));
    partial void OnNoiseFloorDbfsChanged(double value) => OnPropertyChanged(nameof(NoiseFloorText));
    partial void OnNoiseFloorDbmChanged(double value) => OnPropertyChanged(nameof(NoiseFloorText));

    public int FilteredCount => FilteredMessages.Count;
    public int PositionedAircraftCount => MapMarkers.Count;

    public void AddMessage(AdsBMessageRow message)
    {
        Messages.Insert(0, message);
        while (Messages.Count > 1000) Messages.RemoveAt(Messages.Count - 1);
        Aircraft.FirstOrDefault(item => string.Equals(item.Icao, message.Icao, StringComparison.OrdinalIgnoreCase))?.AddHistory(message);
        string filter = SearchText?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter) ||
            (message.Icao?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
            (message.Callsign?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
            (message.Kind?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
            (message.Summary?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
        {
            FilteredMessages.Insert(0, message);
            while (FilteredMessages.Count > 1000) FilteredMessages.RemoveAt(FilteredMessages.Count - 1);
            if (SelectedTimelineMessage is null || !FilteredMessages.Contains(SelectedTimelineMessage))
                SelectedTimelineMessage = FilteredMessages.FirstOrDefault();
            OnPropertyChanged(nameof(FilteredCount));
        }
    }

    private void RefreshFilteredHistory()
    {
        FilteredMessages.Clear();
        string filter = SearchText?.Trim() ?? string.Empty;
        foreach (AdsBMessageRow msg in Messages)
        {
            if (string.IsNullOrEmpty(filter) ||
                (msg.Icao?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.Callsign?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.Kind?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.Summary?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
            {
                FilteredMessages.Add(msg);
            }
        }
        if (SelectedTimelineMessage is null || !FilteredMessages.Contains(SelectedTimelineMessage))
            SelectedTimelineMessage = FilteredMessages.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredCount));
    }

    [RelayCommand]
    private void ClearAircraft()
    {
        SelectedAircraft = null;
        SelectedTimelineMessage = null;
        Aircraft.Clear();
        FilteredAircraft.Clear();
        Messages.Clear();
        FilteredMessages.Clear();
        MapMarkers.Clear();
        trails.Clear();
        OnPropertyChanged(nameof(PositionedAircraftCount));
        RefreshFilteredHistory();
        ClearRequested?.Invoke();
    }

    [RelayCommand]
    private void StartCapture() => CaptureRequested?.Invoke();

    internal Action<int, int, int, int, double?, double?>? SettingsChanged { get; set; }
    internal Action? ClearRequested { get; set; }
    internal Action? CaptureRequested { get; set; }
    internal Action<bool>? SaveRawModeSChanged { get; set; }
    internal Action<string>? HistoryRecordModeChanged { get; set; }

    public long TotalFrames => ValidFrames + RejectedFrames;
    public double AcceptanceRate => TotalFrames == 0 ? 0 : ValidFrames * 100.0 / TotalFrames;
    public string AcceptanceRateText => TotalFrames == 0 ? "—" : $"{AcceptanceRate:F1} %";
    public string InputSampleRateText => InputSampleRateHz <= 0 ? "—" :
        InputSampleRateHz >= 1_000_000
            ? $"{InputSampleRateHz / 1_000_000.0:F2} MS/s"
            : $"{InputSampleRateHz / 1_000.0:F1} kS/s";
    public string CenterFrequencyText => CenterFrequencyHz <= 0 ? "—" : $"{CenterFrequencyHz / 1_000_000.0:F3} MHz";
    public string ChannelOffsetText => CenterFrequencyHz <= 0 ? "—" : $"{ChannelOffsetHz / 1_000.0:+0.0;-0.0;0.0} kHz";
    public string RateConversionSummaryText => InputSampleRateHz <= 0 ? "—" :
        SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
            InputSampleRateHz != ModeSReceiver.DemodulationSampleRateHz,
            "標準チャネル／内部DSP");
    public string RateConversionText => RateConversionSummaryText;
    public string IntermediateRateText => SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
        InputSampleRateHz, 0, ModeSReceiver.DemodulationSampleRateHz);
    public string NoiseFloorText => double.IsFinite(NoiseFloorDbm)
        ? $"{NoiseFloorDbm:F1} dBm"
        : (double.IsFinite(NoiseFloorDbfs) ? $"{NoiseFloorDbfs:F1} dBFS" : "—");
    public string SettingStatus => $"1090.000 MHz / {MaximumAircraft:N0}機 / {RetentionMinutes}分 / 航跡{MaximumTrailPoints}点 / " +
        (ReceiverLatitude is null || ReceiverLongitude is null ? "位置未設定" : $"{ReceiverLatitude:F4}, {ReceiverLongitude:F4}");
    public int MaximumAircraft { get => maximumAircraft; set { maximumAircraft = Math.Clamp(value, 50, 5000); SettingsUpdated(); } }
    public int RetentionMinutes { get => retentionMinutes; set { retentionMinutes = Math.Clamp(value, 1, 240); SettingsUpdated(); } }
    public int MaximumHistory { get => maximumHistory; set { maximumHistory = Math.Clamp(value, 100, 1_000_000); SettingsUpdated(); } }
    public bool SaveRawModeS { get => saveRawModeS; set { if (saveRawModeS == value) return; saveRawModeS=value; SaveRawModeSChanged?.Invoke(value); OnPropertyChanged(); } }
    public string HistoryRecordMode { get => historyRecordMode; set { string n=value is "snapshot" or "message" or "both" ? value : "both"; if(historyRecordMode==n)return; historyRecordMode=n; HistoryRecordModeChanged?.Invoke(n); OnPropertyChanged(); } }
    public int MaximumTrailPoints
    {
        get => maximumTrailPoints;
        set
        {
            maximumTrailPoints = Math.Clamp(value, 10, 1000);
            TrimTrails();
            SettingsUpdated();
        }
    }
    public double? ReceiverLatitude { get => receiverLatitude; set { receiverLatitude = value is >= -90 and <= 90 ? value : null; SettingsUpdated(); } }
    public double? ReceiverLongitude { get => receiverLongitude; set { receiverLongitude = value is >= -180 and <= 180 ? value : null; SettingsUpdated(); } }
    internal void SynchronizeSettings(int aircraftLimit, int retention, int historyLimit, int trailPoints,
        double? latitude, double? longitude, bool saveRaw = true, string recordMode = "both")
    {
        maximumAircraft = aircraftLimit;
        retentionMinutes = retention;
        maximumHistory = historyLimit;
        saveRawModeS = saveRaw;
        historyRecordMode = recordMode;
        maximumTrailPoints = trailPoints;
        receiverLatitude = latitude;
        receiverLongitude = longitude;
        OnPropertyChanged(nameof(MaximumAircraft));
        OnPropertyChanged(nameof(RetentionMinutes));
        OnPropertyChanged(nameof(MaximumHistory));
        OnPropertyChanged(nameof(SaveRawModeS)); OnPropertyChanged(nameof(HistoryRecordMode));
        OnPropertyChanged(nameof(MaximumTrailPoints));
        OnPropertyChanged(nameof(ReceiverLatitude));
        OnPropertyChanged(nameof(ReceiverLongitude));
        OnPropertyChanged(nameof(SettingStatus));
    }

    public void Apply(IReadOnlyCollection<AircraftState> states, long valid, long rejected,
        long sicRecovered = 0, long timingAdjusted = 0,
        ModeSReceiver.DiagnosticsSnapshot? diagnostics = null,
        float? noiseFloorDbm = null)
    {
        var activeAircraft = states.Select(state => state.Icao).ToHashSet(StringComparer.Ordinal);
        foreach (string staleIcao in trails.Keys.Where(icao => !activeAircraft.Contains(icao)).ToArray())
            trails.Remove(staleIcao);
        var rows = Aircraft.ToDictionary(row => row.Icao, StringComparer.Ordinal);
        foreach (AircraftState state in states.OrderByDescending(item => item.LastSeen))
        {
            if (!rows.TryGetValue(state.Icao, out AircraftRow? row))
            {
                row = new AircraftRow(state.Icao);
                Aircraft.Add(row);
            }
            row.Apply(state);
            row.ReplaceHistory(Messages.Where(message =>
                string.Equals(message.Icao, row.Icao, StringComparison.OrdinalIgnoreCase)).Take(20));
            rows.Remove(state.Icao);
        }
        foreach (AircraftRow stale in rows.Values)
        {
            if (ReferenceEquals(SelectedAircraft, stale)) SelectedAircraft = null;
            Aircraft.Remove(stale);
        }
        StableRecencyOrder.Reorder(Aircraft, item => item.LastSeen);
        RefreshFilteredAircraft();
        OnPropertyChanged(nameof(RecentAircraft));
        MapMarkers.Clear();
        foreach (AircraftRow row in Aircraft.Where(item => item.Latitude is not null && item.Longitude is not null))
        {
            string color = row.HasEmergency ? "#FFFF5050" : GetAltitudeBandColor(row.AltitudeFeet);
            List<GeoMapPoint> trail = GetUpdatedTrail(row.Icao, row.Latitude!.Value, row.Longitude!.Value, color);
            MapMarkers.Add(new(row.Icao, row.Latitude!.Value, row.Longitude!.Value,
                string.IsNullOrWhiteSpace(row.Callsign) ? row.Icao : row.Callsign,
                $"ICAO {row.Icao} / {row.AltitudeFeet:N0} ft / {row.SpeedKnots:F0} kt / " +
                $"針路 {row.TrackDegrees:F0}° / 昇降 {row.VerticalRate:+#;-#;0} ft/min",
                color, row.TrackDegrees, trail.ToArray(), "aircraft", true));
        }
        OnPropertyChanged(nameof(PositionedAircraftCount));
        ValidFrames = valid;
        RejectedFrames = rejected;
        SicRecoveredFrames = sicRecovered;
        TimingAdjustedFrames = timingAdjusted;
        if (diagnostics is not null) UpdateDiagnostics(diagnostics, noiseFloorDbm);
    }
    private List<GeoMapPoint> GetUpdatedTrail(string icao, double latitude, double longitude, string color)
    {
        if (!trails.TryGetValue(icao, out List<GeoMapPoint>? trail))
            trails[icao] = trail = [];
        if (trail.Count == 0)
        {
            trail.Add(new(latitude, longitude, color));
            return trail;
        }

        GeoMapPoint previous = trail[^1];
        if (!string.Equals(previous.Color, color, StringComparison.OrdinalIgnoreCase))
            trail.Add(new(previous.Latitude, previous.Longitude, color));
        if (Math.Abs(previous.Latitude - latitude) > 0.00001 ||
            Math.Abs(previous.Longitude - longitude) > 0.00001)
        {
            trail.Add(new(latitude, longitude, color));
        }
        if (trail.Count > MaximumTrailPoints) trail.RemoveRange(0, trail.Count - MaximumTrailPoints);
        return trail;
    }

    private void RefreshFilteredAircraft()
    {
        FilteredAircraft.Clear();
        string filter = SearchText?.Trim() ?? string.Empty;
        foreach (AircraftRow row in Aircraft)
        {
            if (filter.Length == 0 || $"{row.Icao} {row.Callsign}".Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredAircraft.Add(row);
        }
        if (SelectedAircraft is null || !FilteredAircraft.Contains(SelectedAircraft))
            SelectedAircraft = FilteredAircraft.FirstOrDefault();
    }

    private void TrimTrails()
    {
        foreach (List<GeoMapPoint> trail in trails.Values)
            if (trail.Count > maximumTrailPoints)
                trail.RemoveRange(0, trail.Count - maximumTrailPoints);
        for (int index = 0; index < MapMarkers.Count; index++)
        {
            GeoMapMarker marker = MapMarkers[index];
            if (trails.TryGetValue(marker.Id, out List<GeoMapPoint>? trail))
                MapMarkers[index] = marker with { Trail = trail.ToArray() };
        }
    }

    private void UpdateDiagnostics(ModeSReceiver.DiagnosticsSnapshot value, float? noiseFloorDbm = null)
    {
        CorrectedFrames = value.CorrectedFrameCount;
        ValidFramesPerSecond = value.ValidFramesPerSecond;
        InputSampleRateHz = value.InputSampleRateHz;
        CenterFrequencyHz = value.CenterFrequencyHz;
        ChannelOffsetHz = value.ChannelOffsetHz;
        NoiseFloorDbfs = value.NoiseFloorDbfs;
        NoiseFloorDbm = noiseFloorDbm is { } dbm && float.IsFinite(dbm) ? dbm : double.NegativeInfinity;
        LastSignalQuality = value.LastSignalQuality;
        AverageSignalQuality = value.AverageSignalQuality;
        MaximumSignalQuality = value.MaximumSignalQuality;
        FastPreambleCandidates = value.FastPreambleCandidateCount;
        PreambleMatches = value.PreambleMatchCount;

        DateTimeOffset measuredAt = DateTimeOffset.Now;
        UpdateDiagnosticWindow(measuredAt);
        long recentTotal = recentValidFrames + recentRejectedFrames;
        string recentRate = recentTotal == 0 ? "—" : $"{recentValidFrames * 100.0 / recentTotal:F1} %";
        RecentValidationText = $"直近10秒: CRC合格 {recentValidFrames:N0} / 訂正 {recentCorrectedFrames:N0} / 不一致 {recentRejectedFrames:N0} / 検証合格率 {recentRate}";
        RecentDetectionText = $"直近10秒: 高速候補 {recentFastPreambleCandidates:N0} / プリアンブル同期 {recentPreambleMatches:N0}";
        bool isInPassband = InputSampleRateHz > 0 && Math.Abs(ChannelOffsetHz) <= InputSampleRateHz * 0.5;
        PassbandStatusText = InputSampleRateHz <= 0 ? "1090 MHz / 入力待機中" :
            $"1090 MHz / {(isInPassband ? "帯域内" : "帯域外")} / 必要2.0 MS/s以上";

        OverallLastUpdated = measuredAt.LocalDateTime.ToString("HH:mm:ss");
        if (InputSampleRateHz < MinimumInputSampleRateHz)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "入力";
            OverallSummary = "IQサンプルレートが2 MS/s未満です。ADS-B復調には不十分です";
            OverallRecommendation = "確認: SDRソースのサンプルレートを2.0 MS/s以上(推奨2.4 MS/s)に設定してください";
        }
        else if (!isInPassband)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "選局";
            OverallSummary = "1090 MHzが実効入力帯域外です";
            OverallRecommendation = "確認: SDR中心周波数と入力帯域を1090 MHzが含まれるように設定してください";
        }
        else if (recentRejectedFrames >= 5 && recentRejectedFrames / (double)Math.Max(1, recentTotal) > MaximumRecentCrcRejectRate)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "検証";
            OverallSummary = "CRCエラーのフレームが多く検出されています";
            OverallRecommendation = "確認: 信号レベル、アンテナ利得、ノイズ源の有無を確認してください";
        }
        else if (recentCorrectedFrames >= 3 && recentCorrectedFrames / (double)Math.Max(1, recentValidFrames) > MaximumRecentCorrectionRate)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "検証";
            OverallSummary = "CRC 1-bit訂正の割合が直近10秒で25%を超えています";
            OverallRecommendation = "確認: 受信レベル、ノイズ、アンテナ系統を確認してください";
        }
        else if (recentValidFrames > 0 && lastValidFrameAt is not null && measuredAt - lastValidFrameAt <= DiagnosticWindow)
        {
            OverallStatus = "正常";
            OverallStatusKind = OverallStatusKind.Success;
            OverallPhase = "受信中";
            OverallSummary = "ADS-Bフレームを正常に受信・復号しています";
            OverallRecommendation = "確認: 正常に動作しています";
        }
        else
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "受信処理";
            OverallSummary = "1090 MHz を監視中ですが、有効なフレームは未検出です";
            OverallRecommendation = "確認: 周囲の航空機トラフィックの有無を確認してください";
        }
    }

    private void UpdateDiagnosticWindow(DateTimeOffset measuredAt)
    {
        if (diagnosticHistory.Count > 0)
        {
            var previous = diagnosticHistory.Last();
            if (ValidFrames < previous.Valid || RejectedFrames < previous.Rejected || CorrectedFrames < previous.Corrected ||
                FastPreambleCandidates < previous.FastCandidates || PreambleMatches < previous.PreambleMatches)
                diagnosticHistory.Clear();
            else if (ValidFrames > previous.Valid)
                lastValidFrameAt = measuredAt;
        }
        diagnosticHistory.Enqueue((measuredAt, ValidFrames, RejectedFrames, CorrectedFrames,
            FastPreambleCandidates, PreambleMatches));
        while (diagnosticHistory.Count > 1 && measuredAt - diagnosticHistory.Peek().At > DiagnosticWindow)
            diagnosticHistory.Dequeue();
        var baseline = diagnosticHistory.Peek();
        recentValidFrames = Math.Max(0, ValidFrames - baseline.Valid);
        recentRejectedFrames = Math.Max(0, RejectedFrames - baseline.Rejected);
        recentCorrectedFrames = Math.Max(0, CorrectedFrames - baseline.Corrected);
        recentFastPreambleCandidates = Math.Max(0, FastPreambleCandidates - baseline.FastCandidates);
        recentPreambleMatches = Math.Max(0, PreambleMatches - baseline.PreambleMatches);
    }

    private static string GetAltitudeBandColor(int? altitudeFeet) => altitudeFeet switch
    {
        >= HighAltitudeFeet => "#40c8ff",
        >= MediumAltitudeFeet => "#67d391",
        _ => "#ffb454"
    };

    [ObservableProperty] private AircraftRow? _selectedAircraft;

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(TotalFrames));
        OnPropertyChanged(nameof(AcceptanceRate));
    }
    private void SettingsUpdated()
    {
        OnPropertyChanged(nameof(MaximumAircraft)); OnPropertyChanged(nameof(RetentionMinutes));
        OnPropertyChanged(nameof(MaximumHistory));
        OnPropertyChanged(nameof(MaximumTrailPoints));
        OnPropertyChanged(nameof(ReceiverLatitude)); OnPropertyChanged(nameof(ReceiverLongitude));
        OnPropertyChanged(nameof(SettingStatus));
        SettingsChanged?.Invoke(maximumAircraft, retentionMinutes, maximumHistory, maximumTrailPoints,
            receiverLatitude, receiverLongitude);
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await PluginResetHelper.ConfirmAndResetSettingsAsync(
            "ADS-B",
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
            "ADS-B",
            () =>
            {
                ClearAircraft();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await PluginResetHelper.ConfirmAndResetAllAsync(
            "ADS-B",
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
                ClearAircraft();
            });
    }
}

public sealed partial class AircraftRow : ObservableObject
{
    private const int HistoryLimit = 20;
    [ObservableProperty] private string _callsign = string.Empty;
    [ObservableProperty] private int? _altitudeFeet;
    [ObservableProperty] private double? _speedKnots;
    [ObservableProperty] private double? _trackDegrees;
    [ObservableProperty] private int? _verticalRate;
    [ObservableProperty] private double? _latitude;
    [ObservableProperty] private double? _longitude;
    [ObservableProperty] private DateTimeOffset _lastSeen;
    [ObservableProperty] private long _messages;
    [ObservableProperty] private int? _selectedAltitudeFeet;
    [ObservableProperty] private double? _selectedHeadingDegrees;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmergency))]
    private string _emergencyState = string.Empty;
    [ObservableProperty] private string _squawk = string.Empty;
    [ObservableProperty] private int? _adsBVersion;
    [ObservableProperty] private int? _nacP;
    [ObservableProperty] private int? _sil;
    [ObservableProperty] private string _altitudeSource = string.Empty;

    public AircraftRow(string icao) => Icao = icao;
    public string Icao { get; }
    public ObservableCollection<AdsBMessageRow> History { get; } = [];
    public bool HasEmergency => !string.IsNullOrWhiteSpace(EmergencyState) && EmergencyState != "reserved";

    public void AddHistory(AdsBMessageRow message)
    {
        History.Insert(0, message);
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
    }

    public void ReplaceHistory(IEnumerable<AdsBMessageRow> messages)
    {
        History.Clear();
        foreach (AdsBMessageRow message in messages.Reverse()) AddHistory(message);
    }

    public void Apply(AircraftState value)
    {
        Callsign = value.Callsign;
        AltitudeFeet = value.AltitudeFeet;
        AltitudeSource = value.BarometricAltitudeFeet is not null ? "BARO" :
            value.GeometricAltitudeFeet is not null ? "GNSS" : string.Empty;
        SpeedKnots = value.GroundSpeedKnots ?? value.AirspeedKnots;
        TrackDegrees = value.TrackDegrees ?? value.HeadingDegrees;
        VerticalRate = value.VerticalRateFeetPerMinute;
        Latitude = value.Latitude;
        Longitude = value.Longitude;
        LastSeen = value.LastSeen.ToLocalTime();
        Messages = value.MessageCount;
        SelectedAltitudeFeet = value.SelectedAltitudeFeet;
        SelectedHeadingDegrees = value.SelectedHeadingDegrees;
        EmergencyState = value.EmergencyState == "none" ? string.Empty : value.EmergencyState;
        Squawk = value.Squawk;
        AdsBVersion = value.AdsBVersion;
        NacP = value.NacP;
        Sil = value.Sil;
    }
}
