using System.IO;
using SRdeckPlugin.AribT98.ViewModels;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.AribT98;

public sealed partial class AribT98PluginModule
{
    private AribT98FrameRow[] LoadHistory(IPluginHostContext context)
    {
        try
        {
            string path = GetHistoryPath(context);
            AribT98FrameRow[] stored = PluginJsonLinesHistory.LoadAll<AribT98FrameRow>(path)
                .TakeLast(settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, stored);
            return stored.Reverse().ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "arib-t98.history.load-failed",
                "STD-T98 reception history could not be loaded.", exception);
            return [];
        }
    }

    private PluginJsonLinesHistoryWriter<AribT98FrameRow> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<AribT98FrameRow>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(settings.MaximumHistory),
            static row => row.ReceivedAtUtc);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "arib-t98.history.save-failed",
            "STD-T98 reception history could not be saved.", exception);
        return writer;
    }

    private void AppendHistory(AribT98FrameRow row)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        if (historyWriter?.TryEnqueue(row) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "arib-t98.history.queue-full",
            "STD-T98 reception history queue is full; the record was not persisted.");
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "arib-t98.history.delete-failed",
                "STD-T98 reception history could not be deleted.", exception);
        }
    }

    private async Task UpdateMaximumHistoryAsync(int maximumHistory)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        await settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            settings = settings with { MaximumHistory = maximumHistory };
            await context.Settings.SaveAsync(SettingsDocument(settings)).ConfigureAwait(false);
            await Task.Run(() => ApplyHistoryRetention(context, maximumHistory)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "arib-t98.history.settings-save-failed",
                "STD-T98 reception history settings could not be saved.", exception);
        }
        finally { settingsGate.Release(); }
    }

    private void ApplyHistoryRetention(IPluginHostContext context, int maximumHistory) =>
        PluginJsonLinesHistory.ApplyRetention<AribT98FrameRow>(GetHistoryPath(context), maximumHistory,
            null, DateTimeOffset.UtcNow, static row => row.ReceivedAtUtc);

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private async Task FlushHistoryAsync(CancellationToken cancellationToken)
    {
        PluginJsonLinesHistoryWriter<AribT98FrameRow>? writer = historyWriter;
        IPluginHostContext? context = HostContext;
        if (writer is null || context is null) return;

        if (context.Dispatcher.CheckAccess())
            viewModel.CompleteActiveHistoryEntry();
        else
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Dispatcher.Post(() =>
            {
                viewModel.CompleteActiveHistoryEntry();
                completed.TrySetResult(true);
            });
            await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
