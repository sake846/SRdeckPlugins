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
    private void LoadHistory()
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        try
        {
            string path = GetHistoryPath(context);
            AcarsReception[] loaded = PluginJsonLinesHistory.LoadAll<AcarsReception>(path)
                .TakeLast(settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, loaded);
            lock (gate) history.AddRange(loaded);
            context.Dispatcher.Post(() =>
            {
                foreach (AcarsReception item in loaded.TakeLast(500))
                    viewModel.Add(item, 0, 0);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "acars.history.load-failed",
                "ACARS decoded history could not be loaded.", exception);
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

    private void AppendHistory(AcarsReception reception)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        if (historyWriter?.TryEnqueue(reception) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "acars.history.queue-full",
            "ACARS decoded history queue is full; the record was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<AcarsReception> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<AcarsReception>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(
                settings.MaximumHistory),
            static item => item.ReceivedAt);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "acars.history.save-failed",
            "ACARS decoded history could not be saved.", exception);
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
            context.Logger.Log(PluginLogLevel.Warning, "acars.history.delete-failed",
                "ACARS decoded history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

}
