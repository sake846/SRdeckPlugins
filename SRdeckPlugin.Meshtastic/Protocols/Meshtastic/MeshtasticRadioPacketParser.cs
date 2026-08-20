using System;
using System.Buffers.Binary;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Protocols;

public readonly record struct MeshtasticPacketKey(uint From, uint PacketId);

public sealed record MeshtasticRadioPacket(
    DateTimeOffset ReceivedAt,
    uint To,
    uint From,
    uint PacketId,
    int HopLimit,
    int HopStart,
    bool WantAcknowledgement,
    bool ViaMqtt,
    byte ChannelHash,
    byte NextHop,
    byte RelayNode,
    byte[] EncryptedPayload)
{
    public const uint BroadcastNode = 0xFFFFFFFF;
    public MeshtasticPacketKey Key => new(From, PacketId);
    public bool IsBroadcast => To == BroadcastNode;
    public int? HopsTaken => HopStart >= HopLimit ? HopStart - HopLimit : null;
    public bool? WasRelayed => HopsTaken is int hops ? hops > 0 : null;
}

/// <summary>
/// Parses the fixed 16-byte Meshtastic PacketHeader used directly on the
/// LoRa link. Multi-byte values are little-endian on supported firmware.
/// </summary>
public static class MeshtasticRadioPacketParser
{
    public const int HeaderLength = 16;

    private const byte HopLimitMask = 0x07;
    private const byte WantAckMask = 0x08;
    private const byte ViaMqttMask = 0x10;
    private const byte HopStartMask = 0xE0;
    private const int HopStartShift = 5;

    public static MeshtasticRadioPacket Parse(byte[] radioPayload)
    {
        ArgumentNullException.ThrowIfNull(radioPayload);
        return Parse(radioPayload.AsSpan());
    }

    public static MeshtasticRadioPacket Parse(ReadOnlySpan<byte> radioPayload)
    {
        if (radioPayload.Length < HeaderLength)
        {
            throw new ArgumentException($"Meshtastic radio payload must contain at least {HeaderLength} bytes.", nameof(radioPayload));
        }

        uint to = BinaryPrimitives.ReadUInt32LittleEndian(radioPayload);
        uint from = BinaryPrimitives.ReadUInt32LittleEndian(radioPayload[4..]);
        uint packetId = BinaryPrimitives.ReadUInt32LittleEndian(radioPayload[8..]);
        if (from == 0)
        {
            throw new ArgumentException("Meshtastic packets with sender 0 are invalid.", nameof(radioPayload));
        }

        byte flags = radioPayload[12];
        return new MeshtasticRadioPacket(
            DateTimeOffset.UtcNow,
            to,
            from,
            packetId,
            flags & HopLimitMask,
            (flags & HopStartMask) >> HopStartShift,
            (flags & WantAckMask) != 0,
            (flags & ViaMqttMask) != 0,
            radioPayload[13],
            radioPayload[14],
            radioPayload[15],
            radioPayload[HeaderLength..].ToArray());
    }
}
