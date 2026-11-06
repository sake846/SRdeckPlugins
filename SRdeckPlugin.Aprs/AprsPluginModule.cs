using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Aprs.Dsp;
using SRdeckPlugin.Aprs.Models;
using SRdeckPlugin.Aprs.Protocols;
using SRdeckPlugin.Aprs.ViewModels;
using SRdeckPlugin.Aprs.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Aprs;

public sealed class AprsPluginModule : PluginModuleBase, IPluginChannelBlockConsumer,
    IPluginViewProvider, IPluginProfileProvider, IPluginFrequencySelection,
    IFrequencyOverlayProvider, IPluginResultProvider, IPluginExportProvider,
    IPluginProcessingDiagnosticsProvider, IPluginProcessingWarmup,
    IWaterfallDisplayProvider, IWaterfallDisplayRequestChangedProvider
{
    public const string ChannelId = "aprs-afsk1200";
    private readonly object gate = new();
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly AprsReceiver receiver = new();
    private readonly IqStreamContinuityTracker continuity = new();
    private readonly List<AprsPacket> history = [];
    private readonly Queue<AprsPacket> pending = [];
    private readonly AprsViewModel viewModel = new();
    private AprsSettings settings = new();
    private PluginJsonLinesHistoryWriter<AprsPacket>? writer;
    private ChannelIqBlockMetadata currentMetadata;
    private AppliedChannelConfiguration? previousConfiguration;
    private long expectedOutputStart, blockStartSamples, nextUiTick;
    private int uiPosted;
    private string status = "待機中";
    private static readonly (string Id, string Name, long Frequency, AprsModulation Modulation)[] Presets =
    [
        ("jp-1200", "日本 144.660 MHz / 1200 baud", 144_660_000, AprsModulation.Afsk1200),
        ("jp-9600", "日本 144.640 MHz / 9600 baud", 144_640_000, AprsModulation.Gmsk9600),
        ("eu-1200", "欧州 144.800 MHz / 1200 baud", 144_800_000, AprsModulation.Afsk1200),
        ("na-1200", "北米 144.390 MHz / 1200 baud", 144_390_000, AprsModulation.Afsk1200),
        ("iss-1200", "ISS 145.825 MHz / 1200 baud", 145_825_000, AprsModulation.Afsk1200)
    ];

    public AprsPluginModule()
    {
        RegisterStreamReset(ResetReceiver);
        receiver.FrameDecoded += OnFrame;
        viewModel.ApplySettings = (frequency, modulation, token) => ApplySettingsAsync(frequency, modulation, token);
        viewModel.ApplyProfile = SelectProfileAsync;
        viewModel.Export = ExportAsync;
    }

    public override PluginDescriptor Descriptor { get; } = new(
        "aprs", "アマチュア無線（APRS）", "AFSK 1200 / GMSK 9600 (G3RUH) / AX.25 APRS receiver",
        new(1, 0, 0), new(1, 0), new(1, 0),
        PluginCapabilities.ChannelIqConsumer | PluginCapabilities.MainView | PluginCapabilities.SettingsView |
        PluginCapabilities.FrequencyOverlay | PluginCapabilities.ResultPublisher | PluginCapabilities.Export |
        PluginCapabilities.Headless | PluginCapabilities.WaterfallDisplay, "SRdeck", "GPL-3.0-only");
    public long SelectedFrequencyHz => Volatile.Read(ref settings).FrequencyHz;
    public AprsModulation SelectedModulation => Volatile.Read(ref settings).Modulation;
    private int ReceiverBandwidthHz => AprsReceiver.GetBandwidthHz(SelectedModulation);
    private string ModulationLabel => SelectedModulation == AprsModulation.Gmsk9600 ? "GMSK 9600 (G3RUH)" : "AFSK 1200";
    public IReadOnlyList<PluginProfileDescriptor> Profiles { get; } = Presets
        .Select(p => new PluginProfileDescriptor(p.Id, p.Name,
            p.Modulation == AprsModulation.Gmsk9600 ? "GMSK 9600 (G3RUH) / AX.25" : "AFSK 1200 / AX.25", p.Id == "jp-1200")).ToArray();
    public string? SelectedProfileId => Presets.FirstOrDefault(p => p.Frequency == SelectedFrequencyHz && p.Modulation == SelectedModulation).Id;
    public IReadOnlyList<PluginChannelRequest> ChannelRequests =>
    [
        new(ChannelId, SelectedFrequencyHz, ReceiverBandwidthHz, AprsReceiver.SampleRateHz,
            FirTaps: 65, RequestedQueueCapacity: 8, AllowRawIqFallback: false)
        { StopbandBandwidthHz = 30_000 }
    ];
    public IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays =>
    [
        new(ChannelId, SelectedFrequencyHz, ReceiverBandwidthHz, "", true,
            PluginReceiverBandColors.WithAlpha(0x40, PluginReceiverBandColors.Primary),
            "#00000000", "#FFFFFFFF", -1)
    ];
    public WaterfallDisplayRequest WaterfallDisplayRequest => new(
        WaterfallTimeMode.ThreeMinutes, ReceiverBandwidthHz * 10);
    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "AFSK/GMSK復調・AX.25検証・APRS解析", PluginComputeDevice.Cpu, ".NET CPU",
        "FM / Bell 202・G3RUH 9600 / NRZI / HDLC / CRC-16-X25");
    public IReadOnlyList<PluginExportFormat> ExportFormats { get; } =
    [new("json", "JSON", ".json", "application/json"), new("csv", "CSV", ".csv", "text/csv"),
     new("tnc2", "TNC2", ".txt", "text/plain")];
    public event EventHandler? FrequencyOverlaysChanged;
    public event EventHandler? WaterfallDisplayRequestChanged;
    public event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;
    public FrameworkElement CreateMainView() => new AprsPluginView { DataContext = viewModel };
    public FrameworkElement CreateSettingsView() => new AprsSettingsView { DataContext = viewModel };

    protected override async ValueTask OnInitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken)
    {
        PluginSettingsDocument? document = await hostContext.Settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (document is not null)
        {
            try
            {
                if (document.SchemaVersion != 1) throw new JsonException("Unsupported settings schema.");
                settings = (JsonSerializer.Deserialize<AprsSettings>(document.Json) ?? new()).Normalize();
            }
            catch (JsonException)
            { hostContext.Logger.Log(PluginLogLevel.Warning, "aprs.settings.invalid", "APRS settings are invalid; defaults are used."); }
        }
        ResetReceiver();
        foreach (AprsPacket stored in PluginJsonLinesHistory.Load<AprsPacket>(HistoryPath, settings.MaximumHistory))
        {
            // Reparse retained raw frames so histories written before numeric
            // decoding was added also populate the graph after an upgrade.
            AprsPacket packet = AprsParser.Parse(new(stored.Source, stored.Destination,
                stored.Path.Split(',', StringSplitOptions.RemoveEmptyEntries), stored.Information),
                stored.ReceivedAt, stored.StreamId, stored.FrequencyHz);
            history.Add(packet); pending.Enqueue(packet);
        }
        writer = new(HistoryPath, () => new(settings.MaximumHistory, MaximumBytes: 16 * 1024 * 1024), p => p.ReceivedAt);
        writer.SaveFailed += _ => hostContext.Logger.Log(PluginLogLevel.Warning, "aprs.history.failed", "APRS history could not be saved.");
        hostContext.Dispatcher.Post(() =>
        {
            viewModel.RuntimeDiagnostics = hostContext.RuntimeDiagnostics;
            viewModel.FrequencyText = settings.FrequencyHz.ToString(CultureInfo.InvariantCulture);
            viewModel.SelectedModulation = settings.Modulation;
        });
        PublishView(true);
    }

    private string HistoryPath => Path.Combine(HostContext!.Settings.DataDirectory, "aprs-history.jsonl");

    public ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var preset = Presets.FirstOrDefault(p => p.Id == profileId);
        if (preset.Id is null) throw new ArgumentException("Unknown APRS profile.", nameof(profileId));
        return ApplyProfileAsync(preset.Frequency, preset.Modulation, cancellationToken);
    }

    private async ValueTask ApplyProfileAsync(long frequency, AprsModulation modulation, CancellationToken token)
    {
        PluginTuningResult result = await ApplySettingsAsync(frequency, modulation, token).ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new PluginActivationRejectedException(result.Message);
    }

    public ValueTask<PluginTuningResult> SelectFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default) =>
        ApplySettingsAsync(frequencyHz, null, cancellationToken);

    private async ValueTask<PluginTuningResult> ApplySettingsAsync(long frequencyHz, AprsModulation? modulation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (frequencyHz is < 1_000_000 or > 2_147_483_647) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (modulation is { } value && !Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(modulation));
        await settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AprsModulation selectedModulation = modulation ?? settings.Modulation;
            bool bandwidthChanged = selectedModulation != settings.Modulation;
            var result = new PluginTuningResult(PluginTuningOutcome.Accepted, "設定しました。", frequencyHz,
                AprsReceiver.SampleRateHz, frequencyHz - 24_000, frequencyHz + 24_000);
            if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
                result = await RequestTuning(frequencyHz, selectedModulation, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred) return result;
            lock (gate)
            {
                settings = settings with { FrequencyHz = frequencyHz, Modulation = selectedModulation };
                ResetReceiver();
                if (State == PluginLifecycleState.Streaming) status = $"監視中 / {ModulationLabel}";
            }
            if (HostContext is not null)
                await HostContext.Settings.SaveAsync(new(1, JsonSerializer.Serialize(settings)), cancellationToken).ConfigureAwait(false);
            FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
            if (bandwidthChanged) WaterfallDisplayRequestChanged?.Invoke(this, EventArgs.Empty);
            HostContext?.Dispatcher.Post(() =>
            {
                viewModel.FrequencyText = frequencyHz.ToString(CultureInfo.InvariantCulture);
                viewModel.SelectedModulation = selectedModulation;
            });
            PublishView(true);
            return result;
        }
        finally { settingsGate.Release(); }
    }

    private async ValueTask<PluginTuningResult> RequestTuning(long frequency, AprsModulation modulation, CancellationToken token)
    {
        int bandwidth = AprsReceiver.GetBandwidthHz(modulation);
        string label = modulation == AprsModulation.Gmsk9600 ? "GMSK 9600 (G3RUH)" : "AFSK 1200";
        var result = await HostContext!.Tuning.RequestAsync(new(
            modulation == AprsModulation.Gmsk9600 ? "gmsk9600" : "afsk1200", $"APRS {label}", [new(frequency, bandwidth)], frequency,
            AprsReceiver.SampleRateHz, 1000, true, false, PluginGainPreference.Unspecified), token).ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Accepted or PluginTuningOutcome.Adjusted &&
            (frequency - bandwidth / 2 < result.PassbandLowerFrequencyHz ||
             frequency + bandwidth / 2 > result.PassbandUpperFrequencyHz))
            return result with { Outcome = PluginTuningOutcome.Rejected, Message = "APRS受信帯域が入力帯域に収まりません。" };
        return result;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        var result = await RequestTuning(SelectedFrequencyHz, SelectedModulation, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new PluginActivationRejectedException(result.Message);
    }

    protected override ValueTask OnStreamStartedAsync()
    { status = $"監視中 / {ModulationLabel}"; PublishView(true); return ValueTask.CompletedTask; }

    private void ResetReceiver()
    {
        lock (gate)
        {
            receiver.Reset(settings.Modulation); continuity.Reset(); previousConfiguration = null;
            expectedOutputStart = 0;
        }
    }

    public ValueTask ConsumeChannelsAsync(IReadOnlyList<IChannelIqBlockLease> blocks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ThrowIfDisposed();
        lock (gate)
        {
            if (State != PluginLifecycleState.Streaming) return ValueTask.CompletedTask;
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configuration = block.Metadata.Configuration;
                if (configuration.RequestId != ChannelId) continue;
                if (configuration.OutputSampleRateHz != AprsReceiver.SampleRateHz ||
                    configuration.ChannelCenterFrequencyHz != settings.FrequencyHz ||
                    configuration.BandwidthHz < ReceiverBandwidthHz)
                { ResetReceiver(); continue; }
                bool reset = continuity.Observe(block.Metadata.Source).RequiresReset ||
                    previousConfiguration != configuration || expectedOutputStart != block.Metadata.OutputSampleStart;
                if (reset) receiver.Reset();
                previousConfiguration = configuration;
                expectedOutputStart = block.Metadata.OutputSampleStart + block.Samples.Length;
                currentMetadata = block.Metadata;
                blockStartSamples = receiver.SamplesProcessed;
                receiver.Process(block.Samples.Span);
            }
        }
        PublishView(false);
        return ValueTask.CompletedTask;
    }

    private void OnFrame(byte[] bytes)
    {
        if (!Ax25Decoder.TryDecode(bytes, out Ax25Frame? frame)) return;
        long sourcePosition = currentMetadata.MapOutputToSource(currentMetadata.OutputSampleStart +
            receiver.SamplesProcessed - blockStartSamples);
        double elapsed = (sourcePosition - currentMetadata.Source.AbsoluteSampleStart) /
            (double)currentMetadata.Source.SampleRateHz;
        DateTimeOffset timestamp = currentMetadata.Source.UtcTimestamp.AddSeconds(elapsed);
        var packet = AprsParser.Parse(frame!, timestamp, currentMetadata.Source.StreamId, settings.FrequencyHz);
        history.Add(packet);
        if (history.Count > settings.MaximumHistory) history.RemoveRange(0, history.Count - settings.MaximumHistory);
        if (pending.Count >= settings.MaximumHistory) pending.Dequeue();
        pending.Enqueue(packet);
        if (writer?.TryEnqueue(packet) != true)
            HostContext?.Logger.Log(PluginLogLevel.Warning, "aprs.history.queue-full", "APRS history queue is full.");
        HostContext?.Metrics.AddCounter(PluginProcessingStage.ProtocolDecode, "aprs.packets");
        ResultPublished?.Invoke(this, new(new(Guid.NewGuid().ToString("N"), "aprs", packet.ReceivedAt,
            packet.StreamId, packet.Kind, PluginResultSeverity.Information, "APRS受信", packet.Kind,
            packet.FrequencyHz, DetailsSchemaVersion: 1, DetailsJson: JsonSerializer.Serialize(packet))));
    }

    private void PublishView(bool force)
    {
        var host = HostContext;
        long now = Stopwatch.GetTimestamp();
        if (host is null || (!force && now < nextUiTick) || Interlocked.Exchange(ref uiPosted, 1) != 0) return;
        nextUiTick = now + Stopwatch.Frequency / 2;
        host.Dispatcher.Post(() =>
        {
            try
            {
                lock (gate)
                {
                    viewModel.AddPackets(pending.ToArray(), settings.MaximumHistory); pending.Clear();
                    viewModel.Status = status;
                    viewModel.Diagnostics = $"信号電力 {receiver.SignalPowerDbfs:F1} dBFS\nIQ {receiver.SamplesProcessed:N0} samples\nHDLC CRC正常 {receiver.Hdlc.ValidFrames:N0} / 棄却 {receiver.Hdlc.RejectedFrames:N0}\n履歴保存キュー棄却 {writer?.DroppedCount ?? 0:N0}";
                }
            }
            finally { Volatile.Write(ref uiPosted, 0); }
        });
    }

    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        lock (gate) { status = "停止中"; }
        if (writer is not null) await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        PublishView(true);
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (gate) receiver.FrameDecoded -= OnFrame;
        if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask WarmUpProcessingAsync(PluginProcessingWarmupContext context, CancellationToken cancellationToken) =>
        PluginProcessingWarmup.RunChannelAsync(context, ChannelId, SelectedFrequencyHz, AprsReceiver.SampleRateHz,
            ReceiverBandwidthHz, (samples, _) => receiver.Process(samples), ResetReceiver, cancellationToken);

    public async ValueTask<PluginExportResult> ExportAsync(PluginExportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AprsPacket[] packets;
        lock (gate) packets = history.Where(p => (request.From is null || p.ReceivedAt >= request.From) &&
            (request.To is null || p.ReceivedAt <= request.To)).ToArray();
        string text = request.FormatId switch
        {
            "json" => JsonSerializer.Serialize(packets, new JsonSerializerOptions { WriteIndented = true }),
            "tnc2" => string.Join('\n', packets.Select(p => p.MonitorText)),
            "csv" => "receivedAt,frequencyHz,source,destination,path,kind,latitude,longitude,information,telemetrySequence," +
                string.Join(',', CsvMeasurements.Select(v => v.Header)) + "\r\n" + string.Join("\r\n", packets.Select(CsvRow)),
            _ => throw new ArgumentException("Unknown APRS export format.", nameof(request))
        };
        try
        {
            await File.WriteAllTextAsync(request.DestinationPath, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new(true, packets.Length, $"{packets.Length:N0}件を保存しました。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(false, 0, "履歴を保存できませんでした。"); }
    }

    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    private static readonly (string Key, string Header)[] CsvMeasurements =
    [
        ("speed", "speedKnots"), ("course", "courseDegrees"), ("temperature", "temperatureCelsius"),
        ("humidity", "humidityPercent"), ("pressure", "pressureHectopascals"),
        ("windDirection", "windDirectionDegrees"), ("windSpeed", "windSpeedMetersPerSecond"),
        ("gust", "gustMetersPerSecond"), ("rainHour", "rainLastHourMillimeters"),
        ("rain24Hours", "rainLast24HoursMillimeters"), ("rainMidnight", "rainSinceMidnightMillimeters"),
        ("luminosity", "luminosityWattsPerSquareMeter"),
        .. Enumerable.Range(1, 5).Select(i => ($"analog{i}", $"analog{i}Raw")),
        .. Enumerable.Range(1, 8).Select(i => ($"digital{i}", $"digital{i}"))
    ];

    private static string CsvRow(AprsPacket packet)
    {
        var values = AprsMeasurements.From(packet).ToDictionary(v => v.Key, v => v.Value);
        string[] columns =
        [
            packet.ReceivedAt.ToString("O"), packet.FrequencyHz.ToString(CultureInfo.InvariantCulture),
            packet.Source, packet.Destination, packet.Path, packet.Kind,
            packet.Latitude?.ToString(CultureInfo.InvariantCulture) ?? "",
            packet.Longitude?.ToString(CultureInfo.InvariantCulture) ?? "", packet.Information,
            packet.Telemetry?.Sequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            .. CsvMeasurements.Select(v => values.TryGetValue(v.Key, out double number)
                ? number.ToString(CultureInfo.InvariantCulture) : "")
        ];
        return string.Join(',', columns.Select(Csv));
    }
}
