using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Ais.Dsp;
using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Ais.Protocols;
using SRdeckPlugin.Ais.ViewModels;
using SRdeckPlugin.Ais.Views;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais;

public sealed partial class AisPluginModule
{
    private void PruneStoredHistory()
    {
        lock (gate) PruneStoredHistoryUnsafe();
    }

    private void PruneStoredHistoryUnsafe()
    {
        if (history.Count > settings.MaximumHistory)
            history.RemoveRange(0, history.Count - settings.MaximumHistory);
    }

    private async Task PersistSettingsAsync()
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        try
        {
            AisSettings settingsToPersist = settings with
            {
                MapState = GeoMapStateStore.GetState(Descriptor.Id)
            };
            string json = JsonSerializer.Serialize(settingsToPersist);
            await context.Settings.SaveAsync(new(1, json), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            context.Logger.Log(PluginLogLevel.Warning, "ais.settings.save-failed",
                "AIS settings could not be saved.", exception);
        }
    }

    private void LoadHistory()
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        try
        {
            string path = GetHistoryPath(context);
            ExportRecord[] loaded = PluginJsonLinesHistory.LoadAll<ExportRecord>(path)
                .Where(item => settings.ChannelFilter == "both" ||
                    (settings.ChannelFilter == "ais1" && item.Channel.Contains("1", StringComparison.OrdinalIgnoreCase)) ||
                    (settings.ChannelFilter == "ais2" && item.Channel.Contains("2", StringComparison.OrdinalIgnoreCase)))
                .TakeLast(settings.MaximumHistory).ToArray();
            if (File.Exists(path)) PluginJsonLinesHistory.Rewrite(path, loaded);
            lock (gate)
            {
                history.AddRange(loaded);
                foreach (IGrouping<uint, ExportRecord> group in loaded.GroupBy(item => item.Mmsi))
                {
                    var target = new AisTargetState(group.Key);
                    foreach (ExportRecord item in group)
                    {
                        target.Apply(new AisMessage(item.MessageType, item.RepeatIndicator, item.Mmsi, item.Kind,
                            item.Summary, item.Latitude, item.Longitude, item.SpeedKnots,
                            item.CourseDegrees, item.HeadingDegrees, item.RateOfTurn,
                            NavigationStatus: item.NavigationStatus, VesselName: item.Name,
                            CallSign: item.CallSign, ImoNumber: item.ImoNumber, ShipType: item.ShipType,
                            Destination: item.Destination, DraughtMetres: item.DraughtMetres,
                            DimensionToBowMetres: item.DimensionToBowMetres,
                            DimensionToSternMetres: item.DimensionToSternMetres,
                            DimensionToPortMetres: item.DimensionToPortMetres,
                            DimensionToStarboardMetres: item.DimensionToStarboardMetres,
                            PositionAccurate: item.PositionAccurate, UtcSecond: item.UtcSecond,
                            AidType: item.AidType),
                            new AisFrame(DecodeHex(item.RawHex), item.ReceivedAt, item.StreamId, item.SamplePosition,
                                item.Channel, item.FrequencyHz, item.SignalQuality));
                    }
                    targets[group.Key] = target;
                }
            }

            foreach (ExportRecord item in loaded.TakeLast(500))
                QueueMessage(new AisMessageRow(item.ReceivedAt.ToLocalTime(), item.Channel,
                    item.Mmsi, item.MessageType, item.Kind, item.Name, item.Summary,
                    item.Latitude, item.Longitude, item.SpeedKnots, item.CourseDegrees));
            PublishViewSnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "ais.history.load-failed",
                "AIS decoded history could not be loaded.", exception);
        }
    }

    private void AppendHistory(ExportRecord record)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        if (historyWriter?.TryEnqueue(record) == true) return;
        context.Logger.Log(PluginLogLevel.Warning, "ais.history.queue-full",
            "AIS decoded history queue is full; the record was not persisted.");
    }

    private bool IsChannelSelectedForStorage(string channel) => settings.ChannelFilter == "both" ||
        (settings.ChannelFilter == "ais1" && channel.Contains("1", StringComparison.OrdinalIgnoreCase)) ||
        (settings.ChannelFilter == "ais2" && channel.Contains("2", StringComparison.OrdinalIgnoreCase));

    private PluginJsonLinesHistoryWriter<ExportRecord> CreateHistoryWriter(IPluginHostContext context)
    {
        var writer = new PluginJsonLinesHistoryWriter<ExportRecord>(
            GetHistoryPath(context),
            () => new PluginJsonLinesHistoryPolicy(
                settings.MaximumHistory),
            static item => item.ReceivedAt);
        writer.SaveFailed += exception => context.Logger.Log(
            PluginLogLevel.Warning, "ais.history.save-failed",
            "AIS decoded history could not be saved.", exception);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "ais.history.delete-failed",
                "AIS decoded history could not be deleted.", exception);
        }
    }

    private string GetHistoryPath(IPluginHostContext context) =>
        Path.Combine(context.Settings.DataDirectory, $"{Descriptor.Id}-history.jsonl");

    private void OnTuningChanged(object? sender, PluginTuningResult result) =>
        SetStatus(State == PluginLifecycleState.Streaming
            ? $"受信中 / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz / AIS 1・AIS 2"
            : $"待機中 / {result.CenterFrequencyHz / 1_000_000.0:F3} MHz");

    private void SetStatus(string value)
    {
        IPluginHostContext? context = HostContext;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
    }

    private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static byte[] DecodeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
        try { return Convert.FromHexString(value); }
        catch (FormatException) { return Array.Empty<byte>(); }
    }
    private static string Invariant<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;

    public sealed record ExportRecord(
        DateTimeOffset ReceivedAt,
        string Channel,
        long FrequencyHz,
        uint Mmsi,
        int MessageType,
        int RepeatIndicator,
        string Kind,
        string Name,
        string CallSign,
        int? ImoNumber,
        double? Latitude,
        double? Longitude,
        double? SpeedKnots,
        double? CourseDegrees,
        int? HeadingDegrees,
        double? RateOfTurn,
        int? NavigationStatus,
        int? ShipType,
        string Destination,
        double? DraughtMetres,
        int? DimensionToBowMetres,
        int? DimensionToSternMetres,
        int? DimensionToPortMetres,
        int? DimensionToStarboardMetres,
        bool PositionAccurate,
        int? UtcSecond,
        string AidType,
        string Summary,
        string RawHex = "",
        Guid StreamId = default,
        long SamplePosition = 0,
        double SignalQuality = 0);
}
