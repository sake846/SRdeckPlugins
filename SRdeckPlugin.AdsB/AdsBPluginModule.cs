using System.Globalization;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.AdsB.Dsp;
using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.AdsB.Protocols;
using SRdeckPlugin.AdsB.ViewModels;
using SRdeckPlugin.AdsB.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AdsB;

public sealed partial class AdsBPluginModule : PluginModuleBase, IIqBlockConsumer, IPluginChannelBlockConsumer, IPluginViewProvider,
    IFrequencyOverlayProvider, IPluginResultProvider, IPluginExportProvider, IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup
{
    private const long FrequencyHz = 1_090_000_000;
    private readonly object gate = new();
    private readonly object processingGate = new();
    private readonly ModeSReceiver receiver = new();
    private readonly CprDecoder cprDecoder = new();
    private readonly Dictionary<string, AircraftState> aircraft = new(StringComparer.Ordinal);
    private readonly List<ExportRecord> history = [];
    private PluginJsonLinesHistoryWriter<ExportRecord>? historyWriter;
    private readonly AdsBViewModel viewModel = new();
    private readonly PackedIqHistoryBuffer pretriggerBuffer = new(3);
    private readonly IqStreamContinuityTracker continuity = new();
    private readonly object pendingMessageGate = new();
    private readonly Queue<AdsBMessageRow> pendingMessages = [];
    private IPluginHostContext? host;
    private IPluginMetrics metrics = NullPluginMetrics.Instance;
    private AdsBSettings settings = new();
    private IqBlockMetadata? lastCaptureMetadata;
    private int captureSaveInProgress;
    private long lastViewSnapshotTick;
    private bool messageDrainPosted;
    private int insufficientSampleRateDetected;

    public AdsBPluginModule()
    {
        RegisterStreamReset(pretriggerBuffer.Reset);
        viewModel.SettingsChanged = (maximum, retention, historyLimit, trailPoints, latitude, longitude) =>
        {
            settings = settings with
            {
                MaximumAircraft = maximum, RetentionMinutes = retention,
                ReceiverLatitude = latitude, ReceiverLongitude = longitude,
                MaximumTrailPoints = trailPoints,
                MaximumHistory = historyLimit
            };
            settings = settings.Normalize();
            PruneStoredHistory();
            cprDecoder.ConfigureReceiverReference(settings.ReceiverLatitude, settings.ReceiverLongitude);
            PersistSettings();
        };
        viewModel.ClearRequested = () =>
        {
            lock (gate) { aircraft.Clear(); history.Clear(); }
            lock (pendingMessageGate) pendingMessages.Clear();
            DeleteHistoryFile();
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
        settings = new AdsBSettings().Normalize();
        cprDecoder.ConfigureReceiverReference(settings.ReceiverLatitude, settings.ReceiverLongitude);
        viewModel.SynchronizeSettings(settings.MaximumAircraft, settings.RetentionMinutes,
            settings.MaximumHistory, settings.MaximumTrailPoints,
            settings.ReceiverLatitude, settings.ReceiverLongitude,
            settings.SaveRawModeS, settings.HistoryRecordMode);
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "adsb",
        "ADS-B",
        "1090 MHz Mode S Extended Squitter receiver",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.ChannelIqConsumer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.ResultPublisher | PluginCapabilities.Export |
        PluginCapabilities.FrequencyOverlay,
        "SRdeck",
        "GPL-3.0");

    public event EventHandler? FrequencyOverlaysChanged { add { } remove { } }
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays { get; } =
    [
        new("adsb-1090", FrequencyHz, 1_850_000, string.Empty, true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary), "Transparent", "#FFFFFFFF", -1, ToolTip: "ADS-B / 1090 MHz")
    ];

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "Mode Sパルス検出・復調・ADS-B解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "プリアンブル検出、PPM復調、CRC検証、CPR位置復号、機体状態更新");
    public PluginIqPreferences IqPreferences { get; } = new(8);
    public IReadOnlyList<PluginChannelRequest> ChannelRequests { get; } =
    [
        new(
            "adsb-1090",
            FrequencyHz,
            1_850_000,
            ModeSReceiver.DemodulationSampleRateHz,
            4_000_000,
            2_500_000,
            33,
            2,
            8,
            true,
            AccelerationPreference: PluginChannelAccelerationPreference.Cpu)
    ];
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("csv", "CSV", ".csv", "text/csv"),
        new("json", "JSON", ".json", "application/json")
    ];
    internal int RawPipelineConfigurationCount =>
        receiver.RawPipelineConfigurationCount;

    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() => new AdsBPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new AdsBSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        metrics = hostContext.Metrics;
        host = hostContext;
        viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
        host.Tuning.AppliedConfigurationChanged += OnTuningChanged;
        PluginSettingsDocument? document = await host.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try
            {
                settings = (JsonSerializer.Deserialize<AdsBSettings>(document.Json) ?? new AdsBSettings()).Normalize();
            }
            catch (JsonException exception)
            {
                host.Logger.Log(PluginLogLevel.Warning, "adsb.settings.invalid",
                    "ADS-B settings were invalid; defaults are being used.", exception);
                settings = new();
            }
        }
        cprDecoder.ConfigureReceiverReference(settings.ReceiverLatitude, settings.ReceiverLongitude);
        historyWriter = CreateHistoryWriter(hostContext);
        viewModel.SynchronizeSettings(settings.MaximumAircraft, settings.RetentionMinutes,
            settings.MaximumHistory, settings.MaximumTrailPoints,
            settings.ReceiverLatitude, settings.ReceiverLongitude,
            settings.SaveRawModeS, settings.HistoryRecordMode);
        LoadHistory();
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
        PluginTuningResult result = await context.Tuning.RequestAsync(new PluginTuningRequest(
            "adsb-1090",
            "ADS-B 1090 MHz",
            [new TuningTarget(FrequencyHz, 1_900_000)],
            FrequencyHz,
            2_000_000,
            null,
            true,
            false,
            PluginGainPreference.Automatic), cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PluginTuningOutcome.Rejected)
            throw new PluginActivationRejectedException(
                "ADS-B は 2.0 MS/s 以上が必要です。受信を停止して RATE を 2 MS/s 以上に変更してから、再度 ADS-B を選択してください。\n" +
                result.Message);
        if (result.SampleRateHz < ModeSReceiver.MinimumInputSampleRateHz)
            throw new PluginActivationRejectedException(
                "ADS-B は 2.0 MS/s 以上が必要です。受信を停止して RATE を 2 MS/s 以上に変更してから、再度 ADS-B を選択してください。");
        SetStatus($"待機中 / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz / {result.SampleRateHz / 1_000_000.0:F2} MS/s");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.ResetChannel();
            cprDecoder.Reset();
            continuity.Reset();
            lastViewSnapshotTick = 0;
        }
        viewModel.CaptureStatus = "IQ録音: 直前3秒を常時保持中";
        SetStatus($"受信中 / {(host?.Tuning.Current.CenterFrequencyHz is > 0 ? host.Tuning.Current.CenterFrequencyHz : FrequencyHz) / 1_000_000.0:F3} MHz");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
        => ConsumeCore(block.Samples.Span, block.Metadata, null, cancellationToken);

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        if (blocks.Count != 1)
            throw new ArgumentException("ADS-B requires exactly one standard channel block.", nameof(blocks));
        IChannelIqBlockLease block = blocks[0];
        return ConsumeCore(block.Samples.Span, block.Metadata.Source, block.Metadata, cancellationToken);
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        int blockCount = Math.Clamp(context.BlockCount, 1, 8);
        int inputSampleRateHz = Math.Max(
            ModeSReceiver.MinimumInputSampleRateHz,
            context.SampleRateHz);
        return new ValueTask(Task.Run(() =>
        {
            Complex32[] samples = ArrayPool<Complex32>.Shared.Rent(
                ModeSReceiver.DemodulationSampleRateHz / 10);
            try
            {
                int sampleCount = ModeSReceiver.DemodulationSampleRateHz / 10;
                samples.AsSpan(0, sampleCount).Clear();
                lock (processingGate)
                {
                    try
                    {
                        Guid streamId = Guid.NewGuid();
                        var configuration = new AppliedChannelConfiguration(
                            "adsb-warm-up",
                            FrequencyHz,
                            FrequencyHz,
                            inputSampleRateHz,
                            ModeSReceiver.DemodulationSampleRateHz,
                            1_850_000,
                            1,
                            1,
                            1,
                            1,
                            33,
                            2,
                            0,
                            "startup-warm-up");
                        for (int block = 0; block < blockCount; block++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            long sourceStart = (long)block * inputSampleRateHz / 10;
                            var source = new IqBlockMetadata(
                                streamId,
                                0,
                                block,
                                sourceStart,
                                Stopwatch.GetTimestamp(),
                                DateTimeOffset.UtcNow,
                                inputSampleRateHz,
                                FrequencyHz,
                                inputSampleRateHz / 10,
                                IqInputSource.Playback,
                                block == 0
                                    ? IqDiscontinuity.StreamStarted
                                    : IqDiscontinuity.None);
                            var metadata = new ChannelIqBlockMetadata(
                                source,
                                (long)block * sampleCount,
                                0,
                                sampleCount,
                                configuration);
                            receiver.ProcessChannel(
                                samples.AsSpan(0, sampleCount),
                                metadata);
                        }
                    }
                    finally
                    {
                        receiver.ResetChannel(0, inputSampleRateHz);
                        receiver.ResetStatistics();
                        cprDecoder.Reset();
                        continuity.Reset();
                    }
                }
            }
            finally
            {
                ArrayPool<Complex32>.Shared.Return(samples);
            }
        }, cancellationToken));
    }

    private ValueTask ConsumeCore(
        ReadOnlySpan<Complex32> samples,
        IqBlockMetadata metadata,
        ChannelIqBlockMetadata? channelMetadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        if (metadata.SampleRateHz < ModeSReceiver.MinimumInputSampleRateHz)
        {
            if (Interlocked.Exchange(ref insufficientSampleRateDetected, 1) == 0)
                SetStatus(GetMinimumSampleRateMessage(metadata.SampleRateHz));
            return ValueTask.CompletedTask;
        }
        Interlocked.Exchange(ref insufficientSampleRateDetected, 0);

        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            lastCaptureMetadata = metadata;
            metrics.AddCounter(PluginProcessingStage.Input, "blocks");
            metrics.AddCounter(PluginProcessingStage.Input, "samples", samples.Length, "samples");
            int processingSampleRate = channelMetadata?.Configuration.OutputSampleRateHz ?? metadata.SampleRateHz;
            metrics.SetGauge(PluginProcessingStage.Input, "sample_rate", processingSampleRate, "Hz");
            if (continuity.Observe(metadata).RequiresReset)
            {
                if (channelMetadata is null)
                    receiver.Reset(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
                else
                    receiver.ResetChannel(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
                cprDecoder.Reset();
            }

            pretriggerBuffer.Write(samples, processingSampleRate);
            long demodulationStarted = Stopwatch.GetTimestamp();
            IReadOnlyList<ModeSFrame> frames = channelMetadata is ChannelIqBlockMetadata channel
                ? receiver.ProcessChannel(samples, channel)
                : receiver.Process(samples, metadata);
            metrics.RecordDuration(PluginProcessingStage.Demodulation, "receiver",
                Stopwatch.GetElapsedTime(demodulationStarted));
            metrics.AddCounter(PluginProcessingStage.Detection, "frames", frames.Count, "frames");
            foreach (ModeSFrame frame in frames)
            {
                if (!AdsBMessageParser.TryParse(frame, out AdsBMessage? message) || message is null)
                {
                    metrics.AddCounter(PluginProcessingStage.ProtocolDecode, "rejected", 1, "frames");
                    continue;
                }
                metrics.AddCounter(PluginProcessingStage.ProtocolDecode, "accepted", 1, "frames");
                ApplyMessage(frame, message);
            }
            long snapshotTick = Environment.TickCount64;
            // Updating the aircraft list and map is comparatively expensive.  Do not let a
            // burst of decoded frames turn into one UI update per IQ block.
            if (snapshotTick - lastViewSnapshotTick >= 250)
            {
                lastViewSnapshotTick = snapshotTick;
                PublishViewSnapshot();
            }
        }
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.ResetChannel();
            cprDecoder.Reset();
        }
        viewModel.CaptureStatus = "IQ録音: 待機";
        SetStatus("待機中");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        viewModel.CaptureStatus = "IQ録音: 待機";
        SetStatus("停止中");
        return ValueTask.CompletedTask;
    }
}
