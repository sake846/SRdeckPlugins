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
        ApplyMeshtasticHistoryRetention(MeshtasticHistoryPath, GetMeshtasticHistoryPolicy());
        IReadOnlyList<MeshtasticDisplayItem> displayItems = PluginJsonLinesHistory.Load<MeshtasticDisplayItem>(
            MeshtasticHistoryPath, MeshtasticHistoryDisplayLimit, MeshtasticHistoryJsonOptions);
        foreach (MeshtasticDisplayItem item in displayItems.Reverse())
        {
            MeshtasticMessages.Add(item);
            if (item.IsTextMessage) MeshtasticTextMessages.Add(item);
        }
    }

    private List<MeshtasticDisplayItem> ReadAllMeshtasticHistory() =>
        PluginJsonLinesHistory.LoadAll<MeshtasticDisplayItem>(MeshtasticHistoryPath, MeshtasticHistoryJsonOptions).ToList();

    private async Task PruneMeshtasticHistoryFileAsync()
    {
        string path = MeshtasticHistoryPath;
        PluginJsonLinesHistoryPolicy policy = GetMeshtasticHistoryPolicy();
        try
        {
            if (_meshtasticHistoryWriter is { } writer) await writer.FlushAsync();
            await Task.Run(() => ApplyMeshtasticHistoryRetention(path, policy));
        }
        catch (Exception exception)
        {
            _hostContext?.Logger.Log(PluginLogLevel.Warning, "meshtastic.history.retention-failed",
                "Meshtastic history retention could not be applied.", exception);
        }
    }

    private static void ApplyMeshtasticHistoryRetention(string path, PluginJsonLinesHistoryPolicy policy) =>
        PluginJsonLinesHistory.ApplyRetention(path, policy.MaximumEntries, policy.MaximumAge,
            DateTimeOffset.UtcNow, MeshtasticHistoryTimestamp, MeshtasticHistoryJsonOptions, policy.MaximumBytes);

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
        Interlocked.Increment(ref _presentationEpoch);
        _historyVersion++;
        SelectedTimelineMessage = null;
        MeshtasticMessages.Clear();
        VisibleMeshtasticMessages.Clear();
        MeshtasticTextMessages.Clear();
        MeshtasticPacketGroups.Clear();
        MeshtasticReceptionAggregates.Clear();
        SelectedMeshtasticNodeLatestApplications.Clear();
        MeshtasticNodes.Clear();
        FilteredMeshtasticNodes.Clear();
        _meshtasticNodesById.Clear();
        MeshtasticMapPoints.Clear();
        MapMarkers.Clear();
        _meshtasticMapPointsById.Clear();
        SelectedMeshtasticNode = null;
        ResetMeshtasticHistoryWriter();
        PluginJsonLinesHistory.Delete(MeshtasticHistoryPath);
        SaveMeshtasticState();
        SetMeshtasticReceiverStatus("受信待機中", OverallStatusKind.Running);
    }

    private void SetMeshtasticReceiverStatus(string status, OverallStatusKind statusKind)
    {
        MeshtasticReceiverStatus = status;
        MeshtasticReceiverStatusKind = statusKind;
    }
}
