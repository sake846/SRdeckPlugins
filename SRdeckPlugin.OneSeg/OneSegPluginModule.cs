using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Dsp;
using SRdeckPlugin.OneSeg.Media;
using SRdeckPlugin.OneSeg.Models;
using SRdeckPlugin.OneSeg.Transport;
using SRdeckPlugin.OneSeg.ViewModels;
using SRdeckPlugin.OneSeg.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.OneSeg;

public sealed partial class OneSegPluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    ILivePluginProfileProvider, IPluginViewProvider, IPluginProcessingWarmup
{
    private readonly object processingGate = new();
    private readonly object videoSettingsGate = new();
    private readonly OneSegReceiver receiver = new();
    private readonly MpegTsAnalyzer analyzer = new();
    private readonly OneSegViewModel viewModel = new();
    private readonly PackedIqHistoryBuffer iqCaptureBuffer = new(5);
    private readonly Dictionary<(ushort ServiceId, ushort EventId), OneSegEventInfo> audioEvents = [];
    private readonly Dictionary<ushort, byte> streamAudioComponentTypes = [];
    private IPluginHostContext? host;
    private OneSegMediaPlayer? mediaPlayer;
    private OneSegRecordingWriter? recording;
    private Task recordingTail = Task.CompletedTask;
    private int recordingGeneration;
    private OneSegSettings settings = new();
    private ITimer? diagnosticsTimer;
    private IqBlockMetadata? lastCaptureMetadata;
    private AppliedChannelConfiguration? lastChannelConfiguration;
    private CancellationTokenSource? channelScanCancellation;
    private readonly CancellationTokenSource mediaPlayerInitializationCancellation = new();
    private Task? mediaPlayerInitialization;
    private long scanChannelFrequencyHz;
    private int captureSaveInProgress;
    private int channelScanInProgress;
    private int emergencyAlarmActive;
    private long lastEmergencyNotificationTicks;
    private long lastReceivedCenterFrequencyHz;
    private int diagnosticsRefreshInProgress;

    internal long ScanChannelFrequencyHz
    {
        get => Volatile.Read(ref scanChannelFrequencyHz);
        set => Interlocked.Exchange(ref scanChannelFrequencyHz, value);
    }

    public OneSegPluginModule()
    {
        RegisterStreamReset(iqCaptureBuffer.Reset);
        receiver.TransportPacketDecoded += OnTransportPacket;
        analyzer.ProgramsChanged += programs =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
            {
                OneSegStation[] receivedStations = CreateReceivedStations(programs);
                Dispatch(() =>
                {
                    viewModel.SetPrograms(programs);
                    if (receivedStations.Length > 0) UpdateReceivedStations(receivedStations);
                });
            }
        };
        analyzer.EventUpdated += value =>
        {
            lock (processingGate)
            {
                if (audioEvents.TryGetValue((value.ServiceId, value.EventId), out OneSegEventInfo? existing))
                {
                    value = value with
                    {
                        AudioComponentType = value.AudioComponentType ?? existing.AudioComponentType,
                        IsPresentEvent = value.IsPresentEvent || existing.IsPresentEvent
                    };
                }
                audioEvents[(value.ServiceId, value.EventId)] = value;
                if (audioEvents.Count > 512)
                {
                    (ushort ServiceId, ushort EventId) oldest = audioEvents
                        .OrderBy(item => item.Value.StartTime ?? DateTimeOffset.MaxValue)
                        .First().Key;
                    audioEvents.Remove(oldest);
                }
            }
            if (Volatile.Read(ref channelScanInProgress) == 0)
            {
                Dispatch(() => viewModel.SetEvent(value));
            }
        };
        analyzer.AudioComponentTypeDetected += (serviceId, componentType) =>
        {
            lock (processingGate)
            {
                streamAudioComponentTypes[serviceId] = componentType;
            }
        };
        analyzer.CaptionReceived += value =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
                Dispatch(() => viewModel.Caption = value.Text);
        };
        analyzer.ConditionalAccessChanged += values =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
                Dispatch(() => viewModel.SetConditionalAccess(values));
        };
        analyzer.NetworkUpdated += value =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
            {
                Dispatch(() =>
                {
                    viewModel.SetNetwork(value);
                    UpdateStationInfoFromNetwork(value);
                });
            }
        };
        analyzer.BroadcastClockUpdated += value =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
                Dispatch(() => viewModel.SetBroadcastClock(value));
        };
        analyzer.BroadcasterUpdated += value =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
                Dispatch(() => viewModel.SetBroadcaster(value));
        };
        analyzer.TransportClockUpdated += value =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
                Dispatch(() => viewModel.SetTransportClock(value));
        };
        analyzer.DataModuleUpdated += value =>
        {
            if (Volatile.Read(ref channelScanInProgress) == 0)
                Dispatch(() => viewModel.SetDataModule(value));
        };
        viewModel.ChannelSelectionRequested = channel => _ = ChangeChannelFromViewAsync(channel);
        viewModel.PrefectureSelectionRequested = (prefecture, channel) => _ = ChangePrefectureFromViewAsync(prefecture, channel);
        viewModel.RegionSelectionRequested = (prefecture, region, channel) =>
            _ = ChangeRegionFromViewAsync(prefecture, region, channel);
        viewModel.CaptureRequested = StartIqCapture;
        viewModel.ScanRequested = isAppend => _ = ScanChannelsFromViewAsync(isAppend);
        viewModel.StopScanRequested = StopScanFromView;
        viewModel.StationSelectionRequested = station => _ = ChangeStationFromViewAsync(station);
        viewModel.AudioTrackSelectionRequested = trackId =>
        {
            settings = settings with { AudioTrackId = trackId };
            lock (processingGate) mediaPlayer?.SelectAudioTrack(trackId);
            PersistSettings();
        };
        viewModel.VideoOutputChanged = () => _ = Task.Run(() =>
        {
            try { mediaPlayer?.RestartPlayback(); }
            catch (Exception exception)
            {
                host?.Logger.Log(PluginLogLevel.Warning, "oneseg.libvlc.video-output",
                    "Video playback could not restart after changing display.", exception);
            }
        });
        viewModel.VideoSuperResolutionChanged = enabled =>
        {
            settings = settings with { VideoSuperResolutionEnabled = enabled };
            PersistSettings();
            QueueVideoSuperResolutionUpdate();
        };
        viewModel.CaptionSettingsChanged = () =>
        {
            settings = settings with
            {
                CaptionsEnabled = viewModel.CaptionsEnabled,
                CaptionFontSize = viewModel.CaptionFontSize,
                CaptionBackgroundEnabled = viewModel.CaptionBackgroundEnabled,
                CaptionPosition = viewModel.SelectedCaptionPosition.Id
            };
            PersistSettings();
        };
        viewModel.EmergencyNotificationsChanged = enabled =>
        {
            settings = settings with { EmergencyNotificationsEnabled = enabled };
            PersistSettings();
        };
        viewModel.RecordingChanged = enabled => SetRecording(enabled);
        viewModel.RecordingSettingsChanged = () =>
        {
            settings = (settings with
            {
                RecordingDirectory = string.IsNullOrWhiteSpace(viewModel.RecordingDirectory) ? null : viewModel.RecordingDirectory.Trim(),
                RecordingSegmentMinutes = viewModel.RecordingSegmentMinutes,
                RecordingMaxBytes = viewModel.RecordingMaxBytes,
                RecordingStorageLimitBytes = viewModel.RecordingStorageLimitBytes,
                AutoRecordOnStart = viewModel.AutoRecordOnStart,
                RecordingSidecarEnabled = viewModel.RecordingSidecarEnabled,
                IqCaptureDirectory = string.IsNullOrWhiteSpace(viewModel.IqCaptureDirectory) ? null : viewModel.IqCaptureDirectory.Trim(),
                IqCaptureSeconds = viewModel.IqCaptureSeconds
            }).Normalize();
            PersistSettings();
        };
        viewModel.VolumeChanged = volume =>
        {
            settings = settings with { Volume = volume };
            mediaPlayer?.SetVolume(volume);
            PersistSettings();
        };
        viewModel.ResetSettingsRequested = ResetSettingsAsync;
    }

    public async ValueTask ResetSettingsAsync()
    {
        if (host is not null)
        {
            await host.Settings.DeleteAsync().ConfigureAwait(false);
        }
        settings = new OneSegSettings().Normalize();
        OneSegChannel selected = SelectedChannel();
        Dispatch(() => viewModel.Configure(settings, selected, preserveRegionPresets: true));
        mediaPlayer?.SetVolume(settings.Volume);
        QueueVideoSuperResolutionUpdate();
    }

    private void QueueVideoSuperResolutionUpdate() => _ = Task.Run(() =>
    {
        lock (videoSettingsGate)
        {
            OneSegMediaPlayer? previous = mediaPlayer;
            bool enabled = settings.VideoSuperResolutionEnabled;
            if (IsDisposed || previous is null || previous.VideoSuperResolutionEnabled == enabled) return;
            OneSegMediaPlayer? replacement = null;
            try
            {
                replacement = new OneSegMediaPlayer(enabled);
                lock (processingGate)
                {
                    previous = mediaPlayer;
                    if (IsDisposed || previous is null || previous.VideoSuperResolutionEnabled == enabled)
                    {
                        replacement.Dispose();
                        return;
                    }
                    if (State == PluginLifecycleState.Streaming) replacement.Start(settings.Volume);
                    mediaPlayer = replacement;
                }
                OneSegMediaPlayer oldPlayer = previous;
                Dispatch(() =>
                {
                    if (!IsDisposed && ReferenceEquals(mediaPlayer, replacement))
                        viewModel.MediaPlayer = replacement.Player;
                    _ = Task.Run(oldPlayer.Dispose);
                });
            }
            catch (Exception exception)
            {
                if (!ReferenceEquals(mediaPlayer, replacement)) replacement?.Dispose();
                host?.Logger.Log(PluginLogLevel.Warning, "oneseg.libvlc.super-resolution",
                    "Video playback could not switch super resolution.", exception);
            }
        }
    });

    public override PluginDescriptor Descriptor { get; } = new(
        "oneseg", "ワンセグ", "ISDB-T 1セグメント テレビ・音声・字幕・番組表受信機",
        new Version(1,0,0), new Version(1,0), new Version(1,0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView |
        PluginCapabilities.SettingsView, "SRdeck", "GPL-3.0");

    public IReadOnlyList<PluginProfileDescriptor> Profiles => ProfileChannels()
        .Select(channel => new PluginProfileDescriptor(channel.Id, channel.DisplayName,
            $"{SelectedPrefecture().DisplayName} / {SelectedRegion().DisplayName} / {channel.FrequencyText}",
            channel.Number == settings.Channel)).ToArray();
    public string? SelectedProfileId => SelectedChannel().Id;
    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            long frequencyHz = ScanChannelFrequencyHz;
            if (frequencyHz <= 0) frequencyHz = SelectedChannel().CenterFrequencyHz;
            return [new("oneseg-main", frequencyHz, IsdbTParameters.OccupiedBandwidthHz,
                IsdbTParameters.OutputSampleRateHz, 2_000_000, 1_015_873, 129, 3, 8, false,
                1_015_873, 2_000_000, 32)
            {
                StopbandBandwidthHz = IsdbTParameters.ChannelBandwidthHz
            }];
        }
    }

    public FrameworkElement CreateMainView() => new OneSegPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new OneSegSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        host = hostContext;
        Dispatch(() => viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics);
        PluginSettingsDocument? document = await hostContext.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try
            {
                settings = JsonSerializer.Deserialize<OneSegSettings>(document.Json) ?? new();
                settings = settings.Normalize();
            }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "oneseg.settings.invalid", "Invalid OneSeg settings; defaults are used.", exception);
            }
        }
        OneSegChannel selected = SelectedChannel();
        Dispatch(() => viewModel.Configure(settings, selected, preserveRegionPresets: true));
        mediaPlayerInitialization = InitializeMediaPlayerAsync(hostContext);
        diagnosticsTimer = hostContext.TimeProvider.CreateTimer(_ =>
        {
            // LibVLC may wait on its decoder or audio-output threads. Do not
            // overlap refreshes, and never hold the real-time IQ processing
            // lock while making native media-player calls.
            if (Interlocked.Exchange(ref diagnosticsRefreshInProgress, 1) != 0) return;
            try
            {
                OneSegDiagnostics diagnostics;
                IReadOnlyList<OneSegAudioTrackOption> audioTracks;
                OneSegMediaPlayer? player;
                long packets;
                long continuityErrors;
                byte? audioComponentType;
                int requestedAudioTrackId;
                AppliedChannelConfiguration? channelConfiguration;
                lock (processingGate)
                {
                    diagnostics = receiver.Diagnostics;
                    packets = analyzer.PacketCount;
                    continuityErrors = analyzer.ContinuityErrors;
                    player = mediaPlayer;
                    audioComponentType = ResolveSelectedAudioComponentType(DateTimeOffset.Now);
                    requestedAudioTrackId = settings.AudioTrackId;
                    channelConfiguration = lastChannelConfiguration;
                }
                bool restartAudioDecoder = player?.SetAudioComponentType(audioComponentType) == true;
                if (restartAudioDecoder) player?.RestartPlayback();
                audioTracks = player?.GetAudioTracks() ?? [];
                if (player is not null)
                {
                    int requestedAudioTrack = player.ResolveAudioTrackSelection(requestedAudioTrackId);
                    if (requestedAudioTrack >= 0 &&
                        (player.AudioTrackSelectionNeedsApply || player.CurrentAudioTrack != requestedAudioTrack))
                        player.SelectAudioTrack(requestedAudioTrack);
                }
                bool emergency = diagnostics.Tmcc?.EmergencyAlarm == true;
                bool wasEmergency = Interlocked.Exchange(ref emergencyAlarmActive, emergency ? 1 : 0) != 0;
                long nowTicks = DateTimeOffset.UtcNow.Ticks;
                long previousTicks = Interlocked.Read(ref lastEmergencyNotificationTicks);
                if (ShouldNotifyEmergency(settings.EmergencyNotificationsEnabled,
                    emergency, wasEmergency, nowTicks, previousTicks))
                {
                    Interlocked.Exchange(ref lastEmergencyNotificationTicks, nowTicks);
                    hostContext.Notifications.PlayShortReceptionAlarm();
                }
                float? calibratedDbm = hostContext.ReceiverTelemetry?.SignalLevelDbm;
                Dispatch(() =>
                {
                    viewModel.ApplyDiagnostics(diagnostics, packets, continuityErrors,
                        State == PluginLifecycleState.Streaming,
                        channelConfiguration?.InputSampleRateHz ?? 0,
                        channelConfiguration is { } applied
                            ? applied.InputSampleRateHz /
                              (double)Math.Max(1, applied.CoarseDecimationFactor) /
                              Math.Max(1, applied.FineDecimationFactor)
                            : 0,
                        IsdbTParameters.OutputSampleRateHz,
                        channelConfiguration.HasValue,
                        calibratedDbm);
                    viewModel.RefreshProgramSummary(DateTimeOffset.Now);
                    viewModel.SetEmergency(emergency);
                    viewModel.SetAudioTracks(audioTracks, requestedAudioTrackId);
                });
            }
            finally { Volatile.Write(ref diagnosticsRefreshInProgress, 0); }
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        SetStatus("設定済み");
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
        PluginTuningResult result = await context.Tuning.RequestAsync(CreateTuningRequest(SelectedChannel()), cancellationToken).ConfigureAwait(false);
        ValidateTuning(result);
        SetStatus($"待機中 / {SelectedPrefecture().DisplayName} / {SelectedRegion().DisplayName} / {SelectedChannel().DisplayName}");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            analyzer.Reset();
            audioEvents.Clear();
            streamAudioComponentTypes.Clear();
            Interlocked.Exchange(ref emergencyAlarmActive, 0);
            Interlocked.Exchange(ref lastEmergencyNotificationTicks, 0);
            mediaPlayer?.Start(settings.Volume);
            if (settings.AutoRecordOnStart) OpenRecording();
        }
        SetStatus($"受信中 / {SelectedPrefecture().DisplayName} / {SelectedRegion().DisplayName} / {SelectedChannel().DisplayName}");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(IReadOnlyList<IChannelIqBlockLease> blocks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        IChannelIqBlockLease? block = blocks.FirstOrDefault(item => item.Metadata.Configuration.RequestId == "oneseg-main");
        if (block is null) return ValueTask.CompletedTask;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            long currentCenterFreq = block.Metadata.Configuration.ChannelCenterFrequencyHz;
            bool channelChanged = lastReceivedCenterFrequencyHz != currentCenterFreq;
            if (block.Metadata.Source.Discontinuity != IqDiscontinuity.None ||
                lastReceivedCenterFrequencyHz != currentCenterFreq)
            {
                receiver.Reset();
                iqCaptureBuffer.Reset();
            }
            lastReceivedCenterFrequencyHz = currentCenterFreq;
            if (channelChanged) RefreshRecordingSnapshot();
            lastCaptureMetadata = block.Metadata.Source;
            lastChannelConfiguration = block.Metadata.Configuration;
            iqCaptureBuffer.Write(block.Samples.Span, block.Metadata.Configuration.OutputSampleRateHz);
            receiver.Process(block.Samples.Span);
        }
        return ValueTask.CompletedTask;
    }

    internal static bool ShouldNotifyEmergency(bool enabled, bool emergency,
        bool wasEmergency, long nowTicks, long previousTicks)
    {
        return enabled && emergency && !wasEmergency &&
            nowTicks - previousTicks >= TimeSpan.FromSeconds(30).Ticks;
    }

    public async ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        // LibVLC loads its native DLLs and initializes the decoder runtime on
        // the first OneSegMediaPlayer construction.  Initialization is started
        // during plugin initialization, but it must be awaited here so the
        // application startup warm-up absorbs that cost before live IQ arrives.
        if (mediaPlayerInitialization is Task initialization)
            await initialization.ConfigureAwait(false);

        OneSegChannel channel = SelectedChannel();
        await PluginProcessingWarmup.RunChannelAsync(context, "oneseg-main", channel.CenterFrequencyHz,
            IsdbTParameters.OutputSampleRateHz, IsdbTParameters.OccupiedBandwidthHz,
            (samples, _) => { lock (processingGate) receiver.Process(samples); },
            () => { lock (processingGate) receiver.Reset(); }, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        channelScanCancellation?.Cancel();
        OneSegMediaPlayer? player;
        Task recordingStopped;
        lock (processingGate)
        {
            recordingStopped = CloseRecording("stream-stop");
            player = mediaPlayer;
            receiver.Reset();
            Interlocked.Exchange(ref emergencyAlarmActive, 0);
            Interlocked.Exchange(ref lastEmergencyNotificationTicks, 0);
        }
        await recordingStopped.ConfigureAwait(false);
        if (player is not null)
            await Task.Run(player.Stop).ConfigureAwait(false);
        SetStatus($"待機中 / {SelectedPrefecture().DisplayName} / {SelectedRegion().DisplayName} / {SelectedChannel().DisplayName}");
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        SetStatus("停止中");
        return ValueTask.CompletedTask;
    }

    public async ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        OneSegPrefecture prefecture = SelectedPrefecture();
        OneSegChannel next = ProfileChannels().FirstOrDefault(item => item.Id == profileId)
            ?? throw new ArgumentException($"Unknown OneSeg profile '{profileId}'.", nameof(profileId));
        await ApplySelectionAsync(prefecture, SelectedRegion(), next, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplySelectionAsync(OneSegPrefecture prefecture, OneSegRegionPreset region,
        OneSegChannel next,
        CancellationToken cancellationToken, bool forceRestart = false)
    {
        if (!forceRestart && string.Equals(prefecture.Id, settings.PrefectureId,
            StringComparison.OrdinalIgnoreCase) && string.Equals(region.Id, settings.RegionId,
            StringComparison.OrdinalIgnoreCase) && next.Number == settings.Channel) return;
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
            ValidateTuning(await context.Tuning.RequestAsync(CreateTuningRequest(next), cancellationToken).ConfigureAwait(false));
        }
        lock (processingGate)
        {
            bool regionChanged = !string.Equals(prefecture.Id, settings.PrefectureId,
                StringComparison.OrdinalIgnoreCase) || !string.Equals(region.Id, settings.RegionId,
                StringComparison.OrdinalIgnoreCase);
            settings = settings with
            {
                PrefectureId = prefecture.Id,
                RegionId = region.Id,
                Channel = next.Number,
                ScannedStations = regionChanged ? [] : settings.ScannedStations
            };
            receiver.Reset();
            analyzer.Reset();
            audioEvents.Clear();
            streamAudioComponentTypes.Clear();
            iqCaptureBuffer.Reset();
            lastReceivedCenterFrequencyHz = 0;
            Interlocked.Exchange(ref emergencyAlarmActive, 0);
            Interlocked.Exchange(ref lastEmergencyNotificationTicks, 0);
            if (State == PluginLifecycleState.Streaming) mediaPlayer?.Start(settings.Volume);
        }
        Dispatch(() =>
        {
            viewModel.ResetServiceData();
            viewModel.Configure(settings, next, preserveRegionPresets: true);
            viewModel.SetRecordingState(recording?.IsAccepting == true);
        });
        PersistSettings();
        SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / {prefecture.DisplayName} / {region.DisplayName} / {next.DisplayName}");
    }

    protected override ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        channelScanCancellation?.Cancel();
        mediaPlayerInitializationCancellation.Cancel();
        receiver.TransportPacketDecoded -= OnTransportPacket;
        host = null;
        // LibVLC Stop/Dispose can wait for native decoder callbacks.  Never
        // run that synchronous native cleanup on the WPF dispatcher thread.
        return new ValueTask(Task.Run(DisposeResources));
    }

    private void DisposeResources()
    {
        diagnosticsTimer?.Dispose();
        Task recordingStopped;
        OneSegMediaPlayer? player;
        lock (processingGate)
        {
            recordingStopped = CloseRecording("stream-stop");
            player = mediaPlayer;
            mediaPlayer = null;
            receiver.Reset();
        }
        recordingStopped.GetAwaiter().GetResult();
        player?.Dispose();
    }
}
