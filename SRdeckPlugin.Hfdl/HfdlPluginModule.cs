using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Hfdl.Dsp;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Hfdl.Protocols;
using SRdeckPlugin.Hfdl.ViewModels;
using SRdeckPlugin.Hfdl.Views;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Hfdl;

public sealed partial class HfdlPluginModule : PluginModuleBase, IIqBlockConsumer, IPluginChannelBlockConsumer,
    ILivePluginProfileProvider, IFrequencyOverlayProvider,
    IPluginViewProvider, IPluginResultProvider, IPluginExportProvider,
    IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup
{
    public const int SignalOffsetHz = 1_440;
    public const string DefaultChannelId = "gs06-10066";
    public sealed record GroundStation(int Id, string Slug, string Region, string Name,
        string Country, double Latitude, double Longitude, IReadOnlyList<int> FrequenciesKHz)
    {
        public string DisplayName => $"{Region} / {Name} ({Country})";
    }
    public sealed record Channel(string Id, string Name, long FrequencyHz, int GroundStationId,
        string StationName, string Country, string Region, double Latitude, double Longitude)
    {
        public string FrequencyDisplay => $"{FrequencyHz / 1_000_000.0:F3} MHz";
    }
    public static IReadOnlyList<GroundStation> GroundStations { get; } =
    [
        new(1, "san-francisco", "北米西部", "San Francisco", "USA", 38.3844, -121.7592,
            [5508, 6559, 8927, 10081, 11327, 13276, 17919, 21934]),
        new(2, "molokai", "北太平洋", "Molokai", "Hawaii, USA", 21.1842, -157.1864,
            [5514, 6565, 8912, 8936, 10027, 11312, 11348, 13276, 13312, 13324, 17919, 21937]),
        new(3, "reykjavik", "北大西洋", "Reykjavik", "Iceland", 63.8469, -22.4553,
            [3900, 5720, 6712, 8977, 11184, 15025, 17985]),
        new(4, "riverhead", "北米東部", "Riverhead", "New York, USA", 40.8817, -72.6372,
            [5652, 6661, 8912, 11387, 13276, 17919, 21931]),
        new(5, "auckland", "南太平洋", "Auckland", "New Zealand", -37.0153, 174.8094,
            [5583, 6535, 8921, 10084, 13351, 17916]),
        new(6, "hat-yai", "東南アジア", "Hat Yai", "Thailand", 6.9375, 100.3883,
            [5655, 6535, 8825, 10066, 13270, 17928, 21949]),
        new(7, "shannon", "北大西洋", "Shannon", "Ireland", 52.7439, -8.9264,
            [2998, 3455, 5547, 6532, 8843, 8942, 10081, 11384]),
        new(8, "johannesburg", "アフリカ南部", "Johannesburg", "South Africa", -26.1292, 28.2058,
            [3016, 4681, 5529, 8834, 11321, 13321, 17922, 21949]),
        new(9, "barrow", "北極圏", "Barrow", "Alaska, USA", 71.2583, -156.5769,
            [2944, 2992, 3007, 3497, 4654, 4687, 5529, 5538, 5544, 6646, 8927, 8936, 10027, 10093, 11354, 17919, 17934, 21928, 21937]),
        new(10, "muan", "東アジア", "Muan", "South Korea", 35.0322, 126.2386,
            [2941, 5502, 6619, 8939, 10060, 13342, 17958, 21931]),
        new(11, "albrook", "中米", "Albrook", "Panama", 9.0844, -79.3736,
            [5589, 6589, 8894, 10063, 13264, 17901]),
        // Ground-station ID 12 is unassigned in the published HFDL system table.
        new(13, "santa-cruz", "南米", "Santa Cruz", "Bolivia", -17.6708, -63.1567,
            [4660, 6628, 8957, 11318, 13315, 17916, 21997]),
        new(14, "krasnoyarsk", "中央アジア", "Krasnoyarsk", "Russia", 56.1525, 92.5833,
            [5622, 6596, 8886, 10087, 13321, 17912, 21990]),
        new(15, "al-muharraq", "中東", "Al Muharraq", "Bahrain", 26.2736, 50.6397,
            [5544, 8885, 10075, 13354, 17967, 21982]),
        new(16, "agana", "西太平洋", "Agana", "Guam", 13.4942, 144.8281,
            [5451, 6652, 8927, 11306, 13312, 17919, 21928]),
        new(17, "canarias", "東大西洋", "Canarias", "Spain", 27.9608, -15.4050,
            [6529, 8948, 11348, 13303, 17928, 21955])
    ];
    public static IReadOnlyList<Channel> Channels { get; } = GroundStations.SelectMany(station =>
        station.FrequenciesKHz.Select(frequencyKHz => new Channel(
            $"gs{station.Id:D2}-{frequencyKHz}",
            $"{station.Name} {frequencyKHz / 1_000.0:F3} MHz",
            frequencyKHz * 1_000L, station.Id, station.Name, station.Country,
            station.Region, station.Latitude, station.Longitude))).ToArray();

    private readonly object gate = new();
    private readonly object processingGate = new();
    private readonly HfdlReceiver receiver = new();
    private readonly HfdlViewModel viewModel = new();
    private readonly List<HfdlReception> history = [];
    private PluginJsonLinesHistoryWriter<HfdlReception>? historyWriter;
    private readonly PackedIqHistoryBuffer pretriggerBuffer = new(3);
    private readonly IqStreamContinuityTracker continuity = new();
    private IPluginHostContext? host;
    private HfdlSettings settings = new();
    private IqBlockMetadata? lastCaptureMetadata;
    private int captureSaveInProgress;
    private int tuningRequestInProgress;
    private string selectedProfileId = DefaultChannelId;
    private long audioSequence;
    private readonly PluginAudioGenerationTracker audioGeneration = new();
    private long lastDiagnosticsUpdateMilliseconds;
    private ITimer? guidanceTimer;

    public HfdlPluginModule()
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
        viewModel.MaximumTrailPointsChanged = value =>
        {
            settings = settings with { MaximumTrailPoints = value };
            PersistSettings();
        };
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
        settings = new HfdlSettings().Normalize();
        selectedProfileId = settings.SelectedChannelId;
        SynchronizeViewSettings();
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "hfdl",
        "HFDL",
        "HF Data Link ARINC 635 burst receiver and decoder",
        new Version(1, 0, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.ChannelIqConsumer |
        PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.ResultPublisher | PluginCapabilities.Export |
        PluginCapabilities.FrequencyOverlay,
        "SRdeck",
        "GPL-3.0");

    public event EventHandler? FrequencyOverlaysChanged { add { } remove { } }
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
    [
        new($"hfdl-{SelectedChannel().Id}", SelectedChannel().FrequencyHz + SignalOffsetHz, 4_800,
            string.Empty, true, PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary), "Transparent", "#FFFFFFFF", -1)
    ];

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "HFDLバースト同期・PSK復調・ARINC 635解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "信号検出、シンボル同期、等化、FEC/CRC検証、LPDU/SPDU解析");
    public PluginIqPreferences IqPreferences { get; } = new(8);
    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            Channel channel = SelectedChannel();
            return
            [
                new($"hfdl-{channel.Id}", channel.FrequencyHz + SignalOffsetHz, 4_800,
                    HfdlReceiver.MonitorAudioSampleRateHz, 72_000, 56_000,
                    64, 3, 8, true, 240_000, 400_000, 8)
            ];
        }
    }
    public IReadOnlyList<PluginProfileDescriptor> Profiles { get; } = Channels.Select(item =>
        new PluginProfileDescriptor(item.Id, item.Name,
            $"{item.Region} / {item.StationName} ({item.Country}) HFDL channel", item.Id == DefaultChannelId)).ToArray();
    public string? SelectedProfileId => selectedProfileId;
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [
        new("csv", "CSV", ".csv", "text/csv"),
        new("json", "JSON", ".json", "application/json")
    ];

    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;

    public FrameworkElement CreateMainView() => new HfdlPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new HfdlSettingsView { DataContext = viewModel };

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
            try { settings = (JsonSerializer.Deserialize<HfdlSettings>(document.Json) ?? new()).Normalize(); }
            catch (JsonException exception)
            {
                host.Logger.Log(PluginLogLevel.Warning, "hfdl.settings.invalid", "Invalid HFDL settings; defaults are used.", exception);
            }
        }
        selectedProfileId = settings.SelectedChannelId;
        SynchronizeViewSettings();
        historyWriter = CreateHistoryWriter(hostContext);
        LoadHistory();
        guidanceTimer = host.TimeProvider.CreateTimer(_ =>
        {
            IPluginHostContext? context = host;
            if (context is not null) context.Dispatcher.Post(viewModel.RefreshPropagationGuidance);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!Channels.Any(item => item.Id == profileId))
            throw new ArgumentException($"Unknown HFDL profile '{profileId}'.", nameof(profileId));
        if (profileId == selectedProfileId) return;
        Channel channel = Channels.First(item => item.Id == profileId);
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
            SetStatus($"切替中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz");
            PluginTuningResult result = await RequestTuningForCurrentChannelAsync(context, channel, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == PluginTuningOutcome.Rejected)
                throw new InvalidOperationException($"HFDL tuning was rejected: {result.Message}");
            if (result.SampleRateHz < HfdlReceiver.MinimumSampleRateHz)
                throw new InvalidOperationException("HFDL requires at least 48 kS/s.");
        }
        lock (processingGate)
        {
            selectedProfileId = profileId;
            settings = settings with { SelectedChannelId = profileId };
            if (State == PluginLifecycleState.Streaming)
            {
                receiver.Reset();
                continuity.Reset();
                audioGeneration.Reset();
                host?.Audio.Reset();
            }
        }
        PersistSettings();
        SynchronizeViewSettings();
        SetStatus(State == PluginLifecycleState.Streaming
            ? $"受信中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz"
            : State == PluginLifecycleState.Active
                ? $"待機中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz"
                : "設定済み");
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        Channel channel = SelectedChannel();
        IPluginHostContext context = host ?? throw new InvalidOperationException("The plugin is not initialized.");
        PluginTuningResult result = await RequestTuningForCurrentChannelAsync(context, channel, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PluginTuningOutcome.Rejected)
            throw new InvalidOperationException($"HFDL tuning was rejected: {result.Message}");
        if (result.SampleRateHz < HfdlReceiver.MinimumSampleRateHz)
            throw new InvalidOperationException("HFDL requires at least 48 kS/s.");
        SetStatus($"待機中 / {channel.FrequencyHz / 1_000_000.0:F3} MHz");
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
            continuity.Reset();
            audioSequence = 0;
            lastDiagnosticsUpdateMilliseconds = 0;
        }
        viewModel.CaptureStatus = "IQ録音: 直前3秒を常時保持中";
        SetStatus("受信中");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            IqBlockMetadata metadata = block.Metadata;
            lastCaptureMetadata = metadata;
            bool discontinuous = continuity.Observe(metadata).RequiresReset;
            if (discontinuous)
            {
                receiver.Reset(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
            }
            pretriggerBuffer.Write(block.Samples.Span, metadata.SampleRateHz);
            IReadOnlyList<HfdlFrame> frames;
            float[]? monitorBuffer = null;
            try
            {
                if (settings.MonitorAudioEnabled)
                {
                    int capacity = checked((int)Math.Ceiling(block.Samples.Length *
                        (double)HfdlReceiver.MonitorAudioSampleRateHz / metadata.SampleRateHz) + 2);
                    monitorBuffer = ArrayPool<float>.Shared.Rent(capacity);
                    frames = receiver.Process(block.Samples.Span, metadata,
                        monitorBuffer.AsSpan(0, capacity), out int audioSampleCount);
                    bool audioDiscontinuous = audioGeneration.Observe(metadata.Generation, discontinuous);
                    if (audioDiscontinuous) host?.Audio.Reset();
                    SubmitMonitorAudio(monitorBuffer.AsSpan(0, audioSampleCount), metadata, audioDiscontinuous);
                }
                else frames = receiver.Process(block.Samples.Span, metadata);
            }
            finally
            {
                if (monitorBuffer is not null) ArrayPool<float>.Shared.Return(monitorBuffer);
            }
            foreach (HfdlFrame frame in frames)
                if (HfdlMessageParser.TryParse(frame, out HfdlMessage? message) && message is not null && message.IsCrcValid)
                    Publish(frame, message);
            UpdateDiagnostics(metadata);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        if (blocks.Count != 1)
            throw new ArgumentException("HFDL requires exactly one standard channel block.", nameof(blocks));
        IChannelIqBlockLease block = blocks[0];
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            ChannelIqBlockMetadata channelMetadata = block.Metadata;
            IqBlockMetadata metadata = channelMetadata.Source;
            lastCaptureMetadata = metadata;
            bool discontinuous = continuity.Observe(metadata).RequiresReset;
            pretriggerBuffer.Write(block.Samples.Span,
                channelMetadata.Configuration.OutputSampleRateHz);
            IReadOnlyList<HfdlFrame> frames;
            float[]? monitorBuffer = null;
            try
            {
                if (settings.MonitorAudioEnabled)
                {
                    monitorBuffer = ArrayPool<float>.Shared.Rent(block.Samples.Length + 2);
                    frames = receiver.ProcessChannel(block.Samples.Span, channelMetadata,
                        monitorBuffer.AsSpan(0, block.Samples.Length + 2), true,
                        out int audioSampleCount);
                    bool audioDiscontinuous = audioGeneration.Observe(metadata.Generation, discontinuous);
                    if (audioDiscontinuous) host?.Audio.Reset();
                    SubmitMonitorAudio(monitorBuffer.AsSpan(0, audioSampleCount), metadata, audioDiscontinuous);
                }
                else frames = receiver.ProcessChannel(block.Samples.Span, channelMetadata,
                    Span<float>.Empty, false, out _);
            }
            finally
            {
                if (monitorBuffer is not null) ArrayPool<float>.Shared.Return(monitorBuffer);
            }
            foreach (HfdlFrame frame in frames)
                if (HfdlMessageParser.TryParse(frame, out HfdlMessage? message) &&
                    message is not null && message.IsCrcValid)
                    Publish(frame, message);
            UpdateDiagnostics(metadata);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        Channel channel = SelectedChannel();
        return PluginProcessingWarmup.RunChannelAsync(
            context,
            $"hfdl-{channel.Id}",
            channel.FrequencyHz + SignalOffsetHz,
            HfdlReceiver.MonitorAudioSampleRateHz,
            4_800,
            (samples, metadata) =>
            {
                lock (processingGate)
                    receiver.ProcessChannel(
                        samples,
                        metadata,
                        Span<float>.Empty,
                        false,
                        out _);
            },
            () =>
            {
                lock (processingGate)
                {
                    receiver.Reset();
                    continuity.Reset();
                }
            },
            cancellationToken);
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate)
        {
            receiver.Reset();
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
