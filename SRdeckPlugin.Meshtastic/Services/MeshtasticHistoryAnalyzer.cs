using System;
using System.Collections.Generic;
using System.Linq;
using SRdeckPlugin.Meshtastic.ViewModels;

namespace SRdeckPlugin.Meshtastic.Services;

/// <summary>
/// Builds the history and reception views from immutable display items.
/// The analyzer deliberately has no dispatcher or collection ownership so it can
/// be exercised without starting WPF or the receiver.
/// </summary>
public sealed class MeshtasticHistoryAnalyzer
{
    /// <summary>
    /// Returns one display item per packet, preferring a direct reception when
    /// the same packet was also received through a relay.
    /// </summary>
    public IReadOnlyList<MeshtasticDisplayItem> PreferDirectReceptions(
        IEnumerable<MeshtasticDisplayItem> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages
            .GroupBy(item => (item.Sender, Packet: GetPacketIdentity(item)))
            .Select(group => group
                .OrderByDescending(item => item.IsDirect)
                .ThenByDescending(item => item.ReceivedAt)
                .First())
            .OrderByDescending(item => item.ReceivedAt)
            .ToArray();
    }

    public MeshtasticHistoryAnalysisResult Analyze(
        IReadOnlyList<MeshtasticDisplayItem> messages,
        IReadOnlyList<MeshtasticNodeDisplayItem> nodes,
        string? searchText,
        bool decodedOnly,
        bool directOnly,
        int displayLimit)
    {
        IEnumerable<MeshtasticDisplayItem> filtered = messages;
        string search = searchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(item =>
                item.Sender.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.SenderName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.PacketIdText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Port.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Summary.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (decodedOnly) filtered = filtered.Where(item => item.IsDecoded);
        if (directOnly) filtered = filtered.Where(item => item.IsDirect);

        MeshtasticDisplayItem[] filteredMessages = filtered.ToArray();

        var packetGroups = filteredMessages
            .GroupBy(item => (item.Sender, item.PacketId))
            .Select(group =>
            {
                List<MeshtasticDisplayItem> receptions = group.ToList();
                MeshtasticDisplayItem first = receptions[0];
                return new MeshtasticPacketGroupItem(
                    first.Sender,
                    first.SenderName,
                    first.PacketIdText,
                    first.Port,
                    first.Summary,
                    receptions);
            })
            .ToArray();

        IReadOnlyList<MeshtasticDisplayItem> textMessages = BuildTextMessages(messages, displayLimit);
        var receptionAggregates = messages
            .Where(item => item.RadioSlot > 0)
            .GroupBy(item => (Preset: item.ModemPresetName, Slot: item.RadioSlot))
            .OrderBy(group => group.Key.Preset)
            .ThenBy(group => group.Key.Slot)
            .Select(group => new MeshtasticReceptionAggregateItem(
                $"{group.Key.Preset} / slot {group.Key.Slot}",
                group.Count(),
                group.Select(item => (item.Sender, item.PacketId)).Distinct().Count(),
                group.Select(item => item.Sender).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .ToArray();

        return new MeshtasticHistoryAnalysisResult(
            filteredMessages,
            packetGroups,
            receptionAggregates,
            textMessages,
            nodes.Count(node => node.ActivityStatus is "新規" or "活動中"),
            messages.Count(item => !item.IsDecoded));
    }

    public IReadOnlyList<MeshtasticDisplayItem> ResolveSenderNames(
        IReadOnlyList<MeshtasticDisplayItem> messages,
        IReadOnlyList<MeshtasticNodeDisplayItem> nodes)
    {
        Dictionary<string, string> names = nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.DisplayName) &&
                           !string.Equals(node.DisplayName, node.NodeId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(node => node.NodeId, node => node.DisplayName, StringComparer.OrdinalIgnoreCase);

        return messages.Select(item =>
            names.TryGetValue(item.Sender, out string? displayName) && item.SenderName != displayName
                ? item with { SenderName = displayName }
                : item).ToArray();
    }

    private IReadOnlyList<MeshtasticDisplayItem> BuildTextMessages(
        IReadOnlyList<MeshtasticDisplayItem> messages,
        int displayLimit)
    {
        int limit = Math.Max(0, displayLimit);
        return PreferDirectReceptions(messages.Where(item => item.IsTextMessage))
            .OrderByDescending(item => item.ReceivedAt)
            .Take(limit)
            .ToArray();
    }

    private static string GetPacketIdentity(MeshtasticDisplayItem item)
    {
        if (item.PacketId != 0)
            return $"id:{item.PacketId:X8}";

        return $"payload:{item.PayloadHex}|{item.Port}|{item.Summary}";
    }

}

public sealed record MeshtasticHistoryAnalysisResult(
    IReadOnlyList<MeshtasticDisplayItem> FilteredMessages,
    IReadOnlyList<MeshtasticPacketGroupItem> PacketGroups,
    IReadOnlyList<MeshtasticReceptionAggregateItem> ReceptionAggregates,
    IReadOnlyList<MeshtasticDisplayItem> TextMessages,
    int ActiveNodeCount,
    int UndecodedHistoryCount);
