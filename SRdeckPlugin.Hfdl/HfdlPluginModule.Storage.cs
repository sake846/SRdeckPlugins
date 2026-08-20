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
    private void PersistSettings()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            HfdlSettings settingsToPersist = settings with
            {
                MapState = GeoMapStateStore.GetState(Descriptor.Id)
            };
            context.Settings.SaveAsync(new(1, JsonSerializer.Serialize(settingsToPersist)))
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hfdl.settings.save-failed",
                "HFDL settings could not be saved.", exception);
        }
    }

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        if (Volatile.Read(ref tuningRequestInProgress) != 0) return;
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
        {
            Channel channel = SelectedChannel();
            long targetFreqHz = channel.FrequencyHz + SignalOffsetHz;
            if ((result.PassbandLowerFrequencyHz > targetFreqHz ||
                 result.PassbandUpperFrequencyHz < targetFreqHz) && host is not null)
            {
                _ = host.Tuning.RequestAsync(new PluginTuningRequest(
                    $"hfdl-{channel.Id}",
                    $"HFDL {channel.Name}",
                    [new TuningTarget(targetFreqHz, 4_800)],
                    targetFreqHz,
                    HfdlReceiver.MonitorAudioSampleRateHz,
                    null,
                    true,
                    false,
                    PluginGainPreference.Automatic));
            }
            SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / {targetFreqHz / 1_000_000.0:F3} MHz");
        }
    }

    private void LoadHistory()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            string path = GetHistoryPath(context);
            HfdlReception[] loaded = PluginJsonLinesHistory.LoadAll<HfdlReception>(path)
                .TakeLast(settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, loaded);
            lock (gate) history.AddRange(loaded);
            context.Dispatcher.Post(() =>
            {
                foreach (HfdlReception item in loaded)
                    viewModel.Add(item, 0, 0);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hfdl.history.load-failed",
                "HFDL decoded history could not be loaded.", exception);
        }
    }

    private void PruneHistory()
    {
        lock (gate)
        {
            if (history.Count > settings.MaximumHistory)
                history.RemoveRange(0, history.Count - settings.MaximumHistory);
        }
    }

    private void AppendHistory(HfdlReception reception)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        HfdlReception persisted = settings.SaveRawFrames ? reception : reception with { RawFrameHex = string.Empty };
        if (historyWriter?.TryEnqueue(persisted) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "hfdl.history.queue-full",
            "HFDL decoded history queue is full; the record was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<HfdlReception> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<HfdlReception>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(settings.MaximumHistory));
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "hfdl.history.save-failed",
            "HFDL decoded history could not be saved.", exception);
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
            context.Logger.Log(PluginLogLevel.Warning, "hfdl.history.delete-failed",
                "HFDL decoded history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
