using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.ActivityHeatmap.Views;

namespace SRdeckPlugin.ActivityHeatmap;

public sealed class ActivityHeatmapPluginModule : PluginModuleBase, IIqBlockConsumer,
    IPluginViewProvider, IPluginProcessingWarmup
{
    private readonly object gate = new();
    private readonly ActivityAnalyzer analyzer = new();
    private readonly IqStreamContinuityTracker continuity = new();
    private readonly ActivityViewModel viewModel;
    private int thresholdDbm = -100, historyHours = 6;
    private bool hasSpectrum;
    private long publishedAt, lastSpectrumFrameId;
    private PluginSpectrumRegion frequencyRegion = PluginSpectrumRegion.DisplayBand;
    private (double Lower, double Upper)? spectrumRange;
    private int revision, publicationQueued;
    private PendingSnapshot? pending;

    public ActivityHeatmapPluginModule() => viewModel = new(ChangeThreshold, ChangeHistoryHours, ChangeFrequencyRegion, ClearHistory);

    public override PluginDescriptor Descriptor { get; } = new(
        "activity-heatmap", "周波数ごとの活動ヒートマップ",
        "受信帯域全体をdBmで活動判定し、周波数別の推移を最大24時間表示",
        new(1, 0, 0), new(1, 0), new(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.MainView | PluginCapabilities.Headless,
        "SRdeck", "GPL-3.0-only");

    public PluginIqPreferences IqPreferences { get; } = new(4);
    public FrameworkElement CreateMainView() => new ActivityHeatmapView { DataContext = viewModel };
    public FrameworkElement? CreateSettingsView() => null;

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        ClearHistory();
        PostStatus("受信中 / データ待ち");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            revision++;
            pending = null;
            continuity.Reset();
        }
        PostStatus("停止中");
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (gate) Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        lock (gate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            IqBlockMetadata metadata = block.Metadata;
            if (metadata.SampleRateHz <= 0 || metadata.SampleCount != block.Samples.Length)
                throw new ArgumentException("Invalid IQ block metadata.", nameof(block));
            IqStreamTransition transition = continuity.Observe(metadata);
            if (transition.RequiresReset)
            {
                bool changedInput = (transition.Changes & (IqStreamChange.FirstBlock | IqStreamChange.StreamChanged |
                    IqStreamChange.GenerationChanged | IqStreamChange.SampleRateChanged | IqStreamChange.CenterFrequencyChanged)) != 0 ||
                    (metadata.Discontinuity & (IqDiscontinuity.StreamStarted | IqDiscontinuity.TuningChanged | IqDiscontinuity.SampleRateChanged)) != 0;
                long missing = transition.Previous is { } previous
                    ? checked(metadata.AbsoluteSampleStart - checked(previous.AbsoluteSampleStart + previous.SampleCount)) : 0;
                if (changedInput || missing < 0) analyzer.Reset();
                else analyzer.Discontinue(missing);
                if (changedInput || missing < 0) { lastSpectrumFrameId = 0; hasSpectrum = false; spectrumRange = null; }
                revision++;
                publishedAt = Math.Max(0, analyzer.ProcessedSamples - metadata.SampleRateHz);
                pending = null;
                if (changedInput || missing < 0) PostCleared();
            }
            PluginSpectrumSnapshot? spectrum = HostContext?.Spectrum.GetSnapshot(new(ActivityAnalyzer.Columns, frequencyRegion));
            bool matches = spectrum is not null && spectrum.SampleRateHz == metadata.SampleRateHz &&
                spectrum.InputCenterFrequencyHz == metadata.CenterFrequencyHz;
            if (matches && analyzer.ProcessedSamples > 0 && (spectrumRange is not { } previousRange ||
                previousRange.Lower != spectrum!.LowerFrequencyHz || previousRange.Upper != spectrum.UpperFrequencyHz))
            {
                analyzer.Reset();
                publishedAt = lastSpectrumFrameId = 0;
                revision++;
                pending = null;
                PostCleared();
            }
            bool fresh = matches && spectrum!.FrameId > lastSpectrumFrameId;
            if (fresh)
            {
                lastSpectrumFrameId = spectrum!.FrameId;
                hasSpectrum = true;
                spectrumRange = (spectrum.LowerFrequencyHz, spectrum.UpperFrequencyHz);
            }
            analyzer.Push(fresh ? spectrum!.LevelsDbm.Span : [], block.Samples.Length,
                metadata.SampleRateHz, metadata.CenterFrequencyHz, metadata.UtcTimestamp, thresholdDbm,
                spectrumRange?.Lower, spectrumRange?.Upper);
            if (analyzer.ProcessedSamples - publishedAt >= metadata.SampleRateHz)
            {
                publishedAt = analyzer.ProcessedSamples;
                QueueSnapshot();
            }
        }
        return ValueTask.CompletedTask;
    }

    public ActivitySnapshot? GetSnapshot()
    {
        lock (gate) return analyzer.CreateSnapshot(thresholdDbm, historyHours);
    }

    public void ChangeThreshold(int value)
    {
        if (!ActivityViewModel.Thresholds.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate)
        {
            thresholdDbm = value;
            Reset();
            PostCleared();
        }
    }

    public void ChangeHistoryHours(int value)
    {
        if (!ActivityViewModel.HistoryHoursOptions.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate)
        {
            historyHours = value;
            revision++;
            QueueSnapshot();
        }
    }

    public void ChangeFrequencyRegion(PluginSpectrumRegion value)
    {
        if (value is not (PluginSpectrumRegion.InputBand or PluginSpectrumRegion.DisplayBand))
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate)
        {
            frequencyRegion = value;
            Reset();
            PostCleared();
        }
    }

    private void QueueSnapshot()
    {
        pending = new(analyzer.CreateSnapshot(thresholdDbm, historyHours), revision);
        if (HostContext is { } host && Interlocked.Exchange(ref publicationQueued, 1) == 0)
            host.Dispatcher.Post(Publish);
    }

    public void ClearHistory()
    {
        lock (gate)
        {
            Reset();
            PostCleared();
        }
    }

    private void Reset()
    {
        analyzer.Reset();
        continuity.Reset();
        pending = null;
        publishedAt = 0;
        lastSpectrumFrameId = 0;
        spectrumRange = null;
        hasSpectrum = false;
        revision++;
    }

    private void PostCleared()
    {
        int current = revision;
        HostContext?.Dispatcher.Post(() =>
        {
            if (current != Volatile.Read(ref revision) || IsDisposed) return;
            viewModel.Snapshot = null;
            viewModel.Status = State == PluginLifecycleState.Streaming ? "受信中 / データ待ち" : "開始待ち";
        });
    }

    private void Publish()
    {
        lock (gate)
        {
            Interlocked.Exchange(ref publicationQueued, 0);
            PendingSnapshot? latest = pending;
            pending = null;
            if (latest is null || latest.Revision != revision || IsDisposed) return;
            viewModel.Snapshot = latest.Snapshot;
            if (State == PluginLifecycleState.Streaming)
                viewModel.Status = hasSpectrum ? "受信中" : "スペクトラム待ち / 未観測";
        }
    }

    private void PostStatus(string status) => HostContext?.Dispatcher.Post(() =>
    {
        if (!IsDisposed) viewModel.Status = status;
    });

    public ValueTask WarmUpProcessingAsync(PluginProcessingWarmupContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return ValueTask.CompletedTask;
    }

    private sealed record PendingSnapshot(ActivitySnapshot? Snapshot, int Revision);
}
