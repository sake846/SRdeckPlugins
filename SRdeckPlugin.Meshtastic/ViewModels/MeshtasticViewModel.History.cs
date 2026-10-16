using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        int epoch = Volatile.Read(ref _presentationEpoch);
        reception = reception with { Quality = reception.Quality.WithReceiverCalibration(_hostContext?.ReceiverTelemetry) };
        MeshtasticDisplayItem item = new(
            reception.Packet.ReceivedAt.ToLocalTime().ToString("HH:mm:ss"),
            $"!{reception.Packet.From:x8}",
            $"!{reception.Packet.From:x8}",
            MeshtasticRouteDisplay.Format(reception),
            $"Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}",
            reception.Data.PortName,
            reception.Quality.Summary,
            BuildMeshtasticSummary(reception.Data),
            reception.Data.DecodedPayload?.Details ?? $"Payload: {Convert.ToHexString(reception.Data.Payload)}",
            Convert.ToHexString(reception.Data.Payload))
        {
            Radio = reception.Radio.Summary,
            RegionName = reception.Radio.Region != default ? reception.Radio.Region.ToString() : SelectedMeshtasticRegion.ToString(),
            PacketId = reception.Packet.PacketId,
            IsDecoded = true,
            ModemPresetName = ResolveMeshtasticPresetName(reception.Radio),
            RadioSlot = reception.Radio.RadioChannel,
            ReceivedAt = reception.Packet.ReceivedAt,
            EstimatedSnrDb = reception.Quality.EstimatedSnrDb,
            EstimatedRssiDbfs = reception.Quality.EstimatedRssiDbfs,
            EstimatedRssiDbm = reception.Quality.EstimatedRssiDbm,
            PayloadCrcValid = reception.Quality.PayloadCrcValid,
            HopLimit = reception.Packet.HopLimit,
            HopStart = reception.Packet.HopStart,
            WasRelayed = reception.Packet.WasRelayed,
            RelayNode = reception.Packet.RelayNode
        };

        _hostContext?.Dispatcher.Post(() =>
        {
            if (epoch != _presentationEpoch || Volatile.Read(ref _initialized) == 0) return;
            item = item with { SenderName = ResolveMeshtasticSenderName(reception.Packet.From) };
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
            while (MeshtasticMessages.Count > MeshtasticHistoryDisplayLimit)
            {
                MeshtasticMessages.RemoveAt(MeshtasticMessages.Count - 1);
            }
            SelectedTimelineMessage ??= item;
            ScheduleMeshtasticDerivedRefresh();

            MeshtasticNodeDisplayItem node = _nodeStore.GetOrAddNode(reception.Packet.From, out bool addedNode);
            if (addedNode) SelectedMeshtasticNode ??= node;
            node.Update(reception, item.Summary, item.ModemPresetName, item.RegionName);
            if (reception.Data.DecodedPayload is MeshtasticNodeInfo)
            {
                RefreshMeshtasticSenderNames(reception.Packet.From, node.DisplayName);
                if (_nodeStore.TryGetMapPoint(reception.Packet.From, out MeshtasticMapPoint? namedPoint))
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
        _nodeStore.TryGetNode(nodeNumber, out MeshtasticNodeDisplayItem? node)
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
        ScheduleMeshtasticDerivedRefresh();
    }

    private void UpdateMeshtasticMapPoint(MeshtasticNodeDisplayItem node)
    {
        if (!node.HasPosition) return;
        MeshtasticMapPoint point = _nodeStore.GetOrAddMapPoint(node.NodeNumber);
        point.Label = node.DisplayName;
        point.Latitude = node.Latitude!.Value;
        point.Longitude = node.Longitude!.Value;
        point.Coordinates = node.Coordinates;
        point.ActivityStatus = node.ActivityStatus;
        point.HasDirectReception = node.DirectReceptionCount > 0;
        point.IsSelected = node.NodeNumber == SelectedMeshtasticNode?.NodeNumber;
        if (!_restoringMeshtasticState) UpdateMeshtasticMapMarker(point);
    }

    private void LoadMeshtasticState()
    {
        _restoringMeshtasticState = true;
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
                        _nodeStore.AddRestoredNode(node);
                        if (node.HasPosition) UpdateMeshtasticMapPoint(node);
                    }
                }
            }
            RestoreMeshtasticAppTypesFromHistory();
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
        finally { _restoringMeshtasticState = false; RefreshMeshtasticMapMarkers(); }
    }

    private void RestoreMeshtasticAppTypesFromHistory()
    {
        foreach (MeshtasticDisplayItem item in MeshtasticMessages)
        {
            if (string.IsNullOrWhiteSpace(item.Sender) || item.Sender[0] != '!' ||
                !uint.TryParse(item.Sender.AsSpan(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out uint nodeNumber))
                continue;

            if (_nodeStore.TryGetNode(nodeNumber, out MeshtasticNodeDisplayItem? node))
                node.RecordReceivedAppType(item.Port);
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
                node.Mode, node.LastReceivedContent, node.LongName, node.ShortName,
                node.PreferredReceptionRoutes, node.HasCompletePreferredReceptionRoutes,
                node.HardwareName, node.RoleName, node.RadioSlot, node.ModemPresetName,
                node.MinimumRssiDbfs, node.MaximumRssiDbfs, node.MinimumRssiDbm, node.MaximumRssiDbm,
                node.MinimumSnrDb, node.MaximumSnrDb, node.ReceivedAppTypesText, node.RegionName)).ToList();
            string currentModeSummary = IsMeshtasticDiscoveryMode
                ? $"探索 ({SelectedMeshtasticRegion})"
                : $"条件指定 ({SelectedMeshtasticRegion} / {SelectedMeshtasticModemPreset})";
            var state = new MeshtasticPersistedState(DateTimeOffset.Now, nodes, currentModeSummary);
            _stateWriter?.Enqueue(state);
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
