using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Sstv.Dsp;
using SRdeckPlugin.Sstv.Models;
using SRdeckPlugin.Sstv.ViewModels;
using SRdeckPlugin.Sstv.Views;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Sstv;

public sealed class SstvPluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    IPluginViewProvider, IPluginFrequencySelection, IFrequencyOverlayProvider,
    IPluginResultProvider, IPluginExportProvider, IPluginProcessingWarmup,
    IPluginProcessingDiagnosticsProvider
{
    public const string ChannelRequestId = "sstv-channel";
    private readonly object processingGate = new();
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly IqStreamContinuityTracker continuity = new();
    private readonly SstvReceiver receiver = new();
    private readonly SstvViewModel viewModel = new();
    private SstvSettings settings = new();
    private SstvImage? latestImage;
    private SstvImage? pendingImage;
    private AppliedChannelConfiguration? lastConfiguration;
    private long expectedOutputStart = -1;
    private long nextDisplayTick;
    private int displayPosted;
    private Guid sourceStreamId;

    public SstvPluginModule()
    {
        receiver.ImageUpdated += OnImageUpdated;
        viewModel.ApplySettingsRequested = ApplySettingsFromViewAsync;
        viewModel.SaveRequested = SaveImageAsync;
        viewModel.ResetRequested = ResetReceiver;
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "sstv", "アマチュア無線（SSTV）", "Slow-scan television receiver (USB/FM, VIS auto detection)",
        new Version(1, 0, 0), new Version(1, 0), new Version(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView |
        PluginCapabilities.SettingsView | PluginCapabilities.FrequencyOverlay |
        PluginCapabilities.ResultPublisher | PluginCapabilities.Export | PluginCapabilities.Headless,
        "SRdeck", "GPL-3.0");

    public long SelectedFrequencyHz => settings.FrequencyHz;
    public int ReceiveBandCenterOffsetHz => settings.Fm ? 0 : SstvReceiver.AudioCenterHz;
    private static long ChannelCenter(SstvSettings value) => value.FrequencyHz +
        (value.Fm ? 0 : SstvReceiver.AudioCenterHz);
    private static int Bandwidth(SstvSettings value) => value.Fm ? 16_000 : 3_000;

    public IReadOnlyList<PluginChannelRequest> ChannelRequests
    {
        get
        {
            SstvSettings value = settings;
            return [new(ChannelRequestId, ChannelCenter(value), Bandwidth(value), SstvReceiver.SampleRateHz,
                MaximumIntermediateSampleRateHz: 48_000, MinimumIntermediateSampleRateHz: 24_000,
                FirTaps: 97, RequestedQueueCapacity: 8, AllowRawIqFallback: false)
                { StopbandBandwidthHz = value.Fm ? 22_000 : 6_400 }];
        }
    }

    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
    [
        new("sstv-passband", ChannelCenter(settings), Bandwidth(settings), string.Empty, true,
            PluginReceiverBandColors.WithAlpha(0x48, PluginReceiverBandColors.Primary),
            "Transparent", "#FFFFFFFF", -1, ToolTip: "SSTV / USB・FM")
    ];
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
        [new("png", "PNG画像", ".png", "image/png")];
    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "SSTV復調・VIS同期・カラー画像走査", PluginComputeDevice.Cpu, ".NET CPU",
        "USB/FM復調、VISパリティ検査、Martin/Scottie/Robot 36/PD120画像生成");
    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;
    public FrameworkElement CreateMainView() => new SstvPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new SstvSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken)
    {
        PluginSettingsDocument? document = await hostContext.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try { settings = (JsonSerializer.Deserialize<SstvSettings>(document.Json) ?? new()).Normalize(); }
            catch (JsonException exception)
            {
                hostContext.Logger.Log(PluginLogLevel.Warning, "sstv.settings.invalid", "Invalid SSTV settings; using defaults.", exception);
            }
        }
        hostContext.Dispatcher.Post(() =>
        {
            viewModel.Configure(settings);
            viewModel.Status = "設定済み / VIS待機";
        });
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        PluginTuningResult result = await TuneAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!ContainsPassband(result, settings)) throw new PluginActivationRejectedException(result.Message);
    }

    protected override ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        ResetReceiver();
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        ResetReceiver();
        SstvImage? image = latestImage;
        HostContext?.Dispatcher.Post(() =>
        {
            if (image is not null) viewModel.ShowImage(image);
            viewModel.Status = "停止中";
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeChannelsAsync(IReadOnlyList<IChannelIqBlockLease> blocks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
        foreach (IChannelIqBlockLease block in blocks)
        {
            if (block.Metadata.Configuration.RequestId != ChannelRequestId || block.Samples.IsEmpty) continue;
            lock (processingGate)
            {
                if (State != PluginLifecycleState.Streaming) break;
                SstvSettings current = settings;
                ChannelIqBlockMetadata metadata = block.Metadata;
                if (metadata.Configuration.OutputSampleRateHz != SstvReceiver.SampleRateHz ||
                    metadata.Configuration.ChannelCenterFrequencyHz != ChannelCenter(current) ||
                    metadata.Configuration.BandwidthHz != Bandwidth(current))
                {
                    ResetProcessing();
                    continue;
                }
                bool reset = continuity.Observe(metadata.Source).RequiresReset ||
                    lastConfiguration != metadata.Configuration || expectedOutputStart != metadata.OutputSampleStart;
                if (reset) receiver.Reset();
                lastConfiguration = metadata.Configuration;
                expectedOutputStart = metadata.OutputSampleStart + block.Samples.Length;
                sourceStreamId = metadata.Source.StreamId;
                receiver.Process(block.Samples.Span, current.Fm, metadata.Source.UtcTimestamp);
                PostDisplay();
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginTuningResult> SelectFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ApplySettingsAsync(settings with { FrequencyHz = frequencyHz }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplySettingsFromViewAsync(SstvSettings value)
    {
        try
        {
            PluginTuningResult result = await ApplySettingsAsync(value, CancellationToken.None).ConfigureAwait(false);
            HostContext?.Dispatcher.Post(() =>
            {
                viewModel.Configure(settings);
                viewModel.SaveStatus = ContainsPassband(result, settings) ? "設定を適用しました" : result.Message;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HostContext?.Logger.Log(PluginLogLevel.Warning, "sstv.settings.failed", "SSTV settings could not be applied.", exception);
            HostContext?.Dispatcher.Post(() => viewModel.SaveStatus = $"設定失敗: {exception.Message}");
        }
    }

    private async ValueTask<PluginTuningResult> ApplySettingsAsync(SstvSettings value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SstvSettings normalized = value.Normalize();
            IPluginHostContext context = HostContext ?? throw new InvalidOperationException("The plugin is not initialized.");
            PluginTuningResult result = State is PluginLifecycleState.Active or PluginLifecycleState.Streaming
                ? await TuneAsync(normalized, cancellationToken).ConfigureAwait(false)
                : new(PluginTuningOutcome.Accepted, "設定済み", ChannelCenter(normalized), SstvReceiver.SampleRateHz,
                    ChannelCenter(normalized) - Bandwidth(normalized), ChannelCenter(normalized) + Bandwidth(normalized));
            if (!ContainsPassband(result, normalized)) return result;
            lock (processingGate)
            {
                settings = normalized;
                ResetProcessing();
            }
            FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
            context.Dispatcher.Post(() => viewModel.Configure(normalized));
            await context.Settings.SaveAsync(new(1, JsonSerializer.Serialize(normalized)), cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { settingsGate.Release(); }
    }

    private ValueTask<PluginTuningResult> TuneAsync(SstvSettings value, CancellationToken cancellationToken) =>
        HostContext!.Tuning.RequestAsync(new("sstv", "アマチュア無線（SSTV）",
            [new(ChannelCenter(value), Bandwidth(value))], ChannelCenter(value), 48_000, 10,
            true, false, PluginGainPreference.Automatic), cancellationToken);

    private static bool ContainsPassband(PluginTuningResult result, SstvSettings value) =>
        result.Outcome is PluginTuningOutcome.Accepted or PluginTuningOutcome.Adjusted &&
        ChannelCenter(value) - Bandwidth(value) / 2 >= result.PassbandLowerFrequencyHz &&
        ChannelCenter(value) + Bandwidth(value) / 2 <= result.PassbandUpperFrequencyHz;

    private void ResetReceiver()
    {
        lock (processingGate) ResetProcessing();
        HostContext?.Dispatcher.Post(() => viewModel.Status = "VIS待機");
    }

    private void ResetProcessing()
    {
        receiver.Reset();
        continuity.Reset();
        lastConfiguration = null;
        expectedOutputStart = -1;
        Interlocked.Exchange(ref pendingImage, null);
        nextDisplayTick = 0;
    }

    private void OnImageUpdated(SstvImage image)
    {
        latestImage = image;
        Interlocked.Exchange(ref pendingImage, image);
        if (!image.Complete) return;
        HostContext?.Notifications.PlayReceptionAlarm();
        ResultPublished?.Invoke(this, new(new PluginResultSummary(image.Id, Descriptor.Id,
            HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow, sourceStreamId, "sstv.image",
            PluginResultSeverity.Information, $"SSTV {image.Mode}", $"{image.Width} × {image.Height}", settings.FrequencyHz)));
    }

    private void PostDisplay()
    {
        IPluginHostContext? context = HostContext;
        if (context is null || (pendingImage is not { Complete: true } && Environment.TickCount64 < nextDisplayTick) ||
            Interlocked.CompareExchange(ref displayPosted, 1, 0) != 0) return;
        nextDisplayTick = Environment.TickCount64 + 200;
        string status = receiver.Status;
        string tone = receiver.ToneHz is >= 900 and <= 2_600 ? $"復調トーン: {receiver.ToneHz:N0} Hz" : "復調トーン: —";
        context.Dispatcher.Post(() =>
        {
            try
            {
                if (IsDisposed) return;
                SstvImage? image = Interlocked.Exchange(ref pendingImage, null);
                if (image is not null) viewModel.ShowImage(image);
                viewModel.Status = status;
                viewModel.ToneText = tone;
            }
            finally { Volatile.Write(ref displayPosted, 0); }
        });
    }

    private async Task SaveImageAsync()
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        string path = Path.Combine(context.Settings.DataDirectory, "images", $"sstv-{Guid.NewGuid():N}.png");
        PluginExportResult result = await Task.Run(async () =>
            await ExportAsync(new("png", path), CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        context.Dispatcher.Post(() => viewModel.SaveStatus = result.Succeeded ? $"保存済み: {path}" : result.Message);
    }

    public ValueTask<PluginExportResult> ExportAsync(PluginExportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (!string.Equals(request.FormatId, "png", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(new PluginExportResult(false, 0, "未対応の保存形式です"));
        SstvImage? image;
        lock (processingGate) image = receiver.CurrentImage ?? latestImage;
        if (image is null) return ValueTask.FromResult(new PluginExportResult(false, 0, "保存できる画像がありません"));
        try
        {
            string? directory = Path.GetDirectoryName(request.DestinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
                PixelFormats.Rgb24, null, image.Pixels, image.Width * 3);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = new(request.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
            return ValueTask.FromResult(new PluginExportResult(true, 1, "SSTV画像を保存しました"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(new PluginExportResult(false, 0, exception.Message));
        }
    }

    public ValueTask WarmUpProcessingAsync(PluginProcessingWarmupContext context, CancellationToken cancellationToken)
    {
        SstvSettings current = settings;
        var warmupReceiver = new SstvReceiver();
        return PluginProcessingWarmup.RunChannelAsync(context, ChannelRequestId, ChannelCenter(current),
            SstvReceiver.SampleRateHz, Bandwidth(current),
            (samples, metadata) => warmupReceiver.Process(samples, current.Fm, metadata.Source.UtcTimestamp),
            warmupReceiver.Reset, cancellationToken);
    }
}
