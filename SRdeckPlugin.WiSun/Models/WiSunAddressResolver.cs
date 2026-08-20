using System;
using System.Collections.Generic;
using System.Globalization;

namespace SRdeckPlugin.WiSun.Models;

public sealed class WiSunAddressResolver
{
    private const byte MacCommandFrameType = 3;
    private const byte AssociationRequestCommand = 0x01;
    private const byte AssociationResponseCommand = 0x02;
    private const byte AssociationSuccessful = 0x00;
    private const ushort MaximumAssignedShortAddress = 0xFFFD;

    private readonly Dictionary<(ushort PanId, ushort ShortAddress), string> _bindings = new();
    private readonly Dictionary<(ushort PanId, string DeviceAddress), ushort> _reverseBindings = new();
    private readonly Dictionary<(ushort PanId, string DeviceAddress), ushort>
        _pendingAssociationRequests = new();

    public void Observe(WiSunPacketFrame frame)
    {
        if (TryReadAssociationResponse(
                frame.RawPayload, out ushort panId, out ushort shortAddress,
                out string? deviceAddress, out string? coordinatorAddress) &&
            deviceAddress is not null &&
            coordinatorAddress is not null)
        {
            _bindings[(panId, shortAddress)] = deviceAddress;
            _reverseBindings[(panId, deviceAddress)] = shortAddress;
            if (_pendingAssociationRequests.Remove(
                    (panId, deviceAddress), out ushort coordinatorShortAddress))
            {
                _bindings[(panId, coordinatorShortAddress)] = coordinatorAddress;
                _reverseBindings[(panId, coordinatorAddress)] = coordinatorShortAddress;
            }
        }
        else if (TryReadAssociationRequest(
                     frame.RawPayload, out panId, out shortAddress,
                     out deviceAddress) &&
                 deviceAddress is not null)
        {
            _pendingAssociationRequests[(panId, deviceAddress)] = shortAddress;
        }
    }

    public string Resolve(ushort? panId, string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;

        string cleanAddress = Strip0x(address);

        if (panId is not ushort id)
        {
            return cleanAddress;
        }

        if (TryParseShortAddress(address, out ushort shortAddress))
        {
            if (_bindings.TryGetValue((id, shortAddress), out string? extendedAddress))
            {
                return $"{Strip0x(extendedAddress)}[{shortAddress:X4}]";
            }
        }

        string canonicalExtAddress = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? address
            : $"0x{address}";
        if (_reverseBindings.TryGetValue((id, canonicalExtAddress), out ushort boundShortAddress))
        {
            return $"{Strip0x(canonicalExtAddress)}[{boundShortAddress:X4}]";
        }

        return cleanAddress;
    }

    public static string Strip0x(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return address;
        return address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? address[2..]
            : address;
    }

    public void Clear()
    {
        _bindings.Clear();
        _reverseBindings.Clear();
        _pendingAssociationRequests.Clear();
    }

    private static bool TryReadAssociationRequest(
        byte[] frame,
        out ushort panId,
        out ushort coordinatorShortAddress,
        out string? deviceAddress)
    {
        panId = 0;
        coordinatorShortAddress = 0;
        deviceAddress = null;
        if (!TryReadMacHeader(
                frame, requiredDestinationMode: 2, requiredSourceMode: 3,
                out ushort destinationPanId, out int offset))
        {
            return false;
        }

        if (!TryReadUInt16(frame, ref offset, out ushort destinationAddress))
        {
            return false;
        }
        if (destinationAddress > MaximumAssignedShortAddress) return false;

        ushort frameControl = (ushort)(frame[0] | frame[1] << 8);
        bool panCompression = (frameControl & 0x0040) != 0;
        if (!panCompression &&
            !TryReadUInt16(frame, ref offset, out _))
        {
            return false;
        }

        if (!TryReadExtendedAddress(frame, ref offset, out string? sourceAddress) ||
            offset >= frame.Length ||
            frame[offset] != AssociationRequestCommand)
        {
            return false;
        }

        panId = destinationPanId;
        coordinatorShortAddress = destinationAddress;
        deviceAddress = sourceAddress;
        return true;
    }

    private static bool TryReadAssociationResponse(
        byte[] frame,
        out ushort panId,
        out ushort shortAddress,
        out string? deviceAddress,
        out string? coordinatorAddress)
    {
        panId = 0;
        shortAddress = 0;
        deviceAddress = null;
        coordinatorAddress = null;
        if (!TryReadMacHeader(
                frame, requiredDestinationMode: 3, requiredSourceMode: 3,
                out ushort destinationPanId, out int offset))
        {
            return false;
        }

        if (!TryReadExtendedAddress(frame, ref offset, out string? destinationAddress))
        {
            return false;
        }

        ushort frameControl = (ushort)(frame[0] | frame[1] << 8);
        bool panCompression = (frameControl & 0x0040) != 0;
        ushort sourcePanId = destinationPanId;
        if (!panCompression &&
            !TryReadUInt16(frame, ref offset, out sourcePanId))
        {
            return false;
        }

        if (!TryReadExtendedAddress(frame, ref offset, out string? sourceAddress) ||
            offset + 4 > frame.Length ||
            frame[offset] != AssociationResponseCommand)
        {
            return false;
        }

        ushort assignedAddress = (ushort)(frame[offset + 1] | frame[offset + 2] << 8);
        byte associationStatus = frame[offset + 3];
        if (associationStatus != AssociationSuccessful ||
            assignedAddress > MaximumAssignedShortAddress ||
            sourcePanId != destinationPanId)
        {
            return false;
        }

        panId = destinationPanId;
        shortAddress = assignedAddress;
        deviceAddress = destinationAddress;
        coordinatorAddress = sourceAddress;
        return true;
    }

    private static bool TryReadMacHeader(
        byte[] frame,
        int requiredDestinationMode,
        int requiredSourceMode,
        out ushort destinationPanId,
        out int addressOffset)
    {
        destinationPanId = 0;
        addressOffset = 0;
        if (frame.Length < 3) return false;

        ushort frameControl = (ushort)(frame[0] | frame[1] << 8);
        if ((frameControl & 0x07) != MacCommandFrameType ||
            (frameControl & 0x0008) != 0 ||
            (frameControl >> 10 & 0x03) != requiredDestinationMode ||
            (frameControl >> 14 & 0x03) != requiredSourceMode)
        {
            // A secured MAC command cannot be inspected without decrypting it.
            return false;
        }

        int frameVersion = frameControl >> 12 & 0x03;
        bool sequenceSuppressed = frameVersion == 2 && (frameControl & 0x0100) != 0;
        int offset = sequenceSuppressed ? 2 : 3;
        if (!TryReadUInt16(frame, ref offset, out destinationPanId))
        {
            return false;
        }

        addressOffset = offset;
        return true;
    }

    private static bool TryReadUInt16(byte[] frame, ref int offset, out ushort value)
    {
        value = 0;
        if (offset + 2 > frame.Length) return false;
        value = (ushort)(frame[offset] | frame[offset + 1] << 8);
        offset += 2;
        return true;
    }

    private static bool TryReadExtendedAddress(
        byte[] frame,
        ref int offset,
        out string? address)
    {
        address = null;
        if (offset + 8 > frame.Length) return false;

        Span<byte> canonicalAddress = stackalloc byte[8];
        for (int i = 0; i < canonicalAddress.Length; i++)
        {
            canonicalAddress[i] = frame[offset + canonicalAddress.Length - 1 - i];
        }

        offset += canonicalAddress.Length;
        address = $"0x{Convert.ToHexString(canonicalAddress)}";
        return true;
    }

    private static bool TryParseShortAddress(string address, out ushort shortAddress)
    {
        shortAddress = 0;
        return address.Length == 6 &&
            address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(address.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out shortAddress);
    }
}
