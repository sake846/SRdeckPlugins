using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    private void LoadMeshtasticHistory()
    {
        if (!File.Exists(MeshtasticHistoryPath)) return;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-MeshtasticHistoryRetentionDays);
            var retainedLines = new List<string>();
        var displayItems = new Queue<MeshtasticDisplayItem>(MeshtasticHistoryDisplayLimit);
        lock (MeshtasticHistorySync)
        {
            foreach (string line in File.ReadLines(MeshtasticHistoryPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    MeshtasticDisplayItem? item = JsonSerializer.Deserialize<MeshtasticDisplayItem>(line);
                    if (item is null || item.ReceivedAt < cutoff) continue;
                    retainedLines.Add(line);
                    if (displayItems.Count == MeshtasticHistoryDisplayLimit) displayItems.Dequeue();
                    displayItems.Enqueue(item);
                }
                catch (JsonException) { }
            }
            if (retainedLines.Count != File.ReadLines(MeshtasticHistoryPath).Count())
                File.WriteAllLines(MeshtasticHistoryPath, retainedLines, new UTF8Encoding(false));
        }
        foreach (MeshtasticDisplayItem item in displayItems.Reverse())
        {
            MeshtasticMessages.Add(item);
            if (item.IsTextMessage) MeshtasticTextMessages.Add(item);
        }
    }

    private List<MeshtasticDisplayItem> ReadAllMeshtasticHistory()
    {
        var items = new List<MeshtasticDisplayItem>();
        if (!File.Exists(MeshtasticHistoryPath)) return items;
        lock (MeshtasticHistorySync)
        {
            foreach (string line in File.ReadLines(MeshtasticHistoryPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    MeshtasticDisplayItem? item = JsonSerializer.Deserialize<MeshtasticDisplayItem>(line);
                    if (item is not null) items.Add(item);
                }
                catch (JsonException) { }
            }
        }
        return items;
    }

    private void PruneMeshtasticHistoryFile()
    {
        if (!File.Exists(MeshtasticHistoryPath)) return;
        _meshtasticHistoryWriter?.FlushAsync().AsTask().GetAwaiter().GetResult();
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(MeshtasticHistoryRetentionDays, 1, 3650));
        lock (MeshtasticHistorySync)
        {
            List<MeshtasticDisplayItem> retained = ReadAllMeshtasticHistory()
                .Where(item => item.ReceivedAt >= cutoff)
                .ToList();
            File.WriteAllLines(MeshtasticHistoryPath,
                retained.Select(item => JsonSerializer.Serialize(item,
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })),
                new UTF8Encoding(false));
        }
    }

    private static string BuildMeshtasticTransmission(MeshtasticDataReception reception)
        => MeshtasticRouteDisplay.Format(reception);

    private static string BuildMeshtasticSummary(SRdeckPlugin.Meshtastic.Protocols.MeshtasticData data)
    {
        if (data.DecodedPayload is MeshtasticTelemetry telemetry)
        {
            var summary = new StringBuilder(telemetry.Variant);
            summary.Append(": ");
            int count = Math.Min(5, telemetry.Metrics.Count);
            for (int index = 0; index < count; index++)
            {
                if (index > 0) summary.Append("  /  ");
                summary.Append(FormatTelemetryMetric(telemetry.Metrics[index]));
            }
            return summary.ToString();
        }

        if (data.DecodedPayload is not null)
        {
            return data.DecodedPayload.Summary;
        }

        if (data.Text is not null)
        {
            var builder = new StringBuilder(data.Text.Length);
            foreach (char value in data.Text)
            {
                builder.Append(char.IsControl(value) && value is not '\t' ? ' ' : value);
            }
            return builder.ToString();
        }

        return data.Payload.Length == 0
            ? "(ペイロードなし)"
            : $"{data.PortName} / {data.Payload.Length} bytes";
    }

    private static string FormatTelemetryMetric(string metric)
    {
        return metric
            .Replace("battery=", "Bat ", StringComparison.Ordinal)
            .Replace("voltage=", "V ", StringComparison.Ordinal)
            .Replace("channelUtilization=", "Ch ", StringComparison.Ordinal)
            .Replace("airUtilTx=", "TX ", StringComparison.Ordinal)
            .Replace("uptime=", "Up ", StringComparison.Ordinal);
    }

    [RelayCommand]
    private void ClearMeshtasticMessages()
    {
        SelectedTimelineMessage = null;
        MeshtasticMessages.Clear();
        VisibleMeshtasticMessages.Clear();
        MeshtasticTextMessages.Clear();
        MeshtasticPacketGroups.Clear();
        SelectedMeshtasticNodeReceptions.Clear();
        MeshtasticNodes.Clear();
        FilteredMeshtasticNodes.Clear();
        _meshtasticNodesById.Clear();
        MeshtasticMapPoints.Clear();
        MapMarkers.Clear();
        _meshtasticMapPointsById.Clear();
        SelectedMeshtasticNode = null;
        ResetMeshtasticHistoryWriter();
        lock (MeshtasticHistorySync)
            if (File.Exists(MeshtasticHistoryPath)) File.Delete(MeshtasticHistoryPath);
        SaveMeshtasticState();
        SetMeshtasticReceiverStatus("受信待機中", OverallStatusKind.Running);
    }

    private void SetMeshtasticReceiverStatus(string status, OverallStatusKind statusKind)
    {
        MeshtasticReceiverStatus = status;
        MeshtasticReceiverStatusKind = statusKind;
    }
}
