using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Meshtastic.Views;
using SRdeckPlugin.Meshtastic.ViewModels;
using SRdeckPlugin.Meshtastic.Services;
using SRdeckPlugin.Sdk;
using System.Windows;
using System.Text.Json;
using System.IO;

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
    private const int CaptureDurationSeconds = 10;

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
    private readonly PackedIqHistoryBuffer pretriggerBuffer = new(CaptureDurationSeconds);
    private readonly object captureGate = new();
    private IqBlockMetadata? lastCaptureMetadata;
    private int captureSaveInProgress;

    public MeshtasticPluginModule()
        : this(new MeshtasticReceiveService())
    {
    }

    public MeshtasticPluginModule(IMeshtasticReceiveService receiver)
        : this(receiver, new MeshtasticViewModel(receiver))
    {
        RegisterStreamReset(pretriggerBuffer.Reset);
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
        if (viewModel is not null)
            viewModel.CaptureRequested = StartIqCapture;
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
        lock (captureGate)
            lastCaptureMetadata = null;
        receiver.StartStream();
        viewModel?.StartStream();
        if (viewModel is not null)
            viewModel.CaptureStatus = $"IQ録音: 直前{CaptureDurationSeconds}秒を常時保持中";
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        await receiver.StopStreamAsync(cancellationToken).ConfigureAwait(false);
        lock (captureGate)
            lastCaptureMetadata = null;
        viewModel?.StopStream();
        if (viewModel is not null)
            viewModel.CaptureStatus = "IQ録音: 待機";
    }

    protected override ValueTask OnDeactivateAsync(CancellationToken cancellationToken)
    {
        lock (captureGate)
            lastCaptureMetadata = null;
        viewModel?.Deactivate();
        if (viewModel is not null)
            viewModel.CaptureStatus = "IQ録音: 待機";
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming)
            return ValueTask.CompletedTask;
        IqBlockMetadata metadata = block.Metadata;
        lock (captureGate)
        {
            if (metadata.Discontinuity != IqDiscontinuity.None)
                pretriggerBuffer.Reset();
            pretriggerBuffer.Write(block.Samples.Span, metadata.SampleRateHz);
            lastCaptureMetadata = metadata;
        }
        receiver.TrySubmitNormalized(
            block.Samples.Span,
            metadata.SampleRateHz,
            checked((int)metadata.CenterFrequencyHz),
            metadata.Sequence,
            metadata.AbsoluteSampleStart);
        return ValueTask.CompletedTask;
    }

    private void StartIqCapture()
    {
        IPluginHostContext? context = HostContext;
        if (State != PluginLifecycleState.Streaming || context is null)
        {
            if (viewModel is not null)
                viewModel.CaptureStatus = "IQ録音: 受信中に開始してください";
            return;
        }
        if (Interlocked.CompareExchange(ref captureSaveInProgress, 1, 0) != 0)
        {
            if (viewModel is not null)
                viewModel.CaptureStatus = "IQ録音: 保存処理中です";
            return;
        }

        if (viewModel is not null)
            viewModel.CaptureStatus = $"IQ録音: 直前{CaptureDurationSeconds}秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(context));
    }

    private void SavePretriggerCapture(IPluginHostContext context)
    {
        try
        {
            PackedIqHistorySnapshot snapshot;
            IqBlockMetadata? metadata;
            lock (captureGate)
            {
                snapshot = pretriggerBuffer.TakeSnapshot() ??
                    throw new InvalidOperationException("まだ保存できるIQデータがありません。");
                metadata = lastCaptureMetadata;
            }

            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            long centerFrequencyHz = metadata?.CenterFrequencyHz ?? 0;
            string basePath = Path.Combine(directory,
                $"meshtastic-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{centerFrequencyHz}");
            string path = $"{basePath}.wav";
            using (var capture = new MeshtasticIqCapture(
                       path, snapshot.SampleRateHz,
                       TimeSpan.FromSeconds(CaptureDurationSeconds)))
            {
                capture.WritePcm(snapshot.RawInterleaved);
            }

            var document = new
            {
                Format = "SRdeck Meshtastic analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = $"{CaptureDurationSeconds}-second rolling pre-trigger",
                RawIqFile = Path.GetFileName(path),
                CenterFrequencyHz = centerFrequencyHz,
                snapshot.SampleRateHz,
                snapshot.DurationSeconds,
                InputMetadata = metadata
            };
            string diagnosticsPath = $"{basePath}-diagnostics.json";
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions { WriteIndented = true }));
            context.Dispatcher.Post(() =>
            {
                if (viewModel is not null)
                    viewModel.CaptureStatus = $"IQ録音保存済み: {path}";
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "meshtastic.iq-capture.save-failed",
                "Could not save Meshtastic rolling IQ capture.", exception);
            context.Dispatcher.Post(() =>
            {
                if (viewModel is not null)
                    viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}";
            });
        }
        finally
        {
            Interlocked.Exchange(ref captureSaveInProgress, 0);
        }
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
