using System.Text;

namespace SRdeckPlugin.Acars.Protocols;

/// <summary>
/// ACARS ARINC 622 AT1/FANS-1/A CPDLC unaligned PER decoder.
/// Covers the air-to-ground DM message set and the value types commonly seen
/// on VHF ACARS. Unsupported large compound bodies are preserved by the caller.
/// </summary>
internal static class AcarsCpdlcDecoder
{
    internal sealed record Header(int MessageId, int? ReferenceId, TimeSpan? Timestamp);

    internal sealed record Element(int Id, string Name, string JapaneseText, bool FullyDecoded);

    internal sealed record Message(Header Header, IReadOnlyList<Element> Elements, bool FullyDecoded);

    private static readonly string[] DownlinkTemplates =
    [
        "WILCO", "UNABLE", "STANDBY", "ROGER", "AFFIRM", "NEGATIVE",
        "REQUEST [altitude]", "REQUEST BLOCK [altitude] TO [altitude]",
        "REQUEST CRUISE CLIMB TO [altitude]", "REQUEST CLIMB TO [altitude]",
        "REQUEST DESCENT TO [altitude]", "AT [position] REQUEST CLIMB TO [altitude]",
        "AT [position] REQUEST DESCENT TO [altitude]", "AT [time] REQUEST CLIMB TO [altitude]",
        "AT [time] REQUEST DESCENT TO [altitude]", "REQUEST OFFSET [distance] [direction] OF ROUTE",
        "AT [position] REQUEST OFFSET [distance] [direction] OF ROUTE",
        "AT [time] REQUEST OFFSET [distance] [direction] OF ROUTE", "REQUEST [speed]",
        "REQUEST [speed] TO [speed]", "REQUEST VOICE CONTACT", "REQUEST VOICE CONTACT [frequency]",
        "REQUEST DIRECT TO [position]", "REQUEST [procedure]", "REQUEST [route]", "REQUEST CLEARANCE",
        "REQUEST WEATHER DEVIATION TO [position] VIA [route]",
        "REQUEST WEATHER DEVIATION UP TO [distance] [direction] OF ROUTE", "LEAVING [altitude]",
        "CLIMBING TO [altitude]", "DESCENDING TO [altitude]", "PASSING [position]",
        "PRESENT ALTITUDE [altitude]", "PRESENT POSITION [position]", "PRESENT SPEED [speed]",
        "PRESENT HEADING [degrees]", "PRESENT GROUND TRACK [degrees]", "LEVEL [altitude]",
        "ASSIGNED ALTITUDE [altitude]", "ASSIGNED SPEED [speed]", "ASSIGNED ROUTE [route]",
        "BACK ON ROUTE", "NEXT WAYPOINT [position]", "NEXT WAYPOINT ETA [time]",
        "ENSUING WAYPOINT [position]", "REPORTED WAYPOINT [position]", "REPORTED WAYPOINT [time]",
        "SQUAWKING [beacon]", "POSITION REPORT [positionreport]", "WHEN CAN WE EXPECT [speed]",
        "WHEN CAN WE EXPECT [speed] TO [speed]", "WHEN CAN WE EXPECT BACK ON ROUTE",
        "WHEN CAN WE EXPECT LOWER ALTITUDE", "WHEN CAN WE EXPECT HIGHER ALTITUDE",
        "WHEN CAN WE EXPECT CRUISE CLIMB TO [altitude]", "PAN PAN PAN", "MAYDAY MAYDAY MAYDAY",
        "[fuel] OF FUEL REMAINING AND [souls] SOULS ON BOARD", "CANCEL EMERGENCY",
        "DIVERTING TO [position] VIA [route]", "OFFSETTING [distance] [direction] OF ROUTE",
        "DESCENDING TO [altitude]", "ERROR [error]", "NOT CURRENT DATA AUTHORITY", "[facility]",
        "DUE TO WEATHER", "DUE TO AIRCRAFT PERFORMANCE", "[freetext]", "[freetext]",
        "REQUEST VMC DESCENT", "REQUEST HEADING [degrees]", "REQUEST GROUND TRACK [degrees]",
        "REACHING [altitude]", "[version]", "MAINTAIN OWN SEPARATION AND VMC",
        "AT PILOTS DISCRETION", "REACHING BLOCK [altitude] TO [altitude]",
        "ASSIGNED BLOCK [altitude] TO [altitude]", "AT [time] [distance] [tofrom] [position]",
        "ATIS [atis]", "DEVIATING [distance] [direction] OF ROUTE"
    ];

    public static bool TryDecodeDownlink(string payloadHex, out Message? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(payloadHex) || payloadHex.Length % 2 != 0) return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(payloadHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var bits = new BitReader(bytes);
        if (!bits.TryReadBool(out bool hasAdditional) ||
            !bits.TryReadBool(out bool hasReference) ||
            !bits.TryReadBool(out bool hasTimestamp) ||
            !bits.TryRead(6, out int messageId)) return false;

        int? referenceId = null;
        if (hasReference)
        {
            if (!bits.TryRead(6, out int value)) return false;
            referenceId = value;
        }

        TimeSpan? timestamp = null;
        if (hasTimestamp)
        {
            if (!bits.TryRead(5, out int hour) || !bits.TryRead(6, out int minute) ||
                !bits.TryRead(6, out int second)) return false;
            if (hour < 24 && minute < 60 && second < 60)
                timestamp = new TimeSpan(hour, minute, second);
        }

        var elements = new List<Element>();
        if (!TryReadElement(bits, out Element? first) || first == null) return false;
        elements.Add(first);
        bool fullyDecoded = first.FullyDecoded;

        if (hasAdditional && fullyDecoded)
        {
            if (!bits.TryRead(2, out int encodedCount)) fullyDecoded = false;
            else
            {
                int count = encodedCount + 1;
                for (int i = 0; i < count; i++)
                {
                    if (!TryReadElement(bits, out Element? additional) || additional == null)
                    {
                        fullyDecoded = false;
                        break;
                    }
                    elements.Add(additional);
                    fullyDecoded &= additional.FullyDecoded;
                }
            }
        }

        fullyDecoded &= first.FullyDecoded && bits.HasOnlyZeroPadding();
        message = new Message(new Header(messageId, referenceId, timestamp), elements, fullyDecoded);
        return true;
    }

    private static bool TryReadElement(BitReader bits, out Element? element)
    {
        element = null;
        if (!bits.TryRead(8, out int id) || id >= DownlinkTemplates.Length) return false;

        string template = DownlinkTemplates[id];
        List<string> values = [];
        bool decoded = TryReadBody(id, bits, values);
        string text = decoded ? FormatJapanese(id, values) : $"DM{id}: {template}";
        element = new Element(id, $"DM{id}", text, decoded);
        return true;
    }

    private static bool TryReadBody(int id, BitReader bits, List<string> values)
    {
        if (id is 0 or 1 or 2 or 3 or 4 or 5 or 20 or 25 or 41 or 51 or 52 or 53 or
            55 or 56 or 58 or 63 or 65 or 66 or 69 or 74 or 75) return true;

        if (id is 6 or 8 or 9 or 10 or 28 or 29 or 30 or 32 or 37 or 38 or 54 or 61 or 72)
            return TryReadAltitude(bits, out string altitude) && Add(values, altitude);

        if (id is 7 or 76 or 77)
            return TryReadAltitude(bits, out string altitude1) && Add(values, altitude1) &&
                TryReadAltitude(bits, out string altitude2) && Add(values, altitude2);

        if (id is 11 or 12)
            return TryReadPosition(bits, out string position) && Add(values, position) &&
                TryReadAltitude(bits, out string positionAltitude) && Add(values, positionAltitude);

        if (id is 13 or 14)
            return TryReadTime(bits, out string time) && Add(values, time) &&
                TryReadAltitude(bits, out string timeAltitude) && Add(values, timeAltitude);

        if (id is 15 or 27 or 60 or 80)
            return TryReadDistanceOffset(bits, out string distance) && Add(values, distance) &&
                TryReadDirection(bits, out string direction) && Add(values, direction);

        if (id == 16)
            return TryReadPosition(bits, out string offsetPosition) && Add(values, offsetPosition) &&
                TryReadDistanceOffset(bits, out string offsetDistance) && Add(values, offsetDistance) &&
                TryReadDirection(bits, out string offsetDirection) && Add(values, offsetDirection);

        if (id == 17)
            return TryReadTime(bits, out string offsetTime) && Add(values, offsetTime) &&
                TryReadDistanceOffset(bits, out string timeDistance) && Add(values, timeDistance) &&
                TryReadDirection(bits, out string timeDirection) && Add(values, timeDirection);

        if (id is 18 or 34 or 39 or 49)
            return TryReadSpeed(bits, out string speed) && Add(values, speed);

        if (id is 19 or 50)
            return TryReadSpeed(bits, out string speed1) && Add(values, speed1) &&
                TryReadSpeed(bits, out string speed2) && Add(values, speed2);

        if (id == 21) return TryReadFrequency(bits, out string frequency) && Add(values, frequency);

        if (id is 22 or 31 or 33 or 42 or 44 or 45)
            return TryReadPosition(bits, out string simplePosition) && Add(values, simplePosition);

        if (id == 23) return TryReadProcedure(bits, out string procedure) && Add(values, procedure);
        if (id is 35 or 36 or 70 or 71)
            return TryReadDegrees(bits, out string degrees) && Add(values, degrees);
        if (id is 43 or 46) return TryReadTime(bits, out string simpleTime) && Add(values, simpleTime);
        if (id == 47) return TryReadBeacon(bits, out string beacon) && Add(values, beacon);
        if (id == 62) return TryReadError(bits, out string error) && Add(values, error);
        if (id == 64) return TryReadIa5(bits, 4, out string facility) && Add(values, facility);
        if (id is 67 or 68) return TryReadFreeText(bits, out string freeText) && Add(values, freeText);
        if (id == 73) return bits.TryRead(4, out int version) && Add(values, version.ToString());
        if (id == 79) return TryReadIa5(bits, 1, out string atis) && Add(values, atis);

        // Route clearances, full position reports, emergency fuel/souls and
        // DM78 use larger compound ASN.1 types. Their IDs are still retained.
        return false;
    }

    private static string FormatJapanese(int id, IReadOnlyList<string> v) => id switch
    {
        0 => "WILCO（管制指示に従います）",
        1 => "UNABLE（指示に従えません）",
        2 => "STANDBY（待機してください）",
        3 => "ROGER（了解）",
        4 => "AFFIRM（肯定）",
        5 => "NEGATIVE（否定）",
        6 => $"高度要求: {v[0]}",
        7 => $"ブロック高度要求: {v[0]}～{v[1]}",
        8 => $"巡航上昇要求: {v[0]}",
        9 => $"上昇要求: {v[0]}",
        10 => $"降下要求: {v[0]}",
        11 => $"{v[0]}で上昇要求: {v[1]}",
        12 => $"{v[0]}で降下要求: {v[1]}",
        13 => $"{v[0]}に上昇要求: {v[1]}",
        14 => $"{v[0]}に降下要求: {v[1]}",
        15 => $"経路オフセット要求: {v[0]} {v[1]}",
        16 => $"{v[0]}で経路オフセット要求: {v[1]} {v[2]}",
        17 => $"{v[0]}に経路オフセット要求: {v[1]} {v[2]}",
        18 => $"速度要求: {v[0]}",
        19 => $"速度範囲要求: {v[0]}～{v[1]}",
        20 => "音声交信を要求",
        21 => $"音声交信を要求: {v[0]}",
        22 => $"直行要求: {v[0]}",
        23 => $"方式要求: {v[0]}",
        25 => "クリアランス要求",
        27 => $"気象回避要求: 経路から{v[0]} {v[1]}",
        28 => $"高度離脱中: {v[0]}",
        29 => $"上昇中: {v[0]}",
        30 => $"降下中: {v[0]}",
        31 => $"通過地点: {v[0]}",
        32 => $"現在高度: {v[0]}",
        33 => $"現在位置: {v[0]}",
        34 => $"現在速度: {v[0]}",
        35 => $"現在針路: {v[0]}",
        36 => $"現在対地航跡: {v[0]}",
        37 => $"水平飛行高度: {v[0]}",
        38 => $"指定高度: {v[0]}",
        39 => $"指定速度: {v[0]}",
        41 => "経路へ復帰",
        42 => $"次のウェイポイント: {v[0]}",
        43 => $"次のウェイポイントETA: {v[0]}",
        44 => $"次々ウェイポイント: {v[0]}",
        45 => $"報告ウェイポイント: {v[0]}",
        46 => $"報告時刻: {v[0]}",
        47 => $"スコーク: {v[0]}",
        49 => $"速度{v[0]}はいつ可能か照会",
        50 => $"速度{v[0]}～{v[1]}はいつ可能か照会",
        51 => "経路復帰可能時刻を照会",
        52 => "より低い高度が可能な時刻を照会",
        53 => "より高い高度が可能な時刻を照会",
        54 => $"巡航上昇{v[0]}が可能な時刻を照会",
        55 => "PAN PAN PAN（緊急状態）",
        56 => "MAYDAY MAYDAY MAYDAY（遭難状態）",
        58 => "緊急状態を取消",
        60 => $"経路からオフセット中: {v[0]} {v[1]}",
        61 => $"降下中: {v[0]}",
        62 => $"CPDLCエラー: {v[0]}",
        63 => "現在の管制データ権限機関ではありません",
        64 => $"管制施設: {v[0]}",
        65 => "理由: 気象",
        66 => "理由: 航空機性能",
        67 or 68 => $"フリーテキスト: {v[0]}",
        69 => "有視界気象状態での降下を要求",
        70 => $"針路要求: {v[0]}",
        71 => $"対地航跡要求: {v[0]}",
        72 => $"高度到達: {v[0]}",
        73 => $"CPDLCバージョン: {v[0]}",
        74 => "自機間隔・有視界飛行を維持",
        75 => "操縦士の裁量で実施",
        76 => $"ブロック高度到達: {v[0]}～{v[1]}",
        77 => $"指定ブロック高度: {v[0]}～{v[1]}",
        79 => $"ATISコード: {v[0]}",
        80 => $"経路から逸脱中: {v[0]} {v[1]}",
        _ => $"DM{id}: {DownlinkTemplates[id]}"
    };

    private static bool TryReadAltitude(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(3, out int type)) return false;
        int count = type switch { 0 or 2 => 12, 1 => 14, 3 => 13, 4 => 18, 5 => 16, 6 => 10, 7 => 11, _ => 0 };
        if (!bits.TryRead(count, out int raw)) return false;
        value = type switch
        {
            0 => $"{raw:N0} ft QNH", 1 => $"{raw:N0} m QNH", 2 => $"{raw:N0} ft QFE",
            3 => $"{raw:N0} m QFE", 4 => $"{raw:N0} ft GNSS", 5 => $"{raw:N0} m GNSS",
            6 => $"FL{raw + 30:D3}", 7 => $"M{raw + 100}", _ => raw.ToString()
        };
        return true;
    }

    private static bool TryReadTime(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(5, out int hour) || !bits.TryRead(6, out int minute)) return false;
        value = hour < 24 && minute < 60 ? $"{hour:D2}:{minute:D2} UTC" : $"{hour:D2}:{minute:D2}";
        return true;
    }

    private static bool TryReadDistanceOffset(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryReadBool(out bool metric)) return false;
        if (!bits.TryRead(metric ? 8 : 7, out int raw)) return false;
        value = $"{raw + 1} {(metric ? "km" : "NM")}";
        return true;
    }

    private static bool TryReadDirection(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(4, out int raw)) return false;
        string[] directions = ["左", "右", "左右いずれか", "北", "南", "東", "西", "北東", "北西", "南東", "南西"];
        if (raw >= directions.Length) return false;
        value = directions[raw];
        return true;
    }

    private static bool TryReadSpeed(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(3, out int type)) return false;
        int count = type switch { 0 => 5, 1 => 7, 2 => 6, 3 => 7, 4 => 6, 5 => 8, 6 => 5, 7 => 9, _ => 0 };
        if (!bits.TryRead(count, out int raw)) return false;
        value = type switch
        {
            0 => $"{raw + 7} kt IAS", 1 => $"{raw + 10} km/h IAS", 2 => $"{raw + 7} kt TAS",
            3 => $"{raw + 10} km/h TAS", 4 => $"{raw + 7} kt GS", 5 => $"{raw + 10} km/h GS",
            6 => $"Mach 0.{raw + 61:D2}", 7 => $"Mach {(raw + 93) / 1000.0:0.000}", _ => raw.ToString()
        };
        return true;
    }

    private static bool TryReadDegrees(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(9, out int raw) || !bits.TryReadBool(out bool isTrue)) return false;
        value = $"{raw + 1}°{(isTrue ? "T" : "M")}";
        return true;
    }

    private static bool TryReadFrequency(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(2, out int type)) return false;
        if (type == 3)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                if (!bits.TryRead(4, out int code) || code > 10) return false;
                sb.Append(code == 0 ? ' ' : (char)('0' + code - 1));
            }
            value = $"SAT {sb.ToString().Trim()}";
            return true;
        }

        int count = type == 2 ? 18 : 15;
        if (!bits.TryRead(count, out int raw)) return false;
        int khz = raw + (type switch { 0 => 2_850, 1 => 117_000, _ => 225_000 });
        value = type == 0 ? $"{khz} kHz HF" : $"{khz / 1000.0:0.000} MHz";
        return true;
    }

    private static bool TryReadPosition(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(3, out int type)) return false;
        if (type == 0)
        {
            if (!bits.TryRead(3, out int rawLength)) return false;
            return TryReadIa5(bits, rawLength + 1, out value);
        }
        if (type == 1)
        {
            if (!bits.TryRead(2, out int rawLength)) return false;
            return TryReadIa5(bits, rawLength + 1, out value);
        }
        if (type == 2) return TryReadIa5(bits, 4, out value);
        if (type != 3) return false;

        if (!TryReadCoordinate(bits, 7, out double latitude) ||
            !TryReadCoordinate(bits, 8, out double longitude)) return false;
        value = $"{(latitude >= 0 ? 'N' : 'S')}{Math.Abs(latitude):00.0000}° " +
            $"{(longitude >= 0 ? 'E' : 'W')}{Math.Abs(longitude):000.0000}°";
        return true;
    }

    private static bool TryReadCoordinate(BitReader bits, int degreeBits, out double value)
    {
        value = 0;
        if (!bits.TryReadBool(out bool hasMinutes) || !bits.TryRead(degreeBits, out int degrees)) return false;
        double minutes = 0;
        if (hasMinutes)
        {
            if (!bits.TryRead(10, out int rawMinutes)) return false;
            minutes = rawMinutes / 10.0;
        }
        if (!bits.TryReadBool(out bool negative) || minutes >= 60) return false;
        value = degrees + minutes / 60.0;
        if (negative) value = -value;
        return true;
    }

    private static bool TryReadProcedure(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryReadBool(out bool hasTransition) || !bits.TryRead(2, out int type) || type > 2 ||
            !bits.TryRead(3, out int rawLength) || !TryReadIa5(bits, rawLength + 1, out string name)) return false;
        string typeName = type switch { 0 => "到着方式", 1 => "進入方式", _ => "出発方式" };
        if (hasTransition)
        {
            if (!bits.TryRead(3, out int transitionLength) ||
                !TryReadIa5(bits, transitionLength + 1, out string transition)) return false;
            value = $"{typeName} {name}（トランジション {transition}）";
        }
        else value = $"{typeName} {name}";
        return true;
    }

    private static bool TryReadBeacon(BitReader bits, out string value)
    {
        value = string.Empty;
        var sb = new StringBuilder(4);
        for (int i = 0; i < 4; i++)
        {
            if (!bits.TryRead(3, out int digit) || digit > 7) return false;
            sb.Append((char)('0' + digit));
        }
        value = sb.ToString();
        return true;
    }

    private static bool TryReadError(BitReader bits, out string value)
    {
        value = string.Empty;
        if (!bits.TryRead(5, out int code) || code > 16) return false;
        string[] errors =
        [
            "アプリケーションエラー", "メッセージID重複", "参照メッセージID不明", "保留メッセージありでサービス終了",
            "有効応答なしでサービス終了", "メッセージ保存容量不足", "利用可能なメッセージIDなし", "指示による終了",
            "データ不足", "予期しないデータ", "無効なデータ", "予約エラー1", "予約エラー2", "予約エラー3",
            "予約エラー4", "予約エラー5", "予約エラー6"
        ];
        value = errors[code];
        return true;
    }

    private static bool TryReadFreeText(BitReader bits, out string value)
    {
        value = string.Empty;
        return bits.TryRead(8, out int rawLength) && TryReadIa5(bits, rawLength + 1, out value);
    }

    private static bool TryReadIa5(BitReader bits, int length, out string value)
    {
        value = string.Empty;
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            if (!bits.TryRead(7, out int character)) return false;
            sb.Append((char)character);
        }
        value = sb.ToString().TrimEnd();
        return true;
    }

    private static bool Add(List<string> values, string value)
    {
        values.Add(value);
        return true;
    }

    private sealed class BitReader(byte[] bytes)
    {
        private int _position;

        public bool TryReadBool(out bool value)
        {
            bool success = TryRead(1, out int bit);
            value = bit != 0;
            return success;
        }

        public bool TryRead(int count, out int value)
        {
            value = 0;
            if (count < 0 || _position + count > bytes.Length * 8) return false;
            for (int i = 0; i < count; i++)
            {
                int bit = (bytes[_position / 8] >> (7 - _position % 8)) & 1;
                value = (value << 1) | bit;
                _position++;
            }
            return true;
        }

        public bool HasOnlyZeroPadding()
        {
            int remaining = bytes.Length * 8 - _position;
            if (remaining > 7) return false;
            while (_position < bytes.Length * 8)
            {
                if (!TryRead(1, out int bit) || bit != 0) return false;
            }
            return true;
        }
    }
}
