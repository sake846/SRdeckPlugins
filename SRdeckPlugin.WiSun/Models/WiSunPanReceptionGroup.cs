using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SRdeckPlugin.WiSun.Models;

public enum WiSunNodeRole
{
    None,
    Sender,
    Receiver
}

public sealed record WiSunNodeItem(
    string AddressText,
    string SeparatorText,
    bool IsSender,
    bool IsReceiver = false,
    WiSunNodeRole Role = WiSunNodeRole.None);

public sealed class WiSunPanReceptionGroup
{
    public WiSunPanReceptionGroup(
        ushort? panId,
        IEnumerable<WiSunPacketFrame> packets,
        WiSunPacketFrame? overallLatestPacket = null)
    {
        PanId = panId;
        Packets = packets.OrderByDescending(packet => packet.Timestamp).ToArray();
        var addressResolver = new WiSunAddressResolver();
        for (int i = Packets.Count - 1; i >= 0; i--)
        {
            addressResolver.Observe(Packets[i]);
        }
        PopulateDisplayAddresses(addressResolver, overallLatestPacket);
    }

    public WiSunPanReceptionGroup(
        ushort? panId,
        IEnumerable<WiSunPacketFrame> packets,
        WiSunAddressResolver addressResolver,
        WiSunPacketFrame? overallLatestPacket = null)
    {
        PanId = panId;
        Packets = packets.OrderByDescending(packet => packet.Timestamp).ToArray();
        PopulateDisplayAddresses(addressResolver, overallLatestPacket);
    }

    public ushort? PanId { get; set; }
    public IReadOnlyList<WiSunPacketFrame> Packets { get; set; }
    public IReadOnlyList<string> Nodes { get; set; } = [];
    public IReadOnlyList<WiSunNodeItem> NodeItems { get; set; } = [];
    public IReadOnlyList<WiSunPacketFrame> RecentCommunications { get; set; } = [];
    public string PanIdText { get => PanId is ushort panId ? $"PAN ID {panId:X4}" : "PAN ID 不明"; set { } }
    public int PacketCount { get => Packets.Count; set { } }
    public int NodeCount { get => Nodes.Count; set { } }
    public DateTimeOffset LatestTimestamp { get => Packets.Count > 0 ? Packets[0].Timestamp.ToLocalTime() : DateTimeOffset.Now; set { } }
    public string NodesText { get => Nodes.Count == 0 ? "識別済みノードなし" : string.Join(" ", Nodes); set { } }

    private void PopulateDisplayAddresses(
        WiSunAddressResolver addressResolver,
        WiSunPacketFrame? overallLatestPacket = null)
    {
        Nodes = Packets
            .SelectMany(packet => new[] { packet.SrcAddress, packet.DstAddress })
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => addressResolver.Resolve(PanId, address!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => address, NodeAddressComparer.Instance)
            .ToArray();
        WiSunPacketFrame? targetPacket = overallLatestPacket ?? Packets.FirstOrDefault();
        bool isTargetForThisPan = targetPacket != null &&
            ((targetPacket.PanId.HasValue && targetPacket.PanId == PanId) ||
             (!targetPacket.PanId.HasValue && !PanId.HasValue));

        string? latestSender = (isTargetForThisPan && targetPacket != null) ? Resolve(addressResolver, PanId, targetPacket.SrcAddress) : null;
        string? latestReceiver = (isTargetForThisPan && targetPacket != null) ? Resolve(addressResolver, PanId, targetPacket.DstAddress) : null;

        NodeItems = Nodes
            .Select((node, index) =>
            {
                bool isSender = !string.IsNullOrWhiteSpace(latestSender) &&
                                string.Equals(node, latestSender, StringComparison.OrdinalIgnoreCase);
                bool isReceiver = !isSender &&
                                  !string.IsNullOrWhiteSpace(latestReceiver) &&
                                  string.Equals(node, latestReceiver, StringComparison.OrdinalIgnoreCase);
                WiSunNodeRole role = isSender ? WiSunNodeRole.Sender : (isReceiver ? WiSunNodeRole.Receiver : WiSunNodeRole.None);
                return new WiSunNodeItem(
                    node,
                    index < Nodes.Count - 1 ? " " : "",
                    isSender,
                    isReceiver,
                    role);
            })
            .ToArray();
        RecentCommunications = Packets
            .Take(20)
            .Select(packet => packet with
            {
                Timestamp = packet.Timestamp.ToLocalTime(),
                SrcAddress = Resolve(addressResolver, PanId, packet.SrcAddress),
                DstAddress = Resolve(addressResolver, PanId, packet.DstAddress)
            })
            .ToArray();
    }

    private static string? Resolve(
        WiSunAddressResolver resolver,
        ushort? panId,
        string? address) =>
        string.IsNullOrWhiteSpace(address) ? address : resolver.Resolve(panId, address);

    private sealed class NodeAddressComparer : IComparer<string>
    {
        public static NodeAddressComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            bool leftIsAddress = TryParseAddress(left, out ulong leftValue);
            bool rightIsAddress = TryParseAddress(right, out ulong rightValue);
            if (leftIsAddress && rightIsAddress)
            {
                int valueOrder = leftValue.CompareTo(rightValue);
                if (valueOrder != 0) return valueOrder;
                int lengthOrder = left.Length.CompareTo(right.Length);
                if (lengthOrder != 0) return lengthOrder;
            }
            else if (leftIsAddress != rightIsAddress)
            {
                return leftIsAddress ? -1 : 1;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static bool TryParseAddress(string value, out ulong address)
        {
            address = 0;
            ReadOnlySpan<char> span = value.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }
            int bracketIndex = span.IndexOf('[');
            if (bracketIndex >= 0)
            {
                span = span[..bracketIndex];
            }
            return ulong.TryParse(span, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out address);
        }
    }
}
