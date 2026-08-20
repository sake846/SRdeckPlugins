using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Views;
using SRdeckPlugin.Meshtastic.ViewModels;
using SRdeckPlugin.Meshtastic.Services;
using SRdeckPlugin.Sdk;
using System.Windows;

// Plugin entry point exported by SRdeckPlugin.Meshtastic.
namespace SRdeckPlugin.Meshtastic;

/// <summary>
/// Meshtastic module that owns lifecycle, IQ delivery, and its WPF workspace views
/// behind the generic plugin contracts.
/// </summary>
public sealed class MeshtasticPluginModule(
    IMeshtasticReceiveService receiver,
    MeshtasticViewModel? viewModel)
    : PluginModuleBase, IIqBlockConsumer, IPluginViewProvider, IFrequencyOverlayProvider,
      IPluginProfileProvider, IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup
{
    private static readonly IReadOnlyDictionary<string, MeshtasticModemPreset> ProfilePresets =
        new Dictionary<string, MeshtasticModemPreset>(StringComparer.Ordinal)
        {
            ["long-fast"] = MeshtasticModemPreset.LongFast,
            ["long-moderate"] = MeshtasticModemPreset.LongModerate,
            ["long-slow"] = MeshtasticModemPreset.LongSlow,
            ["medium-fast"] = MeshtasticModemPreset.MediumFast,
            ["medium-slow"] = MeshtasticModemPreset.MediumSlow,
            ["short-fast"] = MeshtasticModemPreset.ShortFast,
            ["short-slow"] = MeshtasticModemPreset.ShortSlow,
            ["auto-sf-250"] = MeshtasticModemPreset.AutoSf250,
            ["auto-sf-125"] = MeshtasticModemPreset.AutoSf125,
            ["auto-sf-mixed"] = MeshtasticModemPreset.AutoSf250And125
        };
    public MeshtasticPluginModule()
        : this(new MeshtasticReceiveService())
    {
    }

    public MeshtasticPluginModule(IMeshtasticReceiveService receiver)
        : this(receiver, new MeshtasticViewModel(receiver))
    {
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "meshtastic",
        "Meshtastic",
        "Meshtastic LoRa packet receiver and decoder",
        new Version(1, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.MainView |
        PluginCapabilities.SettingsView | PluginCapabilities.FrequencyOverlay,
        "SRdeck",
        "GPL-3.0",
        IsEnabledByDefault: true);

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "LoRaチャンネル抽出・復調・Meshtastic解析",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "周波数探索、チャープ復調、FEC/CRC検証、Meshtasticパケット解析");
    public PluginIqPreferences IqPreferences { get; } = new(4);
    public IReadOnlyList<PluginProfileDescriptor> Profiles { get; } = ProfilePresets
        .Select(item => new PluginProfileDescriptor(
            item.Key,
            MeshtasticJpLongFastProfile.GetProfile(item.Value).Name,
            $"Meshtastic {MeshtasticJpLongFastProfile.GetProfile(item.Value).Name} modem preset",
            item.Value == MeshtasticModemPreset.LongFast))
        .ToArray();
    public string? SelectedProfileId => viewModel is null
        ? null
        : ProfilePresets.FirstOrDefault(item => item.Value == viewModel.SelectedMeshtasticModemPreset).Key;

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
        viewModel?.FrequencyOverlays ?? [];

    public event EventHandler? FrequencyOverlaysChanged
    {
        add
        {
            if (viewModel is not null) viewModel.FrequencyOverlaysChanged += value;
        }
        remove
        {
            if (viewModel is not null) viewModel.FrequencyOverlaysChanged -= value;
        }
    }

    public FrameworkElement CreateMainView() => new MeshtasticPluginView { DataContext = viewModel };

    public FrameworkElement CreateSettingsView() => new MeshtasticSettingsView { DataContext = viewModel };

    protected override ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        viewModel?.Initialize(hostContext);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        viewModel?.Activate();
        return ValueTask.CompletedTask;
    }

    public ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!ProfilePresets.TryGetValue(profileId, out MeshtasticModemPreset preset))
            throw new ArgumentException($"Unknown Meshtastic profile '{profileId}'.", nameof(profileId));
        if (State == PluginLifecycleState.Streaming)
            throw new InvalidOperationException("The Meshtastic profile cannot change while streaming.");
        if (viewModel is not null) viewModel.SelectedMeshtasticModemPreset = preset;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        receiver.StartStream();
        viewModel?.StartStream();
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        await receiver.StopStreamAsync(cancellationToken).ConfigureAwait(false);
        viewModel?.StopStream();
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        viewModel?.Deactivate();
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming)
            return ValueTask.CompletedTask;
        IqBlockMetadata metadata = block.Metadata;
        receiver.TrySubmitNormalized(
            block.Samples.Span,
            metadata.SampleRateHz,
            checked((int)metadata.CenterFrequencyHz),
            metadata.Sequence,
            metadata.AbsoluteSampleStart);
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken) =>
        receiver.WarmUpProcessingAsync(
            context.SampleRateHz,
            checked((int)context.CenterFrequencyHz),
            context.BlockCount,
            cancellationToken);

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (viewModel is not null) await viewModel.ShutdownAsync().ConfigureAwait(false);
        receiver.Dispose();
    }
}
