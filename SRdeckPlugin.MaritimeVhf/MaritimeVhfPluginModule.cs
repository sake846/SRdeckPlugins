using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.MaritimeVhf.Dsp;
using SRdeckPlugin.MaritimeVhf.Models;
using SRdeckPlugin.MaritimeVhf.Protocols;
using SRdeckPlugin.MaritimeVhf.ViewModels;
using SRdeckPlugin.MaritimeVhf.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.MaritimeVhf;

public sealed class MaritimeVhfPluginModule : PluginModuleBase,
    IPluginChannelBlockConsumer,
    IPluginViewProvider,
    IFrequencyOverlayProvider,
    IPluginResultProvider,
    IPluginProcessingDiagnosticsProvider
{
    private readonly object processingGate = new();
    private readonly object stateGate = new();
    private readonly NarrowFmReceiver voiceReceiver = new();
    private readonly NarrowFmReceiver dscFmReceiver = new();
    private readonly DscSignalDecoder dscDecoder = new();
    private readonly MaritimeVhfViewModel viewModel = new();
    private readonly SemaphoreSlim tuningGate = new(1, 1);
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> recentMessages = [];
    private MaritimeVhfSettings settings = new();
    private DscWorkingChannel? followOverride;
    private long voiceFrequencyHz = MaritimeVhfChannelPlan.Channel16.ShipFrequencyHz;
    private bool dscChannelAvailable = true;
    private string tuningStatus = "選局待機中";
    private DateTimeOffset followStartedAt;
    private DateTimeOffset lastVoiceCarrierAt;
    private long lastUiTick;
    private long audioSequence;
    private int autoReturnPending;

    public MaritimeVhfPluginModule()
    {
        viewModel.SettingsChanged = OnSettingsChanged;
        viewModel.ReturnFromFollowRequested = ReturnFromFollowAsync;
        viewModel.ClearRequested = () =>
        {
            lock (stateGate) recentMessages.Clear();
        };
        viewModel.ResetPluginSettingsRequested = ResetSettingsAsync;
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "maritime-vhf",
        "国際VHF",
        "International maritime VHF voice receiver with DSC channel 70 auto-follow",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView |
        PluginCapabilities.SettingsView | PluginCapabilities.AudioProducer |
        PluginCapabilities.FrequencyOverlay | PluginCapabilities.ResultPublisher,
        "SRdeck",
        "GPL-3.0");

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "国際VHF NFM復調・DSC検証",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "48 kS/s NFM音声、CH 70 AFSK、時間ダイバーシティ、ECC、自動追尾");

    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            lock (stateGate)
            {
                var requests = new List<PluginChannelRequest>(2)
                {
                    ChannelRequest("maritime-vhf-voice", voiceFrequencyHz)
                };
                if (settings.DscWatchEnabled && dscChannelAvailable &&
                    voiceFrequencyHz != MaritimeVhfChannelPlan.DscFrequencyHz)
                    requests.Add(ChannelRequest("maritime-vhf-dsc",
                        MaritimeVhfChannelPlan.DscFrequencyHz));
                return requests;
            }
        }
    }

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays
    {
        get
        {
            lock (stateGate)
            {
                var items = new List<FrequencyOverlayItem>(2)
                {
                    new("maritime-vhf-voice", voiceFrequencyHz,
                        MaritimeVhfChannelPlan.VoiceBandwidthHz, "VHF", true,
                        PluginReceiverBandColors.WithAlpha(0x48,
                            PluginReceiverBandColors.Primary),
                        "Transparent", "#FFFFFFFF", -1,
                        ToolTip: ActiveVoiceChannelText())
                };
                if (settings.DscWatchEnabled && dscChannelAvailable &&
                    voiceFrequencyHz != MaritimeVhfChannelPlan.DscFrequencyHz)
                    items.Add(new("maritime-vhf-dsc", MaritimeVhfChannelPlan.DscFrequencyHz,
                        MaritimeVhfChannelPlan.VoiceBandwidthHz, "DSC", false,
                        PluginReceiverBandColors.WithAlpha(0x38,
                            PluginReceiverBandColors.Secondary),
                        "Transparent", "#FFFFFFFF", -1,
                        ToolTip: "DSC CH 70 / 156.525 MHz"));
                return items;
            }
        }
    }

    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() =>
        new MaritimeVhfPluginView { DataContext = viewModel };

    public FrameworkElement CreateSettingsView() =>
        new MaritimeVhfSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
        PluginSettingsDocument? document =
            await hostContext.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try
            {
                settings = (JsonSerializer.Deserialize<MaritimeVhfSettings>(document.Json) ??
                    new MaritimeVhfSettings()).Normalize();
            }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "maritime-vhf.settings.invalid",
                    "International VHF settings were invalid; defaults are being used.", exception);
                settings = new MaritimeVhfSettings();
            }
        }
        viewModel.SynchronizeSettings(settings);
        hostContext.Tuning.AppliedConfigurationChanged += OnTuningChanged;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!await ApplyTuningAsync("起動", cancellationToken).ConfigureAwait(false))
            throw new PluginActivationRejectedException(tuningStatus);
    }

    protected override ValueTask OnActivatedAsync()
    {
        PublishUi(force: true);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            ResetReceivers();
            audioSequence = 0;
            lastUiTick = 0;
        }
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStreamStartedAsync()
    {
        PublishUi(force: true);
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

        List<DscMessage>? decoded = null;
        bool requestAutoReturn = false;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            foreach (IChannelIqBlockLease block in blocks)
            {
                string requestId = block.Metadata.Configuration.RequestId;
                if (block.Metadata.Source.Discontinuity != IqDiscontinuity.None)
                {
                    if (requestId == "maritime-vhf-voice") voiceReceiver.Reset();
                    if (requestId == "maritime-vhf-dsc")
                    {
                        dscFmReceiver.Reset();
                        dscDecoder.Reset();
                    }
                }

                float[] audio = ArrayPool<float>.Shared.Rent(block.Samples.Length);
                try
                {
                    if (requestId == "maritime-vhf-voice")
                    {
                        MaritimeVhfSettings snapshot;
                        lock (stateGate) snapshot = settings;
                        float thresholdDbfs = HostContext?.ReceiverTelemetry?.DbmToDbfs(
                            snapshot.SquelchThresholdDbm) ?? snapshot.SquelchThresholdDbm + 80f;
                        int count = voiceReceiver.Process(block.Samples.Span, audio,
                            voiceAudio: true, snapshot.SquelchEnabled, thresholdDbfs);
                        if (voiceReceiver.IsSquelchOpen)
                            lastVoiceCarrierAt = HostContext?.TimeProvider.GetUtcNow() ??
                                DateTimeOffset.UtcNow;
                        SubmitAudio(audio.AsSpan(0, count), block.Metadata.Source, snapshot);
                        requestAutoReturn = ShouldAutoReturn(snapshot);
                    }
                    else if (requestId == "maritime-vhf-dsc")
                    {
                        int count = dscFmReceiver.Process(block.Samples.Span, audio,
                            voiceAudio: false, squelchEnabled: false, squelchThresholdDbfs: -160f);
                        IReadOnlyList<DscMessage> messages = dscDecoder.ProcessAudio(
                            audio.AsSpan(0, count), block.Metadata.Source.UtcTimestamp,
                            block.Metadata.Source.StreamId,
                            block.Metadata.MapOutputToSource(0));
                        if (messages.Count > 0)
                        {
                            decoded ??= [];
                            decoded.AddRange(messages);
                        }
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(audio);
                }
            }
        }

        if (decoded is not null)
            foreach (DscMessage message in decoded) PublishDscMessage(message);
        if (requestAutoReturn && Interlocked.Exchange(ref autoReturnPending, 1) == 0)
            _ = AutoReturnAsync();
        PublishUi();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate) ResetReceivers();
        HostContext?.Audio.Reset();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStreamStoppedAsync()
    {
        PublishUi(force: true);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDeactivatedAsync()
    {
        lock (stateGate)
        {
            followOverride = null;
            dscChannelAvailable = settings.DscWatchEnabled;
        }
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (hostContext is not null)
            hostContext.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
        tuningGate.Dispose();
        settingsSaveGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<bool> ApplyTuningAsync(string reason, CancellationToken cancellationToken)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return false;
        await tuningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MaritimeVhfSettings snapshot;
            DscWorkingChannel? follow;
            lock (stateGate)
            {
                snapshot = settings;
                follow = followOverride;
            }
            MaritimeVhfChannel channel = follow?.Channel ??
                MaritimeVhfChannelPlan.Find(snapshot.SelectedChannel) ??
                MaritimeVhfChannelPlan.Channel16;
            MaritimeVhfReceiveSide side = follow?.ForcedSide ?? snapshot.ReceiveSide;
            long frequency = channel.FrequencyFor(side);
            var targets = new List<TuningTarget>
            {
                new(frequency, MaritimeVhfChannelPlan.VoiceBandwidthHz)
            };
            if (snapshot.DscWatchEnabled && frequency != MaritimeVhfChannelPlan.DscFrequencyHz)
                targets.Add(new(MaritimeVhfChannelPlan.DscFrequencyHz,
                    MaritimeVhfChannelPlan.VoiceBandwidthHz));

            PluginTuningResult result = await context.Tuning.RequestAsync(new(
                "maritime-vhf",
                targets.Count == 2
                    ? $"国際VHF CH {channel.Number} + DSC CH 70"
                    : $"国際VHF CH {channel.Number}",
                targets,
                targets.Count == 2 ? (frequency + MaritimeVhfChannelPlan.DscFrequencyHz) / 2 : frequency,
                targets.Count == 2 ? 768_000 : 96_000,
                25_000,
                true,
                false,
                PluginGainPreference.Automatic), cancellationToken).ConfigureAwait(false);

            bool dscAvailable = targets.Count == 2;
            if (result.Outcome == PluginTuningOutcome.Rejected && targets.Count == 2)
            {
                result = await context.Tuning.RequestAsync(new(
                    "maritime-vhf-voice-only",
                    $"国際VHF CH {channel.Number}",
                    [new(frequency, MaritimeVhfChannelPlan.VoiceBandwidthHz)],
                    frequency,
                    96_000,
                    25_000,
                    true,
                    false,
                    PluginGainPreference.Automatic), cancellationToken).ConfigureAwait(false);
                dscAvailable = false;
            }
            if (result.Outcome == PluginTuningOutcome.Rejected)
            {
                tuningStatus = $"{reason}: 選局失敗 / {result.Message}";
                return false;
            }

            lock (stateGate)
            {
                voiceFrequencyHz = frequency;
                dscChannelAvailable = snapshot.DscWatchEnabled && dscAvailable;
                tuningStatus = dscChannelAvailable
                    ? $"{reason}: CH {channel.Number} + DSC CH 70"
                    : snapshot.DscWatchEnabled
                        ? $"{reason}: CH {channel.Number}（帯域不足のためDSC監視休止）"
                        : $"{reason}: CH {channel.Number}";
            }
            lock (processingGate) ResetReceivers();
            context.Audio.Reset();
            FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
            PublishUi(force: true);
            return true;
        }
        finally
        {
            tuningGate.Release();
        }
    }

    private void OnSettingsChanged(MaritimeVhfSettings proposed)
    {
        MaritimeVhfSettings normalized = proposed.Normalize();
        bool retune;
        bool resetAudio;
        lock (stateGate)
        {
            retune = normalized.SelectedChannel != settings.SelectedChannel ||
                normalized.ReceiveSide != settings.ReceiveSide ||
                normalized.DscWatchEnabled != settings.DscWatchEnabled;
            resetAudio = normalized.MonitorAudioEnabled != settings.MonitorAudioEnabled ||
                normalized.SquelchEnabled != settings.SquelchEnabled ||
                Math.Abs(normalized.SquelchThresholdDbm - settings.SquelchThresholdDbm) > 0.01f;
            settings = normalized;
            if (retune) followOverride = null;
        }
        if (resetAudio) HostContext?.Audio.Reset();
        _ = PersistSettingsAsync();
        if (retune) _ = RetuneFromUiAsync();
    }

    private async Task RetuneFromUiAsync()
    {
        try
        {
            await ApplyTuningAsync("設定変更", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HostContext?.Logger.Log(PluginLogLevel.Warning, "maritime-vhf.tuning.failed",
                "International VHF retuning failed.", exception);
            tuningStatus = $"選局失敗 / {exception.Message}";
            PublishUi(force: true);
        }
    }

    private void PublishDscMessage(DscMessage message)
    {
        string fingerprint = Convert.ToHexString(message.Symbols.ToArray());
        DateTimeOffset now = HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow;
        int maximumHistory;
        lock (stateGate)
        {
            if (recentMessages.TryGetValue(fingerprint, out DateTimeOffset previous) &&
                now - previous < TimeSpan.FromSeconds(2)) return;
            recentMessages[fingerprint] = now;
            foreach (string stale in recentMessages.Where(item => now - item.Value >
                         TimeSpan.FromMinutes(2)).Select(item => item.Key).ToArray())
                recentMessages.Remove(stale);
            maximumHistory = settings.MaximumHistory;
        }

        HostContext?.Dispatcher.Post(() => viewModel.AddMessage(message, maximumHistory));
        IPluginHostContext? context = HostContext;
        if (message.Priority == DscPriority.Distress)
            context?.Notifications.PlayReceptionAlarm();
        else if (message.Priority is DscPriority.Urgency or DscPriority.Safety)
            context?.Notifications.PlayShortReceptionAlarm();

        string details = JsonSerializer.Serialize(new
        {
            message.FormatSpecifier,
            message.Priority,
            message.SourceMmsi,
            message.DestinationMmsi,
            WorkingChannel = message.WorkingChannel?.ChannelNumber,
            ForcedSide = message.WorkingChannel?.ForcedSide?.ToString(),
            message.NatureOfDistress,
            message.Latitude,
            message.Longitude,
            PositionTimeUtc = message.PositionTimeUtc?.ToString("HH:mm")
        });
        ResultPublished?.Invoke(this, new(new PluginResultSummary(
            $"maritime-vhf-dsc-{message.StreamId:N}-{message.SamplePosition}",
            Descriptor.Id,
            message.ReceivedAt,
            message.StreamId,
            "dsc",
            message.Priority switch
            {
                DscPriority.Distress => PluginResultSeverity.Critical,
                DscPriority.Urgency => PluginResultSeverity.Warning,
                DscPriority.Safety => PluginResultSeverity.Notice,
                _ => PluginResultSeverity.Information
            },
            $"DSC {DscProtocolParser.PriorityText(message.Priority)} / " +
            (message.SourceMmsi.Length == 0 ? "MMSI不明" : message.SourceMmsi),
            message.Summary,
            MaritimeVhfChannelPlan.DscFrequencyHz,
            null,
            1,
            details)));
        _ = BeginAutoFollowAsync(message);
    }

    private async Task BeginAutoFollowAsync(DscMessage message)
    {
        MaritimeVhfSettings snapshot;
        DscWorkingChannel? target = message.WorkingChannel;
        lock (stateGate) snapshot = settings;
        if (!snapshot.AutoFollowEnabled || !message.IsAddressedTo(snapshot.OwnMmsi)) return;
        target ??= message.Priority == DscPriority.Distress ? new(16) : null;
        if (target?.Channel is null) return;

        lock (stateGate)
        {
            if (followOverride == target) return;
            followOverride = target;
            followStartedAt = HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow;
            lastVoiceCarrierAt = followStartedAt;
        }
        try
        {
            if (!await ApplyTuningAsync($"DSC追尾 {target.DisplayName}",
                    CancellationToken.None).ConfigureAwait(false))
            {
                lock (stateGate) followOverride = null;
            }
        }
        catch (Exception exception)
        {
            lock (stateGate) followOverride = null;
            HostContext?.Logger.Log(PluginLogLevel.Warning, "maritime-vhf.follow.failed",
                "DSC automatic channel follow failed.", exception);
        }
    }

    private bool ShouldAutoReturn(MaritimeVhfSettings snapshot)
    {
        if (snapshot.AutoReturnSeconds <= 0) return false;
        DateTimeOffset now = HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow;
        lock (stateGate)
        {
            return followOverride is not null &&
                now - followStartedAt >= TimeSpan.FromSeconds(snapshot.AutoReturnSeconds) &&
                now - lastVoiceCarrierAt >= TimeSpan.FromSeconds(snapshot.AutoReturnSeconds);
        }
    }

    private async Task AutoReturnAsync()
    {
        try
        {
            await ReturnFromFollowAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref autoReturnPending, 0);
        }
    }

    private async Task ReturnFromFollowAsync()
    {
        bool changed;
        lock (stateGate)
        {
            changed = followOverride is not null;
            followOverride = null;
        }
        if (changed) await ApplyTuningAsync("通常監視へ復帰", CancellationToken.None)
            .ConfigureAwait(false);
        else PublishUi(force: true);
    }

    private async Task ResetSettingsAsync()
    {
        IPluginHostContext? context = HostContext;
        if (context is not null) await context.Settings.DeleteAsync().ConfigureAwait(false);
        MaritimeVhfSettings defaults = new MaritimeVhfSettings().Normalize();
        lock (stateGate)
        {
            settings = defaults;
            followOverride = null;
        }
        context?.Dispatcher.Post(() => viewModel.SynchronizeSettings(defaults));
        await ApplyTuningAsync("設定初期化", CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PersistSettingsAsync()
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        await settingsSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            MaritimeVhfSettings snapshot;
            lock (stateGate) snapshot = settings;
            await context.Settings.SaveAsync(new(1, JsonSerializer.Serialize(snapshot)))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            context.Logger.Log(PluginLogLevel.Warning, "maritime-vhf.settings.save-failed",
                "International VHF settings could not be saved.", exception);
        }
        finally
        {
            settingsSaveGate.Release();
        }
    }

    private void SubmitAudio(
        ReadOnlySpan<float> audio,
        IqBlockMetadata metadata,
        MaritimeVhfSettings snapshot)
    {
        IPluginHostContext? context = HostContext;
        if (context is null || !snapshot.MonitorAudioEnabled || audio.IsEmpty) return;
        float volume = snapshot.MonitorAudioVolume / 100f;
        byte[] pcm = new byte[audio.Length * sizeof(short)];
        for (int index = 0; index < audio.Length; index++)
        {
            short value = (short)MathF.Round(
                Math.Clamp(audio[index] * volume, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        context.Audio.TrySubmit(new PcmAudioFrame(
            context.PluginId,
            metadata.StreamId,
            Interlocked.Increment(ref audioSequence),
            NarrowFmReceiver.SampleRateHz,
            1,
            PcmSampleFormat.Signed16LittleEndian,
            pcm,
            metadata.Discontinuity != IqDiscontinuity.None));
    }

    private void PublishUi(bool force = false)
    {
        long nowTick = Environment.TickCount64;
        if (!force && nowTick - Interlocked.Read(ref lastUiTick) < 250) return;
        Interlocked.Exchange(ref lastUiTick, nowTick);

        string active;
        string dsc;
        string follow;
        string tune;
        bool following;
        MaritimeVhfSettings snapshot;
        lock (stateGate)
        {
            snapshot = settings;
            active = ActiveVoiceChannelText();
            dsc = !snapshot.DscWatchEnabled
                ? "OFF"
                : dscChannelAvailable ? "CH 70 / 156.525 MHz" : "休止（帯域外）";
            following = followOverride is not null;
            follow = following
                ? $"自動追尾中: {followOverride!.DisplayName}"
                : snapshot.AutoFollowEnabled ? "自動追尾: 待機" : "自動追尾: OFF";
            tune = tuningStatus;
        }
        float signalDbm = HostContext?.ReceiverTelemetry?.DbfsToDbm(
            voiceReceiver.SignalLevelDbfs) ?? voiceReceiver.SignalLevelDbfs - 80f;
        bool squelchOpen = voiceReceiver.IsSquelchOpen;
        HostContext?.Dispatcher.Post(() => viewModel.UpdateReceiver(active, dsc,
            signalDbm, squelchOpen, tune, following, follow));
    }

    private string ActiveVoiceChannelText()
    {
        MaritimeVhfChannel? channel = MaritimeVhfChannelPlan.Channels
            .FirstOrDefault(item => item.ShipFrequencyHz == voiceFrequencyHz ||
                item.CoastFrequencyHz == voiceFrequencyHz);
        return channel is null
            ? $"{voiceFrequencyHz / 1_000_000.0:F3} MHz"
            : $"CH {channel.Number} / {voiceFrequencyHz / 1_000_000.0:F3} MHz";
    }

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome != PluginTuningOutcome.Rejected)
            tuningStatus = $"中心 {result.CenterFrequencyHz / 1_000_000.0:F3} MHz / " +
                $"SR {result.SampleRateHz / 1_000.0:F0} kS/s";
        PublishUi(force: true);
    }

    private void ResetReceivers()
    {
        voiceReceiver.Reset();
        dscFmReceiver.Reset();
        dscDecoder.Reset();
    }

    private static PluginChannelRequest ChannelRequest(string id, long frequencyHz) =>
        new(id, frequencyHz, MaritimeVhfChannelPlan.VoiceBandwidthHz,
            NarrowFmReceiver.SampleRateHz, 384_000, 96_000, 65, 2, 6, false)
        {
            StopbandBandwidthHz = 24_000
        };
}
