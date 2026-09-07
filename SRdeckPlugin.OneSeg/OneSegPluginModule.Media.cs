using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Dsp;
using SRdeckPlugin.OneSeg.Media;
using SRdeckPlugin.OneSeg.Models;
using SRdeckPlugin.OneSeg.Transport;
using SRdeckPlugin.OneSeg.ViewModels;
using SRdeckPlugin.OneSeg.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;


namespace SRdeckPlugin.OneSeg;

public sealed partial class OneSegPluginModule
{
    private async Task InitializeMediaPlayerAsync(IPluginHostContext context)
    {
        OneSegMediaPlayer? created = null;
        try
        {
            created = await Task.Run(
                () => new OneSegMediaPlayer(),
                mediaPlayerInitializationCancellation.Token).ConfigureAwait(false);

            lock (processingGate)
            {
                if (IsDisposed)
                {
                    created.Dispose();
                    return;
                }

                mediaPlayer = created;
                if (State == PluginLifecycleState.Streaming) created.Start(settings.Volume);
            }
            Dispatch(() => viewModel.MediaPlayer = created.Player);
        }
        catch (OperationCanceledException) when (mediaPlayerInitializationCancellation.IsCancellationRequested)
        {
            created?.Dispose();
        }
        catch (Exception exception)
        {
            lock (processingGate)
            {
                if (ReferenceEquals(mediaPlayer, created)) mediaPlayer = null;
            }
            created?.Dispose();
            context.Logger.Log(PluginLogLevel.Warning, "oneseg.libvlc.initialize",
                "LibVLC could not be initialized; TS decoding and recording remain available.", exception);
        }
    }

    private void OnTransportPacket(byte[] packet)
    {
        analyzer.Process(packet);
        if (Volatile.Read(ref channelScanInProgress) != 0) return;
        mediaPlayer?.Submit(packet);
        WriteRecordingPacket(packet);
    }

    private void WriteRecordingPacket(byte[] packet)
    {
        lock (processingGate)
        {
            if (recording is null) return;
            bool segmentByTime = settings.RecordingSegmentMinutes > 0 &&
                DateTimeOffset.UtcNow - recordingOpenedAt >= TimeSpan.FromMinutes(settings.RecordingSegmentMinutes);
            bool segmentBySize = settings.RecordingMaxBytes > 0 && recording.Length > 0 &&
                recording.Length + packet.Length > settings.RecordingMaxBytes;
            if (segmentByTime || segmentBySize)
            {
                CloseRecording("segment");
                recordingSegmentIndex++;
                OpenRecording();
            }
            if (settings.RecordingStorageLimitBytes > 0 && recording is not null &&
                recording.Length + packet.Length > settings.RecordingStorageLimitBytes)
            {
                CloseRecording("capacity");
                Dispatch(() => viewModel.RecordingStatus = "TS保存停止: 保存先容量上限に到達");
                return;
            }
            try
            {
                recording?.Write(packet);
                recordingPacketCount++;
            }
            catch (IOException exception)
            {
                host?.Logger.Log(PluginLogLevel.Warning, "oneseg.recording.write", "Could not write the OneSeg TS recording.", exception);
                CloseRecording("io-error");
                Dispatch(() => viewModel.RecordingStatus = $"TS保存失敗: {exception.Message}");
            }
        }
    }

    private string recordingEndReason = "normal";
    private long recordingPacketCount;

    private void CloseRecording(string reason = "normal")
    {
        if (recording is null) return;
        recordingEndReason = reason;
        try { recording.Flush(true); } catch (IOException) { recordingEndReason = "io-error"; }
        recording.Dispose();
        recording = null;
        Dispatch(() => viewModel.SetRecordingState(false));
        WriteRecordingMetadata(DateTimeOffset.UtcNow);
        recordingPath = null;
        recordingMetadataPath = null;
    }

    private void WriteRecordingMetadata(DateTimeOffset? endedAt)
    {
        if (!settings.RecordingSidecarEnabled || recordingMetadataPath is null) return;
        try
        {
            var metadata = new
            {
                Schema = "SRdeck OneSeg transport stream recording v1",
                StartedAtUtc = recordingOpenedAt,
                EndedAtUtc = endedAt,
                RawTsFile = recordingPath is null ? null : Path.GetFileName(recordingPath),
                settings.PrefectureId,
                settings.Channel,
                SelectedStation = viewModel.SelectedStation?.DisplayName,
                settings.LastServiceId,
                ServiceName = viewModel.SelectedStation?.ServiceName,
                AudioTrack = viewModel.SelectedAudioTrack?.DisplayName,
                CaptionsEnabled = settings.CaptionsEnabled,
                TsPacketCount = recordingPacketCount,
                EndReason = endedAt is null ? "in-progress" : recordingEndReason
            };
            string temporaryPath = recordingMetadataPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, recordingMetadataPath, true);
        }
        catch (IOException exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "oneseg.recording.metadata", "Could not write OneSeg recording metadata.", exception);
        }
    }

    private void EnforceRecordingStorageLimit(string directory)
    {
        if (settings.RecordingStorageLimitBytes <= 0) return;
        try
        {
            var files = Directory.EnumerateFiles(directory, "oneseg-*.ts")
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            long total = files.Sum(file => file.Exists ? file.Length : 0) +
                files.Sum(file =>
                {
                    string sidecar = Path.ChangeExtension(file.FullName, ".json");
                    return File.Exists(sidecar) ? new FileInfo(sidecar).Length : 0;
                });
            foreach (FileInfo file in files)
            {
                if (total <= settings.RecordingStorageLimitBytes) break;
                if (recordingPath is not null && string.Equals(file.FullName, recordingPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    total -= file.Length;
                    file.Delete();
                    File.Delete(Path.ChangeExtension(file.FullName, ".json"));
                }
                catch (IOException) { }
            }
        }
        catch (IOException exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "oneseg.recording.storage", "Could not enforce OneSeg recording storage limit.", exception);
        }
    }

    private byte? ResolveSelectedAudioComponentType(DateTimeOffset now)
    {
        byte? streamComponentType = null;
        if (settings.LastServiceId is ushort serviceId &&
            streamAudioComponentTypes.TryGetValue(serviceId, out byte selectedComponentType))
            streamComponentType = selectedComponentType;
        else if (settings.LastServiceId is null)
            streamComponentType = streamAudioComponentTypes.OrderBy(value => value.Key)
                .Select(value => (byte?)value.Value).FirstOrDefault();
        return streamComponentType ?? ResolveCurrentAudioComponentType(
            audioEvents.Values, settings.LastServiceId, now);
    }

    internal static byte? ResolveCurrentAudioComponentType(
        IEnumerable<OneSegEventInfo> events, ushort? serviceId, DateTimeOffset now)
    {
        OneSegEventInfo[] candidates = events
            .Where(value => !serviceId.HasValue || value.ServiceId == serviceId.Value)
            .ToArray();
        return candidates
            .Where(value => value.IsActiveAt(now) && value.AudioComponentType is not null)
            .OrderByDescending(value => value.StartTime)
            .ThenBy(value => value.ServiceId)
            .Concat(candidates
                .Where(value => value.IsPresentEvent && value.AudioComponentType is not null)
                .OrderByDescending(value => value.StartTime))
            .Select(value => value.AudioComponentType)
            .FirstOrDefault();
    }

    private void SetRecording(bool enabled)
    {
        lock (processingGate)
        {
            if (State == PluginLifecycleState.Streaming && enabled && recording is null) OpenRecording();
            else if (!enabled) { CloseRecording("user-stop"); Dispatch(() => viewModel.RecordingStatus = "TS保存停止"); }
        }
        PersistSettings();
    }

    private void OpenRecording()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            string directory = settings.RecordingDirectory ?? Path.Combine(context.Settings.DataDirectory, "recordings");
            Directory.CreateDirectory(directory);
            string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory;
            if (new DriveInfo(root).AvailableFreeSpace < 64L * 1024 * 1024)
                throw new IOException("録画先の空き容量が安全しきい値未満です。");
            EnforceRecordingStorageLimit(directory);
            string suffix = recordingSegmentIndex == 0 ? "" : $"-{recordingSegmentIndex:D3}";
            string stationPart = SanitizeFilePart(viewModel.SelectedStation?.ServiceName ?? viewModel.SelectedStation?.DisplayName ?? "station");
            string path = Path.Combine(directory, $"oneseg-ch{settings.Channel}-{stationPart}-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}.ts");
            while (File.Exists(path))
            {
                suffix = $"-{recordingSegmentIndex:D3}-{Guid.NewGuid():N}";
                path = Path.Combine(directory, $"oneseg-ch{settings.Channel}-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}.ts");
            }
            CloseRecording();
            recording = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            Dispatch(() => viewModel.SetRecordingState(true));
            recordingPath = path;
            recordingMetadataPath = Path.ChangeExtension(path, ".json");
            recordingOpenedAt = DateTimeOffset.UtcNow;
            recordingPacketCount = 0;
            recordingEndReason = "normal";
            WriteRecordingMetadata(null);
            Dispatch(() => viewModel.RecordingStatus = $"TS保存中: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "oneseg.recording.open", "Could not open the OneSeg TS recording.", exception);
            Dispatch(() => { viewModel.RecordingStatus = $"TS保存失敗: {exception.Message}"; viewModel.Recording = false; });
        }
    }

    private static string SanitizeFilePart(string value)
    {
        string sanitized = string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(sanitized) ? "station" : sanitized[..Math.Min(32, sanitized.Length)];
    }

    private void StartIqCapture()
    {
        if (State != PluginLifecycleState.Streaming || host is null)
        {
            viewModel.CaptureStatus = "IQ保存: 受信中に実行してください";
            return;
        }
        if (Interlocked.CompareExchange(ref captureSaveInProgress, 1, 0) != 0)
        {
            viewModel.CaptureStatus = "IQ保存: 保存処理中です";
            return;
        }

        IPluginHostContext context = host;
        OneSegChannel channel;
        OneSegPrefecture prefecture;
        OneSegDiagnostics diagnostics;
        IqBlockMetadata? metadata;
        lock (processingGate)
        {
            channel = SelectedChannel();
            prefecture = SelectedPrefecture();
            diagnostics = receiver.Diagnostics;
            metadata = lastCaptureMetadata;
        }
        viewModel.CaptureStatus = "IQ保存: 直前5秒を保存中…";
        _ = Task.Run(() => SaveIqCapture(context, prefecture, channel, diagnostics, metadata));
    }

    private void SaveIqCapture(IPluginHostContext context, OneSegPrefecture prefecture, OneSegChannel channel,
        OneSegDiagnostics diagnostics, IqBlockMetadata? metadata)
    {
        try
        {
            PackedIqHistorySnapshot snapshot = iqCaptureBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = settings.IqCaptureDirectory ?? Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"oneseg-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-ch{channel.Number}");
            string wavPath = $"{basePath}.wav";
            TimeSpan captureDuration = TimeSpan.FromSeconds(Math.Clamp(settings.IqCaptureSeconds, 1, 60));
            using (var capture = new OneSegIqCapture(wavPath, snapshot.SampleRateHz, captureDuration))
                capture.WritePcm(snapshot.RawInterleaved);

            var document = new
            {
                Format = "SRdeck OneSeg analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = $"{captureDuration.TotalSeconds:0}-second rolling pre-trigger / channelized IQ",
                Prefecture = prefecture.DisplayName,
                prefecture.Id,
                Channel = channel.Number,
                CenterFrequencyHz = channel.CenterFrequencyHz,
                RawIqFile = Path.GetFileName(wavPath),
                snapshot.SampleRateHz,
                snapshot.DurationSeconds,
                InputMetadata = metadata,
                ReceiverDiagnostics = diagnostics
            };
            string metadataPath = $"{basePath}-metadata.json";
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ保存済み: {wavPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "oneseg.iq-capture.save-failed", "Could not save OneSeg IQ capture.", exception);
            context.Dispatcher.Post(() => viewModel.CaptureStatus = $"IQ保存失敗: {exception.Message}");
        }
        finally { Interlocked.Exchange(ref captureSaveInProgress, 0); }
    }
}
