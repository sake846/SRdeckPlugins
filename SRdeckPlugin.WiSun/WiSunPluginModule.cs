using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.WiSun.Dsp;
using SRdeckPlugin.WiSun.Models;
using SRdeckPlugin.WiSun.ViewModels;
using SRdeckPlugin.WiSun.Views;

namespace SRdeckPlugin.WiSun;

public sealed partial class WiSunPluginModule :
    PluginModuleBase,
    IPluginChannelBlockConsumer,
    ILivePluginProfileProvider,
    IPluginViewProvider,
    IPluginExportProvider,
    IFrequencyOverlayProvider,
    IPluginProcessingDiagnosticsProvider,
    IPluginProcessingWarmup
{
    public sealed record WiSunChannelOption(
        int Channel,
        long FrequencyHz,
        string DisplayName);

    private sealed record WiSunProfileDefinition(
        string Id,
        string DisplayName,
        string Description,
        long FrequencyHz,
        WiSunPhyProfile PhyProfile,
        int TuningBandwidthHz,
        bool IsDefault = false);

    private static readonly IReadOnlyList<WiSunProfileDefinition> ProfileDefinitions =
        CreateProfileDefinitions();
    private static readonly IReadOnlyList<PluginProfileDescriptor> SupportedProfiles =
        ProfileDefinitions.Select(profile => new PluginProfileDescriptor(
            profile.Id, profile.DisplayName, profile.Description, profile.IsDefault)).ToArray();

    private static readonly IReadOnlyList<PluginExportFormat> SupportedExportFormats =
    [
        new("csv", "CSV ファイル (*.csv)", ".csv", "text/csv"),
        new("json", "JSON ファイル (*.json)", ".json", "application/json")
    ];

    private IPluginHostContext? _hostContext;
    private WiSunSettings _settings = new();
    private readonly Dictionary<string, WiSunDemodulator> _demodulators =
        new(StringComparer.Ordinal);
    private readonly List<WiSunPacketFrame> _packetHistory = [];
    private WiSunDemodulator _demodulator = new();
    private WiSunDemodulator _lastDemodulator = new();
    private WiSunViewModel? _viewModel;
    private string? _selectedProfileId = "fan-21-09";
    private readonly object _consumptionGate = new();
    private readonly object _processingGate = new();
    private readonly SemaphoreSlim _settingsUpdateGate = new(1, 1);
    private readonly object _settingsSaveGate = new();
    private Task _settingsSaveTail = Task.CompletedTask;
    private int _tuningUpdateInProgress;
    private int _hasReceivedStreamData;
    public override PluginDescriptor Descriptor { get; } = new(
        "wisun",
        "Wi-SUN",
        "920MHz Band Wi-SUN / IEEE 802.15.4g SUN FSK Signal Demodulator",
        new Version(1, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView | PluginCapabilities.SettingsView | PluginCapabilities.Export | PluginCapabilities.Headless | PluginCapabilities.FrequencyOverlay,
        "SRdeck Team",
        "GPL-3.0");

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "SUN FSK復調・同期・PHY/MAC解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "各監視チャンネルの2-FSK復調、プリアンブル/SFD同期、FCS検証、MAC解析");
    public IReadOnlyList<PluginChannelRequest> ChannelRequests =>
        CreateChannelRequests(_settings);

    public string? SelectedProfileId => _selectedProfileId;
    public IReadOnlyList<PluginProfileDescriptor> Profiles => SupportedProfiles;
    public IReadOnlyList<PluginExportFormat> ExportFormats => SupportedExportFormats;

    private EventHandler? _frequencyOverlaysChanged;

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
        _viewModel?.FrequencyOverlays ?? [];

    public event EventHandler? FrequencyOverlaysChanged
    {
        add
        {
            _frequencyOverlaysChanged += value;
            if (_viewModel is not null) _viewModel.FrequencyOverlaysChanged += value;
        }
        remove
        {
            _frequencyOverlaysChanged -= value;
            if (_viewModel is not null) _viewModel.FrequencyOverlaysChanged -= value;
        }
    }

    public WiSunSettings Settings => _settings;
    public WiSunDemodulator Demodulator => _lastDemodulator;
    public IReadOnlyCollection<WiSunDemodulator> Demodulators => _demodulators.Values;
    public long TotalSyncAttempts => _demodulators.Values.Sum(value => value.TotalSyncAttempts);
    public long TotalRfBursts => _demodulators.Values.Sum(value => value.TotalRfBursts);
    public long TotalPreambleMatches => _demodulators.Values.Sum(value => value.TotalPreambleMatches);
    public long TotalSfdMatches => _demodulators.Values.Sum(value => value.TotalSfdMatches);
    public long TotalPhrValid => _demodulators.Values.Sum(value => value.TotalPhrValid);
    public long TotalPayloadRead => _demodulators.Values.Sum(value => value.TotalPayloadRead);
    public long TotalFramesPublished => _demodulators.Values.Sum(value => value.TotalFramesPublished);
    public long TotalCrcOk => _demodulators.Values.Sum(value => value.TotalCrcOk);
    public long TotalCrcNg => _demodulators.Values.Sum(value => value.TotalCrcNg);
    public float? SignalLevelDbm => _hostContext?.ReceiverTelemetry?.SignalLevelDbm;
    public float? NoiseFloorDbm => _hostContext?.ReceiverTelemetry?.NoiseFloorDbm;
    public double DiagnosticInputLevelDbfs => _demodulators.Values
        .Select(value => value.LastInputLevelDbfs)
        .Where(double.IsFinite)
        .DefaultIfEmpty(double.NaN)
        .Max();
    public double DiagnosticNoiseFloorDbfs => _demodulators.Values
        .Select(value => value.LastNoiseFloorDbfs)
        .Where(double.IsFinite)
        .DefaultIfEmpty(double.NaN)
        .Max();
    public int DiagnosticInputSampleRateHz => _demodulators.Values
        .Select(value => value.LastInputSampleRateHz)
        .DefaultIfEmpty(0)
        .Max();
    public DateTimeOffset? DiagnosticLastMeasuredAt => _demodulators.Values
        .Select(value => value.LastMeasuredAt)
        .Where(value => value.HasValue)
        .DefaultIfEmpty(null)
        .Max();
    public bool RejectInvalidFcs
    {
        get => _demodulator.RejectInvalidFcs;
        set
        {
            lock (_consumptionGate)
                lock (_processingGate)
                    foreach (WiSunDemodulator demodulator in _demodulators.Values)
                        demodulator.RejectInvalidFcs = value;
        }
    }

    public event Action<string>? OnDiagnosticLog;
    public event Action? OnDiagnosticCountersChanged;
    public event Action<WiSunPacketFrame>? PacketDecoded;

    public WiSunPluginModule()
    {
        _settings = _settings.Normalize();
        RebuildDemodulators();
    }

    public void ResetDiagnosticCounters()
    {
        lock (_consumptionGate)
            lock (_processingGate)
                foreach (WiSunDemodulator demodulator in _demodulators.Values)
                    demodulator.ResetCounters();
        OnDiagnosticCountersChanged?.Invoke();
    }

    public static IReadOnlyList<WiSunChannelOption> GetChannelOptions(
        WiSunPhyProfile profile) =>
        ProfileDefinitions
            .Where(value => value.PhyProfile == profile)
            .Select(value => new WiSunChannelOption(
                ChannelNumber(value), value.FrequencyHz, value.DisplayName))
            .ToArray();

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext context,
        CancellationToken token)
    {
        _hostContext = context;

        try
        {
            var doc = await context.Settings.LoadAsync(token).ConfigureAwait(false);
            if (doc is not null && !string.IsNullOrWhiteSpace(doc.Json))
            {
                var loaded = JsonSerializer.Deserialize<WiSunSettings>(doc.Json);
                if (loaded is not null) _settings = loaded.Normalize();
            }
        }
        catch { }

        _selectedProfileId = ProfileIdForFrequency(_settings.FrequencyHz, _settings.PhyProfile);
        lock (_processingGate) RebuildDemodulators();
        _hostContext.Tuning.AppliedConfigurationChanged += OnTuningChanged;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken token)
    {
        IPluginHostContext context = _hostContext ??
            throw new InvalidOperationException("The Wi-SUN plugin is not initialized.");
        string tuningProfileId = TuningProfileId(_settings);
        PluginTuningResult result = await RequestTuningAsync(
            context, tuningProfileId, _settings, token)
            .ConfigureAwait(false);
        WiSunSettings? reducedSettings = null;
        if (result.Outcome == PluginTuningOutcome.Rejected &&
            TryReduceChannelsToFitHostSampleRate(_settings, result.SampleRateHz,
                out WiSunSettings reduced))
        {
            PluginTuningResult reducedResult = await RequestTuningAsync(
                context, TuningProfileId(reduced), reduced, token).ConfigureAwait(false);
            if (reducedResult.Outcome != PluginTuningOutcome.Rejected)
            {
                result = reducedResult;
                reducedSettings = reduced;
                lock (_processingGate)
                {
                    _settings = reduced;
                    _selectedProfileId = TuningProfileId(reduced) is { } profileId && profileId != "custom"
                        ? profileId
                        : null;
                    RebuildDemodulators();
                }
                PersistSettings();
                _viewModel?.SynchronizeFrequency(reduced.FrequencyHz);
            }
        }
        if (result.Outcome == PluginTuningOutcome.Rejected)
        {
            throw new PluginActivationRejectedException(
                $"Wi-SUN tuning was rejected: {result.Message}");
        }
        else
        {
            int minimumSampleRateHz = RequiredSourceSampleRateHz(_settings);
            if (result.SampleRateHz > 0 && result.SampleRateHz < minimumSampleRateHz)
            {
                SetStatus($"帯域幅注意: 必要レート {minimumSampleRateHz:N0} Hz (現在 {result.SampleRateHz:N0} Hz)");
            }
            else
            {
                SetStatus(reducedSettings is null
                    ? $"待機中 / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz"
                    : $"待機中 / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz / " +
                      $"帯域に合わせて {SelectedChannelSummary(reducedSettings)} に縮小");
            }
        }
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken token)
    {
        Volatile.Write(ref _hasReceivedStreamData, 0);
        lock (_consumptionGate)
            lock (_processingGate)
            {
                foreach (WiSunDemodulator demodulator in _demodulators.Values)
                    demodulator.Reset();
            }
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken token)
    {
        Volatile.Write(ref _hasReceivedStreamData, 0);
        lock (_consumptionGate)
            lock (_processingGate)
            {
                foreach (WiSunDemodulator demodulator in _demodulators.Values)
                    demodulator.Reset();
            }
        return ValueTask.CompletedTask;
    }

    public async ValueTask SelectProfileAsync(string profileId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.Equals(profileId, "custom", StringComparison.OrdinalIgnoreCase))
        {
            WiSunSettings customSettings = (_settings with
            {
                PhyProfile = WiSunPhyProfile.Custom
            }).Normalize();
            if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            {
                IPluginHostContext context = _hostContext ??
                    throw new InvalidOperationException("The Wi-SUN plugin is not initialized.");
                await RequestAndValidateTuningAsync(context, "custom", customSettings, token)
                    .ConfigureAwait(false);
            }
            lock (_consumptionGate)
                lock (_processingGate)
                {
                    _selectedProfileId = null;
                    _settings = customSettings;
                    RebuildDemodulators();
                }
            PersistSettings();
            _viewModel?.SynchronizeFrequency(customSettings.FrequencyHz);
            SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                SelectedChannelSummary(customSettings));
            return;
        }

        WiSunProfileDefinition? profile = ProfileDefinitions.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
            throw new ArgumentException($"Unknown Wi-SUN profile '{profileId}'.", nameof(profileId));

        long frequencyHz = profile.FrequencyHz;
        WiSunSettings requestedSettings = SettingsForSingleProfile(profile);
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            IPluginHostContext context = _hostContext ??
                throw new InvalidOperationException("The Wi-SUN plugin is not initialized.");
            await RequestAndValidateTuningAsync(context, profileId, requestedSettings, token)
                .ConfigureAwait(false);
        }
        lock (_consumptionGate)
            lock (_processingGate)
            {
                _selectedProfileId = profileId;
                _settings = requestedSettings;
                RebuildDemodulators();
            }
        PersistSettings();
        _viewModel?.SynchronizeFrequency(frequencyHz);
        SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
            $"{frequencyHz / 1_000_000.0:F3} MHz");
    }

    public ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (State != PluginLifecycleState.Streaming || !_settings.IsReceiverEnabled || blocks.Count == 0)
            return ValueTask.CompletedTask;

        lock (_consumptionGate)
        {
            if (State != PluginLifecycleState.Streaming || !_settings.IsReceiverEnabled)
                return ValueTask.CompletedTask;

            List<(IChannelIqBlockLease Block, WiSunDemodulator Demodulator)> workItems;
            float squelchThresholdDbfs;
            lock (_processingGate)
            {
                squelchThresholdDbfs = _hostContext?.ReceiverTelemetry?.DbmToDbfs(_settings.SquelchThresholdDbm)
                    ?? (_settings.SquelchThresholdDbm - (-80f));
                workItems = new List<(IChannelIqBlockLease, WiSunDemodulator)>(blocks.Count);
                foreach (IChannelIqBlockLease block in blocks)
                {
                    if (block.Samples.IsEmpty) continue;
                    string requestId = block.Metadata.Configuration.RequestId;
                    if (_demodulators.TryGetValue(requestId, out WiSunDemodulator? demodulator))
                    {
                        workItems.Add((block, demodulator));
                    }
                }
                if (workItems.Count > 0)
                    Volatile.Write(ref _hasReceivedStreamData, 1);
            }

            if (workItems.Count == 0)
                return ValueTask.CompletedTask;

            if (workItems.Count == 1)
            {
                var (block, demodulator) = workItems[0];
                demodulator.ProcessChannel(
                    block.Samples.Span, block.Metadata, squelchThresholdDbfs);
            }
            else
            {
                Parallel.ForEach(workItems, new ParallelOptions
                {
                    CancellationToken = token,
                    // Channelization already uses a bounded worker pool. Do not let
                    // Wi-SUN's per-channel decode create a second, unbounded pool
                    // that starves the WPF input and render threads.
                    MaxDegreeOfParallelism = Math.Min(workItems.Count,
                        Math.Clamp(Environment.ProcessorCount - 2, 1, 4))
                }, item =>
                {
                    item.Demodulator.ProcessChannel(
                        item.Block.Samples.Span, item.Block.Metadata, squelchThresholdDbfs);
                });
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        PluginChannelRequest[] requests = CreateChannelRequests(_settings).ToArray();
        PluginChannelRequest primary = requests[0];
        return PluginProcessingWarmup.RunChannelAsync(
            context,
            primary.Id,
            primary.CenterFrequencyHz,
            primary.OutputSampleRateHz,
            primary.BandwidthHz,
            (samples, metadata) =>
            {
                lock (_processingGate)
                {
                    foreach (PluginChannelRequest request in requests)
                    {
                        if (!_demodulators.TryGetValue(
                                request.Id,
                                out WiSunDemodulator? demodulator))
                            continue;
                        demodulator.ProcessChannel(
                            samples,
                            metadata with
                            {
                                Configuration = metadata.Configuration with
                                {
                                    RequestId = request.Id,
                                    ChannelCenterFrequencyHz = request.CenterFrequencyHz
                                }
                            },
                            _hostContext?.ReceiverTelemetry?.DbmToDbfs(_settings.SquelchThresholdDbm)
                                ?? (_settings.SquelchThresholdDbm - (-80f)));
                    }
                }
            },
            () =>
            {
                lock (_processingGate)
                {
                    foreach (WiSunDemodulator demodulator in _demodulators.Values)
                    {
                        demodulator.Reset();
                        demodulator.ResetCounters();
                    }
                }
            },
            cancellationToken);
    }

    public FrameworkElement CreateMainView()
    {
        EnsureViewModel();
        return new WiSunPluginView { DataContext = _viewModel };
    }

    public FrameworkElement? CreateSettingsView()
    {
        EnsureViewModel();
        return new WiSunSettingsView { DataContext = _viewModel };
    }

    private void EnsureViewModel()
    {
        if (_viewModel is not null) return;
        _viewModel = new WiSunViewModel(this)
        {
            RuntimeDiagnostics = _hostContext?.RuntimeDiagnostics ??
                NullPluginRuntimeDiagnostics.Instance
        };
        if (_frequencyOverlaysChanged is not null)
        {
            _viewModel.FrequencyOverlaysChanged += _frequencyOverlaysChanged;
        }
    }

    public async ValueTask ResetSettingsAsync()
    {
        if (_hostContext is not null)
        {
            await _hostContext.Settings.DeleteAsync().ConfigureAwait(false);
        }
        lock (_processingGate)
        {
            _settings = new WiSunSettings();
            _selectedProfileId = ProfileIdForFrequency(_settings.FrequencyHz, _settings.PhyProfile);
            RebuildDemodulators();
        }
    }
}
