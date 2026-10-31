using System.IO;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.SignalWorkbench.ViewModels;
using SRdeckPlugin.SignalWorkbench.Views;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.SignalWorkbench;

public sealed class SignalWorkbenchPluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    IPluginFrequencySelection, IFrequencyOverlayProvider, IPluginViewProvider
{
    public const string ChannelRequestId = "signal-workbench-baseband";
    private readonly object processingGate = new();
    private readonly SemaphoreSlim tuningGate = new(1, 1);
    private readonly SignalAnalyzer analyzer = new();
    private readonly IqStreamContinuityTracker continuity = new();
    private readonly WorkbenchViewModel viewModel;
    private WorkbenchSettings settings = new();
    private AppliedChannelConfiguration? lastConfiguration;
    private long? nextOutputSample;
    private int revision;
    private PendingSnapshot? pendingSnapshot;
    private int publicationQueued;

    public SignalWorkbenchPluginModule() => viewModel = new(ApplyBandwidthAsync);

    public override PluginDescriptor Descriptor { get; } = new(
        "signal-workbench", "未知信号の解析ワークベンチ",
        "選択帯域のIQ、周波数偏移分布、FSKトーン解析、シンボルレート候補を表示",
        new(1, 0, 0), new(1, 0), new(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView |
        PluginCapabilities.FrequencyOverlay | PluginCapabilities.Headless,
        "SRdeck", "GPL-3.0-only");

    public long SelectedFrequencyHz => Volatile.Read(ref settings).CenterFrequencyHz;

    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            WorkbenchSettings current = Volatile.Read(ref settings);
            return [new(ChannelRequestId, current.CenterFrequencyHz, current.BandwidthHz,
                current.OutputSampleRateHz, FirTaps: 129, RequestedQueueCapacity: 4,
                AllowRawIqFallback: false) { StopbandBandwidthHz = current.BandwidthHz * 2 }];
        }
    }

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays
    {
        get
        {
            WorkbenchSettings current = Volatile.Read(ref settings);
            return [new("signal-workbench-band", current.CenterFrequencyHz, current.BandwidthHz,
                string.Empty, true, PluginReceiverBandColors.WithAlpha(0x40, PluginReceiverBandColors.Primary),
                "#00000000", "#FFFFFFFF", -1, ToolTip: $"未知信号解析 / {current.BandwidthHz:N0} Hz")];
        }
    }

    public event EventHandler? FrequencyOverlaysChanged;
    public FrameworkElement CreateMainView() => new WorkbenchView { DataContext = viewModel };
    public FrameworkElement? CreateSettingsView() => null;

    protected override async ValueTask OnInitializeAsync(IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        PluginSettingsDocument? document = await hostContext.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try { settings = (JsonSerializer.Deserialize<WorkbenchSettings>(document.Json) ?? new()).Normalize(); }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "signal-workbench.settings.invalid",
                    "解析設定を読み込めないため、既定値を使用します。", exception);
            }
        }
        settings = settings with { CenterFrequencyHz = ResolveFrequency(hostContext.Tuning.Current) };
        hostContext.Tuning.AppliedConfigurationChanged += HandleTuningChanged;
        PostConfiguration("解析開始待ち");
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        PluginTuningResult result = await TuneAsync(ResolveFrequency(HostContext!.Tuning.Current), null,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new PluginActivationRejectedException(result.Message);
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate) ResetProcessing();
        PostConfiguration("受信中 / 解析窓の蓄積待ち");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (processingGate) ResetProcessing();
        HostContext?.Dispatcher.Post(() => viewModel.Status = "停止中");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (HostContext is { } host) host.Tuning.AppliedConfigurationChanged -= HandleTuningChanged;
        lock (processingGate) ResetProcessing();
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        lock (processingGate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            foreach (IChannelIqBlockLease block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ChannelIqBlockMetadata metadata = block.Metadata;
                AppliedChannelConfiguration configuration = metadata.Configuration;
                if (configuration.RequestId != ChannelRequestId ||
                    configuration.ChannelCenterFrequencyHz != settings.CenterFrequencyHz ||
                    configuration.BandwidthHz != settings.BandwidthHz ||
                    configuration.OutputSampleRateHz != settings.OutputSampleRateHz) continue;
                bool discontinuous = continuity.Observe(metadata.Source).RequiresReset ||
                    lastConfiguration != configuration || nextOutputSample != metadata.OutputSampleStart;
                if (discontinuous)
                {
                    analyzer.Reset();
                    revision++;
                    PostConfiguration("受信中 / 解析窓の蓄積待ち");
                }
                lastConfiguration = configuration;
                nextOutputSample = metadata.OutputSampleStart + block.Samples.Length;
                SignalSnapshot? snapshot = analyzer.Push(block.Samples.Span, configuration.OutputSampleRateHz);
                if (snapshot is null) continue;
                Interlocked.Exchange(ref pendingSnapshot, new(snapshot, revision));
                if (Interlocked.Exchange(ref publicationQueued, 1) == 0)
                    HostContext?.Dispatcher.Post(PublishSnapshot);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void PublishSnapshot()
    {
        PendingSnapshot? pending = Interlocked.Exchange(ref pendingSnapshot, null);
        Interlocked.Exchange(ref publicationQueued, 0);
        if (pending is not null && pending.Revision == Volatile.Read(ref revision) && !IsDisposed)
            viewModel.ApplySnapshot(pending.Snapshot);
    }

    public async ValueTask<PluginTuningResult> SelectFrequencyAsync(long frequencyHz,
        CancellationToken cancellationToken = default)
    {
        if (frequencyHz is < 1 or > 10_000_000_000) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        return await TuneAsync(frequencyHz, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyBandwidthAsync(int bandwidthHz)
    {
        if (!WorkbenchSettings.Bandwidths.Contains(bandwidthHz))
            throw new ArgumentOutOfRangeException(nameof(bandwidthHz));
        PluginTuningResult result = await TuneAsync(null, bandwidthHz, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new InvalidOperationException(result.Message);
    }

    private async ValueTask<PluginTuningResult> TuneAsync(long? frequencyHz, int? bandwidthHz,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        IPluginHostContext host = HostContext ?? throw new InvalidOperationException("プラグインが初期化されていません。");
        await tuningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkbenchSettings previous = Volatile.Read(ref settings);
        WorkbenchSettings requested = previous with
        {
            CenterFrequencyHz = frequencyHz ?? previous.CenterFrequencyHz,
            BandwidthHz = bandwidthHz ?? previous.BandwidthHz
        };
        bool accepted = false;
        try
        {
            ThrowIfDisposed();
            SetConfiguration(requested);
            PluginTuningResult result = await host.Tuning.RequestAsync(new(ChannelRequestId,
                Descriptor.DisplayName, [new(requested.CenterFrequencyHz, requested.BandwidthHz)],
                requested.CenterFrequencyHz, requested.OutputSampleRateHz, null, true, false,
                PluginGainPreference.Unspecified), cancellationToken).ConfigureAwait(false);
            if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred) return result;
            if (result.PassbandLowerFrequencyHz > requested.CenterFrequencyHz - requested.BandwidthHz / 2 ||
                result.PassbandUpperFrequencyHz < requested.CenterFrequencyHz + requested.BandwidthHz / 2)
                return result with { Outcome = PluginTuningOutcome.Rejected, Message = "選択帯域が入力の受信範囲に収まりません。" };
            accepted = true;
            try
            {
                await host.Settings.SaveAsync(new(1, JsonSerializer.Serialize(settings)), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                host.Logger.Log(PluginLogLevel.Warning, "signal-workbench.settings.save",
                    "解析設定を保存できません。", exception);
                host.Dispatcher.Post(() => viewModel.Status = "設定を適用しましたが、保存できませんでした。");
            }
            return result;
        }
        finally
        {
            if (!accepted) SetConfiguration(previous);
            tuningGate.Release();
        }
    }

    private void HandleTuningChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred || IsDisposed) return;
        long frequency = ResolveFrequency(result);
        if (frequency != SelectedFrequencyHz)
            SetConfiguration(settings with { CenterFrequencyHz = frequency });
    }

    private long ResolveFrequency(PluginTuningResult result) => result.TargetFrequencyHz > 0
        ? result.TargetFrequencyHz : result.CenterFrequencyHz > 0 ? result.CenterFrequencyHz : settings.CenterFrequencyHz;

    private void SetConfiguration(WorkbenchSettings value)
    {
        lock (processingGate)
        {
            Volatile.Write(ref settings, value);
            ResetProcessing();
        }
        PostConfiguration(State == PluginLifecycleState.Streaming ? "受信中 / 解析窓の蓄積待ち" : "解析開始待ち");
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetProcessing()
    {
        analyzer.Reset();
        continuity.Reset();
        lastConfiguration = null;
        nextOutputSample = null;
        revision++;
        Interlocked.Exchange(ref pendingSnapshot, null);
    }

    private void PostConfiguration(string status)
    {
        WorkbenchSettings current = settings;
        HostContext?.Dispatcher.Post(() =>
        {
            viewModel.Configure(current);
            viewModel.Status = status;
        });
    }

    private sealed record PendingSnapshot(SignalSnapshot Snapshot, int Revision);
}
