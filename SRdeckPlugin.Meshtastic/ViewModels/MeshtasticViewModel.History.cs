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

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    private void HandleMeshtasticDataReceived(MeshtasticDataReception reception)
    {
        RefreshMeshtasticStatistics();
        MeshtasticDisplayItem item = new(
            reception.Packet.ReceivedAt.ToLocalTime().ToString("HH:mm:ss"),
            $"!{reception.Packet.From:x8}",
            ResolveMeshtasticSenderName(reception.Packet.From),
            BuildMeshtasticTransmission(reception),
            $"Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}",
            reception.Data.PortName,
            reception.Quality.Summary,
            BuildMeshtasticSummary(reception.Data),
            reception.Data.DecodedPayload?.Details ?? $"Payload: {Convert.ToHexString(reception.Data.Payload)}",
            Convert.ToHexString(reception.Data.Payload))
        {
            Radio = reception.Radio.Summary,
            PacketId = reception.Packet.PacketId,
            IsDecoded = true,
            ModemPresetName = ResolveMeshtasticPresetName(reception.Radio),
            RadioSlot = reception.Radio.RadioChannel,
            ReceivedAt = reception.Packet.ReceivedAt,
            PreambleMarginDb = reception.Quality.PreambleMarginDb,
            PayloadCrcValid = reception.Quality.PayloadCrcValid,
            HopLimit = reception.Packet.HopLimit,
            HopStart = reception.Packet.HopStart,
            WasRelayed = reception.Packet.WasRelayed,
            RelayNode = reception.Packet.RelayNode
        };

        _hostContext?.Dispatcher.Post(() =>
        {
            MeshtasticMessages.Insert(0, item);
            if (item.IsTextMessage)
            {
                MeshtasticTextMessages.Insert(0, item);
                while (MeshtasticTextMessages.Count > MeshtasticHistoryDisplayLimit)
                {
                    MeshtasticTextMessages.RemoveAt(MeshtasticTextMessages.Count - 1);
                }
            }
            AppendMeshtasticHistory(item);
            if (SelectedMeshtasticNode?.NodeNumber == reception.Packet.From)
                RefreshSelectedMeshtasticNodeReceptions();
            while (MeshtasticMessages.Count > MeshtasticHistoryDisplayLimit)
            {
                MeshtasticMessages.RemoveAt(MeshtasticMessages.Count - 1);
            }
            SelectedTimelineMessage ??= item;
            ScheduleMeshtasticDerivedRefresh();

            if (!_meshtasticNodesById.TryGetValue(reception.Packet.From, out MeshtasticNodeDisplayItem? node))
            {
                node = new MeshtasticNodeDisplayItem(reception.Packet.From);
                _meshtasticNodesById.Add(reception.Packet.From, node);
                MeshtasticNodes.Insert(0, node);
                SelectedMeshtasticNode ??= node;
            }
            node.Update(reception, item.Summary);
            if (reception.Data.DecodedPayload is MeshtasticNodeInfo)
            {
                RefreshMeshtasticSenderNames(reception.Packet.From, node.DisplayName);
                if (_meshtasticMapPointsById.TryGetValue(reception.Packet.From, out MeshtasticMapPoint? namedPoint))
                    namedPoint.Label = node.DisplayName;
            }
            if (node.HasPosition)
                UpdateMeshtasticMapPoint(node);
            int nodeIndex = MeshtasticNodes.IndexOf(node);
            if (nodeIndex > 0) MeshtasticNodes.Move(nodeIndex, 0);

            SetMeshtasticReceiverStatus(
                $"{reception.Radio.Summary} / " +
                $"受信 {MeshtasticMessages.Count}件 / Node {MeshtasticNodes.Count}",
                OverallStatusKind.Running);
            ScheduleMeshtasticSnapshotSave();
        });
    }

    private string ResolveMeshtasticSenderName(uint nodeNumber) =>
        _meshtasticNodesById.TryGetValue(nodeNumber, out MeshtasticNodeDisplayItem? node)
            ? node.DisplayName
            : $"!{nodeNumber:x8}";

    private void RefreshMeshtasticSenderNames(uint nodeNumber, string displayName)
    {
        string sender = $"!{nodeNumber:x8}";
        for (int index = 0; index < MeshtasticMessages.Count; index++)
        {
            MeshtasticDisplayItem item = MeshtasticMessages[index];
            if (string.Equals(item.Sender, sender, StringComparison.OrdinalIgnoreCase) && item.SenderName != displayName)
                MeshtasticMessages[index] = item with { SenderName = displayName };
        }
        for (int index = 0; index < MeshtasticTextMessages.Count; index++)
        {
            MeshtasticDisplayItem item = MeshtasticTextMessages[index];
            if (string.Equals(item.Sender, sender, StringComparison.OrdinalIgnoreCase) && item.SenderName != displayName)
                MeshtasticTextMessages[index] = item with { SenderName = displayName };
        }
        RefreshSelectedMeshtasticNodeReceptions();
        RefreshMeshtasticPacketGroups();
    }

    private void UpdateMeshtasticMapPoint(MeshtasticNodeDisplayItem node)
    {
        if (!node.HasPosition) return;
        if (!_meshtasticMapPointsById.TryGetValue(node.NodeNumber, out MeshtasticMapPoint? point))
        {
            point = new MeshtasticMapPoint(node.NodeNumber);
            _meshtasticMapPointsById[node.NodeNumber] = point;
            MeshtasticMapPoints.Add(point);
        }
        point.Label = node.DisplayName;
        point.Latitude = node.Latitude!.Value;
        point.Longitude = node.Longitude!.Value;
        point.Coordinates = node.Coordinates;
        point.ActivityStatus = node.ActivityStatus;
        point.HasDirectReception = node.DirectReceptionCount > 0;
        point.IsSelected = node.NodeNumber == SelectedMeshtasticNode?.NodeNumber;
        RefreshMeshtasticMapMarkers();
    }

    private void LoadMeshtasticState()
    {
        try
        {
            LoadMeshtasticHistory();
            if (File.Exists(MeshtasticStatePath))
            {
                MeshtasticPersistedState? state = JsonSerializer.Deserialize<MeshtasticPersistedState>(File.ReadAllText(MeshtasticStatePath));
                if (state is not null)
                {
                    foreach (MeshtasticPersistedNode savedNode in state.Nodes)
                    {
                        var node = new MeshtasticNodeDisplayItem(savedNode.NodeNumber);
                        node.Restore(savedNode);
                        _meshtasticNodesById[savedNode.NodeNumber] = node;
                        MeshtasticNodes.Add(node);
                        if (node.HasPosition) UpdateMeshtasticMapPoint(node);
                    }
                }
            }
            RefreshMeshtasticSenderNamesFromNodeCache();
            RefreshMeshtasticPacketGroups();
            RefreshFilteredMeshtasticNodes();
            SelectedMeshtasticNode = FilteredMeshtasticNodes.FirstOrDefault();
            SelectedTimelineMessage = VisibleMeshtasticMessages.FirstOrDefault();
        }
        catch (Exception exception)
        {
            SetMeshtasticReceiverStatus(
                $"受信履歴の復元に失敗しました: {exception.Message}",
                OverallStatusKind.Error);
        }
    }

    private void RefreshMeshtasticSenderNamesFromNodeCache()
    {
        IReadOnlyList<MeshtasticDisplayItem> resolved = _meshtasticHistoryAnalyzer.ResolveSenderNames(
            MeshtasticMessages,
            MeshtasticNodes);
        for (int index = 0; index < resolved.Count; index++)
        {
            if (MeshtasticMessages[index] != resolved[index])
                MeshtasticMessages[index] = resolved[index];
        }

        IReadOnlyList<MeshtasticDisplayItem> resolvedText = _meshtasticHistoryAnalyzer.ResolveSenderNames(
            MeshtasticTextMessages,
            MeshtasticNodes);
        for (int index = 0; index < resolvedText.Count; index++)
        {
            if (MeshtasticTextMessages[index] != resolvedText[index])
                MeshtasticTextMessages[index] = resolvedText[index];
        }
    }

    private void SaveMeshtasticState()
    {
        try
        {
            List<MeshtasticPersistedNode> nodes = MeshtasticNodes.Select(node => new MeshtasticPersistedNode(
                node.NodeNumber, node.DisplayName, node.Identity, node.LastSeen, node.Route, node.Position,
                node.Telemetry, node.LastMessage, node.SignalQuality, node.Details, node.Latitude, node.Longitude,
                node.AltitudeMeters, node.PacketCount, node.ReceptionCount, node.DirectReceptionCount,
                node.RelayedReceptionCount, node.UnknownRouteReceptionCount, node.FirstSeenAt, node.LastSeenAt,
                node.Mode, node.LastReceivedContent, node.LongName, node.ShortName)).ToList();
            string currentModeSummary = IsMeshtasticDiscoveryMode
                ? $"探索 ({SelectedMeshtasticRegion})"
                : $"条件指定 ({SelectedMeshtasticRegion} / {SelectedMeshtasticModemPreset})";
            var state = new MeshtasticPersistedState(DateTimeOffset.Now, nodes, currentModeSummary);
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            string temporaryPath = MeshtasticStatePath + ".tmp";
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, MeshtasticStatePath, true);
        }
        catch (Exception exception)
        {
            SetMeshtasticReceiverStatus(
                $"受信履歴の保存に失敗しました: {exception.Message}",
                OverallStatusKind.Error);
        }
    }

    private void AppendMeshtasticHistory(MeshtasticDisplayItem item)
    {
        if (_meshtasticHistoryWriter?.TryEnqueue(item) == true) return;
        SetMeshtasticReceiverStatus(
            "受信履歴保存キューが満杯です。受信画面は継続します。",
            OverallStatusKind.Warning);
    }
}
