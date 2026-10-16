using System.Text;
using SRdeckPlugin.Hfdl.Models;

namespace SRdeckPlugin.Hfdl.Protocols;

public static class HfdlMessageParser
{
    public static bool TryParse(HfdlFrame frame, out HfdlMessage? message) => TryParse(frame.Bytes, out message);

    public static bool TryParse(ReadOnlySpan<byte> frame, out HfdlMessage? message)
    {
        message = Parse(frame).FirstOrDefault();
        return message is not null;
    }

    public static IReadOnlyList<HfdlMessage> Parse(ReadOnlySpan<byte> frame)
    {
        if (!HfdlPdu.TryReadLengths(frame, out int headerLength, out int frameLength) ||
            frame.Length < frameLength) return [];
        int station = frame[1] & 0x7f;
        if ((frame[0] & 1) == 0)
            return [new(frame[0], "SPDU (地上局情報)", station, null, "", frame[2..64].ToArray(), true,
                station, IsGroundStationMessage: true, IsUplink: true)];

        var messages = new List<HfdlMessage>();
        int payloadOffset = headerLength + 2;
        bool uplink = (frame[0] & 2) == 0;
        int aircraftCount = uplink ? ((frame[0] >> 4) & 7) + 1 : 1;
        int cursor = uplink ? 2 : 6;
        for (int index = 0; index < aircraftCount; index++)
        {
            int aircraft = uplink ? frame[cursor] : frame[2];
            int count = uplink ? frame[cursor + 1] >> 4 : (frame[0] >> 2) & 15;
            if (uplink) cursor += 2;
            for (int lpdu = 0; lpdu < count; lpdu++)
            {
                int length = frame[cursor++] + 1;
                messages.Add(ParseLpdu(frame.Slice(payloadOffset, length), station, aircraft, uplink));
                payloadOffset += length;
            }
        }
        if (messages.Count == 0)
            messages.Add(new(frame[0], "MPDU (制御情報)", uplink ? station : frame[2],
                uplink ? null : station, "", [], true, station,
                AircraftId: !uplink && frame[2] is > 0 and < 255 ? frame[2] : null,
                IsGroundStationMessage: uplink, IsUplink: uplink));
        return messages;
    }

    public static IEnumerable<HfdlReception> RestoreHistory(HfdlReception reception)
    {
        if (reception.DecoderVersion != 0 || string.IsNullOrWhiteSpace(reception.RawFrameHex))
            return [reception];
        byte[] raw;
        try { raw = Convert.FromHexString(reception.RawFrameHex); }
        catch (FormatException) { return [reception]; }
        IReadOnlyList<HfdlMessage> messages = Parse(raw);
        if (messages.Count > 0)
            return messages.Select(reception.WithMessage).ToArray();

        // Earlier decoders saved only the MPDU header. Keep it, without inventing an aircraft identity.
        if (HfdlPdu.TryReadLengths(raw, out int headerLength, out int frameLength) &&
            raw.Length == headerLength + 2 && raw.Length < frameLength)
        {
            int station = raw[1] & 0x7f;
            bool uplink = (raw[0] & 2) == 0;
            int? aircraft = !uplink && raw[2] is > 0 and < 255 ? raw[2] : null;
            return [reception.WithMessage(new(raw[0], "MPDU (本文未保存)", uplink ? station : raw[2],
                uplink ? null : station, "", [], true, station, aircraft,
                IsGroundStationMessage: uplink, IsUplink: uplink))];
        }
        return [reception];
    }

    private static HfdlMessage ParseLpdu(ReadOnlySpan<byte> lpdu, int station, int aircraft, bool uplink)
    {
        byte type = lpdu[0];
        string kind = type switch
        {
            0x0d or 0x1d => "User data",
            0x2f => "Log-on denied", 0x3f => "Log-off",
            0x4f => "Log-on resume", 0x5f => "Log-on resume confirm",
            0x8f or 0xbf => "Log-on request", 0x9f => "Log-on confirm",
            _ => $"LPDU 0x{type:X2}"
        };
        bool crcValid = HfdlCrc.IsValid(lpdu);
        ReadOnlySpan<byte> body = lpdu[..^2];
        string flight = "", icao = "", registration = "";
        int? aircraftId = aircraft is > 0 and < 255 ? aircraft : null;
        if (crcValid)
        {
            int identityLength = type switch { 0x5f or 0x9f => 8, 0x2f or 0x3f => 5,
                0x4f or 0x8f or 0xbf => 4, _ => 0 };
            if (identityLength > 0 && body.Length >= identityLength)
            {
                int address = Reverse(body[1]) << 16 | Reverse(body[2]) << 8 | Reverse(body[3]);
                if (address is > 0 and < 0xffffff) icao = $"{address:X6}";
                if (type is 0x5f or 0x9f) aircraftId = body[4] is > 0 and < 255 ? body[4] : null;
            }
            if (type is 0x0d or 0x1d && body.Length >= 3 && body[1] == 0xff)
            {
                ReadOnlySpan<byte> network = body[1..];
                kind = network[1] switch { 0xd0 => "System table", 0xd1 => "Performance data",
                    0xd2 => "System table request", 0xd5 => "Frequency data", 0xde => "Delayed echo",
                    0xff => "ACARS", _ => kind };
                if ((network[1] == 0xd1 && network.Length >= 47) ||
                    (network[1] == 0xd5 && network.Length >= 15))
                    flight = IdentityText(network.Slice(2, 6));
                else if (network[1] == 0xff)
                    ReadAcarsIdentity(network[2..], out flight, out registration);
            }
        }
        return new(type, kind, uplink ? station : aircraft, uplink ? aircraft : station,
            flight, body[1..].ToArray(), crcValid, station, aircraftId, icao, registration,
            IsGroundStationMessage: uplink && aircraftId is null && icao.Length == 0 &&
                registration.Length == 0 && flight.Length == 0, IsUplink: uplink);
    }

    private static void ReadAcarsIdentity(ReadOnlySpan<byte> bytes, out string flight, out string registration)
    {
        flight = registration = "";
        if (bytes.Length < 14 || bytes[0] != 1) return;
        int end = -1;
        for (int index = 13; index < bytes.Length; index++)
            if ((bytes[index] & 0x7f) is 3 or 0x17) { end = index; break; }
        if (end < 0) return;
        registration = IdentityText(bytes.Slice(2, 7)).TrimStart('.');
        int cursor = 13;
        if (cursor < end && (bytes[cursor] & 0x7f) == 2) cursor++;
        if ((bytes[12] & 0x7f) is >= '0' and <= '9' && end - cursor >= 10)
            flight = IdentityText(bytes.Slice(cursor + 4, 6));
    }

    private static string IdentityText(ReadOnlySpan<byte> bytes)
    {
        Span<byte> ascii = stackalloc byte[bytes.Length];
        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = (byte)(bytes[index] & 0x7f);
            if (value != 0 && value != ' ' && value != '-' && value != '.' &&
                !char.IsAsciiLetterOrDigit((char)value)) return "";
            ascii[index] = value;
        }
        return Encoding.ASCII.GetString(ascii).Trim('\0', ' ');
    }

    private static int Reverse(byte value)
    {
        int bits = ((value & 0x55) << 1) | ((value >> 1) & 0x55);
        bits = ((bits & 0x33) << 2) | ((bits >> 2) & 0x33);
        return ((bits & 15) << 4) | (bits >> 4);
    }
}
