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

public sealed partial class HfdlPluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(PluginExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<HfdlReception> persisted = host is null
            ? []
            : PluginJsonLinesHistory.LoadAll<HfdlReception>(GetHistoryPath(host));
        HfdlReception[] records;
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
                var csv = new StringBuilder("receivedAt,frequencyHz,kind,flightId,source,destination,payload,modulation,signalQuality,type,isCrcValid,streamId,samplePosition,rawFrameHex,channelId,groundStationId\r\n");
                foreach (HfdlReception item in records)
                    csv.Append(Csv(item.ReceivedAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                        .Append(item.FrequencyHz).Append(',')
                        .Append(Csv(item.Kind)).Append(',')
                        .Append(Csv(item.FlightId)).Append(',')
                        .Append(Csv(item.SourceAddress)).Append(',')
                        .Append(Csv(item.DestinationAddress)).Append(',')
                        .Append(Csv(item.PayloadHex)).Append(',')
                        .Append(item.Modulation).Append(',')
                        .Append(item.SignalQuality.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.Type.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(item.IsCrcValid ? "true" : "false").Append(',')
                        .Append(item.StreamId.ToString("D")).Append(',')
                        .Append(item.SamplePosition.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(Csv(item.RawFrameHex)).Append(',')
                        .Append(Csv(item.ChannelId)).Append(',')
                        .Append(Csv(item.GroundStationId)).Append("\r\n");
                await File.WriteAllTextAsync(request.DestinationPath, csv.ToString(),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else return new(false, 0, $"Unknown HFDL export format '{request.FormatId}'.");
            return new(true, records.Length, $"Exported {records.Length} HFDL messages.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            host?.Logger.Log(PluginLogLevel.Error, "hfdl.export.failed", "HFDL export failed.", exception);
            return new(false, 0, exception.Message);
        }
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (processingGate) receiver.Reset();
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
        guidanceTimer?.Dispose();
        guidanceTimer = null;
        if (host is not null)
        {
            host.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
            PersistSettings();
        }
        host = null;
    }

    private void Publish(HfdlFrame frame, HfdlMessage message)
    {
        string source = message.SourceAddress is int s ? $"{s:X6}" : string.Empty;
        string destination = message.DestinationAddress is int d ? $"{d:X6}" : string.Empty;
        var reception = new HfdlReception(frame.ReceivedAt.ToLocalTime(), frame.FrequencyHz, message.Kind, message.FlightId,
            source, destination, Convert.ToHexString(message.Payload), frame.Modulation, frame.SignalQuality,
            message.Type, message.IsCrcValid, frame.StreamId, frame.SamplePosition,
            Convert.ToHexString(frame.Bytes), selectedProfileId,
            SelectedChannel().GroundStationId.ToString(CultureInfo.InvariantCulture));
        lock (gate)
        {
            history.Add(reception);
            if (history.Count > settings.MaximumHistory)
                history.RemoveRange(0, history.Count - settings.MaximumHistory);
            AppendHistory(reception);
        }
        host?.Notifications.PlayReceptionAlarm(TimeSpan.FromMilliseconds(500));
        host?.Dispatcher.Post(() => viewModel.Add(reception, receiver.ValidFrameCount, receiver.RejectedFrameCount));
        ResultPublished?.Invoke(this, new(new(
            $"{frame.StreamId:N}-{frame.SamplePosition}",
            Descriptor.Id,
            frame.ReceivedAt,
            frame.StreamId,
            "hfdl.lpdu",
            PluginResultSeverity.Information,
            string.IsNullOrEmpty(message.FlightId) ? message.Kind : message.FlightId,
            message.Summary,
            frame.FrequencyHz,
            frame.SignalQuality,
            1,
            JsonSerializer.Serialize(message))));
    }

    private Channel SelectedChannel() => Channels.First(item => item.Id == selectedProfileId);

    public static GroundStation StationFor(Channel channel) =>
        GroundStations.First(station => station.Id == channel.GroundStationId);

    public static string NormalizeChannelId(string? channelId) => channelId switch
    {
        "tokyo-6559" => "gs01-6559",
        "tokyo-10066" => DefaultChannelId,
        "tokyo-11384" => "gs07-11384",
        "tokyo-17916" => "gs05-17916",
        "tokyo-21937" => "gs02-21937",
        _ when channelId is not null && Channels.Any(channel => channel.Id == channelId) => channelId,
        _ => DefaultChannelId
    };

    public static DateTimeOffset GetStationSolarTime(GroundStation station, DateTimeOffset utcNow) =>
        utcNow.ToUniversalTime().AddHours(station.Longitude / 15.0);

    public static IReadOnlyList<Channel> RecommendedChannels(GroundStation station, DateTimeOffset utcNow,
        int count = 3)
    {
        double localHour = GetStationSolarTime(station, utcNow).TimeOfDay.TotalHours;
        double targetMhz = localHour switch
        {
            < 5 => 4.5,
            < 7 => 6.5,
            < 9 => 10,
            < 15 => 16,
            < 17 => 13,
            < 19 => 9,
            < 22 => 6,
            _ => 4.5
        };
        return Channels.Where(channel => channel.GroundStationId == station.Id)
            .OrderBy(channel => Math.Abs(channel.FrequencyHz / 1_000_000.0 - targetMhz))
            .Take(Math.Clamp(count, 1, station.FrequenciesKHz.Count)).ToArray();
    }

    public static string FrequencyTimeGuidance(long frequencyHz) => frequencyHz switch
    {
        < 5_000_000 => "夜間・深夜向け（現地太陽時 21～05時の目安）",
        < 7_000_000 => "夜間～朝夕向け（現地太陽時 18～08時の目安）",
        < 10_000_000 => "朝夕・夜間向け（現地太陽時 16～10時の目安）",
        < 13_000_000 => "朝夕中心（現地太陽時 06～10時／15～20時の目安）",
        < 17_000_000 => "昼間向け（現地太陽時 08～17時の目安）",
        < 20_000_000 => "昼間・長距離向け（現地太陽時 09～16時の目安）",
        _ => "日中・高い太陽活動時向け（現地太陽時 10～15時の目安）"
    };

    private static ValueTask<PluginTuningResult> RequestTuningAsync(
        IPluginHostContext context, Channel channel, CancellationToken cancellationToken)
    {
        long signalCenterHz = channel.FrequencyHz + SignalOffsetHz;
        return context.Tuning.RequestAsync(new(channel.Id, channel.Name,
            [new(signalCenterHz, 2_800)], signalCenterHz, HfdlReceiver.MinimumSampleRateHz,
            100, true, false, PluginGainPreference.Automatic), cancellationToken);
    }

    private async ValueTask<PluginTuningResult> RequestTuningForCurrentChannelAsync(
        IPluginHostContext context, Channel channel, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref tuningRequestInProgress);
        try { return await RequestTuningAsync(context, channel, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref tuningRequestInProgress); }
    }

    private void SubmitMonitorAudio(ReadOnlySpan<float> audio, IqBlockMetadata metadata, bool discontinuous)
    {
        if (audio.IsEmpty || host is null || !settings.MonitorAudioEnabled) return;
        const float volume = 1f;
        byte[] pcm = new byte[audio.Length * sizeof(short)];
        for (int index = 0; index < audio.Length; index++)
        {
            short value = (short)MathF.Round(Math.Clamp(audio[index] * volume, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        host.Audio.TrySubmit(new PcmAudioFrame(
            host.PluginId, metadata.StreamId, Interlocked.Increment(ref audioSequence),
            HfdlReceiver.MonitorAudioSampleRateHz, 1,
            PcmSampleFormat.Signed16LittleEndian, pcm, discontinuous));
    }

    private void UpdateDiagnostics(IqBlockMetadata metadata)
    {
        long now = Environment.TickCount64;
        if (lastDiagnosticsUpdateMilliseconds != 0 && now - lastDiagnosticsUpdateMilliseconds < 250) return;
        lastDiagnosticsUpdateMilliseconds = now;
        HfdlReceiver.DiagnosticsSnapshot snapshot = receiver.GetDiagnostics();
        Channel channel = SelectedChannel();
        float? signalLevelDbm = host?.ReceiverTelemetry?.SignalLevelDbm;
        void Update() => viewModel.UpdateDiagnostics(snapshot, channel, metadata.CenterFrequencyHz, signalLevelDbm);
        if (host is null || host.Dispatcher.CheckAccess()) Update();
        else host.Dispatcher.Post(Update);
    }

    private bool TrySelectChannelFromView(string profileId)
    {
        try
        {
            SelectProfileAsync(profileId, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "hfdl.channel-change.failed",
                "HFDL channel change failed.", exception);
            SetStatus($"チャネル切替失敗 / {exception.Message}");
            return false;
        }
    }

    private void SynchronizeViewSettings()
    {
        void Synchronize() => viewModel.SynchronizeSettings(selectedProfileId,
            settings.MaximumHistory, settings.MonitorAudioEnabled, settings.MonitorAudioVolume,
            settings.SaveRawFrames,
            settings.SplitHistoryByChannel, settings.MaximumAircraft, settings.RetentionMinutes,
            settings.MaximumTrailPoints);
        if (host is null || host.Dispatcher.CheckAccess()) Synchronize();
        else host.Dispatcher.Post(Synchronize);
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
        IqBlockMetadata? metadata = lastCaptureMetadata;
        HfdlReceiver.DiagnosticsSnapshot diagnostics = receiver.GetDiagnostics();
        viewModel.CaptureStatus = "IQ録音: 直前3秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(context, selectedChannel, metadata, diagnostics));
    }

    private void SavePretriggerCapture(IPluginHostContext context, Channel selectedChannel,
        IqBlockMetadata? metadata, HfdlReceiver.DiagnosticsSnapshot diagnostics)
    {
        try
        {
            PackedIqHistorySnapshot snapshot = pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"hfdl-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{selectedChannel.FrequencyHz}");
            string path = $"{basePath}.wav";
            using (var capture = new HfdlIqCapture(path, snapshot.SampleRateHz, TimeSpan.FromSeconds(3)))
            {
                capture.WritePcm(snapshot.RawInterleaved);
            }
            var document = new
            {
                Format = "SRdeck HFDL analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = "3-second rolling pre-trigger",
                SelectedChannel = selectedChannel,
                GroundStation = StationFor(selectedChannel),
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
            context.Logger.Log(PluginLogLevel.Warning, "hfdl.iq-capture.save-failed",
                "Could not save HFDL rolling IQ capture.", exception);
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}");
        }
        finally { Interlocked.Exchange(ref captureSaveInProgress, 0); }
    }

    private void SetStatus(string value)
    {
        IPluginHostContext? context = host;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
    }
}
