using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    private readonly MeshtasticReceiveService _meshtasticReceiveService;
    private readonly MeshtasticHistoryAnalyzer _meshtasticHistoryAnalyzer = new();
    private readonly MeshtasticSettingsService _meshtasticSettingsService = new();
    private IPluginHostContext? _hostContext;
    private PluginJsonLinesHistoryWriter<MeshtasticDisplayItem>? _meshtasticHistoryWriter;
    private IReadOnlyList<(int FrequencyHz, int BandwidthHz)> _tuningTargets = [];
    private bool _canRequestTuning;
    private int _initialized;
    private IPluginRuntimeDiagnostics _runtimeDiagnostics = NullPluginRuntimeDiagnostics.Instance;
    private string _meshtasticDiagnosticLastUpdated = "未更新";
    private long _lastMeshtasticSuccessfulPayloadTicks;
    private long _lastMeshtasticFailureTicks;
    private MeshtasticDiagnosticEvaluation? _meshtasticDiagnosticCache;

    [ObservableProperty] private MeshtasticDisplayItem? _selectedTimelineMessage;

    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => _runtimeDiagnostics;
        private set => SetProperty(ref _runtimeDiagnostics, value);
    }
    private MeshtasticDiagnosticEvaluation CurrentMeshtasticDiagnostic =>
        _meshtasticDiagnosticCache ??= EvaluateMeshtasticDiagnostic();
    public OverallStatusKind MeshtasticDiagnosticStatusKind => CurrentMeshtasticDiagnostic.Kind;
    public string MeshtasticDiagnosticStatusText => CurrentMeshtasticDiagnostic.Status;
    public string MeshtasticDiagnosticPhase => CurrentMeshtasticDiagnostic.Phase;
    public string MeshtasticDiagnosticSummary => CurrentMeshtasticDiagnostic.Summary;
    public string MeshtasticDiagnosticRecommendation => CurrentMeshtasticDiagnostic.Recommendation;
    public string MeshtasticDiagnosticLastUpdated => _meshtasticDiagnosticLastUpdated;
    public string MeshtasticDiagnosticTuningText =>
        $"{SelectedMeshtasticRegion} / {(IsMeshtasticDiscoveryMode ? "探索" : SelectedMeshtasticModemPreset.ToString())}";
    public string MeshtasticDiagnosticSignalText => MeshtasticLastSignalStatus.Replace("最終信号: ", "", StringComparison.Ordinal);

    private MeshtasticDiagnosticEvaluation EvaluateMeshtasticDiagnostic()
    {
        if (_lastWorkerFailure is { } failure)
            return new(OverallStatusKind.Error, "受信停止", "受信ワーカー",
                failure.Exception.GetBaseException().Message, "確認: エラー記録を確認し、受信を停止して再開してください");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? lastSuccess = ReadUtcTicks(ref _lastMeshtasticSuccessfulPayloadTicks);
        DateTimeOffset? lastFailure = ReadUtcTicks(ref _lastMeshtasticFailureTicks);
        if (MeshtasticPerformanceStatus == "過負荷")
            return new(OverallStatusKind.Critical, "過負荷", "リアルタイム処理",
                MeshtasticPerformanceDetails, "確認: 探索プリセット数、スロット数、または入力サンプルレートを減らしてください");
        if (MeshtasticPerformanceStatus == "注意")
            return new(OverallStatusKind.Warning, "注意", "リアルタイム処理",
                MeshtasticPerformanceDetails, "確認: 遅延IQの保持残り時間と入力サンプルレートを確認してください");
        if (MeshtasticPassbandStatus.Contains("帯域外", StringComparison.Ordinal))
            return new(OverallStatusKind.Warning, "要確認", "入力・選局",
                "Meshtastic対象周波数が受信帯域外です", "確認: 地域、slot、中心周波数、入力帯域を確認してください");
        if (MeshtasticPerformanceStatus is "待機中" or "測定準備中")
            return new(OverallStatusKind.Idle, "入力待ち", "入力",
                "IQ入力またはリアルタイム測定の開始を待っています", "確認: SDR接続と受信開始状態を確認してください");
        if (lastFailure is DateTimeOffset failedAt && now - failedAt <= TimeSpan.FromSeconds(60) &&
            (lastSuccess is null || failedAt > lastSuccess.Value))
            return new(OverallStatusKind.Warning, "要確認", MeshtasticPayloadFailureCount > 0 ? "検証・復号" :
                MeshtasticHeaderFailureCount > 0 ? "ヘッダー" : "同期",
                MeshtasticLastFailureStatus.Replace("直近の失敗: ", "", StringComparison.Ordinal),
                "確認: 信号margin、slot/PHY設定、失敗段階の件数を確認してください");
        if (lastSuccess is DateTimeOffset succeededAt && now - succeededAt <= TimeSpan.FromSeconds(60))
            return new(OverallStatusKind.Success, "正常", "検証・復号",
                "直近60秒にMeshtastic payloadを正常に検証・復号しています", "確認: 受信処理は正常に動作しています");
        if (MeshtasticHeaderCount > 0)
            return new(OverallStatusKind.Running, "監視中", "payload",
                "LoRa headerを取得し、payload完了を待っています", "確認: payload失敗とCRC内訳を確認しながら継続監視してください");
        if (MeshtasticSynchronizedCount > 0)
            return new(OverallStatusKind.Running, "監視中", "header",
                "LoRa同期が成立し、headerを探索しています", "確認: header失敗と選択PHYを確認してください");
        if (MeshtasticPreambleCount > 0)
            return new(OverallStatusKind.Running, "監視中", "同期",
                "LoRa preambleを検出し、同期を継続しています", "確認: 信号marginとslot設定を確認してください");
        return new(OverallStatusKind.Running, "監視中", "信号待機",
            "IQ入力は正常でMeshtastic信号を監視しています", "確認: 対象slotと周辺トラフィックを確認してください");
    }

    private static DateTimeOffset? ReadUtcTicks(ref long ticks)
    {
        long value = Interlocked.Read(ref ticks);
        return value <= 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }

    private readonly record struct MeshtasticDiagnosticEvaluation(OverallStatusKind Kind,
        string Status, string Phase, string Summary, string Recommendation);

    public MeshtasticViewModel(MeshtasticReceiveService meshtasticReceiveService)
    {
        _meshtasticReceiveService = meshtasticReceiveService;
    }

    public void Initialize(IPluginHostContext hostContext)
    {
        _hostContext = hostContext;
        RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        _meshtasticSettingsService.Attach(hostContext);
        MeshtasticHardwareCatalog.ConfigureDataDirectory(_meshtasticSettingsService.PluginDataDirectory);
        RegisterMeshtasticReceiver();
        LoadMeshtasticSettings();
        _meshtasticHistoryWriter = CreateMeshtasticHistoryWriter(hostContext);
        _stateWriter = new MeshtasticStateWriter(MeshtasticStatePath);
        _stateWriter.SaveFailed += exception =>
        {
            hostContext.Logger.Log(PluginLogLevel.Error, "meshtastic.state.save-failed",
                "Meshtastic node state could not be saved.", exception);
            int epoch = Volatile.Read(ref _presentationEpoch);
            hostContext.Dispatcher.Post(() =>
            {
                if (epoch == _presentationEpoch)
                    SetMeshtasticReceiverStatus($"ノード状態の保存に失敗しました: {exception.Message}", OverallStatusKind.Error);
            });
        };
        LoadMeshtasticState();
        StartMeshtasticUpdates();
    }

    public async ValueTask ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0) return;
        await InvokeMeshtasticUiAsync(() =>
        {
            Interlocked.Increment(ref _presentationEpoch);
            _historyVersion++;
            _statisticsTimer?.Stop();
            FlushMeshtasticPendingWork();
            SaveMeshtasticState();
            UnregisterMeshtasticReceiver();
        });
        if (_stateWriter is not null)
        {
            try { await _stateWriter.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception)
            {
                await InvokeMeshtasticUiAsync(() => SetMeshtasticReceiverStatus(
                    $"ノード状態の最終保存に失敗しました: {exception.Message}", OverallStatusKind.Error));
            }
            _stateWriter = null;
        }
        if (_meshtasticHistoryWriter is not null)
        {
            await _meshtasticHistoryWriter.DisposeAsync().ConfigureAwait(false);
            _meshtasticHistoryWriter = null;
        }
        _meshtasticSettingsService.Detach();
        _hostContext = null;
    }

    private PluginJsonLinesHistoryWriter<MeshtasticDisplayItem> CreateMeshtasticHistoryWriter(
        IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<MeshtasticDisplayItem>(
            MeshtasticHistoryPath,
            GetMeshtasticHistoryPolicy,
            MeshtasticHistoryTimestamp,
            MeshtasticHistoryJsonOptions);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "meshtastic.history.save-failed",
            "Meshtastic reception history could not be saved.", exception);
        return writer;
    }

    private void ResetMeshtasticHistoryWriter()
    {
        _meshtasticHistoryWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _meshtasticHistoryWriter = _hostContext is null
            ? null
            : CreateMeshtasticHistoryWriter(_hostContext);
    }

    public void Activate()
    {
        _canRequestTuning = true;
        RequestMeshtasticRadioTuning();
    }

    public void Deactivate()
    {
        _canRequestTuning = false;
    }

    public void StartStream()
    {
        _canRequestTuning = true;
        RequestMeshtasticRadioTuning();
    }

    public void StopStream()
    {
    }

    public event EventHandler? FrequencyOverlaysChanged;

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays
    {
        get
        {
            MeshtasticRegionProfile region = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion);
            if (!IsMeshtasticDiscoveryMode && !MeshtasticJpLongFastProfile.IsAutoSf(SelectedMeshtasticModemPreset))
            {
                MeshtasticLoRaProfile profile = MeshtasticJpLongFastProfile.GetProfile(SelectedMeshtasticModemPreset);
                int bandwidthHz = profile.BandwidthHz;
                return MeshtasticSpecifiedSlotOptions.Select(item =>
                {
                    bool isSelected = item.Slot == MeshtasticRadioChannel;
                    return new FrequencyOverlayItem(
                        $"meshtastic-{bandwidthHz}-{item.Slot}",
                        item.FrequencyHz,
                        bandwidthHz,
                        item.Slot.ToString(),
                        isSelected,
                        isSelected ? (bandwidthHz == 125_000
                            ? PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Secondary)
                            : PluginReceiverBandColors.WithAlpha(0x50, PluginReceiverBandColors.Primary)) : "#00000000",
                        bandwidthHz == 125_000
                            ? PluginReceiverBandColors.WithAlpha(0x90, PluginReceiverBandColors.Secondary)
                            : PluginReceiverBandColors.WithAlpha(0x80, PluginReceiverBandColors.Primary),
                        "#FFFFFFFF",
                        bandwidthHz == 125_000 ? 1 : 0);
                }).ToArray();
            }

            return MeshtasticJpLongFastProfile.GetChannelProfiles(SelectedMeshtasticModemPreset)
                .Select(profile => profile.BandwidthHz)
                .Distinct()
                .SelectMany(bandwidthHz =>
                    (bandwidthHz == 125_000 ? MeshtasticSlots125 : MeshtasticSlots250)
                    .Select(item => new FrequencyOverlayItem(
                        $"meshtastic-{bandwidthHz}-{item.Slot}",
                        region.CalculateChannelFrequencyHz(item.Slot, bandwidthHz),
                        bandwidthHz,
                        item.Slot.ToString(),
                        item.IsSelected,
                        item.IsSelected ? (bandwidthHz == 125_000
                            ? PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Secondary)
                            : PluginReceiverBandColors.WithAlpha(0x50, PluginReceiverBandColors.Primary)) : "#00000000",
                        bandwidthHz == 125_000
                            ? PluginReceiverBandColors.WithAlpha(0x90, PluginReceiverBandColors.Secondary)
                            : PluginReceiverBandColors.WithAlpha(0x80, PluginReceiverBandColors.Primary),
                        "#FFFFFFFF",
                        bandwidthHz == 125_000 ? 1 : 0)))
                .ToArray();
        }
    }

    private string MeshtasticStatePath => _meshtasticSettingsService.StatePath;
    private string MeshtasticHistoryPath => _meshtasticSettingsService.HistoryPath;
    private const int MaximumMeshtasticHistoryEntries = 100_000;
    private const long MaximumMeshtasticHistoryBytes = 128L * 1024 * 1024;
    private static readonly Func<MeshtasticDisplayItem, DateTimeOffset> MeshtasticHistoryTimestamp = static item => item.ReceivedAt;
    private static readonly System.Text.Json.JsonSerializerOptions MeshtasticHistoryJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private PluginJsonLinesHistoryPolicy GetMeshtasticHistoryPolicy() => new(
        MaximumMeshtasticHistoryEntries,
        TimeSpan.FromDays(Math.Clamp(MeshtasticHistoryRetentionDays, 1, 3650)),
        MaximumMeshtasticHistoryBytes);
    private DispatcherTimer? _meshtasticDerivedRefreshTimer;
    private DispatcherTimer? _meshtasticSnapshotSaveTimer;

    public ObservableCollection<MeshtasticDisplayItem> MeshtasticMessages { get; } = new();
    public ObservableCollection<MeshtasticDisplayItem> VisibleMeshtasticMessages { get; } = new MeshtasticDisplayCollection<MeshtasticDisplayItem>();
    public ObservableCollection<MeshtasticDisplayItem> MeshtasticTextMessages { get; } = new MeshtasticDisplayCollection<MeshtasticDisplayItem>();
    public ObservableCollection<MeshtasticPacketGroupItem> MeshtasticPacketGroups { get; } = new MeshtasticDisplayCollection<MeshtasticPacketGroupItem>();
    public ObservableCollection<MeshtasticReceptionAggregateItem> MeshtasticReceptionAggregates { get; } = new MeshtasticDisplayCollection<MeshtasticReceptionAggregateItem>();
    public ObservableCollection<MeshtasticDisplayItem> SelectedMeshtasticNodeLatestApplications { get; } = new MeshtasticDisplayCollection<MeshtasticDisplayItem>();
    public ObservableCollection<MeshtasticNodeDisplayItem> MeshtasticNodes { get; } = new();
    public ObservableCollection<MeshtasticNodeDisplayItem> FilteredMeshtasticNodes { get; } = new();
    public ObservableCollection<MeshtasticMapPoint> MeshtasticMapPoints { get; } = new();
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = new MeshtasticDisplayCollection<GeoMapMarker>();
    private readonly Dictionary<uint, MeshtasticNodeDisplayItem> _meshtasticNodesById = new();
    private readonly Dictionary<uint, MeshtasticMapPoint> _meshtasticMapPointsById = new();

    [ObservableProperty]
    private string _meshtasticReceiverStatus = "受信待機中";

    [ObservableProperty]
    private OverallStatusKind _meshtasticReceiverStatusKind = OverallStatusKind.Running;

    [ObservableProperty] private string _captureStatus = "IQ録音: 待機";

    internal Action? CaptureRequested { get; set; }

    [RelayCommand]
    private void StartCapture() => CaptureRequested?.Invoke();

    [ObservableProperty]
    private MeshtasticNodeDisplayItem? _selectedMeshtasticNode;

    [ObservableProperty] private string _meshtasticHistorySearchText = "";
    [ObservableProperty] private bool _meshtasticHistoryDecodedOnly;
    [ObservableProperty] private bool _meshtasticHistoryDirectOnly;
    [ObservableProperty] private string _meshtasticNodeSearchText = "";
    [ObservableProperty] private bool _meshtasticNodesActiveOnly;
    [ObservableProperty] private bool _meshtasticNodesDirectOnly;
    [ObservableProperty] private bool _meshtasticNodesWithPositionOnly;
    [ObservableProperty] private int _meshtasticActiveNodeCount;
    [ObservableProperty] private int _meshtasticUndecodedHistoryCount;
    [ObservableProperty] private bool _meshtasticMapActiveOnly;
    [ObservableProperty] private bool _meshtasticMapDirectOnly;

    partial void OnMeshtasticHistorySearchTextChanged(string value) => ScheduleMeshtasticDerivedRefresh();
    partial void OnMeshtasticHistoryDecodedOnlyChanged(bool value) => ScheduleMeshtasticDerivedRefresh();
    partial void OnMeshtasticHistoryDirectOnlyChanged(bool value) => ScheduleMeshtasticDerivedRefresh();
    partial void OnMeshtasticNodeSearchTextChanged(string value) => RefreshFilteredMeshtasticNodes();
    partial void OnMeshtasticNodesActiveOnlyChanged(bool value) => RefreshFilteredMeshtasticNodes();
    partial void OnMeshtasticNodesDirectOnlyChanged(bool value) => RefreshFilteredMeshtasticNodes();
    partial void OnMeshtasticNodesWithPositionOnlyChanged(bool value) => RefreshFilteredMeshtasticNodes();
    partial void OnMeshtasticMapActiveOnlyChanged(bool value) => RefreshMeshtasticMapMarkers();
    partial void OnMeshtasticMapDirectOnlyChanged(bool value) => RefreshMeshtasticMapMarkers();
    partial void OnSelectedMeshtasticNodeChanged(MeshtasticNodeDisplayItem? value)
    {
        RefreshSelectedMeshtasticNodeLatestApplications();
        foreach (MeshtasticMapPoint point in MeshtasticMapPoints)
            point.IsSelected = point.NodeNumber == value?.NodeNumber;
        RefreshMeshtasticMapMarkers();
    }

}
