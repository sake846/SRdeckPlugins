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

public sealed partial class VdlPluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(
        PluginExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<VdlDecodedFrame> persisted = host is null
            ? []
            : PluginJsonLinesHistory.LoadAll<VdlDecodedFrame>(GetHistoryPath(host));
        VdlDecodedFrame[] records;
        if (persisted.Count > 0)
        {
            records = persisted.Where(item =>
                (request.From is null || item.ReceivedAt >= request.From) &&
                (request.To is null || item.ReceivedAt <= request.To))
                    .TakeLast(settings.MaximumHistory).ToArray();
        }
        else
        {
            lock (historyGate)
            {
                records = history.Where(item =>
                    (request.From is null || item.ReceivedAt >= request.From) &&
                    (request.To is null || item.ReceivedAt <= request.To)).ToArray();
            }
        }

        try
        {
            if (request.FormatId == "json")
            {
                string json = JsonSerializer.Serialize(records, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(request.DestinationPath, json,
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else if (request.FormatId == "csv")
            {
                var csv = new StringBuilder(
                    "receivedAt,frequencyHz,protocol,source,destination,frameType,callsign,text,payload," +
                    "signalQuality,preambleSnrDb,preambleCoherence\r\n");
                foreach (VdlDecodedFrame item in records)
                {
                    csv.Append(Csv(item.ReceivedAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                        .Append(item.FrequencyHz).Append(',')
                        .Append(Csv(item.Protocol)).Append(',')
                        .Append(Csv(item.Source)).Append(',')
                        .Append(Csv(item.Destination)).Append(',')
                        .Append(Csv(item.FrameType)).Append(',')
                        .Append(Csv(item.Callsign)).Append(',')
                        .Append(Csv(item.Text)).Append(',')
                        .Append(Csv(item.Hex)).Append(',')
                        .Append(item.Raw.SignalQuality.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.Raw.PreambleSnrDb.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.Raw.PreambleCoherence.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                await File.WriteAllTextAsync(request.DestinationPath, csv.ToString(),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return new(false, 0, $"Unknown VDL export format '{request.FormatId}'.");
            }
            return new(true, records.Length, $"Exported {records.Length} VDL messages.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            host?.Logger.Log(PluginLogLevel.Error, "vdl.export.failed", "VDL export failed.", exception);
            return new(false, 0, exception.Message);
        }
    }

    private void Publish(VdlDecodedFrame decoded)
    {
        lock (historyGate)
        {
            if (ShouldPersist(decoded))
            {
                VdlDecodedFrame persisted = PrepareForStorage(decoded);
                history.Add(persisted);
                if (history.Count > settings.MaximumHistory)
                    history.RemoveRange(0, history.Count - settings.MaximumHistory);
                AppendHistory(persisted);
            }
        }
        host?.Notifications.PlayReceptionAlarm(TimeSpan.FromMilliseconds(500));
        VdlFrame frame = decoded.Raw;
        ResultPublished?.Invoke(this, new PluginResultPublishedEventArgs(new PluginResultSummary(
            $"{frame.StreamId:N}-{frame.SamplePosition}",
            Descriptor.Id,
            frame.ReceivedAt,
            frame.StreamId,
            "vdl2.avlc",
            PluginResultSeverity.Information,
            decoded.Callsign == "-" ? decoded.Protocol : decoded.Callsign,
            decoded.Summary,
            frame.FrequencyHz,
            DetailsSchemaVersion: 1,
            DetailsJson: JsonSerializer.Serialize(decoded))));
    }

    private bool SubmitMonitorAudio(ReadOnlySpan<float> audio, IqBlockMetadata metadata, bool discontinuous)
    {
        if (audio.IsEmpty || host is null || !settings.MonitorAudioEnabled) return true;
        const float volume = 1f;
        byte[] pcm = new byte[audio.Length * sizeof(short)];
        for (int index = 0; index < audio.Length; index++)
        {
            short value = (short)MathF.Round(Math.Clamp(audio[index] * volume, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        return host.Audio.TrySubmit(new PcmAudioFrame(
            host.PluginId, metadata.StreamId, Interlocked.Increment(ref audioSequence),
            VdlMode2Receiver.MonitorAudioSampleRate, 1,
            PcmSampleFormat.Signed16LittleEndian, pcm, discontinuous));
    }

    private void StartIqCapture()
    {
        if (State != PluginLifecycleState.Streaming || host is null)
        {
            viewModel.CaptureStatus = "IQ録音: 受信中に開始してください";
            return;
        }
        if (Interlocked.CompareExchange(ref captureSaveInProgress, 1, 0) != 0)
        {
            viewModel.CaptureStatus = "IQ録音: 保存処理中です";
            return;
        }
        IPluginHostContext context = host;
        IqBlockMetadata? metadata = lastCaptureMetadata;
        VdlMode2Receiver.DiagnosticsSnapshot diagnostics = receiver.GetDiagnostics();
        Channel selectedChannel = Channels.First(channel => channel.Id == selectedProfileId);
        viewModel.CaptureStatus = "IQ録音: 直前3秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(context, metadata, diagnostics, selectedChannel));
    }

    private void SavePretriggerCapture(IPluginHostContext context, IqBlockMetadata? metadata,
        VdlMode2Receiver.DiagnosticsSnapshot diagnostics, Channel selectedChannel)
    {
        try
        {
            PackedIqHistoryPairSnapshot snapshot = pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            PackedIqHistorySnapshot rawSnapshot = snapshot.First;
            PackedIqHistorySnapshot channelSnapshot = snapshot.Second;
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"vdl-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{selectedChannel.FrequencyHz}");
            using var capture = new VdlIqCapture(basePath, rawSnapshot.SampleRateHz,
                TimeSpan.FromSeconds(3));
            capture.WriteRawPcm(rawSnapshot.RawInterleaved);
            capture.WriteChannelPcm(channelSnapshot.RawInterleaved);
            var document = new
            {
                Format = "SRdeck VDL analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = "3-second rolling pre-trigger",
                SelectedChannel = selectedChannel,
                RawIqFile = Path.GetFileName(capture.RawPath),
                ChannelIqFile = Path.GetFileName(capture.ChannelPath),
                RawSampleRateHz = rawSnapshot.SampleRateHz,
                ChannelSampleRateHz = VdlMode2Receiver.WorkingSampleRate,
                RawDurationSeconds = rawSnapshot.DurationSeconds,
                ChannelDurationSeconds = channelSnapshot.DurationSeconds,
                InputMetadata = metadata,
                ReceiverDiagnostics = diagnostics
            };
            File.WriteAllText(capture.DiagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                }));
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存済み: {capture.BasePath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "vdl.iq-capture.save-failed",
                "Could not save VDL rolling IQ capture.", exception);
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}");
        }
        finally { Interlocked.Exchange(ref captureSaveInProgress, 0); }
    }

    private static ValueTask<PluginTuningResult> RequestTuningAsync(
        IPluginHostContext context,
        Channel channel,
        CancellationToken cancellationToken) =>
        context.Tuning.RequestAsync(new PluginTuningRequest(
            channel.Id,
            channel.Name,
            [new TuningTarget(channel.FrequencyHz, 25_000)],
            channel.FrequencyHz,
            96_000,
            12_500,
            true,
            false,
            PluginGainPreference.Automatic), cancellationToken);

    private async ValueTask<PluginTuningResult> RequestTuningForCurrentChannelAsync(
        IPluginHostContext context, Channel channel, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref tuningRequestInProgress);
        try { return await RequestTuningAsync(context, channel, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref tuningRequestInProgress); }
    }

    private static void ValidateTuningResult(PluginTuningResult result)
    {
        if (result.Outcome == PluginTuningOutcome.Rejected)
            throw new InvalidOperationException($"VDL tuning was rejected: {result.Message}");
        if (result.SampleRateHz < VdlMode2Receiver.SymbolRate * 4)
            throw new InvalidOperationException("VDL Mode 2 requires at least 42 kS/s.");
    }

    private void SetStatus(string value)
    {
        IPluginHostContext? context = host;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
    }

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        if (Volatile.Read(ref tuningRequestInProgress) != 0) return;
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            Channel channel = Channels.First(item => item.Id == selectedProfileId);
            long targetFreqHz = channel.FrequencyHz;
            if ((result.PassbandLowerFrequencyHz > targetFreqHz ||
                 result.PassbandUpperFrequencyHz < targetFreqHz) && host is not null)
            {
                _ = host.Tuning.RequestAsync(new PluginTuningRequest(
                    $"vdl-{channel.Id}",
                    $"VDL {channel.Name}",
                    [new TuningTarget(targetFreqHz, 16_800)],
                    targetFreqHz,
                    VdlMode2Receiver.WorkingSampleRate,
                    null,
                    true,
                    false,
                    PluginGainPreference.Automatic));
            }
            SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                $"{targetFreqHz / 1_000_000.0:F3} MHz");
        }
    }

    private void LoadHistory()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            string path = GetHistoryPath(context);
            VdlDecodedFrame[] loaded = PluginJsonLinesHistory.LoadAll<VdlDecodedFrame>(path)
                .Where(ShouldPersist)
                .TakeLast(settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, loaded);
            lock (historyGate) history.AddRange(loaded);
            context.Dispatcher.Post(() =>
            {
                foreach (VdlDecodedFrame item in loaded)
                    viewModel.AddFrame(item);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "vdl.history.load-failed",
                "VDL decoded history could not be loaded.", exception);
        }
    }

    private void PruneHistory()
    {
        lock (historyGate)
        {
            if (history.Count > settings.MaximumHistory)
                history.RemoveRange(0, history.Count - settings.MaximumHistory);
        }
    }

    private void AppendHistory(VdlDecodedFrame decoded)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        if (!ShouldPersist(decoded)) return;
        VdlDecodedFrame persisted = PrepareForStorage(decoded);
        if (historyWriter?.TryEnqueue(persisted) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "vdl.history.queue-full",
            "VDL decoded history queue is full; the record was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<VdlDecodedFrame> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<VdlDecodedFrame>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(settings.MaximumHistory),
            static item => item.ReceivedAt);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "vdl.history.save-failed",
            "VDL decoded history could not be saved.", exception);
        return writer;
    }

    private bool ShouldPersist(VdlDecodedFrame frame)
    {
        bool parsed = !string.Equals(frame.ParseStatus, "未対応", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(frame.ParseStatus, "部分解析", StringComparison.OrdinalIgnoreCase);
        if (parsed && !settings.SaveDecodedFrames) return false;
        if (!parsed && !settings.SaveUnparsedFrames) return false;
        return settings.ProtocolFilter switch
        {
            "avlc" => frame.Protocol.Contains("AVLC", StringComparison.OrdinalIgnoreCase),
            "acars" => frame.Acars is not null,
            "x25" => frame.X25 is not null,
            "other" => frame.Acars is null && frame.X25 is null &&
                       !frame.Protocol.Contains("AVLC", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private VdlDecodedFrame PrepareForStorage(VdlDecodedFrame frame)
    {
        VdlDecodedFrame persisted = settings.SaveRawFrames
            ? frame : frame with { Raw = frame.Raw with { Payload = [] } };
        return !settings.SaveAcarsText || persisted.Acars is null
            ? persisted
            : persisted with { Acars = persisted.Acars with { Text = string.Empty } };
    }

    private void DeleteHistoryFile()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            historyWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            historyWriter = CreateHistoryWriter(context);
            PluginJsonLinesHistory.Delete(GetHistoryPath(context));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "vdl.history.delete-failed",
                "VDL decoded history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private static string Csv(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private void PersistSettings()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            VdlSettings settingsToPersist = settings with
            {
                MapState = GeoMapStateStore.GetState(Descriptor.Id)
            };
            context.Settings.SaveAsync(new PluginSettingsDocument(
                1, JsonSerializer.Serialize(settingsToPersist))).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "vdl.settings.save-failed",
                "VDL settings could not be saved.", exception);
        }
    }

}
