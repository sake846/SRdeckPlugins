using System.Buffers.Binary;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.AribT98.Codec;
using SRdeckPlugin.AribT98.Dsp;
using SRdeckPlugin.AribT98.Models;
using SRdeckPlugin.AribT98.ViewModels;
using SRdeckPlugin.AribT98.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.AribT98;

public sealed partial class AribT98PluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    IPluginViewProvider, IFrequencyOverlayProvider, IWaterfallDisplayProvider, IPluginProcessingDiagnosticsProvider
{
    private readonly object processingGate = new();
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly IqStreamContinuityTracker continuity = new();
    private readonly AribT98Receiver receiver = new();
    private readonly AribT98PskReceiver pskReceiver = new();
    private readonly AribT98PresetPrivacy presetPrivacy = new();
    private readonly AribT98ViewModel viewModel = new();
    private PluginJsonLinesHistoryWriter<AribT98FrameRow>? historyWriter;
    private readonly short[] pcm = new short[640];
    private readonly byte[] pcmBytes = new byte[640 * sizeof(short)];
    private AribT98Settings settings = new();
    private AribT98Voice? voice;
    private AppliedChannelConfiguration? lastConfiguration;
    private long? expectedOutput;
    private long audioSequence, lastFramePosition = long.MinValue, samplesProcessed, lastUiTick;
    private bool audioDiscontinuous = true;
    private int presentationGeneration;

    public AribT98PluginModule()
    {
        viewModel.ApplyRequested = ApplySettingsAsync;
        viewModel.ResetSettingsRequested = ResetSettingsAsync;
        viewModel.SaveRecentIqRequested = SaveRecentIqAsync;
        viewModel.MaximumHistoryChanged = value => _ = UpdateMaximumHistoryAsync(value);
        viewModel.HistoryEntryCompleted = AppendHistory;
        viewModel.ClearHistoryRequested = DeleteHistoryFile;
    }
    public override PluginDescriptor Descriptor { get; } = new("arib-t98", "簡易無線（ARIB STD-T98）",
        "STD-T98 v1.1: Part 3 4FSK/AMBE+2; Part 1 pi/4 QPSK RICH/PICH/TCH (PSK audio profile unspecified)",
        new(1,0,0), new(1,0), new(1,0), PluginCapabilities.ChannelIqConsumer | PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView | PluginCapabilities.FrequencyOverlay |
        PluginCapabilities.WaterfallDisplay, "SRdeck", "GPL-3.0-only");
    public PluginProcessingStageDefinition ProcessingStage { get; } = new("STD-T98 4FSK / π/4 QPSK",
        PluginComputeDevice.Cpu, ".NET + AMBEドングル (4FSK)", "4FSK: 2400 sym/s / 80 ms / AMBE+2; PSK: 4800 sym/s / 40 ms / TCH");

    public IReadOnlyList<PluginChannelRequest> ChannelRequests => [new("arib-t98", settings.FrequencyHz,
        6_250, AribT98Receiver.SampleRateHz, MaximumIntermediateSampleRateHz: 192_000,
        MinimumIntermediateSampleRateHz: 48_000, FirTaps: 129, RequestedQueueCapacity: 8,
        AllowRawIqFallback: false) { StopbandBandwidthHz = 8_000 }];
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays => [new("arib-t98", settings.FrequencyHz,
        6_250, "", true, PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary),
        "Transparent", "#FFFFFFFF", -1, ToolTip: $"STD-T98 {AribT98Channels.Find(settings.FrequencyHz).Display} / {settings.Modulation}")];
    public WaterfallDisplayRequest WaterfallDisplayRequest { get; } = new(
        WaterfallTimeMode.ThreeMinutes, 6_250 * 10);
    public event EventHandler? FrequencyOverlaysChanged;
    public FrameworkElement CreateMainView() => new AribT98PluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new AribT98SettingsView { DataContext = viewModel };

    public async ValueTask ResetSettingsAsync()
    {
        await ApplySettingsAsync(new()).ConfigureAwait(false);
        if (HostContext is { } host) await host.Settings.DeleteAsync().ConfigureAwait(false);
    }

    protected override async ValueTask OnInitializeAsync(IPluginHostContext host, CancellationToken cancellationToken)
    {
        PluginSettingsDocument? document = await host.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = document is null ? new() : JsonSerializer.Deserialize<AribT98Settings>(document.Json) ?? new();
            settings.Validate();
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            settings = new();
            host.Logger.Log(PluginLogLevel.Warning, "arib-t98.settings.invalid", "Invalid STD-T98 settings; using defaults.", e);
        }
        historyWriter = CreateHistoryWriter(host);
        AribT98FrameRow[] savedHistory = LoadHistory(host);
        host.Dispatcher.Post(() =>
        {
            viewModel.Configure(settings);
            viewModel.LoadHistory(savedHistory);
            viewModel.RuntimeDiagnostics = host.RuntimeDiagnostics;
        });
        host.Tuning.AppliedConfigurationChanged += HandleAppliedConfigurationChanged;
    }

    private async void HandleAppliedConfigurationChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred || result.TargetFrequencyHz <= 0)
            return;
        AribT98Channel channel = AribT98Channels.FindAt(result.TargetFrequencyHz);
        long frequencyHz = channel.IsManual ? result.TargetFrequencyHz : channel.FrequencyHz;
        if (HostContext is not { } host) return;
        try
        {
            await settingsGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State is not (PluginLifecycleState.Active or PluginLifecycleState.Streaming) ||
                    frequencyHz == settings.FrequencyHz) return;
                AribT98Settings requested = settings with { FrequencyHz = frequencyHz };
                requested.Validate();
                await CommitSettingsAsync(requested).ConfigureAwait(false);
            }
            finally { settingsGate.Release(); }
        }
        catch (Exception exception)
        {
            host.Logger.Log(PluginLogLevel.Error, "arib-t98.tuning.follow.failed",
                "Failed to follow the host frequency change.", exception);
        }
    }

    private async Task TuneAsync(AribT98Settings requested, CancellationToken cancellationToken)
    {
        IPluginHostContext host = HostContext ?? throw new InvalidOperationException("Plugin not initialized.");
        PluginTuningResult result = await host.Tuning.RequestAsync(new("arib-t98", "STD-T98",
            [new(requested.FrequencyHz, 6_250)], requested.FrequencyHz, 48_000, 6_250, true, false,
            PluginGainPreference.Automatic), cancellationToken).ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred ||
            result.PassbandLowerFrequencyHz > requested.FrequencyHz - 3125 ||
            result.PassbandUpperFrequencyHz < requested.FrequencyHz + 3125)
            throw new PluginActivationRejectedException($"STD-T98選局を適用できません: {result.Message}");
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        await TuneAsync(settings, cancellationToken).ConfigureAwait(false);
        // Discovery and PSK reception do not load the native AMBE codec.
        string? audioStatus;
        lock (processingGate)
        {
            if (settings.Modulation == AribT98Modulation.FourFsk) voice ??= new(settings);
            audioStatus = voice?.Status;
        }
        if (audioStatus is not null) HostContext?.Dispatcher.Post(() => viewModel.AudioStatus = audioStatus);
        SetStatus("待機中", OverallStatusKind.Idle);
    }

    public async Task ApplySettingsAsync(AribT98Settings requested)
    {
        ArgumentNullException.ThrowIfNull(requested); requested.Validate(); ThrowIfDisposed();
        await settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming && requested.FrequencyHz != settings.FrequencyHz)
                await TuneAsync(requested, CancellationToken.None).ConfigureAwait(false);
            await CommitSettingsAsync(requested).ConfigureAwait(false);
        }
        finally { settingsGate.Release(); }
    }

    private async Task CommitSettingsAsync(AribT98Settings requested)
    {
        int previousMaximumHistory = settings.MaximumHistory;
        string? audioStatus;
        lock (processingGate)
        {
            ThrowIfDisposed();
            if (requested.Modulation == AribT98Modulation.FourFsk && State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            {
                voice?.Dispose();
                voice = new(requested);
            }
            settings = requested; ResetProcessing();
            if (requested.Modulation == AribT98Modulation.PiOver4Qpsk) { voice?.Dispose(); voice = null; }
            audioStatus = voice?.Status;
        }
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.Configure(requested);
            if (audioStatus is not null) viewModel.AudioStatus = audioStatus;
        });
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        if (HostContext is { } host)
        {
            await host.Settings.SaveAsync(SettingsDocument(requested)).ConfigureAwait(false);
            if (requested.MaximumHistory != previousMaximumHistory)
                await Task.Run(() => ApplyHistoryRetention(host, requested.MaximumHistory)).ConfigureAwait(false);
        }
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        string? audioStatus;
        lock (processingGate)
        {
            if (settings.Modulation == AribT98Modulation.FourFsk) voice ??= new(settings);
            ResetProcessing();
            audioStatus = voice?.Status;
        }
        if (audioStatus is not null) HostContext?.Dispatcher.Post(() => viewModel.AudioStatus = audioStatus);
        SetStatus("受信中 / 同期待ち", OverallStatusKind.Running);
        return ValueTask.CompletedTask;
    }
    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate) ResetProcessing(preserveIq: true);
        double seconds = iqHistorySamples / 48000.0;
        HostContext?.Dispatcher.Post(() => viewModel.IqBufferedSeconds = seconds);
        SetStatus("停止中", OverallStatusKind.Idle);
        await FlushHistoryAsync(cancellationToken).ConfigureAwait(false);
    }
    protected override async ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        lock (processingGate) { ResetProcessing(); voice?.Dispose(); voice = null; }
        SetStatus("無効", OverallStatusKind.Idle);
        await FlushHistoryAsync(cancellationToken).ConfigureAwait(false);
    }
    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (hostContext is not null) hostContext.Tuning.AppliedConfigurationChanged -= HandleAppliedConfigurationChanged;
        lock (processingGate) { presentationGeneration++; presetPrivacy.Dispose(); voice?.Dispose(); voice = null; }
        await FlushHistoryAsync(CancellationToken.None).ConfigureAwait(false);
        if (historyWriter is not null) await historyWriter.DisposeAsync().ConfigureAwait(false);
    }

    private static PluginSettingsDocument SettingsDocument(AribT98Settings value) =>
        new(1, JsonSerializer.Serialize(value), ["/PrivacyKey", "/PrivacyKeyPresets", "/PskScramblerSeed"]);

    private void ResetProcessing(bool preserveIq = false)
    {
        presentationGeneration++;
        presetPrivacy.Configure(settings);
        if (!preserveIq) ResetIqHistory();
        receiver.Reset(); pskReceiver.Reset(); voice?.Reset(); continuity.Reset(); lastConfiguration = null; expectedOutput = null;
        samplesProcessed = audioSequence = 0; lastFramePosition = long.MinValue;
        audioDiscontinuous = true; HostContext?.Audio.Reset();
        string? audioStatus = voice?.Status;
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.ClearReception(preserveHistory: true);
            if (audioStatus is not null) viewModel.AudioStatus = audioStatus;
        });
    }
    private void SetStatus(string text, OverallStatusKind kind) => HostContext?.Dispatcher.Post(() =>
        { viewModel.Status = text; viewModel.OverallStatusKind = kind; });

    public ValueTask ConsumeChannelsAsync(IReadOnlyList<IChannelIqBlockLease> blocks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        AribT98Settings? discoveredSettings = null;
        IPluginHostContext? consumingHost = null;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming || HostContext is not { } host) return ValueTask.CompletedTask;
            consumingHost = host;
            foreach (var block in blocks)
            {
                var metadata = block.Metadata;
                var config = metadata.Configuration;
                if (config.RequestId != "arib-t98") continue;
                if (config.OutputSampleRateHz != AribT98Receiver.SampleRateHz || config.ChannelCenterFrequencyHz != settings.FrequencyHz)
                { ResetProcessing(); SetStatus("選局・48 kHzチャネル入力待ち", OverallStatusKind.Warning); continue; }
                bool broken = continuity.Observe(metadata.Source).RequiresReset || lastConfiguration != config ||
                    (expectedOutput.HasValue && expectedOutput.Value != metadata.OutputSampleStart);
                if (broken)
                {
                    ResetProcessing(); continuity.Observe(metadata.Source);
                }
                lastConfiguration = config;
                expectedOutput = metadata.OutputSampleStart + block.Samples.Length;
                CaptureIq(block);
                long blockStart = samplesProcessed;
                bool psk = settings.Modulation == AribT98Modulation.PiOver4Qpsk;
                var frames = psk ? pskReceiver.Process(block.Samples.Span, settings.PskScramblerSeed) : receiver.Process(block.Samples.Span);
                samplesProcessed += block.Samples.Length;
                foreach (AribT98Frame frame in frames)
                {
                    bool gap = lastFramePosition != long.MinValue && frame.SamplePosition - lastFramePosition > (psk ? 1990 : 3980);
                    if (gap) { voice?.Reset(); host.Audio.Reset(); audioDiscontinuous = true; }
                    lastFramePosition = frame.SamplePosition;
                    int previousPresetKey = presetPrivacy.Key;
                    presetPrivacy.Observe(frame, settings.UserCode);
                    if (presetPrivacy.ConfirmedKey > 0 && presetPrivacy.ConfirmedKey != settings.PrivacyKey)
                        discoveredSettings = settings = settings with { PrivacyKey = presetPrivacy.ConfirmedKey };
                    if (previousPresetKey != presetPrivacy.Key)
                    {
                        voice?.Reset(); host.Audio.Reset(); audioDiscontinuous = true;
                    }
                    int? presetKey = frame.CallType == 1 ? presetPrivacy.Key : null;
                    if (!psk && voice?.TryRender(frame, settings, pcm, presetKey) == true)
                    {
                        for (int i = 0; i < pcm.Length; i++)
                            BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.AsSpan(i * sizeof(short)),
                                pcm[i]);
                        bool submitted = host.Audio.TrySubmit(new(Descriptor.Id, metadata.Source.StreamId, audioSequence++,
                            8000, 1, PcmSampleFormat.Signed16LittleEndian, pcmBytes, audioDiscontinuous));
                        audioDiscontinuous = !submitted;
                    }
                    else { host.Audio.Reset(); audioDiscontinuous = true; }
                    long sourcePosition = metadata.MapOutputToSource(metadata.OutputSampleStart + frame.SamplePosition - blockStart);
                    var time = metadata.Source.UtcTimestamp.AddSeconds(
                        (sourcePosition - metadata.Source.AbsoluteSampleStart) / (double)metadata.Source.SampleRateHz);
                    int generation = presentationGeneration;
                    int presentedPresetKey = presetPrivacy.Key;
                    string presentedPresetStatus = presetPrivacy.Status;
                    host.Dispatcher.Post(() =>
                    {
                        if (generation != presentationGeneration) return;
                        viewModel.Observe(frame, time);
                        viewModel.PresentPresetPrivacy(presentedPresetKey, presentedPresetStatus);
                    });
                }
                if (lastFramePosition != long.MinValue && samplesProcessed - lastFramePosition > 48_000 / 2)
                {
                    voice?.Reset(); presetPrivacy.ResetCall(); host.Audio.Reset(); audioDiscontinuous = true; lastFramePosition = long.MinValue;
                }
                host.Metrics.AddCounter(PluginProcessingStage.Validation, "accepted", frames.Count, "frames");
                if (Environment.TickCount64 - lastUiTick >= 250)
                {
                    lastUiTick = Environment.TickCount64;
                    // Snapshot mutable DSP state before posting to the UI thread.
                    long accepted = psk ? pskReceiver.AcceptedFrames : receiver.AcceptedFrames;
                    long rejected = psk ? pskReceiver.RejectedFrames : receiver.RejectedFrames, audio = audioSequence;
                    string rms = $"{20*Math.Log10(Math.Max(psk ? pskReceiver.InputRms : receiver.InputRms, 1e-12)):F1} dBFS";
                    string offset = $"{(psk ? pskReceiver.FrequencyOffsetHz : receiver.FrequencyOffsetHz):+0.0;-0.0;0.0} Hz";
                    string quality = $"{100*Math.Max(0, 1-(psk ? pskReceiver.LastSyncError : receiver.LastSyncError)):F1} %";
                    string audioStatus = psk ? "PSK音声形式未対応・TCH抽出のみ" : voice?.Status ?? "CODEC未初期化";
                    bool fresh = lastFramePosition != long.MinValue;
                    float[] eyePattern = fresh && !psk ? receiver.SnapshotEyePattern() : [];
                    int presetKey = presetPrivacy.Key;
                    string presetStatus = presetPrivacy.Status;
                    double capturedSeconds = iqHistorySamples / 48000.0;
                    int generation = presentationGeneration;
                    host.Dispatcher.Post(() =>
                    {
                        if (generation != presentationGeneration) return;
                        viewModel.AcceptedFrames = accepted; viewModel.RejectedFrames = rejected; viewModel.AudioFrames = audio;
                        viewModel.InputLevel = rms; viewModel.Offset = fresh ? offset : "—"; viewModel.SyncQuality = fresh ? quality : "—";
                        viewModel.EyePattern = eyePattern;
                        viewModel.AudioStatus = audioStatus; viewModel.Status = fresh ? "受信中 / 同期" : "受信中 / 同期待ち";
                        viewModel.PresentPresetPrivacy(presetKey, presetStatus);
                        viewModel.IqBufferedSeconds = capturedSeconds;
                        viewModel.OverallStatusKind = fresh ? OverallStatusKind.Success : OverallStatusKind.Running;
                    });
                }
            }
        }
        return discoveredSettings is null ? ValueTask.CompletedTask :
            PersistDiscoveredPrivacyKeyAsync(consumingHost!, discoveredSettings, cancellationToken);
    }

    private async ValueTask PersistDiscoveredPrivacyKeyAsync(IPluginHostContext host,
        AribT98Settings discovered, CancellationToken cancellationToken)
    {
        await settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (processingGate)
                if (!ReferenceEquals(settings, discovered)) return;
            try
            {
                await host.Settings.SaveAsync(SettingsDocument(discovered), cancellationToken).ConfigureAwait(false);
                host.Dispatcher.Post(() => viewModel.AdoptPrivacyKey(discovered.PrivacyKey));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                host.Logger.Log(PluginLogLevel.Warning, "arib-t98.privacy-key.save-failed",
                    "Discovered privacy key is active but could not be saved.", e);
                host.Dispatcher.Post(() => viewModel.AdoptPrivacyKey(discovered.PrivacyKey, saved: false));
            }
        }
        finally { settingsGate.Release(); }
    }
}
