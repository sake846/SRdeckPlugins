using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Acars.Dsp;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Acars.ViewModels;
using SRdeckPlugin.Acars.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars;

public sealed partial class AcarsPluginModule : PluginModuleBase, IIqBlockConsumer, IPluginChannelBlockConsumer,
    ILivePluginProfileProvider,
    IPluginViewProvider, IFrequencyOverlayProvider, IPluginResultProvider, IPluginExportProvider,
    IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup
{
    public sealed record Channel(string Id, string Region, string Name, long FrequencyHz)
    {
        public string DisplayName => $"{Region} {Name}";
    }
    public static IReadOnlyList<Channel> Channels { get; } =
    [
        // Keep the original IDs stable because they are persisted in plugin settings.
        new("jp-primary", "日本", "131.450 MHz", 131_450_000),
        new("jp-secondary", "日本", "131.250 MHz", 131_250_000),
        new("jp-airport-auxiliary", "日本", "131.950 MHz（空港補助）", 131_950_000),
        new("us-primary", "世界共通", "131.550 MHz（世界共通）", 131_550_000),
        new("global-arinc", "世界共通", "129.125 MHz", 129_125_000),

        new("north-america-130025", "北米", "130.025 MHz", 130_025_000),
        new("north-america-130450", "北米", "130.450 MHz", 130_450_000),
        new("north-america-136850", "北米", "136.850 MHz", 136_850_000),
        new("us-130425", "北米", "130.425 MHz（米国）", 130_425_000),
        new("us-131125", "北米", "131.125 MHz（米国）", 131_125_000),
        new("us-136700", "北米", "136.700 MHz（米国）", 136_700_000),
        new("us-europe-136750", "北米・欧州", "136.750 MHz", 136_750_000),
        new("us-136800", "北米", "136.800 MHz（米国）", 136_800_000),

        new("europe-primary", "欧州", "131.725 MHz", 131_725_000),
        new("europe-secondary", "欧州", "131.525 MHz", 131_525_000),
        new("europe-131850", "欧州", "131.850 MHz", 131_850_000),
        new("europe-136900", "欧州", "136.900 MHz", 136_900_000)
    ];

    public static bool IsChannelAvailableInRegion(Channel channel, string region) =>
        channel.Region == region || region == "日本" && channel.Id == "us-primary";

    private readonly object gate = new();
    private readonly object captureGate = new();
    private readonly object receiverGate = new();
    private readonly object processingGate = new();
    private readonly Dictionary<string, AcarsReceiver> receivers = new(StringComparer.Ordinal);
    private readonly AcarsMessageReassembler messageReassembler = new();
    private readonly AcarsMessageInterpretationService messageInterpretation = new();
    private readonly AcarsViewModel viewModel = new();
    private readonly List<AcarsReception> history = [];
    private PluginJsonLinesHistoryWriter<AcarsReception>? historyWriter;
    private IPluginHostContext? host;
    private AcarsSettings settings = new();
    private string selectedProfileId = "jp-primary";
    private readonly IqStreamContinuityTracker continuity = new();
    private long audioSequence;
    private long lastDiagnosticsUpdateMilliseconds;
    private int audioDiscontinuityPending;
    private readonly PackedIqHistoryBuffer pretriggerBuffer = new(3);
    private readonly object uninterpretedFileGate = new();
    private IqBlockMetadata? lastCaptureMetadata;
    private int captureSaveInProgress;

    public AcarsPluginModule()
    {
        RegisterStreamReset(pretriggerBuffer.Reset);
        viewModel.ChannelSelectionRequested = TrySelectChannelFromView;
        viewModel.MonitoredChannelsChanged = TrySetMonitoredChannelsFromView;
        viewModel.MaximumHistoryChanged = value =>
        {
            settings = settings with { MaximumHistory = value };
            PruneHistory();
            PersistSettings();
        };
        viewModel.MaximumAircraftChanged = value =>
        {
            settings = settings with { MaximumAircraft = value };
            PersistSettings();
        };
        viewModel.RetentionMinutesChanged = value =>
        {
            settings = settings with { RetentionMinutes = value };
            PersistSettings();
        };
        viewModel.MaximumTrailPointsChanged = value =>
        {
            settings = settings with { MaximumTrailPoints = value };
            PersistSettings();
        };
        viewModel.MonitorAudioEnabledChanged = value =>
        {
            settings = settings with { MonitorAudioEnabled = value };
            if (!value) host?.Audio.Reset();
            PersistSettings();
        };
        viewModel.SquelchEnabledChanged = value =>
        {
            settings = settings with { SquelchEnabled = value };
            lock (receiverGate)
            {
                foreach (AcarsReceiver receiver in receivers.Values)
                    receiver.IsSquelchEnabled = value;
            }
            host?.Audio.Reset();
            PersistSettings();
        };
        viewModel.MonitorAudioVolumeChanged = value =>
        {
            settings = settings with { MonitorAudioVolume = value };
            PersistSettings();
        };
        viewModel.SaveUninterpretedMessagesChanged = value =>
        {
            settings = settings with { SaveUninterpretedMessages = value };
            PersistSettings();
        };
        viewModel.BuzzerEnabledChanged = value =>
        {
            settings = settings with { BuzzerEnabled = value };
            PersistSettings();
        };
        viewModel.ClearRequested = () =>
        {
            lock (gate) history.Clear();
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
        settings = new AcarsSettings().Normalize();
        selectedProfileId = settings.SelectedChannelId;
        ConfigureReceivers();
        viewModel.SynchronizeSettings(selectedProfileId, settings.MonitoredChannelIds,
            settings.MaximumHistory, settings.MonitorAudioEnabled, settings.MonitorAudioVolume,
            settings.SaveUninterpretedMessages, settings.BuzzerEnabled, settings.MaximumTrailPoints,
            settings.SaveRawFrames,
            settings.UninterpretedLogFilePath, settings.SquelchEnabled);
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "acars",
        "ACARS",
        "VHF ACARS AM-MSK receiver and ARINC 618 decoder",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.ChannelIqConsumer |
        PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.FrequencyOverlay | PluginCapabilities.ResultPublisher | PluginCapabilities.Export,
        "SRdeck",
        "GPL-3.0");

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "AM-MSK復調・同期・ARINC 618解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "各監視チャンネルのMSK復調、ビット同期、フレーム検証、メッセージ再構成");
    public PluginIqPreferences IqPreferences { get; } = new(8);
    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            lock (receiverGate)
                return MonitoredChannels().Select(channel => new PluginChannelRequest(
                    $"acars-{channel.Id}", channel.FrequencyHz, 10_000,
                    AcarsReceiver.DemodulationSampleRateHz, 72_000, 56_000,
                    32, 3, 8, true, 240_000, 400_000, 8)).ToArray();
        }
    }
    public IReadOnlyList<PluginProfileDescriptor> Profiles { get; } = Channels.Select((item, index) =>
        new PluginProfileDescriptor(item.Id, item.DisplayName, $"ACARS channel {item.FrequencyHz / 1_000_000.0:F3} MHz", index == 0)).ToArray();
    public string? SelectedProfileId => selectedProfileId;
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays => Channels
        .Where(item => settings.MonitoredChannelIds.Contains(item.Id, StringComparer.Ordinal))
        .Select(item => new FrequencyOverlayItem(
            $"acars-{item.Id}",
            item.FrequencyHz,
            25_000,
            string.Empty,
            true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary),
            "Transparent",
            "#FFFFFFFF",
            -1,
            true,
            1.0,
            item.DisplayName)).ToArray();
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("csv", "CSV", ".csv", "text/csv"),
        new("json", "JSON", ".json", "application/json")
    ];

    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() => new AcarsPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new AcarsSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        host = hostContext;
        viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
        host.Tuning.AppliedConfigurationChanged += OnTuningChanged;
        PluginSettingsDocument? document = await host.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try { settings = (JsonSerializer.Deserialize<AcarsSettings>(document.Json) ?? new()).Normalize(); }
            catch (JsonException exception) { host.Logger.Log(PluginLogLevel.Warning, "acars.settings.invalid", "Invalid ACARS settings; defaults are used.", exception); }
        }
        settings = settings.Normalize();
        selectedProfileId = settings.SelectedChannelId;
        ConfigureReceivers();
        historyWriter = CreateHistoryWriter(hostContext);
        viewModel.SynchronizeSettings(selectedProfileId, settings.MonitoredChannelIds,
            settings.MaximumHistory, settings.MonitorAudioEnabled, settings.MonitorAudioVolume,
            settings.SaveUninterpretedMessages, settings.BuzzerEnabled, settings.MaximumTrailPoints,
            settings.SaveRawFrames,
            settings.UninterpretedLogFilePath, settings.SquelchEnabled,
            settings.MaximumAircraft, settings.RetentionMinutes);

        LoadHistory();
    }

    public ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!Channels.Any(item => item.Id == profileId))
            throw new ArgumentException($"Unknown ACARS profile '{profileId}'.", nameof(profileId));
        if (!TrySelectChannelFromView(profileId))
            throw new InvalidOperationException("The ACARS primary channel is outside the current IQ passband.");
        viewModel.SynchronizeSettings(profileId, settings.MonitoredChannelIds,
            settings.MaximumHistory, settings.MonitorAudioEnabled, settings.MonitorAudioVolume,
            settings.SaveUninterpretedMessages, settings.BuzzerEnabled, settings.MaximumTrailPoints,
            settings.SaveRawFrames,
            settings.UninterpretedLogFilePath, settings.SquelchEnabled,
            settings.MaximumAircraft, settings.RetentionMinutes);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        Channel primaryChannel = SelectedChannel();
        Channel[] monitoredChannels = MonitoredChannels();
        IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
        PluginTuningResult result = await RequestTuningAsync(context, primaryChannel,
            monitoredChannels, cancellationToken).ConfigureAwait(false);
        int removedChannelCount = 0;
        if (result.Outcome == PluginTuningOutcome.Rejected && monitoredChannels.Length > 1)
        {
            PluginTuningResult primaryResult = await RequestTuningAsync(context, primaryChannel,
                [primaryChannel], cancellationToken).ConfigureAwait(false);
            if (primaryResult.Outcome != PluginTuningOutcome.Rejected)
            {
                Channel[] fittedChannels = FitChannelsToSampleRate(
                    primaryChannel, monitoredChannels, primaryResult.SampleRateHz);
                result = fittedChannels.Length > 1
                    ? await RequestTuningAsync(context, primaryChannel, fittedChannels,
                        cancellationToken).ConfigureAwait(false)
                    : primaryResult;
                if (result.Outcome == PluginTuningOutcome.Rejected)
                {
                    fittedChannels = [primaryChannel];
                    result = primaryResult;
                }
                removedChannelCount = monitoredChannels.Length - fittedChannels.Length;
                monitoredChannels = fittedChannels;
                ApplyMonitoredChannels(fittedChannels.Select(channel => channel.Id).ToArray());
            }
        }
        if (result.Outcome == PluginTuningOutcome.Rejected) throw new InvalidOperationException($"ACARS tuning was rejected: {result.Message}");
        if (result.SampleRateHz < AcarsReceiver.DemodulationSampleRateHz) throw new InvalidOperationException("ACARS requires at least 48 kS/s.");
        SetStatus("復調器を最適化中");
        WarmUpReceivers(result.SampleRateHz, result.CenterFrequencyHz, cancellationToken);
        SetStatus(removedChannelCount > 0
            ? $"待機中 / {monitoredChannels.Length} ch / 帯域外 {removedChannelCount} chを解除"
            : $"待機中 / {monitoredChannels.Length} ch");
    }

    private static ValueTask<PluginTuningResult> RequestTuningAsync(
        IPluginHostContext context, Channel primaryChannel, IReadOnlyList<Channel> channels,
        CancellationToken cancellationToken)
    {
        long lowerEdgeHz = channels.Min(channel => channel.FrequencyHz - 4_000);
        long upperEdgeHz = channels.Max(channel => channel.FrequencyHz + 4_000);
        long requiredWidthHz = upperEdgeHz - lowerEdgeHz;
        int minimumSampleRateHz = checked((int)Math.Max(AcarsReceiver.DemodulationSampleRateHz,
            (requiredWidthHz * 20 + 18) / 19));
        return context.Tuning.RequestAsync(new(primaryChannel.Id,
            $"ACARS {channels.Count} ch",
            channels.Select(channel => new TuningTarget(channel.FrequencyHz, 8_000)).ToArray(),
            SelectPreferredCenterFrequency(channels), minimumSampleRateHz,
            5_000, true, false, PluginGainPreference.Automatic), cancellationToken);
    }

    internal static long SelectPreferredCenterFrequency(IReadOnlyList<Channel> channels)
    {
        ArgumentOutOfRangeException.ThrowIfZero(channels.Count);
        long lowerEdgeHz = channels.Min(channel => channel.FrequencyHz - 4_000);
        long upperEdgeHz = channels.Max(channel => channel.FrequencyHz + 4_000);
        long midpointHz = lowerEdgeHz + (upperEdgeHz - lowerEdgeHz) / 2;

        // Keep an ACARS carrier away from the zero-IF/DC region when the host
        // passband has enough room. The host tuning service validates the
        // preferred value and falls back to the occupied-span midpoint when a
        // narrow sample rate cannot accommodate the offset.
        const long dcGuardHz = 25_000;
        const long dcAvoidanceOffsetHz = 50_000;
        if (channels.All(channel => Math.Abs(channel.FrequencyHz - midpointHz) >= dcGuardHz))
            return midpointHz;

        long lowerCandidateHz = midpointHz - dcAvoidanceOffsetHz;
        long upperCandidateHz = midpointHz + dcAvoidanceOffsetHz;
        long lowerSeparationHz = channels.Min(channel =>
            Math.Abs(channel.FrequencyHz - lowerCandidateHz));
        long upperSeparationHz = channels.Min(channel =>
            Math.Abs(channel.FrequencyHz - upperCandidateHz));
        return upperSeparationHz >= lowerSeparationHz ? upperCandidateHz : lowerCandidateHz;
    }

    private static Channel[] FitChannelsToSampleRate(Channel primaryChannel,
        IReadOnlyList<Channel> requestedChannels, int sampleRateHz)
    {
        long maximumWidthHz = sampleRateHz * 19L / 20;
        var fitted = new List<Channel> { primaryChannel };
        long lowerEdgeHz = primaryChannel.FrequencyHz - 4_000;
        long upperEdgeHz = primaryChannel.FrequencyHz + 4_000;
        foreach (Channel channel in requestedChannels
                     .Where(channel => channel.Id != primaryChannel.Id)
                     .OrderBy(channel => Math.Abs(channel.FrequencyHz - primaryChannel.FrequencyHz)))
        {
            long candidateLowerHz = Math.Min(lowerEdgeHz, channel.FrequencyHz - 4_000);
            long candidateUpperHz = Math.Max(upperEdgeHz, channel.FrequencyHz + 4_000);
            if (candidateUpperHz - candidateLowerHz > maximumWidthHz) continue;
            fitted.Add(channel);
            lowerEdgeHz = candidateLowerHz;
            upperEdgeHz = candidateUpperHz;
        }
        return fitted.OrderBy(channel => channel.Id == primaryChannel.Id ? 0 : 1)
            .ThenBy(channel => channel.FrequencyHz).ToArray();
    }

    private void ApplyMonitoredChannels(string[] channelIds)
    {
        lock (receiverGate)
        {
            settings = (settings with { MonitoredChannelIds = channelIds }).Normalize();
            ConfigureReceiversLocked(settings.MonitoredChannelIds);
        }
        PersistSettings();
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        host?.Dispatcher.Post(() => viewModel.SynchronizeSettings(selectedProfileId,
            settings.MonitoredChannelIds, settings.MaximumHistory,
            squelchEnabled: settings.SquelchEnabled));
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        Channel[] channels = MonitoredChannels();
        int sampleRateHz = Math.Max(
            AcarsReceiver.DemodulationSampleRateHz,
            context.SampleRateHz);
        long centerFrequencyHz = SelectPreferredCenterFrequency(channels);
        return new ValueTask(Task.Run(
            () => WarmUpReceivers(
                sampleRateHz,
                centerFrequencyHz,
                cancellationToken),
            cancellationToken));
    }

    private void WarmUpReceivers(
        int sampleRateHz,
        long centerFrequencyHz,
        CancellationToken cancellationToken)
    {
        (Channel _, (Channel Channel, AcarsReceiver Receiver)[] targets) =
            ReceiverProcessingPlan();
        int sampleCount = Math.Max(AcarsReceiver.DemodulationSampleRateHz,
            sampleRateHz / 10);
        int audioCapacity = checked((int)Math.Ceiling(
            sampleCount * (double)AcarsReceiver.DemodulationSampleRateHz /
            sampleRateHz) + 2);
        Complex32[] samples = ArrayPool<Complex32>.Shared.Rent(sampleCount);
        var channelAudio = new float[targets.Length][];
        try
        {
            for (int index = 0; index < channelAudio.Length; index++)
                channelAudio[index] = ArrayPool<float>.Shared.Rent(audioCapacity);
            samples.AsSpan(0, sampleCount).Clear();
            var metadata = new IqBlockMetadata(Guid.NewGuid(), 0, 0, 0,
                Stopwatch.GetTimestamp(), host?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow,
                sampleRateHz, centerFrequencyHz, sampleCount,
                IqInputSource.Playback, IqDiscontinuity.StreamStarted);
            Parallel.For(0, targets.Length, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumChannelParallelism(targets.Length)
            }, index =>
            {
                (Channel channel, AcarsReceiver receiver) = targets[index];
                receiver.Process(samples.AsSpan(0, sampleCount), metadata, channel.FrequencyHz,
                    channelAudio[index].AsSpan(0, audioCapacity), out _);
            });
            MixSquelchedChannelAudio(targets, channelAudio,
                (int)Math.Floor(sampleCount * (double)AcarsReceiver.DemodulationSampleRateHz /
                    sampleRateHz));
        }
        finally
        {
            foreach ((_, AcarsReceiver receiver) in targets)
                receiver.Reset(0, sampleRateHz);
            foreach (float[] audio in channelAudio)
                if (audio is not null) ArrayPool<float>.Shared.Return(audio);
            ArrayPool<Complex32>.Shared.Return(samples);
        }
    }

    private static int MaximumChannelParallelism(int channelCount) =>
        Math.Min(channelCount, Math.Clamp(Environment.ProcessorCount - 2, 1, 4));

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
            foreach (AcarsReceiver receiver in ReceiverSnapshot()) receiver.Reset();
        continuity.Reset();
        audioSequence = 0;
        Volatile.Write(ref audioDiscontinuityPending, 0);
        lastDiagnosticsUpdateMilliseconds = 0;
        viewModel.CaptureStatus = "IQ録音: 直前3秒を常時保持中";
        SetStatus("受信中");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            foreach (ReassembledMessage incomplete in messageReassembler.Drain())
                Publish(incomplete.Frame, incomplete.Message);
            foreach (AcarsReceiver receiver in ReceiverSnapshot()) receiver.Reset();
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
