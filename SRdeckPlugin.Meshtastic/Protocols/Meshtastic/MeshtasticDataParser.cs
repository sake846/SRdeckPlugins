using System;
using System.Buffers.Binary;
using System.Text;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Protocols;

public sealed record MeshtasticData(
    uint PortNumber,
    byte[] Payload,
    bool WantResponse,
    uint? Destination,
    uint? Source,
    uint? RequestId,
    uint? ReplyId,
    uint? Emoji,
    uint? Bitfield)
{
    public MeshtasticApplicationPayload? DecodedPayload { get; init; }

    public string PortName => PortNumber switch
    {
        1 => "TEXT_MESSAGE_APP",
        2 => "REMOTE_HARDWARE_APP",
        3 => "POSITION_APP",
        4 => "NODEINFO_APP",
        5 => "ROUTING_APP",
        10 => "DETECTION_SENSOR_APP",
        65 => "STORE_FORWARD_APP",
        67 => "TELEMETRY_APP",
        70 => "TRACEROUTE_APP",
        71 => "NEIGHBORINFO_APP",
        _ => $"PORT_{PortNumber}"
    };

    public string? Text => PortNumber is 1 or 10 ? Encoding.UTF8.GetString(Payload) : null;
}

/// <summary>Minimal dependency-free decoder for the Meshtastic Data protobuf.</summary>
public static class MeshtasticDataParser
{
    public static bool TryParse(ReadOnlySpan<byte> bytes, out MeshtasticData? data, out string error)
    {
        uint portNumber = 0;
        byte[] payload = [];
        bool wantResponse = false;
        uint? destination = null, source = null, requestId = null, replyId = null, emoji = null, bitfield = null;
        int offset = 0;

        try
        {
            while (offset < bytes.Length)
            {
                ulong tag = ReadVarint(bytes, ref offset);
                int field = checked((int)(tag >> 3));
                int wireType = (int)(tag & 7);
                if (field == 0) throw new FormatException("protobuf field number is zero");

                switch (field)
                {
                    case 1 when wireType == 0:
                        portNumber = checked((uint)ReadVarint(bytes, ref offset));
                        break;
                    case 2 when wireType == 2:
                        int length = checked((int)ReadVarint(bytes, ref offset));
                        EnsureAvailable(bytes, offset, length);
                        payload = bytes.Slice(offset, length).ToArray();
                        offset += length;
                        break;
                    case 3 when wireType == 0:
                        wantResponse = ReadVarint(bytes, ref offset) != 0;
                        break;
                    case >= 4 and <= 8 when wireType == 5:
                        EnsureAvailable(bytes, offset, 4);
                        uint fixedValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                        offset += 4;
                        if (field == 4) destination = fixedValue;
                        else if (field == 5) source = fixedValue;
                        else if (field == 6) requestId = fixedValue;
                        else if (field == 7) replyId = fixedValue;
                        else emoji = fixedValue;
                        break;
                    case 9 when wireType == 0:
                        bitfield = checked((uint)ReadVarint(bytes, ref offset));
                        break;
                    default:
                        SkipField(bytes, ref offset, wireType);
                        break;
                }
            }

            data = new MeshtasticData(portNumber, payload, wantResponse, destination, source, requestId, replyId, emoji, bitfield);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            data = null;
            error = exception.Message;
            return false;
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            EnsureAvailable(bytes, offset, 1);
            byte value = bytes[offset++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new FormatException("protobuf varint is too long");
    }

    private static void SkipField(ReadOnlySpan<byte> bytes, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(bytes, ref offset);
                break;
            case 1:
                EnsureAvailable(bytes, offset, 8);
                offset += 8;
                break;
            case 2:
                int length = checked((int)ReadVarint(bytes, ref offset));
                EnsureAvailable(bytes, offset, length);
                offset += length;
                break;
            case 5:
                EnsureAvailable(bytes, offset, 4);
                offset += 4;
                break;
            default:
                throw new FormatException($"unsupported protobuf wire type {wireType}");
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > bytes.Length - count)
            throw new FormatException("truncated protobuf data");
    }
}
