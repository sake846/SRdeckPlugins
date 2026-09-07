using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Dsp;
using SRdeckPlugin.HfFax.Models;
using SRdeckPlugin.HfFax.Storage;
using SRdeckPlugin.HfFax.ViewModels;
using SRdeckPlugin.HfFax.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.HfFax;

public sealed partial class HfFaxPluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    ILivePluginProfileProvider, IPluginViewProvider, IFrequencyOverlayProvider,
    IPluginResultProvider, IPluginExportProvider, IPluginProcessingDiagnosticsProvider,
    IPluginProcessingWarmup, IWaterfallDisplayProvider
{
    public const string ChannelRequestId = "hffax-channel";
    public const string DefaultStationId = "jmh-7795";
    private static readonly double MonitorStepCos = Math.Cos(
        2 * Math.PI * HfFaxReceiver.AudioCenterFrequencyHz /
        HfFaxReceiver.OutputSampleRateHz);
    private static readonly double MonitorStepSin = Math.Sin(
        2 * Math.PI * HfFaxReceiver.AudioCenterFrequencyHz /
        HfFaxReceiver.OutputSampleRateHz);
    // Keep the monitor useful even when the SDR channel level is far below
    // full scale. This AGC is only for the user-facing monitor path; the
    // decoder and APT detector continue to see the original samples.
    private const double MonitorTargetRms = 0.22;
    private const double MonitorMinimumGain = 0.1;
    private const double MonitorMaximumGain = 2_000;
    private const double MonitorPeakLimit = 0.95;
    private static readonly double MonitorPowerAttackCoefficient =
        MonitorTimeConstant(0.025);
    private static readonly double MonitorPowerReleaseCoefficient =
        MonitorTimeConstant(0.500);
    private static readonly double MonitorGainAttackCoefficient =
        MonitorTimeConstant(0.005);
    private static readonly double MonitorGainReleaseCoefficient =
        MonitorTimeConstant(0.100);

    public sealed record Station(string Id, string DisplayName, long FrequencyHz, string Description)
    {
        public string FrequencyDisplay => FrequencyHz > 0
            ? $"{FrequencyHz / 1_000.0:N1} kHz"
            : "任意周波数";
    }

    public static IReadOnlyList<Station> Stations { get; } =
    [
        new("custom", "カスタム", 0, "任意のHF-FAX割当周波数"),
        new("jmh-3622", "JMH 東京 3,622.5 kHz", 3_622_500, "気象庁 JMH / 夜間向け"),
        new(DefaultStationId, "JMH 東京 7,795.0 kHz", 7_795_000, "気象庁 JMH / 中間帯"),
        new("jmh-13988", "JMH 東京 13,988.5 kHz", 13_988_500, "気象庁 JMH / 昼間向け"),
        new("nmf-4235", "NMF Boston 4,235.0 kHz", 4_235_000, "USCG Boston radiofax"),
        new("nmf-6340", "NMF Boston 6,340.5 kHz", 6_340_500, "USCG Boston radiofax"),
        new("nmf-9110", "NMF Boston 9,110.0 kHz", 9_110_000, "USCG Boston radiofax"),
        new("nmf-12750", "NMF Boston 12,750.0 kHz", 12_750_000, "USCG Boston radiofax"),
        new("nmc-4346", "NMC Pt. Reyes 4,346.0 kHz", 4_346_000, "USCG Point Reyes radiofax"),
        new("nmc-8682", "NMC Pt. Reyes 8,682.0 kHz", 8_682_000, "USCG Point Reyes radiofax"),
        new("nmc-12786", "NMC Pt. Reyes 12,786.0 kHz", 12_786_000, "USCG Point Reyes radiofax"),
        new("nmc-17151", "NMC Pt. Reyes 17,151.2 kHz", 17_151_200, "USCG Point Reyes radiofax")
    ];

    private readonly object processingGate = new();
    private readonly object resultGate = new();
    private readonly HfFaxReceiver receiver = new();
    private readonly HfFaxViewModel viewModel = new();
    private readonly List<HfFaxImageSnapshot> history = [];
    private PluginJsonLinesHistoryWriter<HfFaxImageSnapshot>? historyWriter;
    private HfFaxSettings settings = new();
    private HfFaxImageSnapshot? latestImage;
    private IqBlockMetadata? lastSourceMetadata;
    private long centerFrequencyHz = 7_795_000;
    private long audioSequence;
    private double monitorOscillatorCos = 1;
    private double monitorOscillatorSin;
    private long monitorOscillatorSamples;
    private double monitorPower;
    private double monitorGain = 1;
    private long nextDiagnosticsTick;
    private int saveInProgress;

    public HfFaxPluginModule()
    {
        receiver.ImageStarted += OnImageStarted;
        receiver.LineCompleted += OnLineCompleted;
        receiver.ImageRealigned += OnImageRealigned;
        receiver.ImageCompleted += OnImageCompleted;
        receiver.StateChanged += OnReceiverStateChanged;
        viewModel.SettingsChanged = ApplySettings;
        viewModel.ManualStartRequested = StartImageManually;
        viewModel.ManualStopRequested = StopImageManually;
        viewModel.SaveImageRequested = SaveCurrentImage;
        viewModel.ClearRequested = ClearResults;
        viewModel.ResetSettingsRequested = ResetSettingsAsync;
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "hffax",
        "HF-FAX",
        "HF weather radiofacsimile receiver with APT start/stop detection",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.FrequencyOverlay | PluginCapabilities.ResultPublisher |
        PluginCapabilities.Export | PluginCapabilities.WaterfallDisplay |
        PluginCapabilities.Headless,
        "SRdeck",
        "GPL-3.0");

    public IReadOnlyList<PluginProfileDescriptor> Profiles { get; } = Stations.Select(station =>
        new PluginProfileDescriptor(station.Id, station.DisplayName, station.Description,
            station.Id == DefaultStationId)).ToArray();

    public string? SelectedProfileId => settings.SelectedStationId;

    public IReadOnlyList<PluginChannelRequest> ChannelRequests =>
    [
        new(ChannelRequestId, Volatile.Read(ref centerFrequencyHz),
            HfFaxReceiver.ReceiverBandwidthHz, HfFaxReceiver.OutputSampleRateHz,
            // Keep the Blackman FIR transition resolvable with 65 taps. At the
            // supported SDR rates this selects an exact 20 kS/s CIC output and
            // a simple 3/5 conversion to the 12 kS/s decoder stream.
            MaximumIntermediateSampleRateHz: 20_000,
            MinimumIntermediateSampleRateHz: 12_000,
            FirTaps: 65,
            CicStages: 2,
            RequestedQueueCapacity: 8,
            AllowRawIqFallback: false)
        {
            StopbandBandwidthHz = 6_400
        }
    ];

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
    [
        new("hffax-passband", Volatile.Read(ref centerFrequencyHz),
            HfFaxReceiver.ReceiverBandwidthHz, string.Empty, true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary),
            "Transparent", "#FFFFFFFF", -1,
            ToolTip: "HF-FAX / 1500 Hz（黒）～2300 Hz（白）")
    ];

    public WaterfallDisplayRequest WaterfallDisplayRequest { get; } = new(
        WaterfallTimeMode.Uncompressed,
        HfFaxReceiver.ReceiverBandwidthHz * 10);

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "HF-FAX FM復調・APT同期・画像走査",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "FM偏移検出、VBW平滑化、300/675 Hz開始・450 Hz停止検出、IOC/LPM走査、8-bit画像生成");

    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("png", "PNG画像", ".png", "image/png")
    ];

    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() => new HfFaxPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new HfFaxSettingsView { DataContext = viewModel };

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
                settings = (JsonSerializer.Deserialize<HfFaxSettings>(document.Json) ?? new()).Normalize();
            }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "hffax.settings.invalid",
                    "Invalid HF-FAX settings; defaults are used.", exception);
                settings = new HfFaxSettings().Normalize();
            }
        }
        else settings = settings.Normalize();

        centerFrequencyHz = FrequencyFor(settings);
        historyWriter = CreateHistoryWriter(hostContext);
        hostContext.Tuning.AppliedConfigurationChanged += OnAppliedTuningChanged;
        hostContext.Dispatcher.Post(() =>
        {
            viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
            viewModel.Configure(settings, centerFrequencyHz);
            viewModel.Status = "設定済み / APT待機";
        });
        LoadHistory(hostContext);
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext context = HostContext ??
            throw new InvalidOperationException("The plugin is not initialized.");
        long frequencyHz = FrequencyFor(settings);
        PluginTuningResult result = await context.Tuning.RequestAsync(
            CreateTuningRequest(settings.SelectedStationId, frequencyHz), cancellationToken)
            .ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new PluginActivationRejectedException(result.Message);
        if (!ContainsFaxPassband(result, frequencyHz))
            throw new PluginActivationRejectedException("The receiver cannot provide the requested HF-FAX passband.");
        SetCenterFrequency(frequencyHz);
        PostStatus($"待機中 / {frequencyHz / 1_000.0:N1} kHz / APT待機");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            lastSourceMetadata = null;
            ResetMonitorAudio();
        }
        Interlocked.Exchange(ref audioSequence, 0);
        Volatile.Write(ref nextDiagnosticsTick, 0);
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.ResetCurrentImage();
            viewModel.Status = settings.AutomaticStartStop
                ? "受信中 / APT開始信号を待機"
                : "受信中 / 手動開始を待機";
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

        HfFaxReceiver.DiagnosticsSnapshot diagnostics;
        HfFaxSettings currentSettings = settings;
        bool discontinuous = block.Metadata.Source.Discontinuity != IqDiscontinuity.None;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            if (discontinuous)
            {
                HostContext?.Audio.Reset();
                ResetMonitorAudio();
            }
            lastSourceMetadata = block.Metadata.Source;
            receiver.Process(block.Samples.Span, block.Metadata,
                Volatile.Read(ref centerFrequencyHz), DecodeOptions(currentSettings));
            SubmitMonitorAudio(block.Samples.Span, block.Metadata, currentSettings, discontinuous);
            diagnostics = receiver.GetDiagnostics();
        }

        long now = Environment.TickCount64;
        if (now >= Volatile.Read(ref nextDiagnosticsTick))
        {
            Volatile.Write(ref nextDiagnosticsTick, now + 250);
            float? levelDbm = HostContext?.ReceiverTelemetry?.SignalLevelDbm;
            HostContext?.Dispatcher.Post(() => viewModel.UpdateDiagnostics(diagnostics, levelDbm));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        HfFaxSettings current = settings;
        long frequency = Volatile.Read(ref centerFrequencyHz);
        return PluginProcessingWarmup.RunChannelAsync(
            context,
            ChannelRequestId,
            frequency,
            HfFaxReceiver.OutputSampleRateHz,
            HfFaxReceiver.ReceiverBandwidthHz,
            (samples, metadata) =>
            {
                lock (processingGate)
                    receiver.Process(samples, metadata, frequency, DecodeOptions(current));
            },
            () =>
            {
                lock (processingGate) receiver.Reset();
            },
            cancellationToken);
    }

    public async ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        Station station = Stations.FirstOrDefault(item => item.Id == profileId) ??
            throw new ArgumentException($"Unknown HF-FAX profile '{profileId}'.", nameof(profileId));
        long frequency = station.FrequencyHz > 0 ? station.FrequencyHz : settings.CustomFrequencyHz;

        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            IPluginHostContext context = HostContext ??
                throw new InvalidOperationException("The plugin is not initialized.");
            PluginTuningResult result = await context.Tuning.RequestAsync(
                CreateTuningRequest(profileId, frequency), cancellationToken).ConfigureAwait(false);
            if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred ||
                !ContainsFaxPassband(result, frequency))
                throw new InvalidOperationException($"HF-FAX tuning was rejected: {result.Message}");
        }

        settings = (settings with { SelectedStationId = profileId }).Normalize();
        SetCenterFrequency(frequency);
        lock (processingGate)
        {
            if (receiver.State == HfFaxReceiverState.Receiving)
                receiver.Stop(HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow,
                    "周波数変更");
            receiver.Reset();
            ResetMonitorAudio();
        }
        await SaveSettingsAsync(settings).ConfigureAwait(false);
        HostContext?.Dispatcher.Post(() => viewModel.Configure(settings, frequency));
    }

    public ValueTask<PluginExportResult> ExportAsync(
        PluginExportRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.FormatId, "png", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(new PluginExportResult(false, 0,
                $"Unknown HF-FAX export format '{request.FormatId}'."));
        HfFaxImageSnapshot? snapshot = SnapshotForSave("エクスポート");
        if (snapshot is null)
            return ValueTask.FromResult(new PluginExportResult(false, 0, "保存できるHF-FAX画像がありません。"));
        try
        {
            HfFaxPngWriter.Save(request.DestinationPath, snapshot);
            return ValueTask.FromResult(new PluginExportResult(true, 1, "HF-FAX画像を保存しました。"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            HostContext?.Logger.Log(PluginLogLevel.Error, "hffax.export.failed",
                "HF-FAX image export failed.", exception);
            return ValueTask.FromResult(new PluginExportResult(false, 0, exception.Message));
        }
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Stop(HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow,
                "ストリーム停止");
            receiver.Reset();
            lastSourceMetadata = null;
            ResetMonitorAudio();
        }
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.ResetProcessingDiagnostics();
            viewModel.Status = "待機中";
        });
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        HostContext?.Dispatcher.Post(() => viewModel.Status = "停止中");
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
        if (hostContext is not null)
            hostContext.Tuning.AppliedConfigurationChanged -= OnAppliedTuningChanged;
        receiver.ImageStarted -= OnImageStarted;
        receiver.LineCompleted -= OnLineCompleted;
        receiver.ImageCompleted -= OnImageCompleted;
        receiver.StateChanged -= OnReceiverStateChanged;
        lock (processingGate) receiver.Reset();
    }

    public async ValueTask ResetSettingsAsync()
    {
        IPluginHostContext? context = HostContext;
        if (context is not null) await context.Settings.DeleteAsync().ConfigureAwait(false);
        settings = new HfFaxSettings().Normalize();
        SetCenterFrequency(FrequencyFor(settings));
        context?.Dispatcher.Post(() => viewModel.Configure(settings, FrequencyFor(settings)));
    }

    public static string NormalizeStationId(string? id) =>
        id is not null && Stations.Any(item => item.Id == id) ? id : DefaultStationId;

    private void ApplySettings(HfFaxSettings value)
    {
        HfFaxSettings normalized = value.Normalize();
        HfFaxSettings previous = settings;
        long oldFrequency = FrequencyFor(previous);
        long newFrequency = FrequencyFor(normalized);
        settings = normalized;
        if (previous.MonitorAudioEnabled && !normalized.MonitorAudioEnabled)
        {
            HostContext?.Audio.Reset();
            lock (processingGate) ResetMonitorAudio();
        }
        bool formatChanged = previous.Ioc != normalized.Ioc ||
            previous.LinesPerMinute != normalized.LinesPerMinute ||
            previous.MaximumLines != normalized.MaximumLines ||
            previous.SlantCorrectionPpm != normalized.SlantCorrectionPpm ||
            previous.VideoBandwidthHz != normalized.VideoBandwidthHz;
        if (formatChanged)
        {
            lock (processingGate)
                receiver.Stop(HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow,
                    "画像設定変更");
        }
        else if (previous.HorizontalShiftPixels != normalized.HorizontalShiftPixels)
        {
            HfFaxImageSnapshot? realigned;
            lock (processingGate)
                realigned = receiver.ApplyHorizontalShift(normalized.HorizontalShiftPixels,
                    HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow);
            if (realigned is not null)
                HostContext?.Dispatcher.Post(() => viewModel.RealignCurrentImage(
                    realigned, normalized.HorizontalShiftPixels));
        }
        if (previous.MaximumHistory != normalized.MaximumHistory)
        {
            lock (resultGate) TrimHistory(normalized.MaximumHistory);
        }
        if (oldFrequency != newFrequency)
        {
            SetCenterFrequency(newFrequency);
            if (HostContext is { } context &&
                State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
                _ = RetuneAsync(context, normalized.SelectedStationId, newFrequency);
        }
        _ = SaveSettingsAsync(normalized);
    }

    private async Task RetuneAsync(IPluginHostContext context, string profileId, long frequencyHz)
    {
        try
        {
            PluginTuningResult result = await context.Tuning.RequestAsync(
                CreateTuningRequest(profileId, frequencyHz), CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred ||
                !ContainsFaxPassband(result, frequencyHz))
            {
                context.Dispatcher.Post(() => viewModel.Status = $"選局失敗 / {result.Message}");
                return;
            }
            lock (processingGate)
            {
                receiver.Stop(context.TimeProvider.GetUtcNow(), "周波数変更");
                receiver.Reset();
                ResetMonitorAudio();
            }
            context.Dispatcher.Post(() => viewModel.Status =
                $"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                $"{frequencyHz / 1_000.0:N1} kHz");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hffax.tuning.failed",
                "HF-FAX could not retune to the configured frequency.", exception);
            context.Dispatcher.Post(() => viewModel.Status = $"選局失敗 / {exception.Message}");
        }
    }

    private void StartImageManually()
    {
        if (State != PluginLifecycleState.Streaming)
        {
            viewModel.Status = "手動開始失敗 / 先に受信を開始してください";
            return;
        }
        lock (processingGate)
        {
            IqBlockMetadata metadata = lastSourceMetadata ?? new IqBlockMetadata(
                Guid.NewGuid(), 0, 0, 0, 0,
                HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow,
                HfFaxReceiver.OutputSampleRateHz, Volatile.Read(ref centerFrequencyHz), 0,
                IqInputSource.Sdr, IqDiscontinuity.StreamStarted);
            receiver.StartManually(metadata.UtcTimestamp, metadata.StreamId,
                metadata.AbsoluteSampleStart, Volatile.Read(ref centerFrequencyHz),
                DecodeOptions(settings));
        }
    }

    private void StopImageManually()
    {
        lock (processingGate)
            receiver.StopManually(HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow);
    }

    private void SaveCurrentImage()
    {
        if (Interlocked.CompareExchange(ref saveInProgress, 1, 0) != 0)
        {
            viewModel.SaveStatus = "画像保存中です";
            return;
        }
        IPluginHostContext? context = HostContext;
        HfFaxImageSnapshot? snapshot = SnapshotForSave("手動保存");
        if (context is null || snapshot is null)
        {
            viewModel.SaveStatus = "保存できる画像がありません";
            Interlocked.Exchange(ref saveInProgress, 0);
            return;
        }

        viewModel.SaveStatus = "PNG保存中…";
        _ = Task.Run(() =>
        {
            try
            {
                string directory = Path.Combine(context.Settings.DataDirectory, "images");
                string path = Path.Combine(directory,
                    $"hffax-{snapshot.StartedAt:yyyyMMdd-HHmmss}-{snapshot.FrequencyHz}.png");
                HfFaxPngWriter.Save(path, snapshot);
                context.Dispatcher.Post(() => viewModel.SaveStatus = $"保存済み: {path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Logger.Log(PluginLogLevel.Warning, "hffax.image.save-failed",
                    "HF-FAX image could not be saved.", exception);
                context.Dispatcher.Post(() => viewModel.SaveStatus = $"保存失敗: {exception.Message}");
            }
            finally { Interlocked.Exchange(ref saveInProgress, 0); }
        });
    }

    private HfFaxImageSnapshot? SnapshotForSave(string reason)
    {
        lock (processingGate)
        {
            HfFaxImageSnapshot? current = receiver.GetCurrentSnapshot(
                HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow, reason);
            if (current is not null) return current;
        }
        lock (resultGate) return latestImage;
    }

    private void ClearResults()
    {
        lock (resultGate)
        {
            history.Clear();
            latestImage = null;
        }
        viewModel.ClearImages();
        DeleteHistoryFile();
    }

    private void OnImageStarted(HfFaxImageStarted image) =>
        HostContext?.Dispatcher.Post(() => viewModel.BeginImage(image));

    private void OnLineCompleted(HfFaxLine line) =>
        HostContext?.Dispatcher.Post(() => viewModel.AppendLine(line));

    private void OnImageRealigned(HfFaxImageSnapshot image) =>
        HostContext?.Dispatcher.Post(() => viewModel.RealignCurrentImage(
            image, settings.HorizontalShiftPixels, automaticallyAligned: true));

    private void OnImageCompleted(HfFaxImageSnapshot image)
    {
        if (settings.AutomaticHorizontalAlignmentEnabled)
            image = HfFaxImagePhaseAligner.AlignSnapshot(
                image, settings.HorizontalShiftPixels);
        lock (resultGate)
        {
            latestImage = image;
            history.Insert(0, image);
            TrimHistory(settings.MaximumHistory);
        }
        HostContext?.Notifications.PlayReceptionAlarm();
        HostContext?.Dispatcher.Post(() => viewModel.CompleteImage(image));
        AppendHistory(image);
        ResultPublished?.Invoke(this, new(new PluginResultSummary(
            image.Id,
            Descriptor.Id,
            image.CompletedAt,
            image.StreamId,
            "hffax.image",
            PluginResultSeverity.Information,
            $"HF-FAX画像 {image.SizeDisplay}",
            $"{image.FrequencyDisplay} / {image.FormatDisplay} / {image.CompletionReason}",
            image.FrequencyHz,
            image.SignalQuality,
            1,
            JsonSerializer.Serialize(new
            {
                image.Width,
                image.Height,
                image.Ioc,
                image.LinesPerMinute,
                image.SignalQuality,
                image.CompletionReason
            }))));
    }

    private void OnReceiverStateChanged(HfFaxReceiverState state) =>
        HostContext?.Dispatcher.Post(() => viewModel.SetReceiverState(state));

    private void TrimHistory(int maximum)
    {
        if (history.Count > maximum)
            history.RemoveRange(maximum, history.Count - maximum);
    }

    private async Task SaveSettingsAsync(HfFaxSettings value)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        try
        {
            await context.Settings.SaveAsync(new PluginSettingsDocument(1,
                JsonSerializer.Serialize(value))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hffax.settings.save-failed",
                "HF-FAX settings could not be saved.", exception);
        }
    }

    private void OnAppliedTuningChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome == PluginTuningOutcome.Rejected) return;
        long frequency = Volatile.Read(ref centerFrequencyHz);
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming &&
            !ContainsFaxPassband(result, frequency))
            PostStatus("HF-FAX信号が通過帯域外です");
    }

    private void SetCenterFrequency(long value)
    {
        Volatile.Write(ref centerFrequencyHz, value);
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        HostContext?.Dispatcher.Post(() => viewModel.SetCenterFrequency(value));
    }

    private void PostStatus(string value)
    {
        IPluginHostContext? context = HostContext;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
    }

    private void SubmitMonitorAudio(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata,
        HfFaxSettings current,
        bool discontinuous)
    {
        IPluginHostContext? context = HostContext;
        if (context is null || !current.MonitorAudioEnabled || samples.IsEmpty) return;
        double oscillatorCos = monitorOscillatorCos;
        double oscillatorSin = monitorOscillatorSin;
        long oscillatorSamples = monitorOscillatorSamples;
        double power = monitorPower;
        double gain = monitorGain;
        double volume = current.MonitorAudioVolume / 100.0;
        byte[] pcm = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 sample = samples[index];
            // HF-FAX is conventionally monitored in USB. Re{(I+jQ) *
            // exp(+j*2*pi*1900*t)} puts a positive discriminator deviation
            // above the nominal subcarrier; the decoder continues to use the
            // raw IQ independently of this user-facing monitor path.
            double raw = sample.I * oscillatorCos - sample.Q * oscillatorSin;
            AdvanceMonitorOscillator(ref oscillatorCos, ref oscillatorSin,
                ++oscillatorSamples);
            if (!double.IsFinite(raw))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            double instantaneousPower = raw * raw;
            if (!double.IsFinite(instantaneousPower))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            double powerCoefficient = instantaneousPower > power
                ? MonitorPowerAttackCoefficient
                : MonitorPowerReleaseCoefficient;
            power += powerCoefficient * (instantaneousPower - power);
            if (!double.IsFinite(power))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            double targetGain = MonitorTargetRms /
                                Math.Sqrt(Math.Max(power, 1e-12));
            if (!double.IsFinite(targetGain))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }
            targetGain = Math.Clamp(targetGain, MonitorMinimumGain, MonitorMaximumGain);
            double gainCoefficient = targetGain < gain
                ? MonitorGainAttackCoefficient
                : MonitorGainReleaseCoefficient;
            gain += gainCoefficient * (targetGain - gain);
            if (!double.IsFinite(gain))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            // A soft limiter prevents a strong signal from making a click while
            // the slower power estimate and gain loop are catching up.
            double limited = MonitorPeakLimit *
                             Math.Tanh(raw * gain / MonitorPeakLimit);
            short value = (short)Math.Round(
                Math.Clamp(limited * volume, -1, 1) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2, 2), value);
        }
        monitorOscillatorCos = oscillatorCos;
        monitorOscillatorSin = oscillatorSin;
        monitorOscillatorSamples = oscillatorSamples;
        monitorPower = power;
        monitorGain = gain;
        context.Audio.TrySubmit(new PcmAudioFrame(context.PluginId, metadata.Source.StreamId,
            Interlocked.Increment(ref audioSequence), HfFaxReceiver.OutputSampleRateHz, 1,
            PcmSampleFormat.Signed16LittleEndian, pcm, discontinuous));
    }

    private void ResetMonitorAudio()
    {
        monitorOscillatorCos = 1;
        monitorOscillatorSin = 0;
        monitorOscillatorSamples = 0;
        ResetMonitorAudioLevel();
    }

    private void ResetMonitorAudioLevel()
    {
        monitorPower = 0;
        monitorGain = 1;
    }

    private static double MonitorTimeConstant(double seconds) =>
        1 - Math.Exp(-1 / (seconds * HfFaxReceiver.OutputSampleRateHz));

    internal static void AdvanceMonitorOscillator(ref double oscillatorCos,
        ref double oscillatorSin, long sampleIndex)
    {
        double nextCos = oscillatorCos * MonitorStepCos -
            oscillatorSin * MonitorStepSin;
        oscillatorSin = oscillatorSin * MonitorStepCos +
            oscillatorCos * MonitorStepSin;
        oscillatorCos = nextCos;
        if ((sampleIndex & 1023) != 0) return;
        double inverseMagnitude = 1 / Math.Sqrt(
            oscillatorCos * oscillatorCos + oscillatorSin * oscillatorSin);
        oscillatorCos *= inverseMagnitude;
        oscillatorSin *= inverseMagnitude;
    }

    private static HfFaxDecodeOptions DecodeOptions(HfFaxSettings value) => new(
        value.Ioc,
        value.LinesPerMinute,
        value.MaximumLines,
        value.HorizontalShiftPixels,
        value.SlantCorrectionPpm,
        value.AutomaticStartStop,
        value.InvertImage,
        value.VideoBandwidthHz,
        value.PerLineSynchronizationEnabled,
        value.AutomaticHorizontalAlignmentEnabled);

    private static long FrequencyFor(HfFaxSettings value)
    {
        Station station = Stations.First(item => item.Id == NormalizeStationId(value.SelectedStationId));
        return station.FrequencyHz > 0 ? station.FrequencyHz : value.CustomFrequencyHz;
    }

    private static PluginTuningRequest CreateTuningRequest(string profileId, long frequencyHz) => new(
        profileId,
        $"HF-FAX {frequencyHz / 1_000.0:N1} kHz",
        [new TuningTarget(frequencyHz, HfFaxReceiver.ReceiverBandwidthHz)],
        frequencyHz,
        24_000,
        10,
        true,
        false,
        PluginGainPreference.Automatic);

    private static bool ContainsFaxPassband(PluginTuningResult result, long frequencyHz)
    {
        long half = HfFaxReceiver.ReceiverBandwidthHz / 2;
        return result.PassbandLowerFrequencyHz > 0 &&
            result.PassbandUpperFrequencyHz > result.PassbandLowerFrequencyHz &&
            frequencyHz - half >= result.PassbandLowerFrequencyHz &&
            frequencyHz + half <= result.PassbandUpperFrequencyHz;
    }
}
