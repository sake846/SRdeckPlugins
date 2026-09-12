using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IMeshtasticReceiveService _meshtasticReceiveService;
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

    [ObservableProperty] private MeshtasticDisplayItem? _selectedTimelineMessage;

    public IPluginRuntimeDiagnostics RuntimeDiagnostics
    {
        get => _runtimeDiagnostics;
        private set => SetProperty(ref _runtimeDiagnostics, value);
    }
    public OverallStatusKind MeshtasticDiagnosticStatusKind => EvaluateMeshtasticDiagnostic().Kind;
    public string MeshtasticDiagnosticStatusText => EvaluateMeshtasticDiagnostic().Status;
    public string MeshtasticDiagnosticPhase => EvaluateMeshtasticDiagnostic().Phase;
    public string MeshtasticDiagnosticSummary => EvaluateMeshtasticDiagnostic().Summary;
    public string MeshtasticDiagnosticRecommendation => EvaluateMeshtasticDiagnostic().Recommendation;
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

    public MeshtasticViewModel(IMeshtasticReceiveService meshtasticReceiveService)
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

    public void SelectMeshtasticNode(uint nodeNumber)
    {
        if (_meshtasticNodesById.TryGetValue(nodeNumber, out MeshtasticNodeDisplayItem? node))
            SelectedMeshtasticNode = node;
    }

    [RelayCommand]
    private void DeleteMeshtasticNode(MeshtasticNodeDisplayItem? node)
    {
        if (node is null || !MeshtasticNodes.Contains(node)) return;

        bool wasSelected = ReferenceEquals(SelectedMeshtasticNode, node);
        MeshtasticNodes.Remove(node);
        if (_meshtasticNodesById.TryGetValue(node.NodeNumber, out MeshtasticNodeDisplayItem? cachedNode) &&
            ReferenceEquals(cachedNode, node))
        {
            _meshtasticNodesById.Remove(node.NodeNumber);
        }

        if (_meshtasticMapPointsById.Remove(node.NodeNumber, out MeshtasticMapPoint? mapPoint))
            MeshtasticMapPoints.Remove(mapPoint);

        if (wasSelected) SelectedMeshtasticNode = null;
        RefreshFilteredMeshtasticNodes();
        if (wasSelected) SelectedMeshtasticNode = FilteredMeshtasticNodes.FirstOrDefault();
        RefreshMeshtasticMapMarkers();
        ScheduleMeshtasticSnapshotSave();
    }

    [RelayCommand]
    private void SelectMeshtasticMapMarker(string? markerId)
    {
        if (uint.TryParse(markerId, NumberStyles.None, CultureInfo.InvariantCulture, out uint nodeNumber))
            SelectMeshtasticNode(nodeNumber);
    }

    private void RefreshSelectedMeshtasticNodeLatestApplications()
    {
        ReplaceMeshtasticItems(SelectedMeshtasticNodeLatestApplications,
            SelectedMeshtasticNode is null ? [] : _meshtasticHistoryAnalyzer.GetLatestApplications(
                MeshtasticMessages, SelectedMeshtasticNode.NodeId));
    }

    private void RefreshMeshtasticPacketGroups()
    {
        MeshtasticHistoryAnalysisResult result = _meshtasticHistoryAnalyzer.Analyze(
            MeshtasticMessages,
            MeshtasticNodes,
            MeshtasticHistorySearchText,
            MeshtasticHistoryDecodedOnly,
            MeshtasticHistoryDirectOnly,
            MeshtasticHistoryDisplayLimit);
        ApplyMeshtasticHistoryAnalysis(result);
    }

    private void ApplyMeshtasticHistoryAnalysis(MeshtasticHistoryAnalysisResult result)
    {
        // WPF may clear selection while a row is replaced or a filter resets the view.
        MeshtasticDisplayItem? selected = SelectedTimelineMessage;
        ReplaceMeshtasticItems(VisibleMeshtasticMessages, result.FilteredMessages);
        ReplaceMeshtasticItems(MeshtasticPacketGroups, result.PacketGroups, static (left, right) =>
            left.Sender == right.Sender && left.SenderName == right.SenderName &&
            left.PacketIdText == right.PacketIdText && left.Port == right.Port &&
            left.Summary == right.Summary && left.Receptions.SequenceEqual(right.Receptions));
        ReplaceMeshtasticItems(MeshtasticReceptionAggregates, result.ReceptionAggregates);
        ReplaceMeshtasticItems(MeshtasticTextMessages, result.TextMessages);

        MeshtasticActiveNodeCount = result.ActiveNodeCount;
        MeshtasticUndecodedHistoryCount = result.UndecodedHistoryCount;

        SelectedTimelineMessage = selected is null ? VisibleMeshtasticMessages.FirstOrDefault()
            : VisibleMeshtasticMessages.FirstOrDefault(item => item.Sender == selected.Sender &&
                item.PacketId == selected.PacketId && item.ReceivedAt == selected.ReceivedAt &&
                item.Radio == selected.Radio && item.PayloadHex == selected.PayloadHex)
                ?? VisibleMeshtasticMessages.FirstOrDefault();
    }

    private void ScheduleMeshtasticDerivedRefresh()
    {
        _historyVersion++;
        _meshtasticDerivedRefreshTimer ??= CreateMeshtasticTimer(
            TimeSpan.FromMilliseconds(250),
            () =>
            {
                _ = RefreshMeshtasticHistoryInBackground();
                RefreshSelectedMeshtasticNodeLatestApplications();
                RefreshFilteredMeshtasticNodes();
            });
        if (!_meshtasticDerivedRefreshTimer.IsEnabled) _meshtasticDerivedRefreshTimer.Start();
    }

    private void ScheduleMeshtasticSnapshotSave()
    {
        _meshtasticSnapshotSaveTimer ??= CreateMeshtasticTimer(TimeSpan.FromSeconds(2), SaveMeshtasticState);
        if (!_meshtasticSnapshotSaveTimer.IsEnabled) _meshtasticSnapshotSaveTimer.Start();
    }

    private static DispatcherTimer CreateMeshtasticTimer(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private void FlushMeshtasticPendingWork()
    {
        if (_meshtasticDerivedRefreshTimer?.IsEnabled == true)
        {
            _meshtasticDerivedRefreshTimer.Stop();
            RefreshMeshtasticPacketGroups();
            RefreshSelectedMeshtasticNodeLatestApplications();
            RefreshFilteredMeshtasticNodes();
        }
        if (_meshtasticSnapshotSaveTimer?.IsEnabled == true)
        {
            _meshtasticSnapshotSaveTimer.Stop();
            SaveMeshtasticState();
        }
        ApplyPendingMeshtasticDiagnostics();
    }

    private static string ResolveMeshtasticPresetName(MeshtasticRadioReception radio)
    {
        MeshtasticLoRaProfile? profile = MeshtasticJpLongFastProfile
            .GetDetectionProfiles(MeshtasticModemPreset.AutoSf250And125)
            .FirstOrDefault(candidate => candidate.BandwidthHz == radio.BandwidthHz &&
                                         candidate.SpreadingFactor == radio.SpreadingFactor);
        return profile?.Name ?? $"SF{radio.SpreadingFactor}/{radio.BandwidthHz / 1000.0:0.###}k";
    }

    private void RefreshFilteredMeshtasticNodes()
    {
        IEnumerable<MeshtasticNodeDisplayItem> filtered = MeshtasticNodes;
        string search = MeshtasticNodeSearchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(node =>
                node.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                node.NodeId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                node.Identity.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (MeshtasticNodesActiveOnly)
            filtered = filtered.Where(node => node.ActivityStatus is "新規" or "活動中");
        if (MeshtasticNodesDirectOnly)
            filtered = filtered.Where(node => node.DirectReceptionCount > 0);
        if (MeshtasticNodesWithPositionOnly)
            filtered = filtered.Where(node => node.HasPosition);

        StableRecencyOrder.Replace(
            FilteredMeshtasticNodes,
            filtered,
            node => node.NodeNumber,
            node => node.LastSeenAt ?? DateTimeOffset.MinValue);
        if (SelectedMeshtasticNode is not null && !FilteredMeshtasticNodes.Contains(SelectedMeshtasticNode))
            SelectedMeshtasticNode = FilteredMeshtasticNodes.FirstOrDefault();
    }

    private void RefreshMeshtasticMapMarkers()
    {
        if (_restoringMeshtasticState) return;
        IEnumerable<MeshtasticMapPoint> points = MeshtasticMapPoints;
        if (MeshtasticMapActiveOnly)
            points = points.Where(point => point.ActivityStatus is "新規" or "活動中");
        if (MeshtasticMapDirectOnly)
            points = points.Where(point => point.HasDirectReception);

        var markers = new List<GeoMapMarker>();
        foreach (MeshtasticMapPoint point in points)
        {
            string reception = point.HasDirectReception ? "直接受信あり" : "中継受信のみ";
            markers.Add(new GeoMapMarker(
                point.NodeNumber.ToString(CultureInfo.InvariantCulture),
                point.Latitude,
                point.Longitude,
                point.Label,
                $"{point.ActivityStatus} / {reception}\n{point.Coordinates}",
                MeshtasticMapPresentation.GetNodeColor(point.ActivityStatus),
                Symbol: "station",
                IsSelected: point.IsSelected));
        }
        ReplaceMeshtasticItems(MapMarkers, markers);
    }

    [ObservableProperty] private long _meshtasticPreambleCount;
    [ObservableProperty] private long _meshtasticSynchronizedCount;
    [ObservableProperty] private long _meshtasticHeaderCount;
    [ObservableProperty] private long _meshtasticPayloadCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeshtasticDecodeRateText))]
    private long _meshtasticPacketCount;
    [ObservableProperty] private long _meshtasticDuplicateCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeshtasticDecodeRateText))]
    private long _meshtasticDataCount;
    [ObservableProperty] private long _meshtasticDroppedBlockCount;
    [ObservableProperty] private int _meshtasticQueueDepth;
    [ObservableProperty] private int _meshtasticMaximumQueueDepth;
    [ObservableProperty] private double _meshtasticCurrentQueueDelayMs;
    [ObservableProperty] private double _meshtasticAverageQueueDelayMs;
    [ObservableProperty] private double _meshtasticMaximumQueueDelayMs;
    [ObservableProperty] private double _meshtasticCurrentProcessingTimeMs;
    [ObservableProperty] private double _meshtasticAverageProcessingTimeMs;
    [ObservableProperty] private double _meshtasticMaximumProcessingTimeMs;
    [ObservableProperty] private double _meshtasticAverageChannelizationCpuMs;
    [ObservableProperty] private double _meshtasticAverageDetectionCpuMs;
    [ObservableProperty] private double _meshtasticCurrentInputBlockTimeMs;
    [ObservableProperty] private double _meshtasticCurrentProcessingLoadPercent;
    [ObservableProperty] private double _meshtasticAverageProcessingLoadPercent;
    [ObservableProperty] private double _meshtasticMaximumProcessingLoadPercent;
    [ObservableProperty] private double _meshtasticOldestDeferredIqMs;
    [ObservableProperty] private double _meshtasticDeferredRetentionRemainingMs;
    [ObservableProperty] private long _meshtasticDeferredRecoveredBlocks;
    [ObservableProperty] private long _meshtasticExpiredHistoryBlocks;
    [ObservableProperty] private string _meshtasticPerformanceStatus = "正常";
    [ObservableProperty] private string _meshtasticPerformanceDetails = "処理負荷を計測中です。";

    public string MeshtasticDecodeRateText => MeshtasticPacketCount == 0
        ? "—"
        : $"{MeshtasticDataCount * 100.0 / MeshtasticPacketCount:F1} %";
}
