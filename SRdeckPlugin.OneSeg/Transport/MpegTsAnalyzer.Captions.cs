using System.Buffers.Binary;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Transport;

internal sealed partial class MpegTsAnalyzer
{
    private void ProcessCaptionPes(ushort pid, ReadOnlySpan<byte> payload, bool start)
    {
        if (!captionPes.TryGetValue(pid, out CaptionPesAssembler? assembler))
            captionPes[pid] = assembler = new();
        foreach (byte[] packet in assembler.Push(payload, start)) ParseCaptionPes(pid, packet);
    }

    private void ParseCaptionPes(ushort pid, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || packet[0] != 0 || packet[1] != 0 || packet[2] != 1) return;
        int payloadOffset = 9 + packet[8];
        if (payloadOffset + 3 > packet.Length) return;
        ReadOnlySpan<byte> payload = packet[payloadOffset..];
        if (payload[0] is not (0x80 or 0x81) || payload[1] != 0xff) return;
        int groupOffset = 3 + (payload[2] & 0x0f);
        while (groupOffset + 7 <= payload.Length)
        {
            byte groupId = (byte)(payload[groupOffset] >> 2);
            byte version = (byte)(payload[groupOffset] & 0x03);
            byte link = payload[groupOffset + 1];
            byte lastLink = payload[groupOffset + 2];
            int size = (payload[groupOffset + 3] << 8) | payload[groupOffset + 4];
            int totalLength = 5 + size + 2;
            if (groupOffset + totalLength > payload.Length) break;
            AddCaptionDataGroup(pid, groupId, version, link, lastLink,
                payload.Slice(groupOffset + 5, size));
            groupOffset += totalLength;
        }
    }

    private void AddCaptionDataGroup(ushort pid, byte groupId, byte version, byte link,
        byte lastLink, ReadOnlySpan<byte> data)
    {
        var key = (pid, groupId, version);
        if (link == 0)
            captionGroups[key] = new CaptionDataGroupAssembler(lastLink);
        if (!captionGroups.TryGetValue(key, out CaptionDataGroupAssembler? group) ||
            group.LastLink != lastLink || group.NextLink != link)
        {
            captionGroups.Remove(key);
            return;
        }
        group.Data.AddRange(data);
        group.NextLink++;
        if (link != lastLink) return;
        captionGroups.Remove(key);
        ParseCaptionStatement(pid, groupId, group.Data.ToArray());
    }

    private void ParseCaptionStatement(ushort pid, byte groupId, ReadOnlySpan<byte> data)
    {
        int group = groupId & 0x1f;
        // Statement groups are 1..8. Group 0 is caption management data and can
        // also carry DRCS pattern data, so locate its data-unit loop separately.
        if (group > 8 || data.Length < 4) return;
        int offset = group == 0 ? CaptionManagementDataUnitLoopOffset(data) : 1;
        if (offset < 0) return;
        if (group != 0)
        {
            int tmd = data[0] >> 6;
            if (tmd is 1 or 2) offset += 5;
        }
        if (offset + 3 > data.Length) return;
        int loopLength = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
        offset += 3;
        int end = Math.Min(data.Length, offset + loopLength);
        ReadCaptionDrcs(pid, data[offset..end]);
        var text = new System.Text.StringBuilder();
        bool clear = false;
        while (offset + 5 <= end)
        {
            if (data[offset] != 0x1f) { offset++; continue; }
            byte parameter = data[offset + 1];
            int size = (data[offset + 2] << 16) | (data[offset + 3] << 8) | data[offset + 4];
            offset += 5;
            if (offset + size > end) break;
            if (parameter == 0x20)
            {
                ReadOnlySpan<byte> statement = data.Slice(offset, size);
                CaptionStatementDataReceived?.Invoke(pid, statement.ToArray());
                clear |= statement.Contains((byte)0x0c);
                string decoded = AribTextDecoder.Decode(statement, drcs);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    if (text.Length > 0) text.AppendLine();
                    text.Append(decoded);
                }
            }
            offset += size;
        }
        string value = text.ToString().Trim();
        if (value.Length > 0 || clear)
            CaptionReceived?.Invoke(new(DateTimeOffset.Now, pid, "jpn", value));
    }

    private void ReadCaptionDrcs(ushort pid, ReadOnlySpan<byte> units)
    {
        var received = new List<AribDrcsGlyph>();
        for (int offset = 0; offset + 5 <= units.Length;)
        {
            if (units[offset] != 0x1f) { offset++; continue; }
            byte parameter = units[offset + 1];
            int size = (units[offset + 2] << 16) | (units[offset + 3] << 8) | units[offset + 4];
            offset += 5;
            if (size > units.Length - offset) break;
            if (parameter is 0x30 or 0x31)
            {
                int bytesPerCode = parameter == 0x30 ? 1 : 2;
                received.AddRange(drcs.Register(units.Slice(offset, size), bytesPerCode));
            }
            offset += size;
        }

        if (received.Count > 0) DrcsReceived?.Invoke(pid, received);
    }

    private static int CaptionManagementDataUnitLoopOffset(ReadOnlySpan<byte> data)
    {
        int offset = 1;
        if ((data[0] >> 6) == 2) offset += 5; // original-time-message
        if (offset >= data.Length) return -1;

        int languageCount = data[offset++];
        if (languageCount is < 1 or > 2) return -1;
        for (int language = 0; language < languageCount; language++)
        {
            if (offset >= data.Length) return -1;
            int dmf = data[offset++] & 0x0f;
            if (dmf is 0x0c or 0x0d or 0x0e) offset++; // data-component format
            if (offset + 4 > data.Length) return -1; // ISO 639-2 + format/TCS
            offset += 4;
        }

        return offset + 3 <= data.Length ? offset : -1;
    }

    private static string DescriptorLanguage(ReadOnlySpan<byte> descriptors)
    {
        for (int offset = 0; offset + 2 <= descriptors.Length;)
        {
            int length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length) break;
            if (descriptors[offset] == 0x0a && length >= 3)
                return System.Text.Encoding.ASCII.GetString(descriptors.Slice(offset + 2, 3));
            offset += 2 + length;
        }
        return "";
    }

    private static IReadOnlyList<OneSegDescriptorInfo> ReadDescriptors(ReadOnlySpan<byte> descriptors)
    {
        var result = new List<OneSegDescriptorInfo>();
        for (int offset = 0; offset + 2 <= descriptors.Length;)
        {
            byte tag = descriptors[offset];
            int length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length) break;
            ReadOnlySpan<byte> body = descriptors.Slice(offset + 2, length);
            string name = tag switch
            {
                0x09 => "CA", 0x40 => "ネットワーク名", 0x41 => "サービス一覧",
                0x48 => "サービス", 0x4d => "短形式イベント", 0x4e => "拡張イベント",
                0x50 => "コンポーネント", 0x52 => "コンポーネントタグ", 0x54 => "ジャンル",
                0x58 => "地域時差", 0xc4 => "音声コンポーネント", 0xc7 => "データ内容",
                0xcd => "TS情報", 0xd5 => "シリーズ", 0xd6 => "イベントグループ",
                0xfa => "地上配送システム", 0xfd => "データ符号化方式", 0xf6 => "アクセス制御",
                _ => $"記述子 0x{tag:X2}"
            };
            string value = tag switch
            {
                0x40 => AribTextDecoder.DecodeSi(body),
                0x52 when body.Length > 0 => $"0x{body[0]:X2}",
                0xfd when body.Length >= 2 => $"data_component 0x{BinaryPrimitives.ReadUInt16BigEndian(body):X4}",
                _ => ""
            };
            result.Add(new(tag, name, value, Convert.ToHexString(body)));
            offset += 2 + length;
        }
        return result;
    }

    private static ushort? DescriptorDataComponentId(ReadOnlySpan<byte> descriptors)
    {
        for (int offset = 0; offset + 4 <= descriptors.Length;)
        {
            int length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length) break;
            if (descriptors[offset] == 0xfd && length >= 2)
                return BinaryPrimitives.ReadUInt16BigEndian(descriptors[(offset + 2)..]);
            offset += 2 + length;
        }
        return null;
    }

    private static string ContentGenre(int level1) => level1 switch
    {
        0 => "ニュース・報道", 1 => "スポーツ", 2 => "情報・ワイドショー", 3 => "ドラマ",
        4 => "音楽", 5 => "バラエティ", 6 => "映画", 7 => "アニメ・特撮",
        8 => "ドキュメンタリー・教養", 9 => "劇場・公演", 10 => "趣味・教育",
        11 => "福祉", 14 => "拡張", _ => "その他"
    };

    private static byte? DescriptorComponentTag(ReadOnlySpan<byte> descriptors)
    {
        for (int offset = 0; offset + 2 <= descriptors.Length;)
        {
            int length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length) break;
            if (descriptors[offset] == 0x52 && length >= 1) return descriptors[offset + 2];
            offset += 2 + length;
        }
        return null;
    }

    private static bool IsCaption(ReadOnlySpan<byte> descriptors)
    {
        for (int offset = 0; offset + 4 <= descriptors.Length;)
        {
            int length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length) break;
            if (descriptors[offset] == 0xfd && length >= 2)
            {
                ushort dataComponentId = BinaryPrimitives.ReadUInt16BigEndian(descriptors[(offset + 2)..]);
                // 0x0008 is the conventional ARIB caption component.  One-seg
                // mobile caption streams observed on air use 0x0012.
                if (dataComponentId is 0x0008 or 0x0012) return true;
            }
            offset += 2 + length;
        }
        return false;
    }

    private static DateTimeOffset? DecodeMjd(ReadOnlySpan<byte> value) =>
        DecodeMjd(value, TimeSpan.FromHours(9));

    private static DateTimeOffset? DecodeMjd(ReadOnlySpan<byte> value, TimeSpan offset)
    {
        if (value.Length < 5 || value[0] == 0xff) return null;
        int hour = Bcd(value[2]);
        int minute = Bcd(value[3]);
        int second = Bcd(value[4]);
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59) return null;
        try
        {
            int mjd = (value[0] << 8) | value[1];
            DateTime date = new DateTime(1858, 11, 17).AddDays(mjd);
            return new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, second, offset);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan? DecodeDuration(ReadOnlySpan<byte> value)
    {
        if (value.Length < 3 || value[0] == 0xff) return null;
        int hours = Bcd(value[0]);
        int minutes = Bcd(value[1]);
        int seconds = Bcd(value[2]);
        if (hours < 0 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59) return null;
        try
        {
            return new TimeSpan(hours, minutes, seconds);
        }
        catch
        {
            return null;
        }
    }

    private static int Bcd(byte value)
    {
        int high = value >> 4;
        int low = value & 0x0f;
        if (high > 9 || low > 9) return -1;
        return high * 10 + low;
    }

    private static bool CrcIsValid(ReadOnlySpan<byte> section) => ComputeMpegCrc(section) == 0;

    internal static uint ComputeMpegCrc(ReadOnlySpan<byte> section)
    {
        uint crc = 0xffffffff;
        foreach (byte value in section)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        return crc;
    }

    private sealed class SectionAssembler
    {
        private readonly List<byte> buffer = [];
        public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> payload, bool start)
        {
            var result = new List<byte[]>();
            if (start)
            {
                if (payload.IsEmpty) return result;
                int pointer = payload[0];
                int position = 1;
                if (buffer.Count > 0 && pointer > 0 && position + pointer <= payload.Length)
                {
                    buffer.AddRange(payload.Slice(position, pointer));
                    Extract(result);
                }
                buffer.Clear();
                position += pointer;
                if (position < payload.Length) buffer.AddRange(payload[position..]);
            }
            else buffer.AddRange(payload);
            Extract(result);
            return result;
        }
        private void Extract(List<byte[]> result)
        {
            while (buffer.Count >= 3)
            {
                if (buffer[0] == 0xff) { buffer.Clear(); return; }
                int length = 3 + (((buffer[1] & 0x0f) << 8) | buffer[2]);
                if (length < 4 || length > 4096) { buffer.RemoveAt(0); continue; }
                if (buffer.Count < length) return;
                result.Add(buffer.GetRange(0, length).ToArray());
                buffer.RemoveRange(0, length);
            }
        }
    }

    private sealed class CaptionPesAssembler
    {
        private readonly List<byte> buffer = [];

        public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> payload, bool start)
        {
            var result = new List<byte[]>();
            if (start)
            {
                if (buffer.Count >= 6 && ((buffer[4] << 8) | buffer[5]) == 0)
                    result.Add(buffer.ToArray());
                buffer.Clear();
            }
            buffer.AddRange(payload);
            while (buffer.Count >= 6)
            {
                if (buffer[0] != 0 || buffer[1] != 0 || buffer[2] != 1)
                {
                    buffer.RemoveAt(0);
                    continue;
                }
                int packetLength = (buffer[4] << 8) | buffer[5];
                if (packetLength == 0 || buffer.Count < 6 + packetLength) break;
                result.Add(buffer.GetRange(0, 6 + packetLength).ToArray());
                buffer.RemoveRange(0, 6 + packetLength);
            }
            return result;
        }
    }

    private sealed class CaptionDataGroupAssembler(byte lastLink)
    {
        public byte LastLink { get; } = lastLink;
        public int NextLink { get; set; }
        public List<byte> Data { get; } = [];
    }
}
