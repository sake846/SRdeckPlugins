using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ft8.ViewModels;

public sealed partial class Ft8ViewModel : ObservableObject
{
    private static readonly TimeSpan MaximumSlotBoundaryError = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActiveMapStationWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentMapStationWindow = TimeSpan.FromMinutes(30);
    private const string ActiveMapStationColor = "#4dd0e1";
    private const string RecentMapStationColor = "#26a69a";
    private const string DormantMapStationColor = "#b8b8b8";
    private long lastDiagnosticsValidMessages;
    private long diagnosticsValidMessagesAtClear;
    private DateTimeOffset? lastDiagnosticsDecodedSlotStart;
    private DateTimeOffset? decodedSlotStartAtClear;
    private Ft8DecoderDiagnostics? previousDiagnostics;
    private long diagnosticsCandidatesAtReset;
    private long diagnosticsLdpcRejectedAtReset;
    private long diagnosticsCrcRejectedAtReset;
    [ObservableProperty] private string _status = "未初期化";
    [ObservableProperty] private OverallStatusKind _overallStatusKind = OverallStatusKind.Idle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialFrequencyText))]
    private Ft8Band? _selectedBand;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bands))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    [NotifyPropertyChangedFor(nameof(FecName))]
    private WeakSignalMode _selectedMode = WeakSignalMode.FT8;
    private bool suppressSelection;
    private bool suppressNearbyBandSelection;

    [ObservableProperty] private int _maximumHistory = 10_000;
    [ObservableProperty] private int _maximumStations = 500;
    [ObservableProperty] private int _retentionMinutes = 30;
    [ObservableProperty] private bool _savePayload = true;
    [ObservableProperty] private bool _splitHistoryByBand;
    [ObservableProperty] private int _mapMarkerLimit = 100;
    [ObservableProperty] private int _minimumSyncScore = 4;
    [ObservableProperty] private int _maximumCandidates = 240;
    [ObservableProperty] private int _ldpcIterations = 35;
    [ObservableProperty] private bool _monitorAudioEnabled = true;
    [ObservableProperty] private int _monitorAudioVolume = 100;
    [ObservableProperty] private string _captureStatus = "IQ録音: 待機";
    [ObservableProperty] private long _decodedCount;
    [ObservableProperty] private int _stationsThisSlot;
    [ObservableProperty] private int _lastSlotMessages;
    [ObservableProperty] private string _lastSlotTime = "—";
    [ObservableProperty] private string _lastSlotSnrText = "—";
    [ObservableProperty] private string _lastSlotTimeOffsetText = "—";
    [ObservableProperty] private string _inputRate = "—";
    [ObservableProperty] private string _passbandStatusText = "—";
    [ObservableProperty] private string _rateConversionText = "—";
    [ObservableProperty] private string _ratePathText = "—";
    [ObservableProperty] private string _channelLevelText = "—";
    [ObservableProperty] private string _buffered = "0.0 s";
    [ObservableProperty] private string _decodeTime = "—";
    [ObservableProperty] private string _candidates = "0";
    [ObservableProperty] private string _rejected = "0";
    [ObservableProperty] private string _overallStatusText = "待機中";
    [ObservableProperty] private string _slotTimingText = "スロット境界待ち";
    [ObservableProperty] private string _recentDetectionText = "直近2スロット: sync候補 — / 調査候補 —";
    [ObservableProperty] private string _recentValidationText = "直近2スロット: LDPC — / CRC — / 有効 —";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBandSelectorEnabled))]
    private bool _isBandChangeInProgress;
    [ObservableProperty] private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    [ObservableProperty] private string _overallPhase = "入力";
    [ObservableProperty] private string _overallSummary = "IQ入力を待機しています";
    [ObservableProperty] private string _overallRecommendation = "確認: SDRソースとFT8バンドの選局を確認してください";
    [ObservableProperty] private string _overallLastUpdated = "未更新";
    [ObservableProperty] private string _stationSearchText = string.Empty;
    [ObservableProperty] private string _historySearchText = string.Empty;
    [ObservableProperty] private Ft8StationSummary? _selectedStation;
    [ObservableProperty] private Ft8Reception? _selectedTimelineReception;
    private readonly Dictionary<string, Ft8MapStation> mapStationsById = new(StringComparer.Ordinal);

    public Ft8ViewModel()
    {
    }

    public IReadOnlyList<WeakSignalMode> Modes { get; } = Enum.GetValues<WeakSignalMode>();
    public IReadOnlyList<Ft8Band> Bands => Ft8PluginModule.Bands
        .Where(item => item.Mode == SelectedMode).ToArray();
    public ObservableCollection<Ft8Reception> History { get; } = [];
    public ObservableCollection<Ft8Reception> FilteredHistory { get; } = [];
    public ObservableCollection<Ft8StationSummary> Stations { get; } = [];
    public ObservableCollection<Ft8StationSummary> FilteredStations { get; } = [];
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = [];
    public ObservableCollection<Ft8NearbyBandOption> NearbyBands { get; } = [];


    [RelayCommand]
    private void StartCapture() => CaptureRequested?.Invoke();

    public Action<string>? BandSelectionRequested { get; set; }
    internal Action<int>? MaximumStationsChanged { get; set; }
    internal Action<int>? RetentionMinutesChanged { get; set; }
    internal Action<IReadOnlyList<string>>? NearbyBandSelectionChanged { get; set; }
    public Action<int, int, int, int, bool, int>? DecoderSettingsChanged { get; set; }
    internal Action<bool>? SavePayloadChanged { get; set; }
    internal Action<bool>? SplitHistoryByBandChanged { get; set; }
    internal Action<int>? MapMarkerLimitChanged { get; set; }
    public Action? ClearRequested { get; set; }
    internal Action? CaptureRequested { get; set; }

    partial void OnSelectedBandChanged(Ft8Band? value)
    {
        if (!suppressSelection && value is not null)
            BandSelectionRequested?.Invoke(value.Id);
    }
    partial void OnSelectedModeChanged(WeakSignalMode value)
    {
        if (suppressSelection) return;
        Ft8Band? matching = Ft8PluginModule.Bands.FirstOrDefault(item =>
            item.Mode == value && item.Band == SelectedBand?.Band)
            ?? Ft8PluginModule.Bands.FirstOrDefault(item =>
                item.Mode == value && item.Band.StartsWith("20 m", StringComparison.Ordinal))
            ?? Ft8PluginModule.Bands.FirstOrDefault(item => item.Mode == value);
        if (matching is not null) SelectedBand = matching;
    }
    partial void OnMaximumHistoryChanged(int value)
    {
        int normalized = Math.Clamp(value, 100, 20_000);
        if (value != normalized)
        {
            MaximumHistory = normalized;
            return;
        }
        while (History.Count > MaximumHistory) History.RemoveAt(History.Count - 1);
        RefreshFilteredHistory();
        SettingsUpdated();
    }
    partial void OnMaximumStationsChanged(int value)
    {
        int normalized = Math.Clamp(value, 50, 5000);
        MaximumStationsChanged?.Invoke(normalized);
        RebuildStationSummaries();
    }
    partial void OnRetentionMinutesChanged(int value)
    {
        int normalized = Math.Clamp(value, 1, 240);
        RetentionMinutesChanged?.Invoke(normalized);
        RebuildStationSummaries();
    }
    partial void OnSavePayloadChanged(bool value) => SavePayloadChanged?.Invoke(value);
    partial void OnSplitHistoryByBandChanged(bool value) => SplitHistoryByBandChanged?.Invoke(value);
    partial void OnMapMarkerLimitChanged(int value) => MapMarkerLimitChanged?.Invoke(Math.Clamp(value, 50, 10_000));
    partial void OnMinimumSyncScoreChanged(int value) => SettingsUpdated();
    partial void OnMaximumCandidatesChanged(int value) => SettingsUpdated();
    partial void OnLdpcIterationsChanged(int value) => SettingsUpdated();
    partial void OnMonitorAudioEnabledChanged(bool value) => SettingsUpdated();
    partial void OnMonitorAudioVolumeChanged(int value) => SettingsUpdated();
    partial void OnStationSearchTextChanged(string value) => RefreshFilteredStations();
    partial void OnHistorySearchTextChanged(string value) => RefreshFilteredHistory();

    public string DialFrequencyText => SelectedBand is null ? "—" :
        $"{SelectedBand.DialFrequencyHz / 1_000_000.0:F6} MHz USB";
    public string ModeDescription => SelectedMode switch
    {
        WeakSignalMode.FT4 => "7.5秒スロット / 4-GFSK / LDPC(174,91)",
        WeakSignalMode.JT65 => "60秒スロット / JT65A 65-FSK / RS(63,12)",
        _ => "15秒スロット / 8-GFSK / LDPC(174,91)"
    };
    public string FecName => SelectedMode == WeakSignalMode.JT65 ? "RS" : "LDPC";
    public bool IsBandSelectorEnabled => !IsBandChangeInProgress;
    public bool HasNearbyBands => NearbyBands.Count > 0;
    public int FilteredStationCount => FilteredStations.Count;

    public void Configure(Ft8Settings settings, Ft8Band band)
    {
        MaximumHistory = settings.MaximumHistory;
        MaximumStations = settings.MaximumStations;
        RetentionMinutes = settings.RetentionMinutes;
        SavePayload = settings.SavePayload;
        SplitHistoryByBand = settings.SplitHistoryByBand;
        MapMarkerLimit = settings.MapMarkerLimit;
        MinimumSyncScore = settings.MinimumSyncScore;
        MaximumCandidates = settings.MaximumCandidates;
        LdpcIterations = settings.LdpcIterations;
        MonitorAudioEnabled = settings.MonitorAudioEnabled;
        MonitorAudioVolume = settings.MonitorAudioVolume;
        suppressSelection = true;
        SelectedMode = band.Mode;
        SelectedBand = band;
        suppressSelection = false;
        OnPropertyChanged(string.Empty);
    }

    public void RollbackBand(Ft8Band band)
    {
        suppressSelection = true;
        SelectedMode = band.Mode;
        SelectedBand = band;
        suppressSelection = false;
        OnPropertyChanged(nameof(SelectedBand));
        OnPropertyChanged(nameof(DialFrequencyText));
    }

    public void SetBandChangeInProgress(bool value)
    {
        IsBandChangeInProgress = value;
    }

    public void AddBatch(IReadOnlyList<Ft8Reception> messages)
    {
        if (messages.Count == 0) return;
        LastSlotMessages = messages.Count;
        LastSlotTime = messages[0].SlotStart.ToLocalTime().ToString("HH:mm:ss");
        StationsThisSlot = messages.Select(item => item.FromCall)
            .Where(value => value.Length > 0 && value != "<...>")
            .Distinct(StringComparer.Ordinal).Count();
        int[] sortedSnr = messages.Select(item => item.SnrDb).OrderBy(value => value).ToArray();
        double medianSnr = sortedSnr.Length % 2 == 0
            ? (sortedSnr[sortedSnr.Length / 2 - 1] + sortedSnr[sortedSnr.Length / 2]) / 2.0
            : sortedSnr[sortedSnr.Length / 2];
        LastSlotSnrText = $"{medianSnr:+0.0;-0.0;0.0} dB";
        LastSlotTimeOffsetText = $"{messages.Max(item => Math.Abs(item.TimeOffsetSeconds)):F2} s";
        DecodedCount += messages.Count;
        foreach (Ft8Reception message in messages.OrderByDescending(item => item.AudioFrequencyHz))
        {
            History.Insert(0, message);
            AddMapMarker(message);
        }
        while (History.Count > MaximumHistory) History.RemoveAt(History.Count - 1);
        RefreshFilteredHistory();
        RebuildStationSummaries();
        OverallStatusText = "正常";
        OverallStatusKind = OverallStatusKind.Success;
    }

    public void ApplyDiagnostics(Ft8DecoderDiagnostics diagnostics, float? signalLevelDbm = null)
    {
        DateTimeOffset measuredAt = DateTimeOffset.UtcNow;
        RefreshMapStationActivity(measuredAt);
        lastDiagnosticsValidMessages = diagnostics.ValidMessages;
        lastDiagnosticsDecodedSlotStart = diagnostics.LastDecodedSlotStart;
        DecodedCount = Math.Max(DecodedCount,
            Math.Max(0, diagnostics.ValidMessages - diagnosticsValidMessagesAtClear));
        if (diagnostics.LastDecodedSlotStart is { } decodedSlotStart &&
            decodedSlotStart != decodedSlotStartAtClear)
        {
            LastSlotMessages = diagnostics.LastSlotValidMessages;
            LastSlotTime = decodedSlotStart.ToLocalTime().ToString("HH:mm:ss");
        }
        InputRate = diagnostics.InputSampleRateHz <= 0 ? "—" :
            $"{diagnostics.InputSampleRateHz / 1000.0:N1} kS/s";
        PassbandStatusText = diagnostics.InputSampleRateHz <= 0
            ? "入力待機中"
            : diagnostics.InputSampleRateHz >= Dsp.Ft8Receiver.OutputSampleRateHz
                ? "帯域内 / 音声 200–3000 Hz"
                : $"帯域不足 / 必要 {Dsp.Ft8Receiver.OutputSampleRateHz / 1_000.0:F1} kS/s以上";
        RateConversionText = diagnostics.InputSampleRateHz <= 0
            ? "—"
            : SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                diagnostics.InputSampleRateHz != Dsp.Ft8Receiver.OutputSampleRateHz,
                diagnostics.UsesHostChannelRateConversion ? "標準チャネル" : "プラグイン内部");
        RatePathText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
            diagnostics.InputSampleRateHz,
            diagnostics.IntermediateSampleRateHz,
            Dsp.Ft8Receiver.OutputSampleRateHz);
        ChannelLevelText = signalLevelDbm is { } dbm && float.IsFinite(dbm)
            ? $"{dbm:F1} dBm"
            : (double.IsFinite(diagnostics.ChannelLevelDbfs) ? $"{diagnostics.ChannelLevelDbfs:F1} dBFS" : "—");
        Buffered = $"{diagnostics.BufferedSamples / (double)Dsp.Ft8Decoder.SampleRateHz:F1} s";
        DecodeTime = diagnostics.SlotsProcessed == 0 ? "—" :
            $"{diagnostics.LastDecodeDuration.TotalMilliseconds:N0} ms";
        Candidates = diagnostics.CandidatesExamined.ToString("N0");
        Rejected = (diagnostics.LdpcRejected + diagnostics.CrcRejected).ToString("N0");
        long recentCandidates = Math.Max(0, diagnostics.CandidatesExamined - diagnosticsCandidatesAtReset);
        long recentLdpcRejected = Math.Max(0, diagnostics.LdpcRejected - diagnosticsLdpcRejectedAtReset);
        long recentCrcRejected = Math.Max(0, diagnostics.CrcRejected - diagnosticsCrcRejectedAtReset);
        if (previousDiagnostics is { } previous && diagnostics.LastSlotStart != previous.LastSlotStart)
        {
            recentCandidates = Math.Max(0, diagnostics.CandidatesExamined - previous.CandidatesExamined);
            recentLdpcRejected = Math.Max(0, diagnostics.LdpcRejected - previous.LdpcRejected);
            recentCrcRejected = Math.Max(0, diagnostics.CrcRejected - previous.CrcRejected);
        }
        long ldpcPassed = Math.Max(0, recentCandidates - recentLdpcRejected);
        long crcPassed = diagnostics.LastSlotValidMessages;
        RecentDetectionText = $"直近スロット: sync/調査候補 {recentCandidates:N0} / 上限 {MaximumCandidates:N0}";
        RecentValidationText = $"直近スロット: LDPC通過 {ldpcPassed:N0} / 棄却 {recentLdpcRejected:N0} / CRC合格 {crcPassed:N0} / 不一致 {recentCrcRejected:N0}";

        double slotSeconds = SelectedMode switch
        {
            WeakSignalMode.FT4 => 7.5,
            WeakSignalMode.JT65 => 60.0,
            _ => 15.0
        };
        double boundaryErrorSeconds = diagnostics.LastSlotStart is { } slotStart
            ? Math.Min(slotStart.TimeOfDay.TotalSeconds % slotSeconds,
                slotSeconds - slotStart.TimeOfDay.TotalSeconds % slotSeconds)
            : double.NaN;
        bool clockAligned = double.IsNaN(boundaryErrorSeconds) || boundaryErrorSeconds <= MaximumSlotBoundaryError.TotalSeconds;
        SlotTimingText = diagnostics.LastSlotStart is null ? "スロット境界待ち" :
            $"期待{slotSeconds:g}秒境界 / 最新 {diagnostics.LastSlotStart.Value.ToLocalTime():HH:mm:ss} / 境界ずれ {boundaryErrorSeconds:F2} s";
        bool decodedWithinTwoSlots = diagnostics.LastDecodedSlotStart is { } decoded && diagnostics.LastSlotStart is { } latest &&
            latest >= decoded && latest - decoded <= TimeSpan.FromSeconds(slotSeconds * 2);

        OverallLastUpdated = measuredAt.LocalDateTime.ToString("HH:mm:ss");
        if (diagnostics.InputSampleRateHz <= 0)
        {
            OverallStatusText = "入力待ち";
            OverallStatusKind = OverallStatusKind.Idle;
            OverallPhase = "入力";
            OverallSummary = "IQ入力を待機しています";
            OverallRecommendation = $"確認: SDRソースと{SelectedMode}バンドの選局を確認してください";
        }
        else if (!clockAligned)
        {
            OverallStatusText = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "時刻同期";
            OverallSummary = $"{SelectedMode}スロット境界のずれが許容値{MaximumSlotBoundaryError.TotalSeconds:F0}秒を超えています";
            OverallRecommendation = "確認: OSの時刻同期を有効にし、NTP同期状態を確認してください";
        }
        else if (diagnostics.SlotsProcessed == 0)
        {
            OverallStatusText = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "同期";
            OverallSummary = $"{SelectedMode}スロットを蓄積し、復号開始時刻を待っています";
            OverallRecommendation = $"確認: PC時刻同期と選択バンドを確認し、{slotSeconds:g}秒スロットを待ってください";
        }
        else if (decodedWithinTwoSlots && diagnostics.LastSlotValidMessages > 0)
        {
            OverallStatusText = "正常";
            OverallStatusKind = OverallStatusKind.Success;
            OverallPhase = "検証・復号";
            OverallSummary = $"直近2スロットで{SelectedMode}メッセージを正常に復号しています";
            OverallRecommendation = "確認: 受信処理は正常に動作しています";
        }
        else if (recentCandidates == 0)
        {
            OverallStatusText = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "検出・同期";
            OverallSummary = "時刻同期と入力は正常です。直近スロットのsync候補を監視しています";
            OverallRecommendation = "確認: 探索帯域、アンテナ、選択バンドの運用状況を確認してください";
        }
        else if (ldpcPassed == 0)
        {
            OverallStatusText = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = FecName;
            OverallSummary = $"直近スロットの候補がすべて{FecName}で棄却されました";
            OverallRecommendation = "確認: S/N、時刻ずれ、sync score、候補上限を確認してください";
        }
        else if (crcPassed == 0 && recentCrcRejected > 0)
        {
            OverallStatusText = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "CRC";
            OverallSummary = "LDPC通過後の候補がCRCで全て棄却されました";
            OverallRecommendation = "確認: 信号品質、LDPC iteration、時刻同期を確認してください";
        }
        else
        {
            OverallStatusText = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "検証・復号";
            OverallSummary = "FT8スロットを継続して同期・復号しています";
            OverallRecommendation = "確認: 候補数、棄却数、直前の復号時間を必要に応じて展開してください";
        }
        previousDiagnostics = diagnostics;
    }

    [RelayCommand]
    public void Clear()
    {
        SelectedStation = null;
        SelectedTimelineReception = null;
        History.Clear();
        FilteredHistory.Clear();
        Stations.Clear();
        FilteredStations.Clear();
        OnPropertyChanged(nameof(FilteredStationCount));
        MapMarkers.Clear();
        mapStationsById.Clear();
        diagnosticsValidMessagesAtClear = lastDiagnosticsValidMessages;
        decodedSlotStartAtClear = lastDiagnosticsDecodedSlotStart;
        DecodedCount = 0;
        StationsThisSlot = 0;
        LastSlotMessages = 0;
        LastSlotTime = "—";
        LastSlotSnrText = "—";
        LastSlotTimeOffsetText = "—";
        ClearRequested?.Invoke();
    }

    public void UpdateNearbyBands(IReadOnlyList<Ft8Band> bands,
        IReadOnlyCollection<string> selectedBandIds)
    {
        suppressNearbyBandSelection = true;
        try
        {
            NearbyBands.Clear();
            foreach (Ft8Band band in bands)
            {
                var option = new Ft8NearbyBandOption(band,
                    selectedBandIds.Contains(band.Id, StringComparer.Ordinal));
                option.SelectionChanged = NearbyBandOptionChanged;
                NearbyBands.Add(option);
            }
        }
        finally
        {
            suppressNearbyBandSelection = false;
        }
        OnPropertyChanged(nameof(HasNearbyBands));
    }

    [RelayCommand]
    private void ResetDiagnosticStatistics()
    {
        diagnosticsCandidatesAtReset = previousDiagnostics?.CandidatesExamined ?? 0;
        diagnosticsLdpcRejectedAtReset = previousDiagnostics?.LdpcRejected ?? 0;
        diagnosticsCrcRejectedAtReset = previousDiagnostics?.CrcRejected ?? 0;
        previousDiagnostics = null;
        Candidates = "0";
        Rejected = "0";
        RecentDetectionText = "直近2スロット: sync候補 — / 調査候補 —";
        RecentValidationText = "直近2スロット: LDPC — / CRC — / 有効 —";
    }

    private void AddMapMarker(Ft8Reception reception)
    {
        // RR73 is syntactically also a four-character Maidenhead value, but in
        // an FT8 standard message it is the end-of-contact acknowledgement.
        if (string.Equals(reception.Extra, "RR73", StringComparison.OrdinalIgnoreCase))
            return;
        if (!MaidenheadLocator.TryGetCentre(reception.Extra, out double latitude, out double longitude))
            return;

        string locator = reception.Extra.Trim().ToUpperInvariant();
        string station = string.IsNullOrWhiteSpace(reception.FromCall) ? "不明局" : reception.FromCall;
        string id = $"ft8-{station}-{locator}".ToUpperInvariant();
        int existing = MapMarkers.ToList().FindIndex(item => item.Id == id);
        if (existing >= 0) MapMarkers.RemoveAt(existing);
        var mapStation = new Ft8MapStation(id, latitude, longitude, station, locator, reception.ReceivedAt);
        mapStationsById[id] = mapStation;
        MapMarkers.Insert(0, CreateMapMarker(mapStation, DateTimeOffset.UtcNow));
        while (MapMarkers.Count > MapMarkerLimit)
        {
            string removedId = MapMarkers[^1].Id;
            MapMarkers.RemoveAt(MapMarkers.Count - 1);
            mapStationsById.Remove(removedId);
        }
    }

    internal void RefreshMapStationActivity(DateTimeOffset now)
    {
        for (int index = 0; index < MapMarkers.Count; index++)
        {
            GeoMapMarker marker = MapMarkers[index];
            if (!mapStationsById.TryGetValue(marker.Id, out Ft8MapStation? station)) continue;
            string color = ResolveMapStationActivity(station.LastReceivedAt, now).Color;
            if (!string.Equals(marker.Color, color, StringComparison.Ordinal))
                MapMarkers[index] = CreateMapMarker(station, now);
        }
    }

    private static GeoMapMarker CreateMapMarker(Ft8MapStation station, DateTimeOffset now)
    {
        (string status, string color) = ResolveMapStationActivity(station.LastReceivedAt, now);
        string localTime = station.LastReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return new GeoMapMarker(station.Id, station.Latitude, station.Longitude, station.Callsign,
            $"{station.Locator} / {station.Latitude:F4}°, {station.Longitude:F4}° / 最終受信: {localTime} / {status}",
            color, Symbol: "station");
    }

    private static (string Status, string Color) ResolveMapStationActivity(DateTimeOffset lastReceivedAt,
        DateTimeOffset now)
    {
        TimeSpan sinceLast = now - lastReceivedAt;
        if (sinceLast <= ActiveMapStationWindow)
            return ("活動中（最終受信から5分以内）", ActiveMapStationColor);
        if (sinceLast <= RecentMapStationWindow)
            return ("最近（最終受信から30分以内）", RecentMapStationColor);
        return ("休止（最終受信から30分超）", DormantMapStationColor);
    }

    private sealed record Ft8MapStation(string Id, double Latitude, double Longitude, string Callsign,
        string Locator, DateTimeOffset LastReceivedAt);

    private void RebuildStationSummaries()
    {
        string? selectedCallsign = SelectedStation?.Callsign;
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-RetentionMinutes);
        Ft8StationSummary[] summaries = History
            .GroupBy(item => DisplayStationKey(item.FromCall), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Any(item => item.ReceivedAt >= cutoff))
            .OrderByDescending(group => group.Max(item => item.ReceivedAt))
            .Take(MaximumStations)
            .Select(group =>
            {
                Ft8Reception latest = group.OrderByDescending(item => item.ReceivedAt).First();
                string locator = group.OrderByDescending(item => item.ReceivedAt)
                    .Select(item => DisplayLocator(item.Extra))
                    .FirstOrDefault(value => value != "—") ?? "—";
                return new Ft8StationSummary(group.Key, group.Count(), latest.ReceivedAt.ToLocalTime(),
                    locator, group.OrderByDescending(item => item.ReceivedAt).Take(20).ToArray(), latest);
            })
            .ToArray();
        StableRecencyOrder.Replace(Stations, summaries,
            item => item.Callsign, item => item.LastReceivedAt);
        SelectedStation = selectedCallsign is null
            ? null
            : Stations.FirstOrDefault(item => string.Equals(
                item.Callsign, selectedCallsign, StringComparison.OrdinalIgnoreCase));
        RefreshFilteredStations();
    }

    private void RefreshFilteredStations()
    {
        string filter = StationSearchText?.Trim() ?? string.Empty;
        FilteredStations.Clear();
        foreach (Ft8StationSummary station in Stations)
        {
            if (filter.Length == 0 ||
                station.Callsign.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                station.Locator.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                station.LatestReception.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                station.LatestReception.ToCall.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                station.LatestReception.MessageType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                station.LatestReception.Mode.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredStations.Add(station);
            }
        }

        if (SelectedStation is null || !FilteredStations.Contains(SelectedStation))
            SelectedStation = FilteredStations.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredStationCount));
    }

    private void RefreshFilteredHistory()
    {
        FilteredHistory.Clear();
        string filter = HistorySearchText?.Trim() ?? string.Empty;
        foreach (Ft8Reception message in History)
        {
            string searchable = $"{message.FromCall} {message.ToCall} {message.Message} {message.MessageType} {message.BandFrequencyText}";
            if (filter.Length == 0 || searchable.Contains(filter, StringComparison.OrdinalIgnoreCase))
                FilteredHistory.Add(message);
        }
        if (SelectedTimelineReception is null || !FilteredHistory.Contains(SelectedTimelineReception))
            SelectedTimelineReception = FilteredHistory.FirstOrDefault();
    }

    private static string DisplayStationKey(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "<...>"
            ? "送信局不明"
            : value.Trim().ToUpperInvariant();

    private static string DisplayLocator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "RR73", StringComparison.OrdinalIgnoreCase) ||
            !MaidenheadLocator.TryGetCentre(value, out _, out _))
            return "—";
        return value.Trim().ToUpperInvariant();
    }

    private void SettingsUpdated() =>
        DecoderSettingsChanged?.Invoke(MaximumHistory, MinimumSyncScore, MaximumCandidates,
            LdpcIterations, MonitorAudioEnabled, MonitorAudioVolume);

    private void NearbyBandOptionChanged()
    {
        if (suppressNearbyBandSelection) return;
        NearbyBandSelectionChanged?.Invoke(NearbyBands
            .Where(item => item.IsSelected)
            .Select(item => item.Band.Id)
            .ToArray());
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "FT8",
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
        SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndClearData(
            "FT8",
            () =>
            {
                Clear();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetAllAsync(
            "FT8",
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

public sealed record Ft8StationSummary(
    string Callsign,
    int Count,
    DateTimeOffset LastReceivedAt,
    string Locator,
    IReadOnlyList<Ft8Reception> History,
    Ft8Reception LatestReception);

public sealed class Ft8NearbyBandOption : ObservableObject
{
    private bool isSelected;

    internal Ft8NearbyBandOption(Ft8Band band, bool isSelected)
    {
        Band = band;
        this.isSelected = isSelected;
    }

    public Ft8Band Band { get; }
    public string DisplayText =>
        Band.BandDisplayName;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!SetProperty(ref isSelected, value)) return;
            SelectionChanged?.Invoke();
        }
    }

    internal Action? SelectionChanged { get; set; }
}
