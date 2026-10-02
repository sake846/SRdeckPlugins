using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Cw.Models;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Cw;

public sealed partial class CwPluginModule
{
    public async ValueTask<PluginExportResult> ExportAsync(
        PluginExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IPluginHostContext? context = HostContext;
        CwDecodeResult[] records = context is null
            ? []
            : PluginJsonLinesHistory.LoadAll<CwDecodeResult>(GetHistoryPath(context))
                .Where(item =>
                    (request.From is null || item.ReceivedAt >= request.From) &&
                    (request.To is null || item.ReceivedAt <= request.To))
                .ToArray();
        if (records.Length == 0)
            return new(false, 0, "エクスポート対象のCW復号履歴がありません。");

        try
        {
            string output = request.FormatId switch
            {
                "json" => JsonSerializer.Serialize(records,
                    new JsonSerializerOptions { WriteIndented = true }),
                "csv" => BuildCsv(records),
                _ => throw new ArgumentException(
                    $"Unknown export format '{request.FormatId}'.", nameof(request))
            };
            await File.WriteAllTextAsync(request.DestinationPath, output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
                .ConfigureAwait(false);
            return new(true, records.Length, $"{records.Length:N0}件を保存しました。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context?.Logger.Log(PluginLogLevel.Error, "cw.export.failed",
                "CW decoded history export failed.", exception);
            return new(false, 0, "CW復号履歴を保存できませんでした。");
        }
    }

    private void AppendHistory(CwDecodeResult result)
    {
        if (historyWriter?.TryEnqueue(result) == true) return;
        HostContext?.Logger.Log(PluginLogLevel.Warning, "cw.history.queue-full",
            "CW decoded history queue is full; the record was not persisted.");
    }

    private PluginJsonLinesHistoryWriter<CwDecodeResult> CreateHistoryWriter(
        IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<CwDecodeResult>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(settings.MaximumRows),
            static item => item.ReceivedAt);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "cw.history.save-failed",
            "CW decoded history could not be saved.", exception);
        return writer;
    }

    private static string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, "cw-history.jsonl");

    internal static string BuildCsv(IEnumerable<CwDecodeResult> records)
    {
        var output = new StringBuilder(
            "receivedAt,frequencyHz,frequencyOffsetHz,snrDb,speedWpm,language,confidence,text,streamId,trackId\r\n");
        foreach (CwDecodeResult item in records)
        {
            output.Append(item.ReceivedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.FrequencyHz.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.FrequencyOffsetHz.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.SignalToNoiseDb.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.SpeedWpm.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Language).Append(',')
                .Append(item.Confidence.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(item.Text)).Append(',')
                .Append(item.StreamId.ToString("D")).Append(',')
                .Append(item.TrackId.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return output.ToString();
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
