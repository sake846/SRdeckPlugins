using System;
using System.Buffers.Binary;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Protocols;

/// <summary>
/// Small protobuf wire reader for Meshtastic application payloads. It accepts
/// unknown fields so newer firmware can remain compatible with this receiver.
/// </summary>
internal ref struct MeshtasticProtobufReader
{
    private readonly ReadOnlySpan<byte> _bytes;
    private int _offset;

    public MeshtasticProtobufReader(ReadOnlySpan<byte> bytes) => _bytes = bytes;

    public bool TryReadField(out int fieldNumber, out int wireType)
    {
        if (_offset >= _bytes.Length)
        {
            fieldNumber = 0;
            wireType = 0;
            return false;
        }

        ulong tag = ReadVarint();
        fieldNumber = checked((int)(tag >> 3));
        wireType = (int)(tag & 7);
        if (fieldNumber == 0) throw new FormatException("protobuf field number is zero");
        return true;
    }

    public ulong ReadVarint(int wireType)
    {
        RequireWireType(wireType, 0);
        return ReadVarint();
    }

    public uint ReadFixed32(int wireType)
    {
        RequireWireType(wireType, 5);
        EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_offset..]);
        _offset += 4;
        return value;
    }

    public float ReadFloat(int wireType) => BitConverter.UInt32BitsToSingle(ReadFixed32(wireType));

    public ulong ReadFixed64(int wireType)
    {
        RequireWireType(wireType, 1);
        EnsureAvailable(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_bytes[_offset..]);
        _offset += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int wireType)
    {
        RequireWireType(wireType, 2);
        int length = checked((int)ReadVarint());
        EnsureAvailable(length);
        ReadOnlySpan<byte> value = _bytes.Slice(_offset, length);
        _offset += length;
        return value;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                EnsureAvailable(8);
                _offset += 8;
                break;
            case 2:
                int length = checked((int)ReadVarint());
                EnsureAvailable(length);
                _offset += length;
                break;
            case 5:
                EnsureAvailable(4);
                _offset += 4;
                break;
            default:
                throw new FormatException($"unsupported protobuf wire type {wireType}");
        }
    }

    public static int DecodeZigZag32(ulong value) =>
        unchecked((int)((uint)(value >> 1) ^ (uint)-(int)(value & 1)));

    private ulong ReadVarint()
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            EnsureAvailable(1);
            byte value = _bytes[_offset++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new FormatException("protobuf varint is too long");
    }

    private static void RequireWireType(int actual, int expected)
    {
        if (actual != expected)
            throw new FormatException($"unexpected protobuf wire type {actual}; expected {expected}");
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || _offset > _bytes.Length - count)
            throw new FormatException("truncated protobuf data");
    }
}
