using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SRdeckPlugin.Vdl.Models;

namespace SRdeckPlugin.Vdl.Protocols;

/// <summary>Decodes the AVLC layer and the ACARS or X.25 payload carried by an AVLC I-frame.</summary>
public sealed class VdlUpperLayerDecoder
{
    private readonly Dictionary<string, FragmentSet> acarsFragments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FragmentLifetime = TimeSpan.FromMinutes(5);

    public VdlDecodedFrame Decode(VdlFrame frame)
    {
        Cleanup(frame.ReceivedAt);
        if (!TryParseAvlc(frame.Payload, out AvlcPacket? parsedAvlc) || parsedAvlc is null)
            return new(frame, null, null, null, "AVLC", "AVLCヘッダーが短すぎます");
        AvlcPacket avlc = parsedAvlc;

        if (avlc.Kind == AvlcFrameKind.Information && avlc.Information.AsSpan().StartsWith(new byte[] { 0xff, 0xff, 0x01 }))
        {
            if (!TryParseAcars(avlc.Information.AsSpan(3), out VdlAcarsMessage? parsedAcars) || parsedAcars is null)
                return new(frame, avlc, null, null, "ACARS", "ACARSヘッダーまたは終端が不正です");
            VdlAcarsMessage acars = Reassemble(parsedAcars, avlc, frame.ReceivedAt);
            return new(frame, avlc, acars, null, "ACARS", acars.ReassemblyStatus);
        }

        if (avlc.Kind == AvlcFrameKind.Information && TryParseX25(avlc.Information, out VdlX25Packet? x25) && x25 is not null)
            return new(frame, avlc, null, x25, x25.UpperProtocol, "解析済み");

        string protocol = avlc.Kind == AvlcFrameKind.Unnumbered && avlc.FrameType == "XID"
            ? "AVLC/XID" : $"AVLC/{avlc.Kind}";
        return new(frame, avlc, null, null, protocol,
            avlc.Information.Length == 0 ? "情報部なし" : $"未解析情報部 {avlc.Information.Length} byte");
    }

    public void Reset() => acarsFragments.Clear();

    public static bool TryParseAvlc(ReadOnlySpan<byte> bytes, out AvlcPacket? packet)
    {
        packet = null;
        if (bytes.Length < 9) return false;
        AvlcAddress destination = ParseAddress(bytes[..4]);
        AvlcAddress source = ParseAddress(bytes.Slice(4, 4));
        byte control = bytes[8];
        AvlcFrameKind kind;
        string type;
        int? send = null, receive = null;
        bool pollFinal;
        if ((control & 1) == 0)
        {
            kind = AvlcFrameKind.Information;
            type = "I";
            send = (control >> 1) & 7;
            pollFinal = (control & 0x10) != 0;
            receive = (control >> 5) & 7;
        }
        else if ((control & 3) == 1)
        {
            kind = AvlcFrameKind.Supervisory;
            string[] names = ["RR", "RNR", "REJ", "SREJ"];
            type = names[(control >> 2) & 3];
            pollFinal = (control & 0x10) != 0;
            receive = (control >> 5) & 7;
        }
        else
        {
            kind = AvlcFrameKind.Unnumbered;
            int function = ((control >> 2) & 0x3b);
            type = function switch { 0x00 => "UI", 0x03 => "DM", 0x10 => "DISC", 0x18 => "UA", 0x21 => "FRMR", 0x2b => "XID", 0x38 => "TEST", _ => $"U/{function:X2}" };
            pollFinal = ((control >> 4) & 1) != 0;
        }
        packet = new(destination, source, control, kind, type, send, receive, pollFinal, bytes[9..].ToArray());
        return true;
    }

    public static bool TryParseAcars(ReadOnlySpan<byte> bytes, out VdlAcarsMessage? message)
    {
        message = null;
        // Raw ACARS starts at mode (SOH is omitted) and ends in ETX/ETB, BCS1, BCS2, DEL.
        if (bytes.Length < 16 || bytes[^1] != 0x7f) return false;
        int terminator = bytes.Length - 4;
        byte terminatorValue = (byte)(bytes[terminator] & 0x7f);
        if (terminatorValue is not (0x03 or 0x17)) return false;
        byte[] normalized = bytes.ToArray();
        for (int index = 0; index <= terminator; index++) normalized[index] &= 0x7f;

        string mode = Character(normalized[0]);
        string registration = Text(normalized.AsSpan(1, 7)).Trim();
        string acknowledgement = Character(normalized[8]);
        string label = Text(normalized.AsSpan(9, 2)).Trim();
        char blockId = Character(normalized[11]).FirstOrDefault(' ');
        int cursor = 12;
        if (cursor < terminator && normalized[cursor] == 0x02) cursor++;

        string messageNumber = string.Empty;
        char messageSequence = ' ';
        string flightId = string.Empty;
        if (blockId is >= '0' and <= '9')
        {
            if (terminator - cursor < 10) return false;
            messageNumber = Text(normalized.AsSpan(cursor, 3));
            messageSequence = (char)normalized[cursor + 3];
            flightId = Text(normalized.AsSpan(cursor + 4, 6)).Trim();
            cursor += 10;
        }
        string text = Text(normalized.AsSpan(cursor, Math.Max(0, terminator - cursor))).TrimEnd('\0', '\r', '\n', ' ');
        bool crcValid = ComputeCrc(bytes[..^1]) == 0;
        message = new(mode, registration, acknowledgement, label, blockId, messageNumber,
            messageSequence, flightId, text, terminatorValue == 0x03, crcValid, "単一ブロック");
        return registration.Length > 0 && label.Length > 0;
    }

    public static bool TryParseX25(ReadOnlySpan<byte> bytes, out VdlX25Packet? packet)
    {
        packet = null;
        if (bytes.Length < 3 || (bytes[0] >> 4) != 1) return false;
        int channel = ((bytes[0] & 0x0f) << 8) | bytes[1];
        byte control = bytes[2];
        bool data = (control & 1) == 0;
        int? send = data ? (control >> 1) & 7 : null;
        int? receive = data ? (control >> 5) & 7 : null;
        bool more = data && (control & 0x10) != 0;
        string packetType = data ? "DATA" : (control & 0x1f) switch
        {
            0x0b => "CALL REQUEST", 0x0f => "CALL ACCEPTED", 0x13 => "CLEAR REQUEST",
            0x17 => "CLEAR CONFIRM", 0x01 => "RR", 0x09 => "REJ", 0x1b => "RESET REQUEST",
            0x1f when control == 0x1f => "RESET CONFIRM", _ => control switch
            { 0xfb => "RESTART REQUEST", 0xff => "RESTART CONFIRM", 0xf1 => "DIAGNOSTIC", _ => $"CONTROL/{control:X2}" }
        };
        byte[] userData = bytes[3..].ToArray();
        string upper = "X.25";
        if (data && userData.Length > 0)
        {
            upper = userData[0] switch
            {
                0x81 => "X.25/CLNP", 0x82 => "X.25/ESIS", 0x85 => "X.25/IDRP",
                0xe0 => "X.25/SNDCF Error", _ when IsCompressedClnp(userData[0]) => "X.25/Compressed CLNP", _ => "X.25"
            };
        }
        packet = new(channel, packetType, send, receive, more, upper, userData);
        return true;
    }

    private VdlAcarsMessage Reassemble(VdlAcarsMessage message, AvlcPacket avlc, DateTimeOffset receivedAt)
    {
        bool downlink = message.BlockId is >= '0' and <= '9';
        int sequence = downlink && message.MessageSequence is >= 'A' and <= 'Z' ? message.MessageSequence - 'A' : 0;
        if (!downlink || (message.FinalBlock && sequence == 0)) return message;

        string key = $"{avlc.Source.Address}>{avlc.Destination.Address}:{message.Registration}:{message.Label}:{message.MessageNumber}";
        if (!acarsFragments.TryGetValue(key, out FragmentSet? set))
            acarsFragments[key] = set = new(receivedAt);
        set.LastSeen = receivedAt;
        set.Parts[sequence] = message.Text;
        if (message.FinalBlock) set.FinalSequence = sequence;
        if (set.FinalSequence is not int final || Enumerable.Range(0, final + 1).Any(index => !set.Parts.ContainsKey(index)))
            return message with { ReassemblyStatus = $"分割受信中 ({set.Parts.Count} block)" };

        string text = string.Concat(Enumerable.Range(0, final + 1).Select(index => set.Parts[index]));
        acarsFragments.Remove(key);
        return message with { Text = text, ReassemblyStatus = $"再構築完了 ({final + 1} block)" };
    }

    private void Cleanup(DateTimeOffset now)
    {
        foreach (string key in acarsFragments.Where(pair => now - pair.Value.LastSeen > FragmentLifetime).Select(pair => pair.Key).ToArray())
            acarsFragments.Remove(key);
    }

    private static AvlcAddress ParseAddress(ReadOnlySpan<byte> bytes)
    {
        uint packed = (uint)((bytes[0] >> 1) | (bytes[1] << 6) | (bytes[2] << 13) | ((bytes[3] & 0xfe) << 20));
        uint value = ReverseBits(packed, 28);
        return new(value & 0xffffff, (AvlcAddressType)((value >> 24) & 7), (value & 0x08000000) != 0);
    }

    private static uint ReverseBits(uint value, int count)
    {
        uint result = 0;
        for (int index = 0; index < count; index++) { result = (result << 1) | (value & 1); value >>= 1; }
        return result;
    }

    private static bool IsCompressedClnp(byte value)
    {
        int type = value >> 4;
        return type < 4 || type is 6 or 7 or 9 or 10;
    }

    private static ushort ComputeCrc(ReadOnlySpan<byte> values)
    {
        ushort crc = 0;
        foreach (byte value in values)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }
        return crc;
    }

    private static string Character(byte value)
    {
        char character = (char)(value & 0x7f);
        return char.IsControl(character) ? string.Empty : character.ToString();
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
        {
            char character = (char)(value & 0x7f);
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character)) result.Append(character);
        }
        return result.ToString();
    }

    private sealed class FragmentSet(DateTimeOffset lastSeen)
    {
        public DateTimeOffset LastSeen { get; set; } = lastSeen;
        public SortedDictionary<int, string> Parts { get; } = [];
        public int? FinalSequence { get; set; }
    }
}

public static partial class VdlPositionParser
{
    public static bool TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Match match = DecimalHemispheres().Match(text);
        if (match.Success && Number(match.Groups["lat"].Value, out latitude) && Number(match.Groups["lon"].Value, out longitude))
        {
            if (match.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
            if (match.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
            return Valid(latitude, longitude);
        }
        match = CompactDegreesMinutes().Match(text);
        if (!match.Success || !Number(match.Groups["latDeg"].Value, out double latDeg) ||
            !Minutes(match.Groups["latMin"].Value, out double latMin) || !Number(match.Groups["lonDeg"].Value, out double lonDeg) ||
            !Minutes(match.Groups["lonMin"].Value, out double lonMin) || latMin >= 60 || lonMin >= 60) return false;
        latitude = latDeg + latMin / 60; longitude = lonDeg + lonMin / 60;
        if (match.Groups["latHem"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) latitude = -latitude;
        if (match.Groups["lonHem"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) longitude = -longitude;
        return Valid(latitude, longitude);
    }

    private static bool Minutes(string text, out double value) => Number(text.Contains('.') ? text : text.Length == 3 ? $"{text[..2]}.{text[2]}" : text, out value);
    private static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    private static bool Valid(double latitude, double longitude) => latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    [GeneratedRegex(@"(?<latHem>[NS])\s*(?<lat>\d{1,2}\.\d+)\s*[,/ ]*\s*(?<lonHem>[EW])\s*(?<lon>\d{1,3}\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalHemispheres();
    [GeneratedRegex(@"(?<latHem>[NS])\s*(?<latDeg>\d{2})(?<latMin>\d{2}(?:\.\d+)?|\d{3})\s*[,/ ]*\s*(?<lonHem>[EW])\s*(?<lonDeg>\d{3})(?<lonMin>\d{2}(?:\.\d+)?|\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex CompactDegreesMinutes();
}
