using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Ais.Dsp;
using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Ais.ViewModels;
using SRdeckPlugin.Ais.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais;

public sealed partial class AisPluginModule : PluginModuleBase, IPluginChannelBlockConsumer, IPluginViewProvider,
    IFrequencyOverlayProvider, IPluginResultProvider, IPluginExportProvider,
    IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup
{
    public const long Ais1FrequencyHz = 161_975_000;
    public const long Ais2FrequencyHz = 162_025_000;
    public const long PreferredCenterFrequencyHz = 162_000_000;
    private readonly object gate = new();
    private readonly object processingGate = new();
    private readonly object pendingGate = new();
    private readonly AisReceiver channelA = new("AIS 1", Ais1FrequencyHz);
    private readonly AisReceiver channelB = new("AIS 2", Ais2FrequencyHz);
    private readonly Dictionary<uint, AisTargetState> targets = [];
    private readonly List<ExportRecord> history = [];
    private PluginJsonLinesHistoryWriter<ExportRecord>? historyWriter;
    private readonly Queue<AisMessageRow> pendingMessages = [];
    private readonly AisViewModel viewModel = new();
    private IPluginMetrics metrics = NullPluginMetrics.Instance;
    private AisSettings settings = new();
    private PluginTuningResult? lastTuningResult;
    private long lastViewSnapshotTick;
    private bool messageDrainPosted;
    private long audioSequence;

    public AisPluginModule()
    {
        viewModel.SettingsChanged = (maximum, retention, historyMaximum, trailPoints,
            audioEnabled, audioVolume, squelchEnabled, squelchThreshold, saveRaw, channelFilter) =>
        {
            bool wasAudioEnabled = settings.MonitorAudioEnabled;
            bool squelchChanged = settings.SquelchEnabled != squelchEnabled ||
                Math.Abs(settings.SquelchThresholdDbm - squelchThreshold) > 0.01f;
            settings = settings with
            {
                MaximumTargets = maximum, RetentionMinutes = retention, MonitorAudioEnabled = audioEnabled,
                MonitorAudioVolume = audioVolume, MaximumTrailPoints = trailPoints,
                SquelchEnabled = squelchEnabled, SquelchThresholdDbm = squelchThreshold,
                MaximumHistory = historyMaximum,
                SaveRawFrames = true, ChannelFilter = "both"
            };
            settings = settings.Normalize();
            ApplyReceiverSettings();
            PruneStoredHistory();
            if ((wasAudioEnabled && !settings.MonitorAudioEnabled) || squelchChanged)
                HostContext?.Audio.Reset();
            _ = PersistSettingsAsync();
        };
        viewModel.ClearRequested = () =>
        {
            lock (gate)
            {
                targets.Clear();
                history.Clear();
            }
            lock (pendingGate) pendingMessages.Clear();
            DeleteHistoryFile();
        };
        viewModel.ResetSettingsRequested = ResetSettingsAsync;
    }

    public async ValueTask ResetSettingsAsync()
    {
        if (HostContext is not null)
        {
            await HostContext.Settings.DeleteAsync().ConfigureAwait(false);
        }
        settings = new AisSettings().Normalize();
        ApplyReceiverSettings();
        viewModel.SynchronizeSettings(settings.MaximumTargets, settings.RetentionMinutes,
            settings.MaximumHistory, settings.MaximumTrailPoints,
            settings.MonitorAudioEnabled, settings.MonitorAudioVolume, settings.SquelchEnabled,
            settings.SquelchThresholdDbm, settings.SaveRawFrames,
            settings.ChannelFilter);
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "ais",
        "AIS",
        "Dual-channel maritime AIS GMSK receiver",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView |
        PluginCapabilities.AudioProducer |
        PluginCapabilities.SettingsView | PluginCapabilities.FrequencyOverlay |
        PluginCapabilities.ResultPublisher | PluginCapabilities.Export,
        "SRdeck",
        "GPL-3.0");

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "GMSK復調・HDLC検証・AIS解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "AIS 1/2のGMSK復調、フレーム検出、CRC検証、船舶状態更新");
    public IReadOnlyList<PluginChannelRequest> ChannelRequests { get; } =
    [
        new("ais-1", Ais1FrequencyHz, AisReceiver.ChannelBandwidthHz,
            AisReceiver.DemodulationSampleRateHz, 384_000, 192_000, 33, 2, 8, false),
        new("ais-2", Ais2FrequencyHz, AisReceiver.ChannelBandwidthHz,
            AisReceiver.DemodulationSampleRateHz, 384_000, 192_000, 33, 2, 8, false)
    ];
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays { get; } =
    [
        new("ais-1", Ais1FrequencyHz, AisReceiver.ChannelBandwidthHz, string.Empty, true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary), "Transparent", "#FFFFFFFF", -1, ToolTip: "AIS 1 / 161.975 MHz"),
        new("ais-2", Ais2FrequencyHz, AisReceiver.ChannelBandwidthHz, string.Empty, true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary), "Transparent", "#FFFFFFFF", -1, ToolTip: "AIS 2 / 162.025 MHz")
    ];
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("csv", "CSV", ".csv", "text/csv"),
        new("json", "JSON", ".json", "application/json")
    ];

    public event EventHandler? FrequencyOverlaysChanged { add { } remove { } }
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() => new AisPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new AisSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
        metrics = hostContext.Metrics;
        hostContext.Tuning.AppliedConfigurationChanged += OnTuningChanged;
        PluginSettingsDocument? document = await hostContext.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try
            {
                settings = (JsonSerializer.Deserialize<AisSettings>(document.Json) ?? new()).Normalize();
                if (!document.Json.Contains("\"MaximumHistory\"", StringComparison.OrdinalIgnoreCase))
                    settings = settings with { MaximumHistory = Math.Clamp(settings.MaximumTargets * 4, 2_000, 1_000_000) };
            }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "ais.settings.invalid",
                    "AIS settings were invalid; defaults are being used.", exception);
                settings = new();
            }
        }
        viewModel.SynchronizeSettings(settings.MaximumTargets, settings.RetentionMinutes,
            settings.MaximumHistory, settings.MaximumTrailPoints,
            settings.MonitorAudioEnabled, settings.MonitorAudioVolume,
            settings.SquelchEnabled, settings.SquelchThresholdDbm,
            settings.SaveRawFrames, settings.ChannelFilter);
        ApplyReceiverSettings();
        historyWriter = CreateHistoryWriter(hostContext);
        LoadHistory();
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext context = HostContext ??
            throw new InvalidOperationException("The plugin is not initialized.");
        PluginTuningResult result = await context.Tuning.RequestAsync(new(
            "ais-dual",
            "AIS 1 / AIS 2",
            [new(Ais1FrequencyHz, AisReceiver.ChannelBandwidthHz), new(Ais2FrequencyHz, AisReceiver.ChannelBandwidthHz)],
            PreferredCenterFrequencyHz,
            240_000,
            5_000,
            true,
            false,
            PluginGainPreference.Automatic), cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PluginTuningOutcome.Rejected)
            throw new InvalidOperationException($"AIS tuning was rejected: {result.Message}");
        lastTuningResult = result;
    }

    protected override ValueTask OnActivatedAsync()
    {
        PluginTuningResult result = lastTuningResult ??
            throw new InvalidOperationException("AIS tuning did not produce a result.");
        SetStatus($"待機中 / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz / 2 ch");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            channelA.Reset();
            channelB.Reset();
            lastViewSnapshotTick = 0;
            audioSequence = 0;
        }
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStreamStartedAsync()
    {
        SetStatus("受信中 / AIS 1・AIS 2");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming || blocks.Count == 0)
            return ValueTask.CompletedTask;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            float[]? mixedAudio = settings.MonitorAudioEnabled
                ? ArrayPool<float>.Shared.Rent(
                    blocks.Max(block => block.Samples.Length / 2 + 2)) : null;
            int mixedAudioCount = 0;
            int mixedChannelCount = 0;
            if (mixedAudio is not null) mixedAudio.AsSpan().Clear();
            try
            {
                foreach (IChannelIqBlockLease block in blocks)
                {
                    string requestId = block.Metadata.Configuration.RequestId;
                    AisReceiver? receiver = requestId switch
                    {
                        "ais-1" => channelA,
                        "ais-2" => channelB,
                        _ => null
                    };
                    if (receiver is null) continue;
                    metrics.AddCounter(PluginProcessingStage.Input, "samples", block.Samples.Length, "samples");
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    float[]? channelAudio = settings.MonitorAudioEnabled
                        ? ArrayPool<float>.Shared.Rent(block.Samples.Length / 2 + 2) : null;
                    IReadOnlyList<AisFrame> frames;
                    try
                    {
                        frames = receiver.ProcessChannel(block.Samples.Span, block.Metadata,
                            channelAudio is null ? Span<float>.Empty : channelAudio,
                            out int channelAudioCount);
                        if (channelAudio is not null && mixedAudio is not null && channelAudioCount > 0)
                        {
                            mixedAudioCount = Math.Max(mixedAudioCount, channelAudioCount);
                            for (int index = 0; index < channelAudioCount; index++)
                                mixedAudio[index] += channelAudio[index];
                            mixedChannelCount++;
                        }
                    }
                    finally
                    {
                        if (channelAudio is not null) ArrayPool<float>.Shared.Return(channelAudio);
                    }
                    metrics.RecordDuration(PluginProcessingStage.Demodulation, requestId,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started));
                    metrics.AddCounter(PluginProcessingStage.Detection, "frames", frames.Count, "frames");
                    foreach (AisFrame frame in frames)
                    {
                        if (AisMessageParser.TryParse(frame, out AisMessage? message) && message is not null)
                        {
                            metrics.AddCounter(PluginProcessingStage.ProtocolDecode, "accepted", 1, "frames");
                            Publish(frame, message);
                        }
                        else
                        {
                            metrics.AddCounter(PluginProcessingStage.ProtocolDecode, "rejected", 1, "frames");
                        }
                    }
                }
                if (mixedAudio is not null && mixedChannelCount > 0)
                {
                    float scale = 1f / mixedChannelCount;
                    for (int index = 0; index < mixedAudioCount; index++) mixedAudio[index] *= scale;
                    SubmitMonitorAudio(mixedAudio.AsSpan(0, mixedAudioCount),
                        blocks[0].Metadata.Source);
                }
                long now = Environment.TickCount64;
                if (now - lastViewSnapshotTick >= 250)
                {
                    lastViewSnapshotTick = now;
                    PublishViewSnapshot();
                }
            }
            finally
            {
                if (mixedAudio is not null) ArrayPool<float>.Shared.Return(mixedAudio);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void ApplyReceiverSettings()
    {
        lock (processingGate)
        {
            float thresholdDbfs = HostContext?.ReceiverTelemetry?.DbmToDbfs(settings.SquelchThresholdDbm)
                ?? (settings.SquelchThresholdDbm - (-80f));
            foreach (AisReceiver receiver in new[] { channelA, channelB })
            {
                receiver.IsSquelchEnabled = settings.SquelchEnabled;
                receiver.SquelchThresholdDbfs = thresholdDbfs;
            }
        }
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken) =>
        PluginProcessingWarmup.RunChannelAsync(
            context,
            "ais-warm-up",
            PreferredCenterFrequencyHz,
            AisReceiver.DemodulationSampleRateHz,
            AisReceiver.ChannelBandwidthHz,
            (samples, metadata) =>
            {
                lock (processingGate)
                {
                    channelA.ProcessChannel(samples, metadata with
                    {
                        Configuration = metadata.Configuration with
                        {
                            RequestId = "ais-1",
                            ChannelCenterFrequencyHz = Ais1FrequencyHz
                        }
                    });
                    channelB.ProcessChannel(samples, metadata with
                    {
                        Configuration = metadata.Configuration with
                        {
                            RequestId = "ais-2",
                            ChannelCenterFrequencyHz = Ais2FrequencyHz
                        }
                    });
                }
            },
            () =>
            {
                lock (processingGate)
                {
                    channelA.Reset();
                    channelB.Reset();
                }
            },
            cancellationToken);

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            channelA.Reset();
            channelB.Reset();
        }
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStreamStoppedAsync()
    {
        SetStatus("待機中");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDeactivatedAsync()
    {
        SetStatus("停止中");
        return ValueTask.CompletedTask;
    }
}
