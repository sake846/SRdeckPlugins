using System.IO;
using System.Buffers.Binary;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Cw.Dsp;
using SRdeckPlugin.Cw.Models;
using SRdeckPlugin.Cw.ViewModels;
using SRdeckPlugin.Cw.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Cw;

public sealed partial class CwPluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    IPluginViewProvider, IFrequencyOverlayProvider, IPluginResultProvider,
    IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup,
    IWaterfallAnnotationProvider, IWaterfallDisplayProvider, IPluginExportProvider
{
    public const string ChannelRequestId = "cw-skimmer-10khz";
    public const long DefaultCenterFrequencyHz = 7_030_000;
    private const int CaptureDurationSeconds = 10;
    private const int MonitorAudioCenterHz = 800;
    // These values affect only the user-facing monitor audio. CW detection
    // still receives the unmodified channel samples.
    private const float MonitorTargetRms = 0.22f;
    private const float MonitorMinimumGain = 0.1f;
    private const float MonitorMaximumGain = 2_000f;
    private const float MonitorPeakLimit = 0.95f;
    private const int MaximumWaterfallAnnotations = 4_096;
    private const string WaterfallTextColor = "#FFD6D6D6";

    private readonly object processingGate = new();
    private readonly object resultGate = new();
    private readonly CwSkimmerReceiver receiver = new();
    private readonly CwViewModel viewModel = new();
    private readonly Dictionary<string, DateTimeOffset> publishedCallsigns =
        new(StringComparer.Ordinal);
    private readonly List<CwWaterfallCharacter> waterfallCharacters = [];
    private readonly PackedIqHistoryBuffer pretriggerBuffer = new(CaptureDurationSeconds);
    private PluginJsonLinesHistoryWriter<CwDecodeResult>? historyWriter;
    private CwSettings settings = new();
    private long centerFrequencyHz = DefaultCenterFrequencyHz;
    private long audioSequence;
    private double monitorAudioPhase;
    private float monitorPower;
    private float monitorGain = 1f;
    private byte[] monitorPcmBuffer = [];
    private long nextDiagnosticsTick;
    private long nextWaterfallAnnotationId;
    private IqBlockMetadata? lastCaptureMetadata;
    private AppliedChannelConfiguration? lastCaptureChannelConfiguration;
    private int captureSaveInProgress;
    private WaterfallReference? waterfallReference;

    public CwPluginModule()
    {
        RegisterStreamReset(pretriggerBuffer.Reset);
        receiver.TextDecoded += OnTextDecoded;
        viewModel.SettingsChanged = ApplySettings;
        viewModel.ResetSettingsRequested = ResetSettingsAsync;
        viewModel.CaptureRequested = StartIqCapture;
    }

    public async ValueTask ResetSettingsAsync()
    {
        if (HostContext is not null)
        {
            await HostContext.Settings.DeleteAsync().ConfigureAwait(false);
        }
        settings = new CwSettings().Normalize();
        ApplySettings(settings);
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.Configure(settings, settings.CenterFrequencyHz);
        });
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "cw",
        "CW Skimmer",
        "10/20/50 kHz multi-signal CW skimmer with adaptive Morse decoding",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
         PluginCapabilities.ChannelIqConsumer | PluginCapabilities.AudioProducer |
         PluginCapabilities.MainView |
         PluginCapabilities.SettingsView | PluginCapabilities.FrequencyOverlay |
         PluginCapabilities.ResultPublisher | PluginCapabilities.Export | PluginCapabilities.Headless |
         PluginCapabilities.WaterfallAnnotation | PluginCapabilities.WaterfallDisplay,
        "SRdeck",
        "GPL-3.0");

    public IReadOnlyList<PluginChannelRequest> ChannelRequests =>
    [
        new(
            ChannelRequestId,
            Volatile.Read(ref centerFrequencyHz),
            settings.ReceiveBandwidthHz,
            CwBandwidth.GetOutputSampleRateHz(settings.ReceiveBandwidthHz),
            MaximumIntermediateSampleRateHz:
                CwBandwidth.GetMaximumIntermediateSampleRateHz(settings.ReceiveBandwidthHz),
            MinimumIntermediateSampleRateHz:
                CwBandwidth.GetMinimumIntermediateSampleRateHz(settings.ReceiveBandwidthHz),
            FirTaps: 65,
            CicStages: 2,
            RequestedQueueCapacity: 8,
            AllowRawIqFallback: false)
        {
            // The CW output intentionally retains the complete configured
            // receive passband; its FFT/skimmer applies the signal-level
            // selectivity after this rate-conversion boundary.
            StopbandBandwidthHz = CwBandwidth.GetOutputSampleRateHz(settings.ReceiveBandwidthHz)
        }
    ];

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "FFT解析・CW信号追尾・適応モールス復号",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "帯域に応じたFFT、選択帯域の信号検出・追尾、短点速度推定、モールス文字復号");

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
    [
        new(
            "cw-skimmer-passband",
            Volatile.Read(ref centerFrequencyHz),
            settings.ReceiveBandwidthHz,
            string.Empty,
            true,
            PluginReceiverBandColors.WithAlpha(0x40, PluginReceiverBandColors.Primary),
            "#00000000",
            "#FFFFFFFF",
            -1,
            ToolTip: $"CW Skimmer / {CwBandwidth.Format(settings.ReceiveBandwidthHz)}受信帯域")
    ];

    public WaterfallDisplayRequest WaterfallDisplayRequest => new(
        WaterfallTimeMode.Uncompressed,
        CwBandwidth.DefaultHz * 10);

    public IReadOnlyList<WaterfallAnnotationItem> WaterfallAnnotations
    {
        get
        {
            WaterfallReference? reference = Volatile.Read(ref waterfallReference);
            if (reference is null) return [];
            lock (resultGate)
                return waterfallCharacters
                    .Where(item => item.StreamId == reference.StreamId)
                    .Select(item => item.Annotation)
                    .ToArray();
        }
    }

    public DateTimeOffset? WaterfallReferenceTime =>
        Volatile.Read(ref waterfallReference)?.Time;

    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler? WaterfallAnnotationsChanged;
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("json", "JSON", ".json", "application/json"),
        new("csv", "CSV", ".csv", "text/csv")
    ];

    public FrameworkElement CreateMainView() => new CwPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new CwSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        PluginSettingsDocument? document = await hostContext.Settings.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (document is not null)
        {
            try
            {
                settings = (JsonSerializer.Deserialize<CwSettings>(document.Json) ?? new()).Normalize();
            }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "cw.settings.invalid",
                    "Invalid CW skimmer settings; defaults are used.", exception);
                settings = new();
            }
        }

        hostContext.Tuning.AppliedConfigurationChanged += HandleAppliedConfigurationChanged;
        historyWriter = CreateHistoryWriter(hostContext);

        long initialCenter = ResolveCenterFrequency(hostContext.Tuning.Current, settings.CenterFrequencyHz);
        Volatile.Write(ref centerFrequencyHz, initialCenter);
        hostContext.Dispatcher.Post(() =>
        {
            viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
            viewModel.Configure(settings, initialCenter);
            viewModel.Status = $"設定済み / {CwBandwidth.Format(settings.ReceiveBandwidthHz)}帯域";
        });
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext context = HostContext ??
            throw new InvalidOperationException("The plugin is not initialized.");
        long requestedCenter = ResolveCenterFrequency(context.Tuning.Current, settings.CenterFrequencyHz);
        SetCenterFrequency(requestedCenter);

        PluginTuningResult result = await context.Tuning.RequestAsync(
            CreateTuningRequest(requestedCenter), cancellationToken).ConfigureAwait(false);

        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new PluginActivationRejectedException(result.Message);
        if (result.PassbandLowerFrequencyHz > requestedCenter - settings.ReceiveBandwidthHz / 2 ||
            result.PassbandUpperFrequencyHz < requestedCenter + settings.ReceiveBandwidthHz / 2)
            throw new PluginActivationRejectedException(
                $"The receiver cannot provide the requested {CwBandwidth.Format(settings.ReceiveBandwidthHz)} CW passband.");

        SetCenterFrequency(requestedCenter);
        context.Dispatcher.Post(() =>
            viewModel.Status = $"待機中 / {requestedCenter / 1_000_000.0:F6} MHz / " +
                $"{CwBandwidth.Format(settings.ReceiveBandwidthHz)}");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            ResetMonitorAudioState();
            lastCaptureMetadata = null;
            lastCaptureChannelConfiguration = null;
            Volatile.Write(ref waterfallReference, null);
        }
        lock (resultGate)
        {
            publishedCallsigns.Clear();
            waterfallCharacters.Clear();
        }
        WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        Interlocked.Exchange(ref audioSequence, 0);
        Volatile.Write(ref nextDiagnosticsTick, 0);
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.CompletePendingSentences();
            viewModel.ActiveSignals = 0;
            viewModel.CaptureStatus = $"IQ録音: 直前{CaptureDurationSeconds}秒を常時保持中";
            viewModel.Status = $"受信中 / {Volatile.Read(ref centerFrequencyHz) / 1_000_000.0:F6} MHz / " +
                $"{CwBandwidth.Format(settings.ReceiveBandwidthHz)}";
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        IChannelIqBlockLease? block = blocks.FirstOrDefault(candidate =>
            string.Equals(candidate.Metadata.Configuration.RequestId, ChannelRequestId,
                StringComparison.Ordinal));
        if (block is null || block.Samples.IsEmpty) return ValueTask.CompletedTask;

        CwSettings currentSettings = settings;
        bool discontinuous = block.Metadata.Source.Discontinuity != IqDiscontinuity.None;
        if (discontinuous)
        {
            HostContext?.Audio.Reset();
            HostContext?.Dispatcher.Post(viewModel.CompletePendingSentences);
            lock (resultGate)
                waterfallCharacters.Clear();
            WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdateWaterfallReference(block.Metadata.Source);
        int activeSignalCount = 0;
        long processedFrameCount = 0;
        bool updateDiagnostics;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming)
                return ValueTask.CompletedTask;
            if (discontinuous)
            {
                receiver.Reset();
                ResetMonitorAudioState();
                pretriggerBuffer.Reset();
                lastCaptureMetadata = null;
                lastCaptureChannelConfiguration = null;
            }
            lastCaptureMetadata = block.Metadata.Source;
            lastCaptureChannelConfiguration = block.Metadata.Configuration;
            pretriggerBuffer.Write(block.Samples.Span,
                block.Metadata.Configuration.OutputSampleRateHz);
            receiver.Process(block.Samples.Span, block.Metadata,
                currentSettings.MinimumSnrDb, currentSettings.MaximumSignals,
                currentSettings.ReceiveBandwidthHz, currentSettings.Language);
            SubmitMonitorAudio(block.Samples.Span, block.Metadata, currentSettings, discontinuous);
            long now = Environment.TickCount64;
            updateDiagnostics = now >= Volatile.Read(ref nextDiagnosticsTick);
            if (updateDiagnostics)
            {
                Volatile.Write(ref nextDiagnosticsTick, now + 500);
                activeSignalCount = receiver.ActiveSignalCount;
                processedFrameCount = receiver.ProcessedFrameCount;
            }
        }

        if (updateDiagnostics)
        {
            HostContext?.Dispatcher.Post(() =>
                viewModel.UpdateProcessingDiagnostics(activeSignalCount, processedFrameCount));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        CwSettings currentSettings = settings;
        return PluginProcessingWarmup.RunChannelAsync(
            context,
            ChannelRequestId,
            Volatile.Read(ref centerFrequencyHz),
            CwBandwidth.GetOutputSampleRateHz(currentSettings.ReceiveBandwidthHz),
            currentSettings.ReceiveBandwidthHz,
            (samples, metadata) =>
            {
                lock (processingGate)
                    receiver.Process(samples, metadata, currentSettings.MinimumSnrDb,
                        currentSettings.MaximumSignals, currentSettings.ReceiveBandwidthHz,
                        currentSettings.Language);
            },
            () =>
            {
                lock (processingGate) receiver.Reset();
            },
            cancellationToken);
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            ResetMonitorAudioState();
            lastCaptureMetadata = null;
            lastCaptureChannelConfiguration = null;
        }
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.CompletePendingSentences();
            viewModel.ResetProcessingDiagnostics();
            viewModel.CaptureStatus = "IQ録音: 待機";
            viewModel.Status = "停止中";
        });
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.CompletePendingSentences();
            viewModel.ResetProcessingDiagnostics();
            viewModel.CaptureStatus = "IQ録音: 待機";
            viewModel.Status = "無効";
        });
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        receiver.TextDecoded -= OnTextDecoded;
        if (hostContext is not null)
            hostContext.Tuning.AppliedConfigurationChanged -= HandleAppliedConfigurationChanged;
        lock (processingGate)
        {
            receiver.Reset();
            ResetMonitorAudioState();
            lastCaptureMetadata = null;
            lastCaptureChannelConfiguration = null;
            Volatile.Write(ref waterfallReference, null);
        }
        lock (resultGate)
            waterfallCharacters.Clear();
        WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
    }
}
