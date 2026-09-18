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
            using (var capture = new BoundedIqWavWriter(wavPath, snapshot.SampleRateHz, captureDuration))
                capture.WritePcm(snapshot.RawInterleaved);

            var document = new
            {
                Format = "SRdeck OneSeg analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = $"{captureDuration.TotalSeconds:0}-second rolling pre-trigger / channelized IQ",
                Prefecture = prefecture.DisplayName,
                Region = SelectedRegion().DisplayName,
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
