using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Dsp;
using SRdeckPlugin.HfFax.Models;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.HfFax;

public sealed partial class HfFaxPluginModule
{
    private void LoadHistory(IPluginHostContext context)
    {
        try
        {
            string path = GetHistoryPath(context);
            HfFaxImageSnapshot[] chronological =
                PluginJsonLinesHistory.LoadAll<HfFaxImageSnapshot>(path)
                    .Where(IsValidHistoryImage)
                    .OrderBy(image => image.CompletedAt)
                    .TakeLast(settings.MaximumHistory)
                    .ToArray();
            if (File.Exists(path))
                PluginJsonLinesHistory.Rewrite(path, chronological);

            HfFaxImageSnapshot[] newestFirst = chronological.Reverse().ToArray();
            if (settings.AutomaticHorizontalAlignmentEnabled)
                newestFirst = newestFirst
                    .Select(image => HfFaxImagePhaseAligner.AlignSnapshot(
                        image, settings.HorizontalShiftPixels))
                    .ToArray();
            lock (resultGate)
            {
                history.Clear();
                history.AddRange(newestFirst);
                latestImage = newestFirst.FirstOrDefault();
            }
            context.Dispatcher.Post(() => viewModel.LoadHistory(newestFirst));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hffax.history.load-failed",
                "HF-FAX image history could not be loaded.", exception);
        }
    }

    private void AppendHistory(HfFaxImageSnapshot image)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        if (historyWriter?.TryEnqueue(image) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "hffax.history.queue-full",
            "HF-FAX image history queue is full; the image was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<HfFaxImageSnapshot> CreateHistoryWriter(
        IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<HfFaxImageSnapshot>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(settings.MaximumHistory),
            static image => image.CompletedAt,
            queueCapacity: 64,
            batchSize: 4);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "hffax.history.save-failed",
            "HF-FAX image history could not be saved.", exception);
        return writer;
    }

    private void DeleteHistoryFile()
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        try
        {
            historyWriter?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            historyWriter = CreateHistoryWriter(context);
            PluginJsonLinesHistory.Delete(GetHistoryPath(context));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hffax.history.delete-failed",
                "HF-FAX image history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private static bool IsValidHistoryImage(HfFaxImageSnapshot image)
    {
        if (image.Ioc is not (288 or 576) ||
            image.Width != HfFaxReceiver.WidthFromIoc(image.Ioc) ||
            image.Height is < 1 or > 4_000 ||
            image.Pixels is null)
            return false;
        try { return image.Pixels.Length == checked(image.Width * image.Height); }
        catch (OverflowException) { return false; }
    }
}
