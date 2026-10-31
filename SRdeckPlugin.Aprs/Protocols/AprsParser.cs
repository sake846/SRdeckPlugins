using System.Globalization;
using SRdeckPlugin.Aprs.Models;

namespace SRdeckPlugin.Aprs.Protocols;

/// <summary>APRS 1.0 position, message, status, object/item and telemetry envelopes.</summary>
public static partial class AprsParser
{
    public static AprsPacket Parse(Ax25Frame frame, DateTimeOffset receivedAt, Guid streamId, long frequencyHz)
    {
        string info = frame.Information;
        var packet = new AprsPacket(receivedAt, streamId, frequencyHz, frame.Source,
            frame.Destination, string.Join(',', frame.Path), "未対応", info);
        if (info.Length == 0) return packet;
        ReadOnlySpan<char> position = default;
        switch (info[0])
        {
            case '!': case '=':
                packet = packet with { Kind = "位置" };
                position = info.AsSpan(1);
                break;
            case '/': case '@':
                packet = packet with { Kind = "位置（時刻付）" };
                if (info.Length >= 8 && IsTimestamp(info.AsSpan(1, 7))) position = info.AsSpan(8);
                break;
            case ';':
                packet = packet with { Kind = "オブジェクト" };
                if (info.Length >= 18 && info[10] is '*' or '_' && IsTimestamp(info.AsSpan(11, 7)))
                {
                    packet = packet with { EntityName = info.Substring(1, 9).TrimEnd(), IsAlive = info[10] == '*' };
                    position = info.AsSpan(18);
                }
                break;
            case ')':
                packet = packet with { Kind = "アイテム" };
                int end = info.IndexOfAny(['!', '_'], 1);
                if (end is >= 4 and <= 10)
                {
                    packet = packet with { EntityName = info[1..end], IsAlive = info[end] == '!' };
                    position = info.AsSpan(end + 1);
                }
                break;
            case ':':
                packet = packet with { Kind = "メッセージ" };
                if (info.Length >= 11 && info[10] == ':')
                {
                    string message = info[11..];
                    int id = message.LastIndexOf('{');
                    packet = packet with
                    {
                        Addressee = info.Substring(1, 9).TrimEnd(),
                        Message = id >= 0 ? message[..id] : message,
                        MessageId = id >= 0 ? message[(id + 1)..] : null
                    };
                }
                break;
            case '>': packet = packet with { Kind = "ステータス", Message = info[1..] }; break;
            case '_':
                packet = packet with { Kind = "気象" };
                if (info.Length >= 9 && info.AsSpan(1, 8).IndexOfAnyExceptInRange('0', '9') < 0)
                    packet = packet with { Weather = ParseWeather(info.AsSpan(9)) };
                break;
            case 'T' when info.StartsWith("T#", StringComparison.Ordinal):
                packet = packet with { Kind = "テレメトリ", Telemetry = ParseTelemetry(info.AsSpan(2)) };
                break;
            case '\'': case '`': return ParseMicE(packet);
            case '}': packet = packet with { Kind = "第三者パケット（未解析）" }; break;
        }
        if (TryPosition(position, out double lat, out double lon, out string? symbol))
        {
            packet = packet with { Latitude = lat, Longitude = lon, Symbol = symbol };
            if (symbol![1] == '_')
            {
                bool plain = position.Length >= 19 && position[7] is 'N' or 'S';
                packet = packet with { Weather = ParseWeather(position[(plain ? 19 : 13)..], plain) };
                if (!plain && position[10] is >= '!' and <= 'z' && position[11] is >= '!' and <= 'z' &&
                    ((position[12] - 33) & 0x18) != 0x10 && position[10] - 33 < 90)
                    packet = packet with { Weather = packet.Weather! with
                    {
                        WindDirectionDegrees = (position[10] - 33) * 4,
                        WindSpeedMetersPerSecond = (Math.Pow(1.08, position[11] - 33) - 1) * 0.5144444444
                    } };
            }
        }
        return packet;
    }

    private static bool IsTimestamp(ReadOnlySpan<char> text) => text.Length == 7 &&
        text[..6].IndexOfAnyExceptInRange('0', '9') < 0 && text[6] is 'z' or 'h' or '/';

    public static bool TryPosition(ReadOnlySpan<char> text, out double latitude, out double longitude, out string? symbol)
    {
        latitude = longitude = 0; symbol = null;
        if (text.Length >= 19 && text[7] is 'N' or 'S' && text[17] is 'E' or 'W')
        {
            if (!Coordinate(text[..7], 2, 90, out latitude) ||
                !Coordinate(text.Slice(9, 8), 3, 180, out longitude) || !SymbolTable(text[8]) ||
                text[18] is < '!' or > '~') return false;
            if (text[7] == 'S') latitude = -latitude;
            if (text[17] == 'W') longitude = -longitude;
            symbol = $"{text[8]}{text[18]}";
            return true;
        }
        if (text.Length < 13 || !SymbolTable(text[0]) || text[9] is < '!' or > '~' ||
            !Base91(text.Slice(1, 4), out int y) || !Base91(text.Slice(5, 4), out int x)) return false;
        latitude = 90 - y / 380926.0;
        longitude = -180 + x / 190463.0;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return false;
        symbol = $"{text[0]}{text[9]}";
        return true;
    }

    private static bool SymbolTable(char c) => c is '/' or '\\' or >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'j';

    private static bool Coordinate(ReadOnlySpan<char> text, int degrees, int limit, out double value)
    {
        value = 0;
        if (text[degrees + 2] != '.' || text[..degrees].IndexOfAnyExceptInRange('0', '9') >= 0) return false;
        // APRS ambiguity replaces trailing minute digits with spaces. Place the
        // marker at the centre of the indicated cell, never at an invented edge.
        Span<char> minutes = stackalloc char[5];
        text[degrees..].CopyTo(minutes);
        bool blank = false;
        int firstBlank = -1;
        for (int i = 0; i < minutes.Length; i++)
        {
            if (i == 2) continue;
            if (minutes[i] == ' ') { if (!blank) firstBlank = i; blank = true; minutes[i] = '0'; }
            else if (minutes[i] is < '0' or > '9' || blank) return false;
        }
        if (firstBlank >= 0) minutes[firstBlank] = firstBlank == 0 ? '3' : '5';
        int deg = int.Parse(text[..degrees], CultureInfo.InvariantCulture);
        if (!double.TryParse(minutes, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double min) ||
            min >= 60 || deg > limit || deg == limit && min != 0) return false;
        value = deg + min / 60;
        return true;
    }

    private static bool Base91(ReadOnlySpan<char> chars, out int value)
    {
        value = 0;
        foreach (char c in chars)
        {
            if (c is < '!' or > '{') return false;
            value = value * 91 + c - 33;
        }
        return true;
    }
}
