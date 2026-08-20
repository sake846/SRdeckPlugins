using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Vdl.Dsp;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;
using SRdeckPlugin.Vdl.ViewModels;
using SRdeckPlugin.Vdl.Views;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Vdl;

public sealed partial class VdlPluginModule : PluginModuleBase, IIqBlockConsumer, IPluginChannelBlockConsumer,
    ILivePluginProfileProvider, IFrequencyOverlayProvider,
    IPluginViewProvider, IPluginResultProvider, IPluginExportProvider,
    IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup
{
    public sealed record Channel(string Id, string Name, long FrequencyHz);
    public static IReadOnlyList<Channel> Channels { get; } =
    [
        new("136725", "VDL2 136.725 MHz", 136_725_000),
        new("136775", "VDL2 136.775 MHz", 136_775_000),
        new("136825", "VDL2 136.825 MHz", 136_825_000),
        new("136875", "VDL2 136.875 MHz", 136_875_000),
        new("136925", "VDL2 136.925 MHz", 136_925_000),
        new("136975", "VDL2 136.975 MHz", 136_975_000)
    ];

    private readonly VdlMode2Receiver receiver = new();
    private readonly VdlUpperLayerDecoder upperLayerDecoder = new();
    private readonly object processingGate = new();
    private readonly object historyGate = new();
    private readonly List<VdlDecodedFrame> history = [];
    private PluginJsonLinesHistoryWriter<VdlDecodedFrame>? historyWriter;
    private readonly PackedIqHistoryPairBuffer pretriggerBuffer = new(3);
    private readonly VdlViewModel viewModel = new();
    private readonly VdlPipelineDiagnostics pipelineDiagnostics = new();

    private IPluginHostContext? host;
    private VdlSettings settings = new();
    private string selectedProfileId = "136975";
    private long audioSequence;
    private readonly PluginAudioGenerationTracker audioGeneration = new();
    private IqBlockMetadata? lastCaptureMetadata;
    private int captureSaveInProgress;
    private int tuningRequestInProgress;

    public VdlPluginModule()
    {
        RegisterStreamReset(pretriggerBuffer.Reset);
        RegisterStreamReset(audioGeneration.Reset);
        viewModel.ChannelSelectionRequested = TrySelectChannelFromView;
        viewModel.MaximumHistoryChanged = value =>
        {
            settings = settings with { MaximumHistory = value };
            PruneHistory();
            PersistSettings();
        };
        viewModel.MaximumTrailPointsChanged = value => { settings = settings with { MaximumTrailPoints = value }; PersistSettings(); };
        viewModel.MaximumAircraftChanged = value => { settings = settings with { MaximumAircraft = value }; PersistSettings(); };
        viewModel.RetentionMinutesChanged = value => { settings = settings with { RetentionMinutes = value }; PersistSettings(); };
        viewModel.MonitorAudioEnabledChanged = value =>
        {
            settings = settings with { MonitorAudioEnabled = value };
            if (!value) host?.Audio.Reset();
            else audioGeneration.Reset();
            PersistSettings();
        };
        viewModel.MonitorAudioVolumeChanged = value =>
        {
            settings = settings with { MonitorAudioVolume = value };
            PersistSettings();
        };
        viewModel.SquelchEnabledChanged = value =>
        {
            settings = settings with { SquelchEnabled = value };
            receiver.IsSquelchEnabled = value;
            PersistSettings();
        };
        viewModel.PreambleVerificationSymbolsChanged = value =>
        {
            settings = settings with { PreambleVerificationSymbols = value };
            receiver.PreambleVerificationSymbols = value;
            PersistSettings();
        };
        viewModel.AdaptiveEqualizerEnabledChanged = value =>
        {
            settings = settings with { AdaptiveEqualizerEnabled = value };
            receiver.AdaptiveEqualizerEnabled = value;
            PersistSettings();
        };
        viewModel.ClearRequested = () =>
        {
            lock (historyGate) history.Clear();
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
        settings = new VdlSettings().Normalize();
        selectedProfileId = settings.SelectedChannelId;
        receiver.PreambleVerificationSymbols = settings.PreambleVerificationSymbols;
        receiver.IsSquelchEnabled = settings.SquelchEnabled;
        receiver.AdaptiveEqualizerEnabled = settings.AdaptiveEqualizerEnabled;
        viewModel.SynchronizeSettings(selectedProfileId, settings.MonitorAudioEnabled,
            settings.MonitorAudioVolume, settings.MaximumHistory, settings.PreambleVerificationSymbols,
            settings.SquelchEnabled, settings.AdaptiveEqualizerEnabled,
            settings.SaveRawFrames,
            settings.MaximumAircraft, settings.RetentionMinutes, settings.MaximumTrailPoints);
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "vdl",
        "VDL Mode 2",
        "VHF Data Link Mode 2 (D8PSK / AVLC) receiver",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.ChannelIqConsumer |
        PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.ResultPublisher |
        PluginCapabilities.Export |
        PluginCapabilities.FrequencyOverlay,
        "SRdeck",
        "GPL-3.0");

    public event EventHandler? FrequencyOverlaysChanged { add { } remove { } }
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
    [
        new($"vdl-{selectedProfileId}",
            Channels.First(item => item.Id == selectedProfileId).FrequencyHz,
            25_000,
            string.Empty,
            true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary),
            "Transparent",
            "#FFFFFFFF",
            -1)
    ];

    internal VdlViewModel ViewModel => viewModel;
    internal IReadOnlyList<VdlDecodedFrame> Frames => viewModel.Frames;
    internal int AudioMonitorVolume
    {
        get => settings.MonitorAudioVolume;
        set => viewModel.AudioMonitorVolume = value;
    }

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "D8PSK復調・等化・AVLC/ACARS解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "バースト検出、シンボル同期、適応等化、AVLC検証、上位層メッセージ解析");
    public PluginIqPreferences IqPreferences { get; } = new(8);
    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            Channel channel = Channels.First(item => item.Id == selectedProfileId);
            return
            [
                new($"vdl-{channel.Id}", channel.FrequencyHz, 16_800,
                    VdlMode2Receiver.WorkingSampleRate, 240_000,
                    VdlMode2Receiver.WorkingSampleRate, 32, 3, 8, true)
            ];
        }
    }
    public IReadOnlyList<PluginProfileDescriptor> Profiles => Channels.Select((channel, index) =>
        new PluginProfileDescriptor(channel.Id, channel.Name, "25 kHz VDL Mode 2 channel", index == Channels.Count - 1)).ToArray();
    public string? SelectedProfileId => selectedProfileId;
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("csv", "CSV", ".csv", "text/csv"),
        new("json", "JSON", ".json", "application/json")
    ];

    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() => new VdlPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new VdlSettingsView { DataContext = viewModel };

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
            try { settings = (JsonSerializer.Deserialize<VdlSettings>(document.Json) ?? new()).Normalize(); }
            catch (JsonException exception)
            {
                host.Logger.Log(PluginLogLevel.Warning, "vdl.settings.invalid", "Invalid VDL settings; defaults are used.", exception);
            }
        }
        settings = settings.Normalize();
        selectedProfileId = settings.SelectedChannelId;
        receiver.PreambleVerificationSymbols = settings.PreambleVerificationSymbols;
        receiver.IsSquelchEnabled = settings.SquelchEnabled;
        receiver.AdaptiveEqualizerEnabled = settings.AdaptiveEqualizerEnabled;
        viewModel.SynchronizeSettings(selectedProfileId, settings.MonitorAudioEnabled,
            settings.MonitorAudioVolume, settings.MaximumHistory, settings.PreambleVerificationSymbols,
            settings.SquelchEnabled, settings.AdaptiveEqualizerEnabled,
            settings.SaveRawFrames,
            settings.MaximumAircraft, settings.RetentionMinutes, settings.MaximumTrailPoints);
        historyWriter = CreateHistoryWriter(hostContext);
        LoadHistory();
    }

    public ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!Channels.Any(item => item.Id == profileId))
            throw new ArgumentException($"Unknown VDL profile '{profileId}'.", nameof(profileId));
        if (!TrySelectChannelFromView(profileId))
            throw new InvalidOperationException("Failed to select VDL channel.");
        return ValueTask.CompletedTask;
    }

    private bool TrySelectChannelFromView(string profileId)
    {
        Channel channel = Channels.First(item => item.Id == profileId);
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
            SetStatus($"切替中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz");
            PluginTuningResult result = RequestTuningForCurrentChannelAsync(context, channel, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            ValidateTuningResult(result);
        }

        lock (processingGate)
        {
            selectedProfileId = profileId;
            settings = settings with { SelectedChannelId = profileId };
            if (State == PluginLifecycleState.Streaming)
            {
                receiver.Reset();
                upperLayerDecoder.Reset();
                pretriggerBuffer.Reset();
                audioGeneration.Reset();
                host?.Audio.Reset();
            }
        }
        PersistSettings();
        viewModel.SynchronizeSettings(selectedProfileId, settings.MonitorAudioEnabled,
            settings.MonitorAudioVolume, settings.MaximumHistory, settings.PreambleVerificationSymbols,
            settings.SquelchEnabled, settings.AdaptiveEqualizerEnabled,
            settings.SaveRawFrames,
            settings.MaximumAircraft, settings.RetentionMinutes, settings.MaximumTrailPoints);
        SetStatus(State == PluginLifecycleState.Streaming
            ? $"受信中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz"
            : State == PluginLifecycleState.Active
                ? $"待機中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz"
                : "設定済み");
        return true;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        Channel channel = Channels.First(item => item.Id == selectedProfileId);
        IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
        PluginTuningResult result = await RequestTuningForCurrentChannelAsync(context, channel, cancellationToken).ConfigureAwait(false);
        ValidateTuningResult(result);
        SetStatus($"待機中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            upperLayerDecoder.Reset();
            audioSequence = 0;
        }
        viewModel.CaptureStatus = "IQ録音: 直前3秒を常時保持中";
        SetStatus("受信中");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            upperLayerDecoder.Reset();
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
