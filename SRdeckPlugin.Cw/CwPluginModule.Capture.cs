using System.IO;
using System.Buffers.Binary;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Cw.Dsp;
using SRdeckPlugin.Cw.Models;
using SRdeckPlugin.Cw.ViewModels;
using SRdeckPlugin.Cw.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Cw;

public sealed partial class CwPluginModule
{
    private void StartIqCapture()
    {
        IPluginHostContext? context = HostContext;
        if (State != PluginLifecycleState.Streaming || context is null)
        {
            viewModel.CaptureStatus = "IQ録音: 受信中に開始してください";
            return;
        }
        if (Interlocked.CompareExchange(ref captureSaveInProgress, 1, 0) != 0)
        {
            viewModel.CaptureStatus = "IQ録音: 保存処理中です";
            return;
        }

        IqBlockMetadata? metadata;
        AppliedChannelConfiguration? channelConfiguration;
        lock (processingGate)
        {
            metadata = lastCaptureMetadata;
            channelConfiguration = lastCaptureChannelConfiguration;
        }
        viewModel.CaptureStatus = $"IQ録音: 直前{CaptureDurationSeconds}秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(context, metadata, channelConfiguration));
    }

    private void SavePretriggerCapture(
        IPluginHostContext context,
        IqBlockMetadata? metadata,
        AppliedChannelConfiguration? channelConfiguration)
    {
        try
        {
            PackedIqHistorySnapshot snapshot = pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            long center = Volatile.Read(ref centerFrequencyHz);
            string basePath = Path.Combine(directory,
                $"cw-skimmer-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{center}");
            string path = $"{basePath}.wav";
            using (var capture = new BoundedIqWavWriter(
                       path, snapshot.SampleRateHz,
                       TimeSpan.FromSeconds(CaptureDurationSeconds)))
            {
                capture.WritePcm(snapshot.RawInterleaved);
            }

            var document = new
            {
                Format = "SRdeck CW skimmer capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = $"{CaptureDurationSeconds}-second rolling pre-trigger",
                CaptureInput = "standard-channel-iq",
                CenterFrequencyHz = center,
                RawIqFile = Path.GetFileName(path),
                snapshot.SampleRateHz,
                snapshot.DurationSeconds,
                InputMetadata = metadata,
                ChannelConfiguration = channelConfiguration,
                ReceiverBandwidthHz = settings.ReceiveBandwidthHz
            };
            string diagnosticsPath = $"{basePath}-diagnostics.json";
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions { WriteIndented = true }));
            context.Dispatcher.Post(() =>
                viewModel.CaptureStatus = $"IQ録音保存済み: {path}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "cw.iq-capture.save-failed",
                "Could not save CW skimmer rolling IQ capture.", exception);
            context.Dispatcher.Post(() =>
                viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref captureSaveInProgress, 0);
        }
    }

    internal static IReadOnlyList<WaterfallAnnotationItem> CreateWaterfallAnnotations(
        CwDecodeResult result,
        ref long sequence)
    {
        var annotations = new List<WaterfallAnnotationItem>(result.AppendedText.Length);
        foreach (char character in result.AppendedText)
        {
            if (char.IsWhiteSpace(character)) continue;
            long annotationId = ++sequence;
            annotations.Add(new WaterfallAnnotationItem(
                $"cw-{result.StreamId:N}-{result.TrackId}-{annotationId}",
                result.ReceivedAt,
                result.FrequencyHz,
                WaterfallTextColor,
                character.ToString(),
                $"{character} / {result.FrequencyHz / 1_000.0:F3} kHz / " +
                $"{result.SpeedWpm} WPM / S/N {result.SignalToNoiseDb:F1} dB"));
        }
        return annotations;
    }

    private void UpdateWaterfallReference(IqBlockMetadata metadata)
    {
        double durationSeconds = metadata.SampleRateHz > 0
            ? metadata.SampleCount / (double)metadata.SampleRateHz
            : 0;
        Volatile.Write(ref waterfallReference,
            new WaterfallReference(metadata.StreamId,
                metadata.UtcTimestamp.AddSeconds(durationSeconds)));
    }

    private async Task SaveSettingsAsync(CwSettings value)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        try
        {
            await context.Settings.SaveAsync(new PluginSettingsDocument(
                1, JsonSerializer.Serialize(value))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "cw.settings.save-failed",
                "CW skimmer settings could not be saved.", exception);
        }
    }

    private void SetCenterFrequency(long value)
    {
        Volatile.Write(ref centerFrequencyHz, value);
        HostContext?.Dispatcher.Post(() => viewModel.SetCenterFrequency(value));
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private static long ResolveCenterFrequency(PluginTuningResult current, long configured)
    {
        if (configured > 0) return configured;
        if (current.TargetFrequencyHz > 0) return current.TargetFrequencyHz;
        if (current.CenterFrequencyHz > 0) return current.CenterFrequencyHz;
        return DefaultCenterFrequencyHz;
    }

    private sealed record WaterfallReference(Guid StreamId, DateTimeOffset Time);
    private sealed record CwWaterfallCharacter(
        Guid StreamId,
        WaterfallAnnotationItem Annotation);
}
