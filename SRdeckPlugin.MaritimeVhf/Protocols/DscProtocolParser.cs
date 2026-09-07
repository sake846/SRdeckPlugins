using SRdeckPlugin.MaritimeVhf.Models;

namespace SRdeckPlugin.MaritimeVhf.Protocols;

internal static class DscProtocolParser
{
    private static readonly HashSet<byte> FormatSpecifiers = [102, 112, 114, 116, 120, 123];

    public static bool IsFormatSpecifier(byte value) => FormatSpecifiers.Contains(value);

    public static bool TryParse(
        ReadOnlySpan<byte> symbols,
        DateTimeOffset receivedAt,
        Guid streamId,
        long samplePosition,
        out DscMessage? message)
    {
        message = null;
        if (symbols.Length < 8 || symbols[0] != symbols[1] ||
            !IsFormatSpecifier(symbols[0])) return false;

        int closingIndex = FindClosingSequence(symbols);
        if (closingIndex < 0) return false;
        byte eos = symbols[closingIndex];
        byte expectedEcc = symbols[0];
        for (int index = 2; index <= closingIndex; index++) expectedEcc ^= symbols[index];
        if (symbols[closingIndex + 1] != expectedEcc) return false;

        ReadOnlySpan<byte> content = symbols[2..closingIndex];
        byte format = symbols[0];
        DscPriority priority;
        string source = string.Empty;
        string destination = string.Empty;
        string kind;
        int? nature = null;
        double? latitude = null;
        double? longitude = null;
        TimeOnly? positionTime = null;
        int workingDataStart = content.Length;

        if (format == 112)
        {
            if (content.Length < 14 || !TryDecodeMmsi(content[..5], out source)) return false;
            priority = DscPriority.Distress;
            kind = "遭難警報";
            nature = content[5] is >= 100 and <= 110 ? content[5] : null;
            TryDecodePosition(content.Slice(6, 5), out latitude, out longitude);
            positionTime = TryDecodeTime(content.Slice(11, 2));
        }
        else if (format == 116 && content.Length >= 1 && content[0] == 112)
        {
            // Distress acknowledgement / relay acknowledgement.
            if (content.Length < 7 || !TryDecodeMmsi(content.Slice(1, 5), out source)) return false;
            priority = DscPriority.Distress;
            kind = content.Length > 6 && content[6] == 110 ? "遭難確認" : "遭難中継確認";
            if (content.Length >= 13) TryDecodeMmsi(content.Slice(7, 5), out destination);
            if (content.Length >= 14 && content[12] is >= 100 and <= 110) nature = content[12];
        }
        else if (format == 116)
        {
            if (content.Length < 8 || !TryDecodeMmsi(content.Slice(1, 5), out source)) return false;
            priority = PriorityFor(content[0]);
            kind = "全船呼出";
            workingDataStart = 8;
        }
        else
        {
            if (content.Length < 13) return false;
            if (format == 102)
            {
                destination = "海域指定";
            }
            else if (!TryDecodeMmsi(content[..5], out destination))
            {
                return false;
            }
            if (!TryDecodeMmsi(content.Slice(6, 5), out source)) return false;
            priority = PriorityFor(content[5]);
            kind = format switch
            {
                102 => "海域呼出",
                114 => "グループ呼出",
                123 => "自動接続呼出",
                _ => "個別呼出"
            };
            workingDataStart = 13;
        }

        DscWorkingChannel? working = FindWorkingChannel(content, workingDataStart);
        string summary = BuildSummary(kind, priority, source, destination, working, nature,
            latitude, longitude, positionTime);
        message = new DscMessage(receivedAt, streamId, samplePosition, format, priority,
            source, destination, kind, summary, working, nature, latitude, longitude,
            positionTime, eos, symbols[..(closingIndex + 4)].ToArray());
        return true;
    }

    public static byte CalculateEcc(ReadOnlySpan<byte> logicalSymbolsBeforeClosing, byte eos)
    {
        if (logicalSymbolsBeforeClosing.Length < 2 ||
            logicalSymbolsBeforeClosing[0] != logicalSymbolsBeforeClosing[1])
            throw new ArgumentException("Two identical format specifiers are required.",
                nameof(logicalSymbolsBeforeClosing));
        byte ecc = logicalSymbolsBeforeClosing[0];
        for (int index = 2; index < logicalSymbolsBeforeClosing.Length; index++)
            ecc ^= logicalSymbolsBeforeClosing[index];
        return (byte)(ecc ^ eos);
    }

    private static int FindClosingSequence(ReadOnlySpan<byte> symbols)
    {
        for (int index = 2; index + 3 < symbols.Length; index++)
        {
            byte value = symbols[index];
            if (value is 117 or 122 or 127 &&
                symbols[index + 2] == value && symbols[index + 3] == value)
                return index;
        }
        return -1;
    }

    private static DscPriority PriorityFor(byte category) => category switch
    {
        112 => DscPriority.Distress,
        110 => DscPriority.Urgency,
        108 => DscPriority.Safety,
        _ => DscPriority.Routine
    };

    private static bool TryDecodeMmsi(ReadOnlySpan<byte> values, out string mmsi)
    {
        mmsi = string.Empty;
        if (values.Length < 5 || values[..5].ContainsAnyInRange((byte)100, byte.MaxValue))
            return false;
        string digits = string.Concat(values[..5].ToArray().Select(value => value.ToString("00")));
        if (digits.Length != 10) return false;
        mmsi = digits[..9];
        return true;
    }

    private static DscWorkingChannel? FindWorkingChannel(
        ReadOnlySpan<byte> content,
        int start)
    {
        for (int index = Math.Clamp(start, 0, content.Length); index + 2 < content.Length; index++)
        {
            if (content[index] != 90 || content[index + 1] > 29 || content[index + 2] > 99)
                continue;
            int mode = content[index + 1] / 10;
            int hundreds = content[index + 1] % 10;
            int channel = hundreds * 100 + content[index + 2];
            if (channel <= 0 || MaritimeVhfChannelPlan.Find(channel) is null) continue;
            MaritimeVhfReceiveSide? side = mode switch
            {
                1 => MaritimeVhfReceiveSide.Ship,
                2 => MaritimeVhfReceiveSide.Coast,
                _ => null
            };
            return new(channel, side);
        }
        return null;
    }

    private static void TryDecodePosition(
        ReadOnlySpan<byte> values,
        out double? latitude,
        out double? longitude)
    {
        latitude = null;
        longitude = null;
        if (values.Length < 5) return;
        for (int index = 0; index < 5; index++)
            if (values[index] > 99) return;
        string digits = string.Concat(values[..5].ToArray().Select(value => value.ToString("00")));
        if (digits == "9999999999" || digits.Length != 10) return;
        int quadrant = digits[0] - '0';
        if (quadrant is < 0 or > 3 ||
            !int.TryParse(digits.AsSpan(1, 2), out int latDegrees) ||
            !int.TryParse(digits.AsSpan(3, 2), out int latMinutes) ||
            !int.TryParse(digits.AsSpan(5, 3), out int lonDegrees) ||
            !int.TryParse(digits.AsSpan(8, 2), out int lonMinutes) ||
            latDegrees > 90 || lonDegrees > 180 || latMinutes > 59 || lonMinutes > 59) return;
        latitude = (latDegrees + latMinutes / 60.0) * (quadrant >= 2 ? -1 : 1);
        longitude = (lonDegrees + lonMinutes / 60.0) * (quadrant is 1 or 3 ? -1 : 1);
    }

    private static TimeOnly? TryDecodeTime(ReadOnlySpan<byte> values)
    {
        if (values.Length < 2 || values[0] > 99 || values[1] > 99 ||
            values[0] == 88 && values[1] == 88) return null;
        int hour = values[0];
        int minute = values[1];
        return hour <= 23 && minute <= 59 ? new TimeOnly(hour, minute) : null;
    }

    private static string BuildSummary(
        string kind,
        DscPriority priority,
        string source,
        string destination,
        DscWorkingChannel? working,
        int? nature,
        double? latitude,
        double? longitude,
        TimeOnly? positionTime)
    {
        var parts = new List<string> { kind, PriorityText(priority) };
        if (source.Length > 0) parts.Add($"送信 {source}");
        if (destination.Length > 0) parts.Add($"宛先 {destination}");
        if (nature is not null) parts.Add(NatureText(nature.Value));
        if (latitude is not null && longitude is not null)
            parts.Add($"{latitude:F3}, {longitude:F3}");
        if (positionTime is not null) parts.Add($"{positionTime:HH\\:mm} UTC");
        if (working is not null) parts.Add($"作業 {working.DisplayName}");
        return string.Join(" / ", parts);
    }

    public static string PriorityText(DscPriority priority) => priority switch
    {
        DscPriority.Distress => "遭難",
        DscPriority.Urgency => "緊急",
        DscPriority.Safety => "安全",
        _ => "通常"
    };

    public static string NatureText(int nature) => nature switch
    {
        100 => "火災・爆発",
        101 => "浸水",
        102 => "衝突",
        103 => "座礁",
        104 => "傾斜・転覆危険",
        105 => "沈没",
        106 => "航行不能・漂流",
        107 => "遭難種別未指定",
        108 => "退船",
        109 => "海賊・武装強盗",
        110 => "落水者",
        _ => $"遭難種別 {nature}"
    };
}
