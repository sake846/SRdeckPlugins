using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Dsp;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.ViewModels;
using SRdeckPlugin.Ft8.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ft8;

public sealed partial class Ft8PluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(PluginExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<Ft8Reception> persisted = host is null
            ? []
            : PluginJsonLinesHistory.LoadAll<Ft8Reception>(GetHistoryPath(host));
        Ft8Reception[] records;
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
                records = history.Where(item =>
                        (request.From is null || item.ReceivedAt >= request.From) &&
                        (request.To is null || item.ReceivedAt <= request.To))
                    .ToArray();
        }
        if (records.Length == 0) return new(false, 0, "エクスポート対象のFT8受信履歴がありません。");
        try
        {
            string output = request.FormatId switch
            {
                "json" => JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
                "csv" => BuildCsv(records),
                _ => throw new ArgumentException($"Unknown export format '{request.FormatId}'.", nameof(request))
            };
            await File.WriteAllTextAsync(request.DestinationPath, output, new UTF8Encoding(true), cancellationToken)
                .ConfigureAwait(false);
            return new(true, records.Length, $"{records.Length:N0}件を保存しました。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Error, "ft8.export.failed", "FT8 export failed.", exception);
            return new(false, 0, "FT8受信履歴を保存できませんでした。");
        }
    }

    protected override async ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (historyWriter is not null)
        {
            await historyWriter.DisposeAsync().ConfigureAwait(false);
            historyWriter = null;
        }
        diagnosticsTimer?.Dispose();
        diagnosticsTimer = null;
        receiver.MessagesDecoded -= OnMessagesDecoded;
        foreach (Ft8Receiver additionalReceiver in additionalReceivers.Values)
            additionalReceiver.MessagesDecoded -= OnMessagesDecoded;
        if (host is not null) host.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
        try { await DrainAllReceiversAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "ft8.decoder.dispose",
                "FT8 decoder worker ended with an error.", exception);
        }
        lock (processingGate) ResetAllReceivers();
        host = null;
    }

    private void SubmitMonitorAudio(ReadOnlySpan<float> audio, IqBlockMetadata metadata)
    {
        if (audio.IsEmpty || host is null || !settings.MonitorAudioEnabled) return;
        const float volume = 1f;
        byte[] pcm = new byte[audio.Length * sizeof(short)];
        for (int index = 0; index < audio.Length; index++)
        {
            short value = (short)MathF.Round(
                Math.Clamp(audio[index] * volume, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        host.Audio.TrySubmit(new PcmAudioFrame(
            host.PluginId, metadata.StreamId, Interlocked.Increment(ref audioSequence),
            Ft8Receiver.OutputSampleRateHz, 1, PcmSampleFormat.Signed16LittleEndian,
            pcm, metadata.Discontinuity != IqDiscontinuity.None));
    }

    private void OnMessagesDecoded(object? sender, IReadOnlyList<Ft8Reception> messages)
    {
        Ft8Reception[] batch = messages.ToArray();
        try
        {
            lock (gate)
            {
                history.AddRange(batch);
                if (history.Count > settings.MaximumHistory)
                    history.RemoveRange(0, history.Count - settings.MaximumHistory);
                waterfallHistory.AddRange(batch);
                if (waterfallHistory.Count > MaximumWaterfallAnnotations)
                    waterfallHistory.RemoveRange(0,
                        waterfallHistory.Count - MaximumWaterfallAnnotations);
                foreach (Ft8Reception message in batch) AppendHistory(message);
            }
            // Keep the receiver pane independent from external result subscribers.
            // A subscriber exception must never hide a successfully decoded batch.
            host?.Dispatcher.Post(() => viewModel.AddBatch(batch));
            WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Error, "ft8.results.failed",
                "Failed to update the FT8 receiver pane.", exception);
        }

        foreach (Ft8Reception message in batch)
        {
            try
            {
                Publish(message);
            }
            catch (Exception exception)
            {
                host?.Logger.Log(PluginLogLevel.Warning, "ft8.result-publish.failed",
                    "A decoded FT8 message could not be published to result subscribers.",
                    exception);
            }
        }
    }

    private void Publish(Ft8Reception reception)
    {
        var details = new
        {
            schemaVersion = 1,
            reception.SlotStart,
            reception.AudioFrequencyHz,
            reception.TimeOffsetSeconds,
            reception.SnrDb,
            reception.SyncScore,
            reception.MessageType,
            reception.FromCall,
            reception.ToCall,
            reception.Extra,
            reception.Mode
        };
        ResultPublished?.Invoke(this, new(new(
            $"{reception.StreamId:N}-{reception.SlotStart.UtcTicks}-{Convert.ToHexString(reception.Payload)}",
            Descriptor.Id,
            reception.ReceivedAt,
            reception.StreamId,
            $"{reception.Mode.ToString().ToLowerInvariant()}.message",
            PluginResultSeverity.Information,
            reception.Message,
            $"{reception.SnrDb:+0;-0;0} dB / {reception.AudioFrequencyHz} Hz",
            reception.FrequencyHz,
            reception.SyncScore,
            1,
            JsonSerializer.Serialize(details))));
    }

    internal static WaterfallAnnotationItem CreateWaterfallAnnotation(Ft8Reception reception) =>
        new(
            $"{reception.StreamId:N}-{reception.SlotStart.UtcTicks}-" +
            $"{reception.AudioFrequencyHz}-{reception.TimeOffsetSeconds:F3}-" +
            Convert.ToHexString(reception.Payload),
            reception.SlotStart.AddSeconds(reception.TimeOffsetSeconds) +
            TransmissionDuration(reception.Mode),
            reception.FrequencyHz + OccupiedSignalBandwidth(reception.Mode) / 2,
            "#FFD6D6D6",
            ToolTip: $"{reception.Message}  {reception.SnrDb:+0;-0;0} dB");

    private void UpdateWaterfallReference(IqBlockMetadata metadata)
    {
        double durationSeconds = metadata.SampleRateHz > 0
            ? metadata.SampleCount / (double)metadata.SampleRateHz
            : 0;
        Volatile.Write(ref waterfallReference,
            new WaterfallReference(metadata.StreamId,
                metadata.UtcTimestamp.AddSeconds(durationSeconds)));
    }
}
