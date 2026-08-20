using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Acars.Dsp;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Acars.ViewModels;
using SRdeckPlugin.Acars.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars;

public sealed partial class AcarsPluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(PluginExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<AcarsReception> persisted = host is null
            ? []
            : PluginJsonLinesHistory.LoadAll<AcarsReception>(GetHistoryPath(host));
        AcarsReception[] records;
        if (persisted.Count > 0)
        {
                records = persisted.Where(item =>
                (request.From is null || item.ReceivedAt >= request.From) &&
                (request.To is null || item.ReceivedAt <= request.To))
                    .TakeLast(settings.MaximumHistory).ToArray();
        }
        else
        {
            lock (gate)
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
                await File.WriteAllTextAsync(request.DestinationPath,
                    JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else if (request.FormatId == "csv")
            {
                var csv = new StringBuilder("receivedAt,frequencyHz,aircraft,label,blockId,text,signalQuality,mode,acknowledgement,isBlockCheckValid,hasValidOddParity,isContinuationBlock,rawHex,streamId,samplePosition\r\n");
                foreach (AcarsReception item in records)
                    csv.Append(Csv(item.ReceivedAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                        .Append(item.FrequencyHz).Append(',')
                        .Append(Csv(item.Aircraft)).Append(',')
                        .Append(Csv(item.Label)).Append(',')
                        .Append(Csv(item.BlockId)).Append(',')
                        .Append(Csv(item.Text)).Append(',')
                        .Append(item.SignalQuality.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(Csv(item.Mode)).Append(',')
                        .Append(Csv(item.Acknowledgement)).Append(',')
                        .Append(item.IsBlockCheckValid ? "true" : "false").Append(',')
                        .Append(item.HasValidOddParity ? "true" : "false").Append(',')
                        .Append(item.IsContinuationBlock ? "true" : "false").Append(',')
                        .Append(Csv(item.RawHex)).Append(',')
                        .Append(item.StreamId.ToString("D", CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.SamplePosition.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                await File.WriteAllTextAsync(request.DestinationPath, csv.ToString(),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else return new(false, 0, $"Unknown ACARS export format '{request.FormatId}'.");
            return new(true, records.Length, $"Exported {records.Length} ACARS messages.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            host?.Logger.Log(PluginLogLevel.Error, "acars.export.failed", "ACARS export failed.", exception);
            return new(false, 0, exception.Message);
        }
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (processingGate)
            foreach (AcarsReceiver receiver in ReceiverSnapshot()) receiver.Reset();
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
        if (host is not null)
        {
            host.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
            PersistSettings();
        }
        host = null;
    }

    private void SubmitAudio(ReadOnlySpan<float> audio, IqBlockMetadata metadata, bool isDiscontinuous)
    {
        if (audio.IsEmpty || host is null || !settings.MonitorAudioEnabled) return;
        const float gain = 1f;
        byte[] pcm = new byte[audio.Length * sizeof(short)];
        for (int index = 0; index < audio.Length; index++)
        {
            short value = (short)MathF.Round(Math.Clamp(audio[index] * gain, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)),
                value);
        }
        host.Audio.TrySubmit(new PcmAudioFrame(
            host.PluginId,
            metadata.StreamId,
            Interlocked.Increment(ref audioSequence),
            AcarsReceiver.DemodulationSampleRateHz,
            1,
            PcmSampleFormat.Signed16LittleEndian,
            pcm,
            isDiscontinuous));
    }

    internal static int MixSquelchedChannelAudio(
        (Channel Channel, AcarsReceiver Receiver)[] targets,
        float[][] channelAudio,
        int audioSampleCount)
    {
        Span<float> mixed = channelAudio[0].AsSpan(0, audioSampleCount);
        int openCount = !targets[0].Receiver.IsSquelchEnabled ||
            targets[0].Receiver.IsMskSquelchOpen ? 1 : 0;
        for (int channelIndex = 1; channelIndex < targets.Length; channelIndex++)
        {
            if (!targets[channelIndex].Receiver.IsSquelchEnabled ||
                targets[channelIndex].Receiver.IsMskSquelchOpen) openCount++;
            ReadOnlySpan<float> source = channelAudio[channelIndex].AsSpan(0, audioSampleCount);
            VectorAdd(mixed, source);
        }
        return openCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VectorAdd(Span<float> target, ReadOnlySpan<float> source)
    {
        int index = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && target.Length >= System.Numerics.Vector<float>.Count)
        {
            int vectorEnd = target.Length - (target.Length % System.Numerics.Vector<float>.Count);
            for (; index < vectorEnd; index += System.Numerics.Vector<float>.Count)
            {
                System.Numerics.Vector<float> vt = new(target.Slice(index, System.Numerics.Vector<float>.Count));
                System.Numerics.Vector<float> vs = new(source.Slice(index, System.Numerics.Vector<float>.Count));
                (vt + vs).CopyTo(target.Slice(index, System.Numerics.Vector<float>.Count));
            }
        }
        for (; index < target.Length; index++)
        {
            target[index] += source[index];
        }
    }

    private void UpdateDiagnostics(IqBlockMetadata metadata, AcarsReceiver primaryReceiver,
        int monitoredChannelCount, int openAudioChannelCount)
    {
        long now = Environment.TickCount64;
        if (lastDiagnosticsUpdateMilliseconds != 0 && now - lastDiagnosticsUpdateMilliseconds < 250) return;
        lastDiagnosticsUpdateMilliseconds = now;
        AcarsReceiver.DiagnosticsSnapshot snapshot = primaryReceiver.GetDiagnostics();
        long targetFrequencyHz = SelectedChannel().FrequencyHz;
        (long valid, long rejected) = FrameCounts();
        float? signalLevelDbm = host?.ReceiverTelemetry?.SignalLevelDbm;
        host?.Dispatcher.Post(() =>
            viewModel.UpdateDiagnostics(snapshot, metadata.CenterFrequencyHz, targetFrequencyHz,
                monitoredChannelCount, openAudioChannelCount, valid, rejected, signalLevelDbm));
    }

    private void Publish(AcarsFrame frame, AcarsMessage message)
    {
        var reception = new AcarsReception(
            frame.ReceivedAt.ToLocalTime(), frame.FrequencyHz, message.AircraftRegistration,
            message.Label, message.BlockId, message.Text, frame.SignalQuality,
            message.Mode, message.Acknowledgement, message.IsBlockCheckValid,
            message.HasValidOddParity, message.IsContinuationBlock,
            settings.SaveRawFrames ? Convert.ToHexString(frame.Bytes) : string.Empty,
            frame.StreamId, frame.SamplePosition);

        AcarsInterpretation interpretation = messageInterpretation.Interpret(message);
        if (interpretation.RequiresReviewLog && settings.SaveUninterpretedMessages)
        {
            // Uninterpreted records are a low-rate diagnostic side channel, but
            // they must not make the demodulation callback wait on disk I/O.
            _ = Task.Run(() => SaveUninterpretedMessage(frame, message));
        }

        lock (gate)
        {
            history.Add(reception);
            if (history.Count > settings.MaximumHistory)
                history.RemoveRange(0, history.Count - settings.MaximumHistory);
            AppendHistory(reception);
        }
        (long valid, long rejected) = FrameCounts();
        host?.Dispatcher.Post(() => viewModel.Add(reception, valid, rejected));

        if (settings.BuzzerEnabled)
        {
            host?.Notifications.PlayReceptionAlarm(TimeSpan.FromMilliseconds(500));
        }

        ResultPublished?.Invoke(this, new(new(
            $"{frame.StreamId:N}-{frame.SamplePosition}",
            Descriptor.Id,
            frame.ReceivedAt,
            frame.StreamId,
            "acars.message",
            PluginResultSeverity.Information,
            message.AircraftRegistration,
            message.Summary,
            frame.FrequencyHz,
            frame.SignalQuality,
            1,
            JsonSerializer.Serialize(message))));
    }

    private void SaveUninterpretedMessage(AcarsFrame frame, AcarsMessage message)
    {
        try
        {
            string logPath = settings.UninterpretedLogFilePath;
            if (!Path.IsPathRooted(logPath))
            {
                string baseDir = host?.Settings.DataDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
                logPath = Path.Combine(baseDir, "logs", logPath);
            }
            string? dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 80));
            sb.AppendLine($"[{frame.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}] Freq: {frame.FrequencyHz / 1_000_000.0:F3} MHz | Reg: {message.AircraftRegistration} | Label: {message.Label} | Block ID: {message.BlockId} | Quality: {frame.SignalQuality * 100:F1}%");
            sb.AppendLine("Text:");
            sb.AppendLine(message.Text);

            lock (uninterpretedFileGate)
            {
                File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "acars.uninterpreted.save-failed",
                "Could not save uninterpreted ACARS message to log file.", exception);
        }
    }

    private Channel SelectedChannel() => Channels.First(item => item.Id == selectedProfileId);
    private Channel[] MonitoredChannels() => Channels
        .Where(channel => settings.MonitoredChannelIds.Contains(channel.Id, StringComparer.Ordinal))
        .OrderBy(channel => channel.Id == selectedProfileId ? 0 : 1)
        .ThenBy(channel => channel.FrequencyHz)
        .ToArray();

    private void ConfigureReceivers()
    {
        lock (receiverGate) ConfigureReceiversLocked(settings.MonitoredChannelIds);
    }

    private void ConfigureReceiversLocked(IReadOnlyList<string> monitoredChannelIds)
    {
        var required = new HashSet<string>(monitoredChannelIds, StringComparer.Ordinal);
        foreach (string id in receivers.Keys.Where(id => !required.Contains(id)).ToArray())
            receivers.Remove(id);
        foreach (string id in required)
        {
            if (!receivers.TryGetValue(id, out AcarsReceiver? receiver))
            {
                receiver = new AcarsReceiver();
                receivers.Add(id, receiver);
            }
            receiver.IsSquelchEnabled = settings.SquelchEnabled;
        }
    }

    private AcarsReceiver[] ReceiverSnapshot()
    {
        lock (receiverGate) return receivers.Values.ToArray();
    }

    private (Channel Primary, (Channel Channel, AcarsReceiver Receiver)[] Targets)
        ReceiverProcessingPlan()
    {
        lock (receiverGate)
        {
            Channel primary = SelectedChannel();
            return (primary, MonitoredChannels().Select(channel =>
                (channel, receivers[channel.Id])).ToArray());
        }
    }

    private (long Valid, long Rejected) FrameCounts()
    {
        AcarsReceiver[] snapshot = ReceiverSnapshot();
        return (snapshot.Sum(receiver => receiver.ValidFrameCount),
            snapshot.Sum(receiver => receiver.RejectedFrameCount));
    }
    private bool TrySelectChannelFromView(string profileId)
    {
        Channel channel = Channels.First(item => item.Id == profileId);
        if (State == PluginLifecycleState.Streaming && host is not null &&
            !IsInPassband(channel, host.Tuning.Current))
        {
            SetStatus($"{channel.FrequencyHz / 1_000_000.0:F3} MHzは現在のIQ通過帯域外です");
            return false;
        }
        lock (receiverGate)
        {
            selectedProfileId = profileId;
            string[] monitored = settings.MonitoredChannelIds.Contains(profileId, StringComparer.Ordinal)
                ? settings.MonitoredChannelIds : [.. settings.MonitoredChannelIds, profileId];
            settings = (settings with
            {
                SelectedChannelId = profileId,
                MonitoredChannelIds = monitored
            }).Normalize();
            ConfigureReceiversLocked(settings.MonitoredChannelIds);
            if (State == PluginLifecycleState.Streaming)
                Interlocked.Exchange(ref audioDiscontinuityPending, 1);
        }
        PersistSettings();
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        SetStatus(State == PluginLifecycleState.Streaming
            ? $"受信中 / {settings.MonitoredChannelIds.Length} ch"
            : State == PluginLifecycleState.Active
            ? $"待機中 / {settings.MonitoredChannelIds.Length} ch（再起動で反映）"
            : "設定済み");
        return true;
    }

    private static bool IsInPassband(Channel channel, PluginTuningResult tuning) =>
        channel.FrequencyHz - 4_000 >= tuning.PassbandLowerFrequencyHz &&
        channel.FrequencyHz + 4_000 <= tuning.PassbandUpperFrequencyHz;
    private bool TrySetMonitoredChannelsFromView(IReadOnlyList<string> channelIds)
    {
        AcarsSettings proposedSettings = (settings with
        {
            MonitoredChannelIds = channelIds.ToArray()
        }).Normalize();
        if (State == PluginLifecycleState.Streaming && host is not null)
        {
            PluginTuningResult tuning = host.Tuning.Current;
            Channel? outside = Channels.FirstOrDefault(channel =>
                proposedSettings.MonitoredChannelIds.Contains(channel.Id, StringComparer.Ordinal) &&
                !settings.MonitoredChannelIds.Contains(channel.Id, StringComparer.Ordinal) &&
                !IsInPassband(channel, tuning));
            if (outside is not null)
            {
                SetStatus($"{outside.FrequencyHz / 1_000_000.0:F3} MHzは現在のIQ通過帯域外です");
                return false;
            }
        }
        lock (receiverGate)
        {
            settings = proposedSettings;
            ConfigureReceiversLocked(settings.MonitoredChannelIds);
        }
        if (State == PluginLifecycleState.Streaming && host is not null)
        {
            Channel primaryChannel = SelectedChannel();
            Channel[] monitoredChannels = MonitoredChannels();
            _ = RequestTuningAsync(host, primaryChannel, monitoredChannels, CancellationToken.None);
        }
        PersistSettings();
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        SetStatus(State == PluginLifecycleState.Streaming
            ? $"受信中 / {settings.MonitoredChannelIds.Length} ch"
            : State == PluginLifecycleState.Active
            ? $"待機中 / {settings.MonitoredChannelIds.Length} ch（再起動で反映）"
            : $"設定済み / {settings.MonitoredChannelIds.Length} ch");
        return true;
    }

    private void SetStatus(string value)
    {
        IPluginHostContext? context = host;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
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
        Channel selectedChannel = SelectedChannel();
        Channel[] monitoredChannels = MonitoredChannels();
        IqBlockMetadata? metadata = lastCaptureMetadata;
        AcarsReceiver.DiagnosticsSnapshot diagnostics = ReceiverSnapshot().FirstOrDefault()?.GetDiagnostics() ?? default;
        viewModel.CaptureStatus = "IQ録音: 直前3秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(context, selectedChannel, monitoredChannels, metadata, diagnostics));
    }

    private void SavePretriggerCapture(IPluginHostContext context, Channel selectedChannel,
        Channel[] monitoredChannels, IqBlockMetadata? metadata, AcarsReceiver.DiagnosticsSnapshot diagnostics)
    {
        try
        {
            PackedIqHistorySnapshot snapshot = pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"acars-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{selectedChannel.FrequencyHz}");
            string path = $"{basePath}.wav";
            using (var capture = new AcarsIqCapture(path, snapshot.SampleRateHz, TimeSpan.FromSeconds(3)))
            {
                capture.WritePcm(snapshot.RawInterleaved);
            }
            var document = new
            {
                Format = "SRdeck ACARS analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = "3-second rolling pre-trigger",
                SelectedChannel = selectedChannel,
                MonitoredChannels = monitoredChannels,
                RawIqFile = Path.GetFileName(path),
                SampleRateHz = snapshot.SampleRateHz,
                DurationSeconds = snapshot.DurationSeconds,
                InputMetadata = metadata,
                ReceiverDiagnostics = diagnostics
            };
            string diagnosticsPath = $"{basePath}-diagnostics.json";
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                }));
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存済み: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "acars.iq-capture.save-failed",
                "Could not save ACARS rolling IQ capture.", exception);
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}");
        }
        finally { Interlocked.Exchange(ref captureSaveInProgress, 0); }
    }

    private void PersistSettings()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            AcarsSettings settingsToPersist = settings with
            {
                MapState = GeoMapStateStore.GetState(Descriptor.Id)
            };
            context.Settings.SaveAsync(new(1, JsonSerializer.Serialize(settingsToPersist)))
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "acars.settings.save-failed",
                "ACARS settings could not be saved.", exception);
        }
    }

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz");
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
