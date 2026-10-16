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
    private MeshtasticPerformanceState _diagnosticPerformanceState;
    private bool _diagnosticInPassband = true;

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

    private MeshtasticDiagnosticEvaluation EvaluateMeshtasticDiagnostic() =>
        MeshtasticDiagnosticEvaluator.Evaluate(new(
            _diagnosticPerformanceState,
            _diagnosticInPassband,
            _lastWorkerFailure?.Exception.GetBaseException().Message,
            MeshtasticPerformanceDetails,
            ReadUtcTicks(ref _lastMeshtasticSuccessfulPayloadTicks),
            ReadUtcTicks(ref _lastMeshtasticFailureTicks),
            MeshtasticPayloadFailureCount,
            MeshtasticHeaderFailureCount,
            MeshtasticHeaderCount,
            MeshtasticSynchronizedCount,
            MeshtasticPreambleCount,
            MeshtasticLastFailureStatus.Replace("直近の失敗: ", "", StringComparison.Ordinal)),
            DateTimeOffset.UtcNow);

    private static DateTimeOffset? ReadUtcTicks(ref long ticks)
    {
        long value = Interlocked.Read(ref ticks);
        return value <= 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }

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
    public ObservableCollection<MeshtasticNodeDisplayItem> MeshtasticNodes => _nodeStore.Nodes;
    public ObservableCollection<MeshtasticNodeDisplayItem> FilteredMeshtasticNodes { get; } = new();
    public ObservableCollection<MeshtasticMapPoint> MeshtasticMapPoints => _nodeStore.MapPoints;
    public ObservableCollection<GeoMapMarker> MapMarkers { get; } = new MeshtasticDisplayCollection<GeoMapMarker>();
    private readonly MeshtasticNodeStore _nodeStore = new();

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
