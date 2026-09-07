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
        0 => "UNKNOWN_APP",
        1 => "TEXT_MESSAGE_APP",
        2 => "REMOTE_HARDWARE_APP",
        3 => "POSITION_APP",
        4 => "NODEINFO_APP",
        5 => "ROUTING_APP",
        6 => "ADMIN_APP",
        7 => "TEXT_MESSAGE_COMPRESSED_APP",
        8 => "WAYPOINT_APP",
        9 => "AUDIO_APP",
        10 => "DETECTION_SENSOR_APP",
        11 => "ALERT_APP",
        12 => "KEY_VERIFICATION_APP",
        13 => "REMOTE_SHELL_APP",
        32 => "REPLY_APP",
        33 => "IP_TUNNEL_APP",
        34 => "PAXCOUNTER_APP",
        35 => "STORE_FORWARD_PLUSPLUS_APP",
        36 => "NODE_STATUS_APP",
        37 => "MESH_BEACON_APP",
        64 => "SERIAL_APP",
        65 => "STORE_FORWARD_APP",
        66 => "RANGE_TEST_APP",
        67 => "TELEMETRY_APP",
        68 => "ZPS_APP",
        69 => "SIMULATOR_APP",
        70 => "TRACEROUTE_APP",
        71 => "NEIGHBORINFO_APP",
        72 => "ATAK_PLUGIN",
        73 => "MAP_REPORT_APP",
        74 => "POWERSTRESS_APP",
        75 => "LORAWAN_BRIDGE",
        76 => "RETICULUM_TUNNEL_APP",
        77 => "CAYENNE_APP",
        78 => "ATAK_PLUGIN_V2",
        79 => "LORA_OTA_APP",
        112 => "GROUPALARM_APP",
        256 => "PRIVATE_APP",
        257 => "ATAK_FORWARDER",
        _ => $"PORT_{PortNumber}"
    };

    public string? Text => PortNumber is 1 or 10 or 11 or 32 or 66
        ? Encoding.UTF8.GetString(Payload).TrimEnd('\0')
        : null;
}

/// <summary>Minimal dependency-free decoder for the Meshtastic Data protobuf.</summary>
public static class MeshtasticDataParser
{
    public const string OtherApplicationName = "その他";

    // Application names shown on the third line of each node in the list view.
    public static IReadOnlyList<string> KnownApplicationNames { get; } =
    [
        "TEXT_MESSAGE",
        "POSITION",
        "NODEINFO",
        "ROUTING",
        "RANGE_TEST",
        "TELEMETRY",
        "TRACEROUTE",
        "NEIGHBORINFO",
        OtherApplicationName
    ];

    public static string GetNodeListApplicationName(uint portNumber) => portNumber switch
    {
        1 => "TEXT_MESSAGE",
        3 => "POSITION",
        4 => "NODEINFO",
        5 => "ROUTING",
        66 => "RANGE_TEST",
        67 => "TELEMETRY",
        70 => "TRACEROUTE",
        71 => "NEIGHBORINFO",
        _ => OtherApplicationName
    };

    public static string NormalizeNodeListApplicationName(string? applicationName)
    {
        string normalized = applicationName?.Trim() ?? string.Empty;
        if (normalized.EndsWith("_APP", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return normalized.ToUpperInvariant() switch
        {
            "TEXT_MESSAGE" => "TEXT_MESSAGE",
            "POSITION" => "POSITION",
            "NODEINFO" => "NODEINFO",
            "ROUTING" => "ROUTING",
            "RANGE_TEST" => "RANGE_TEST",
            "TELEMETRY" => "TELEMETRY",
            "TRACEROUTE" => "TRACEROUTE",
            "NEIGHBORINFO" => "NEIGHBORINFO",
            _ => OtherApplicationName
        };
    }

    public static int GetNodeListApplicationOrder(string? applicationName)
    {
        string normalized = NormalizeNodeListApplicationName(applicationName);
        for (int index = 0; index < KnownApplicationNames.Count; index++)
        {
            if (string.Equals(KnownApplicationNames[index], normalized, StringComparison.Ordinal))
                return index;
        }

        return KnownApplicationNames.Count;
    }

    public static uint GetPortNumber(string? portName)
    {
        string normalized = portName?.Trim() ?? string.Empty;
        if (normalized.StartsWith("PORT_", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(normalized.AsSpan(5), out uint portNumber))
            return portNumber;

        return normalized.ToUpperInvariant() switch
        {
            "UNKNOWN_APP" => 0,
            "TEXT_MESSAGE_APP" => 1,
            "REMOTE_HARDWARE_APP" => 2,
            "POSITION_APP" => 3,
            "NODEINFO_APP" => 4,
            "ROUTING_APP" => 5,
            "ADMIN_APP" => 6,
            "TEXT_MESSAGE_COMPRESSED_APP" => 7,
            "WAYPOINT_APP" => 8,
            "AUDIO_APP" => 9,
            "DETECTION_SENSOR_APP" => 10,
            "ALERT_APP" => 11,
            "KEY_VERIFICATION_APP" => 12,
            "REMOTE_SHELL_APP" => 13,
            "REPLY_APP" => 32,
            "IP_TUNNEL_APP" => 33,
            "PAXCOUNTER_APP" => 34,
            "STORE_FORWARD_PLUSPLUS_APP" => 35,
            "NODE_STATUS_APP" => 36,
            "MESH_BEACON_APP" => 37,
            "SERIAL_APP" => 64,
            "STORE_FORWARD_APP" => 65,
            "RANGE_TEST_APP" => 66,
            "TELEMETRY_APP" => 67,
            "ZPS_APP" => 68,
            "SIMULATOR_APP" => 69,
            "TRACEROUTE_APP" => 70,
            "NEIGHBORINFO_APP" => 71,
            "ATAK_PLUGIN" => 72,
            "MAP_REPORT_APP" => 73,
            "POWERSTRESS_APP" => 74,
            "LORAWAN_BRIDGE" => 75,
            "RETICULUM_TUNNEL_APP" => 76,
            "CAYENNE_APP" => 77,
            "ATAK_PLUGIN_V2" => 78,
            "LORA_OTA_APP" => 79,
            "GROUPALARM_APP" => 112,
            "PRIVATE_APP" => 256,
            "ATAK_FORWARDER" => 257,
            _ => uint.MaxValue
        };
    }

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
