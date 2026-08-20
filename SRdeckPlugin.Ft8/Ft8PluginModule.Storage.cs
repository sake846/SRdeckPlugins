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
    private void PersistSettings()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                Ft8Settings settingsToPersist = settings.Normalize() with
                {
                    MapState = GeoMapStateStore.GetState(Descriptor.Id)
                };
                string json = JsonSerializer.Serialize(settingsToPersist);
                await context.Settings.SaveAsync(new(1, json)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                context.Logger.Log(PluginLogLevel.Warning, "ft8.settings.save",
                    "FT8 settings could not be saved.", exception);
            }
        });
    }

    private void LoadHistory()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            string path = GetHistoryPath(context);
            Ft8Reception[] loaded = PluginJsonLinesHistory.LoadAll<Ft8Reception>(path)
                .TakeLast(settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, loaded);
            lock (gate)
            {
                history.AddRange(loaded);
                waterfallHistory.AddRange(loaded.TakeLast(MaximumWaterfallAnnotations));
            }
            context.Dispatcher.Post(() => viewModel.AddBatch(loaded));
            WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "ft8.history.load-failed",
                "FT8 decoded history could not be loaded.", exception);
        }
    }

    private void PruneStoredHistory()
    {
        lock (gate)
        {
            if (history.Count > settings.MaximumHistory)
                history.RemoveRange(0, history.Count - settings.MaximumHistory);
        }
    }

    private void AppendHistory(Ft8Reception reception)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        Ft8Reception persisted = settings.SavePayload ? reception : reception with { Payload = [] };
        if (historyWriter?.TryEnqueue(persisted) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "ft8.history.queue-full",
            "FT8 decoded history queue is full; the record was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<Ft8Reception> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<Ft8Reception>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(settings.MaximumHistory),
            static item => item.ReceivedAt);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "ft8.history.save-failed",
            "FT8 decoded history could not be saved.", exception);
        return writer;
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
            context.Logger.Log(PluginLogLevel.Warning, "ft8.history.delete-failed",
                "FT8 decoded history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private void SetStatus(string value)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        context.Dispatcher.Post(() =>
        {
            viewModel.Status = value;
            viewModel.OverallStatusKind = State switch
            {
                PluginLifecycleState.Streaming => OverallStatusKind.Running,
                PluginLifecycleState.Faulted => OverallStatusKind.Error,
                _ => OverallStatusKind.Idle
            };
        });
    }

    private static string BuildCsv(IEnumerable<Ft8Reception> records)
    {
        var output = new StringBuilder();
        output.AppendLine("Mode,SlotUtc,ReceivedUtc,FrequencyHz,AudioFrequencyHz,SnrDb,SyncScore,DtSeconds,Type,From,To,Extra,Message,PayloadHex,StreamId");
        foreach (Ft8Reception item in records)
        {
            output.Append(item.Mode).Append(',')
                .Append(item.SlotStart.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.ReceivedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.FrequencyHz.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.AudioFrequencyHz.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.SnrDb.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.SyncScore.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.TimeOffsetSeconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(item.MessageType)).Append(',').Append(Csv(item.FromCall)).Append(',')
                .Append(Csv(item.ToCall)).Append(',').Append(Csv(item.Extra)).Append(',')
                .Append(Csv(item.Message)).Append(',').Append(Csv(Convert.ToHexString(item.Payload))).Append(',')
                .Append(item.StreamId.ToString("D")).AppendLine();
        }
        return output.ToString();
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

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
        Ft8Band selectedBand;
        IqBlockMetadata? metadata;
        AppliedChannelConfiguration? channelConfiguration;
        Ft8DecoderDiagnostics diagnostics;
        lock (processingGate)
        {
            selectedBand = SelectedBand();
            metadata = lastCaptureMetadata;
            channelConfiguration = lastCaptureChannelConfiguration;
            diagnostics = receiver.Diagnostics;
        }
        viewModel.CaptureStatus = $"IQ録音: 直前{CaptureDurationSeconds}秒を保存中…";
        _ = Task.Run(() => SavePretriggerCapture(
            context, selectedBand, metadata, channelConfiguration, diagnostics));
    }

    private void SavePretriggerCapture(
        IPluginHostContext context,
        Ft8Band selectedBand,
        IqBlockMetadata? metadata,
        AppliedChannelConfiguration? channelConfiguration,
        Ft8DecoderDiagnostics diagnostics)
    {
        try
        {
            PackedIqHistorySnapshot snapshot = pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"ft8-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-" +
                $"{selectedBand.Band}-{selectedBand.DialFrequencyHz}");
            string path = $"{basePath}.wav";
            using (var capture = new Ft8IqCapture(
                       path, snapshot.SampleRateHz, TimeSpan.FromSeconds(CaptureDurationSeconds)))
            {
                capture.WritePcm(snapshot.RawInterleaved);
            }

            var document = new
            {
                Format = "SRdeck FT8 analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = $"{CaptureDurationSeconds}-second rolling pre-trigger",
                CaptureInput = channelConfiguration is null
                    ? "raw-device-iq"
                    : "standard-channel-iq",
                SelectedBand = selectedBand,
                selectedBand.DialFrequencyHz,
                selectedBand.ChannelCenterFrequencyHz,
                RawIqFile = Path.GetFileName(path),
                snapshot.SampleRateHz,
                snapshot.DurationSeconds,
                InputMetadata = metadata,
                ChannelConfiguration = channelConfiguration,
                ReceiverDiagnostics = diagnostics
            };
            string diagnosticsPath = $"{basePath}-diagnostics.json";
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                }));
            context.Dispatcher.Post(() =>
                viewModel.CaptureStatus = $"IQ録音保存済み: {path}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "ft8.iq-capture.save-failed",
                "Could not save FT8 rolling IQ capture.", exception);
            context.Dispatcher.Post(() =>
                viewModel.CaptureStatus = $"IQ録音保存失敗: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref captureSaveInProgress, 0);
        }
    }

    private sealed record WaterfallReference(Guid StreamId, DateTimeOffset Time);
}
