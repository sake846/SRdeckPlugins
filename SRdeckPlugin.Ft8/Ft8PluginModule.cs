using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Dsp;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.ViewModels;
using SRdeckPlugin.Ft8.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ft8;

public sealed partial class Ft8PluginModule : PluginModuleBase, IIqBlockConsumer, IPluginChannelBlockConsumer,
    ILivePluginProfileProvider, IPluginViewProvider, IPluginResultProvider,
    IPluginExportProvider, IFrequencyOverlayProvider, IWaterfallAnnotationProvider,
    IWaterfallDisplayProvider,
    IPluginProcessingDiagnosticsProvider,
    IPluginProcessingWarmup
{
    private const int CaptureDurationSeconds = 20;
    private const int MaximumWaterfallAnnotations = 4_096;
    private const int Ft8OccupiedBandwidthHz = 50;
    private static readonly string PrimaryBandOverlayFill =
        PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary);
    private static readonly string SimultaneousBandOverlayFill =
        PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Secondary);
    public const string BandListFileName = Ft8BandCatalog.FileName;
    public const string DefaultBandId = "ft8-band-20m";
    public static IReadOnlyList<Ft8Band> Bands { get; } = Ft8BandCatalog.Bands;

    private readonly object gate = new();
    private readonly object processingGate = new();
    private readonly Ft8Receiver receiver = new();
    private readonly Dictionary<string, Ft8Receiver> additionalReceivers = new(StringComparer.Ordinal);
    private readonly Ft8ViewModel viewModel = new();
    private readonly List<Ft8Reception> history = [];
    private PluginJsonLinesHistoryWriter<Ft8Reception>? historyWriter;
    private readonly List<Ft8Reception> waterfallHistory = [];
    private readonly PackedIqHistoryBuffer pretriggerBuffer = new(CaptureDurationSeconds);
    private IPluginHostContext? host;
    private Ft8Settings settings = new();
    private string selectedBandId = DefaultBandId;
    private string[] enabledAdditionalBandIds = [];
    private IqBlockMetadata? lastCaptureMetadata;
    private AppliedChannelConfiguration? lastCaptureChannelConfiguration;
    private ITimer? diagnosticsTimer;
    private int profileChangeInProgress;
    private int captureSaveInProgress;
    private long audioSequence;
    private WaterfallReference? waterfallReference;

    public Ft8PluginModule()
    {
        RegisterStreamReset(pretriggerBuffer.Reset);
        receiver.MessagesDecoded += OnMessagesDecoded;
        viewModel.BandSelectionRequested = SelectBandFromView;
        viewModel.NearbyBandSelectionChanged = ApplyNearbyBandSelection;
        viewModel.DecoderSettingsChanged = (maximumHistory, minimumScore, maximumCandidates,
            iterations, monitorAudioEnabled, monitorAudioVolume) =>
        {
            bool wasAudioEnabled = settings.MonitorAudioEnabled;
            settings = settings with
            {
                SelectedBandId = selectedBandId,
                Mode = SelectedBand().Mode,
                MaximumHistory = maximumHistory,
                MinimumSyncScore = minimumScore,
                MaximumCandidates = maximumCandidates,
                LdpcIterations = iterations,
                MonitorAudioEnabled = monitorAudioEnabled,
                MonitorAudioVolume = monitorAudioVolume
            };
            settings = settings.Normalize();
            if (wasAudioEnabled && !settings.MonitorAudioEnabled) host?.Audio.Reset();
            PruneStoredHistory();
            lock (gate)
                if (history.Count > settings.MaximumHistory)
                    history.RemoveRange(0, history.Count - settings.MaximumHistory);
            PersistSettings();
        };
        viewModel.MaximumStationsChanged = value => { settings = settings with { MaximumStations = value }; PersistSettings(); };
        viewModel.RetentionMinutesChanged = value => { settings = settings with { RetentionMinutes = value }; PersistSettings(); };
        viewModel.MapMarkerLimitChanged = value => { settings = settings with { MapMarkerLimit = value }; PersistSettings(); };
        viewModel.ClearRequested = () =>
        {
            lock (gate)
            {
                history.Clear();
                waterfallHistory.Clear();
            }
            DeleteHistoryFile();
            WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        };
        viewModel.ResetSettingsRequested = ResetSettingsAsync;
        viewModel.CaptureRequested = StartIqCapture;
    }

    public async ValueTask ResetSettingsAsync()
    {
        if (host is not null)
        {
            await host.Settings.DeleteAsync().ConfigureAwait(false);
        }
        settings = new Ft8Settings().Normalize();
        selectedBandId = settings.SelectedBandId;
        enabledAdditionalBandIds = settings.AdditionalBandIds?.ToArray() ?? [];
        host?.Dispatcher.Post(() =>
        {
            viewModel.Configure(settings, SelectedBand());
            if (host is not null)
            {
                RefreshNearbyBandOptions(host.Tuning.Current, false);
            }
        });
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "ft8",
        "FT8",
        "Multi-signal FT8, FT4, and JT65 weak-signal receiver and decoder",
        new Version(1, 1, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.ChannelIqConsumer |
        PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.ResultPublisher | PluginCapabilities.Export |
        PluginCapabilities.FrequencyOverlay | PluginCapabilities.WaterfallAnnotation |
        PluginCapabilities.WaterfallDisplay |
        PluginCapabilities.Headless,
        "SRdeck",
        "GPL-3.0");

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "FT8/FT4/JT65同期・スペクトル探索・FEC復号",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "UTCスロット蓄積、候補探索、同期評価、LDPC/RS復号、メッセージ解析");
    public PluginIqPreferences IqPreferences { get; } = new(4);
    public IReadOnlyList<PluginProfileDescriptor> Profiles { get; } = Bands.Select(band =>
        new PluginProfileDescriptor(band.Id, band.DisplayName,
            $"{band.Region} / {band.Mode} USBダイヤル周波数 {band.DialFrequencyHz / 1_000_000.0:F6} MHz",
            band.Id == DefaultBandId)).ToArray();
    public string? SelectedProfileId => selectedBandId;
    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get => ActiveBands().Select(CreateChannelRequest).ToArray();
    }
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays
    {
        get
        {
            return ActiveBands().Select((band, index) =>
                new FrequencyOverlayItem($"ft8-passband-{band.Id}", band.ChannelCenterFrequencyHz,
                    Ft8Receiver.OccupiedPassbandHz, string.Empty, true,
                    index == 0 ? PrimaryBandOverlayFill : SimultaneousBandOverlayFill, "Transparent", "#FFFFFFFF",
                    -1, true, 1, $"{band.DisplayName} USB + 200～3000 Hz"))
                .ToArray();
        }
    }
    public IReadOnlyList<WaterfallAnnotationItem> WaterfallAnnotations
    {
        get
        {
            WaterfallReference? reference = Volatile.Read(ref waterfallReference);
            if (reference is null) return [];
            lock (gate)
                return waterfallHistory
                    .Where(item => item.StreamId == reference.StreamId)
                    .Select(CreateWaterfallAnnotation)
                    .ToArray();
        }
    }
    public DateTimeOffset? WaterfallReferenceTime =>
        Volatile.Read(ref waterfallReference)?.Time;
    public WaterfallDisplayRequest WaterfallDisplayRequest { get; } = new(
        WaterfallTimeMode.ThreeMinutes,
        Ft8Receiver.OccupiedPassbandHz * 10);
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("csv", "CSV", ".csv", "text/csv"),
        new("json", "JSON", ".json", "application/json")
    ];

    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;
    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler? WaterfallAnnotationsChanged;

    public FrameworkElement CreateMainView() => new Ft8PluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new Ft8SettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        host = hostContext;
        if (Ft8BandCatalog.LoadWarning is { } bandListWarning)
            host.Logger.Log(PluginLogLevel.Warning, "ft8.bands.fallback", bandListWarning);
        viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
        host.Tuning.AppliedConfigurationChanged += OnTuningChanged;
        PluginSettingsDocument? document = await host.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try { settings = (JsonSerializer.Deserialize<Ft8Settings>(document.Json) ?? new()).Normalize(); }
            catch (JsonException exception)
            {
                host.Logger.Log(PluginLogLevel.Warning, "ft8.settings.invalid",
                    "Invalid FT8 settings; defaults are used.", exception);
                settings = new();
            }
        }
        selectedBandId = settings.SelectedBandId;
        enabledAdditionalBandIds = settings.AdditionalBandIds?.ToArray() ?? [];
        historyWriter = CreateHistoryWriter(hostContext);
        host.Dispatcher.Post(() =>
        {
            viewModel.Configure(settings, SelectedBand());
            RefreshNearbyBandOptions(hostContext.Tuning.Current, false);
        });
        LoadHistory();
        diagnosticsTimer = host.TimeProvider.CreateTimer(_ =>
        {
            IPluginHostContext? context = host;
            if (context is not null)
            {
                float? signalLevelDbm = context.ReceiverTelemetry?.SignalLevelDbm;
                context.Dispatcher.Post(() => viewModel.ApplyDiagnostics(receiver.Diagnostics, signalLevelDbm));
            }
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        SetStatus("設定済み");
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
        Ft8Band band = SelectedBand();
        PluginTuningResult result = await context.Tuning.RequestAsync(
                CreateTuningRequest(band, ConfiguredAdditionalBands(band)), cancellationToken)
            .ConfigureAwait(false);
        ValidateTuningResult(result);
        RefreshNearbyBandOptions(result, true);
        SetStatus($"待機中 / {band.DisplayName}");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            ResetAllReceivers();
            lastCaptureMetadata = null;
            lastCaptureChannelConfiguration = null;
            Volatile.Write(ref waterfallReference, null);
        }
        lock (gate) waterfallHistory.Clear();
        WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        audioSequence = 0;
        viewModel.CaptureStatus = $"IQ録音: 直前{CaptureDurationSeconds}秒を常時保持中";
        SetStatus($"受信中 / {SelectedBand().DisplayName}");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        Ft8Band primaryBand = SelectedBand();
        (Ft8Band Band, IChannelIqBlockLease Block)[] receivedBlocks = ActiveBands()
            .Select(band => (Band: band, Block: blocks.FirstOrDefault(item =>
                item.Metadata.Configuration.RequestId == CreateChannelRequestId(band))))
            .Where(item => item.Block is not null)
            .Select(item => (item.Band, item.Block!))
            .ToArray();
        if (receivedBlocks.Length == 0) return ValueTask.CompletedTask;
        IChannelIqBlockLease? primaryBlock = receivedBlocks
            .FirstOrDefault(item => item.Band.Id == primaryBand.Id).Block;
        float[]? monitor = settings.MonitorAudioEnabled && primaryBlock is not null
            ? ArrayPool<float>.Shared.Rent(primaryBlock.Samples.Length + 2) : null;
        try
        {
            lock (processingGate)
            {
                if (State != PluginLifecycleState.Streaming)
                    return ValueTask.CompletedTask;
                foreach ((Ft8Band band, IChannelIqBlockLease block) in receivedBlocks)
                {
                    bool isPrimary = band.Id == primaryBand.Id;
                    Ft8Receiver bandReceiver = isPrimary ? receiver : GetAdditionalReceiver(band);
                    if (isPrimary)
                    {
                        lastCaptureMetadata = block.Metadata.Source;
                        lastCaptureChannelConfiguration = block.Metadata.Configuration;
                        UpdateWaterfallReference(block.Metadata.Source);
                        pretriggerBuffer.Write(block.Samples.Span,
                            block.Metadata.Configuration.OutputSampleRateHz);
                    }
                    bandReceiver.ProcessChannel(block.Samples.Span, block.Metadata, settings,
                        isPrimary && monitor is not null
                            ? monitor.AsSpan(0, block.Samples.Length + 2)
                            : Span<float>.Empty,
                        out int monitorCount);
                    if (isPrimary && monitor is not null)
                        SubmitMonitorAudio(monitor.AsSpan(0, monitorCount), block.Metadata.Source);
                }
            }
        }
        finally
        {
            if (monitor is not null) ArrayPool<float>.Shared.Return(monitor);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        Ft8Band band = SelectedBand();
        return PluginProcessingWarmup.RunChannelAsync(
            context,
            CreateChannelRequestId(band),
            band.ChannelCenterFrequencyHz,
            Ft8Receiver.OutputSampleRateHz,
            Ft8Receiver.OccupiedPassbandHz,
            (samples, metadata) =>
            {
                lock (processingGate)
                    receiver.ProcessChannel(samples, metadata, settings);
            },
            () =>
            {
                lock (processingGate) receiver.Reset();
            },
            cancellationToken);
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming || block.Samples.IsEmpty)
            return ValueTask.CompletedTask;
        int monitorCapacity = checked((int)Math.Ceiling(block.Samples.Length *
            (double)Ft8Receiver.OutputSampleRateHz / block.Metadata.SampleRateHz) + 4);
        float[]? monitor = settings.MonitorAudioEnabled
            ? ArrayPool<float>.Shared.Rent(monitorCapacity) : null;
        try
        {
            lock (processingGate)
            {
                if (State != PluginLifecycleState.Streaming)
                    return ValueTask.CompletedTask;
                Ft8Band primaryBand = SelectedBand();
                lastCaptureMetadata = block.Metadata;
                lastCaptureChannelConfiguration = null;
                UpdateWaterfallReference(block.Metadata);
                pretriggerBuffer.Write(block.Samples.Span, block.Metadata.SampleRateHz);
                receiver.ProcessRaw(block.Samples.Span, block.Metadata,
                    primaryBand.ChannelCenterFrequencyHz, settings,
                    monitor is null ? Span<float>.Empty : monitor.AsSpan(0, monitorCapacity),
                    out int monitorCount);
                if (monitor is not null)
                    SubmitMonitorAudio(monitor.AsSpan(0, monitorCount), block.Metadata);
                foreach (Ft8Band band in ActiveBands().Skip(1))
                    GetAdditionalReceiver(band).ProcessRaw(block.Samples.Span, block.Metadata,
                        band.ChannelCenterFrequencyHz, settings);
            }
        }
        finally
        {
            if (monitor is not null) ArrayPool<float>.Shared.Return(monitor);
        }
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        await DrainAllReceiversAsync(cancellationToken).ConfigureAwait(false);
        lock (processingGate) ResetAllReceivers();
        viewModel.CaptureStatus = "IQ録音: 待機";
        SetStatus($"待機中 / {SelectedBand().DisplayName}");
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        viewModel.CaptureStatus = "IQ録音: 待機";
        SetStatus("停止中");
        return ValueTask.CompletedTask;
    }

    public async ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        Ft8Band next = Bands.FirstOrDefault(item => item.Id == profileId) ??
            throw new ArgumentException($"Unknown weak-signal profile '{profileId}'.", nameof(profileId));
        if (profileId == selectedBandId) return;
        PluginTuningResult? appliedTuning = host?.Tuning.Current;
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
            PluginTuningResult result = await context.Tuning.RequestAsync(CreateTuningRequest(next), cancellationToken)
                .ConfigureAwait(false);
            ValidateTuningResult(result);
            appliedTuning = result;
        }
        lock (processingGate)
        {
            selectedBandId = profileId;
            Volatile.Write(ref enabledAdditionalBandIds, []);
            settings = settings with
            {
                SelectedBandId = profileId,
                Mode = next.Mode,
                AdditionalBandIds = []
            };
            ResetAllReceivers();
            pretriggerBuffer.Reset();
            lastCaptureMetadata = null;
            lastCaptureChannelConfiguration = null;
            Volatile.Write(ref waterfallReference, null);
        }
        lock (gate) waterfallHistory.Clear();
        WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        PersistSettings();
        IPluginHostContext? currentHost = host;
        currentHost?.Dispatcher.Post(() =>
        {
            viewModel.RollbackBand(next);
            viewModel.UpdateNearbyBands(
                appliedTuning is null
                    ? []
                    : Ft8NearbyBandPolicy.FindCandidates(next, appliedTuning, Bands),
                []);
        });
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        SetStatus(State == PluginLifecycleState.Streaming
            ? $"受信中 / {next.DisplayName}" : $"待機中 / {next.DisplayName}");
    }
}
