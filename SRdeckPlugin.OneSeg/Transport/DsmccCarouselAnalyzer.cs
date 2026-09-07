using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Transport;

/// <summary>Reassembles ARIB DSM-CC download-info and download-data blocks.</summary>
internal sealed class DsmccCarouselAnalyzer
{
    private readonly Dictionary<(uint Download, ushort Module, byte Version), ModuleState> modules = [];
    private readonly Dictionary<uint, ushort> blockSizes = [];
    private readonly Dictionary<ushort, byte> componentTags = [];

    public void RegisterStream(ushort pid, byte? componentTag)
    {
        if (componentTag is byte value) componentTags[pid] = value;
    }

    public void Reset()
    {
        modules.Clear();
        blockSizes.Clear();
        componentTags.Clear();
    }

    public IReadOnlyList<OneSegDataModule> Process(ushort pid, ReadOnlySpan<byte> section)
    {
        if (section.Length < 20) return [];
        if (section[0] == 0x3d)
            return [new(pid, 0, 0, section[5], "データイベント", "ARIBデータ放送制御",
                section.Length, true, Convert.ToHexString(section.Slice(0, Math.Min(section.Length, 64))))];
        if (section[0] is not (0x3b or 0x3c) || section[8] != 0x11) return [];
        ushort messageId = BinaryPrimitives.ReadUInt16BigEndian(section[10..]);
        uint transaction = BinaryPrimitives.ReadUInt32BigEndian(section[12..]);
        return messageId switch
        {
            0x1002 => ParseDii(pid, section),
            0x1003 => ParseDdb(pid, transaction, section),
            _ => []
        };
    }

    private IReadOnlyList<OneSegDataModule> ParseDii(ushort pid, ReadOnlySpan<byte> section)
    {
        // ARIB carousel DII uses the complete 12-byte DSM-CC message header.
        int bodyOffset = 20;
        if (bodyOffset + 22 > section.Length - 4) return [];
        uint downloadId = BinaryPrimitives.ReadUInt32BigEndian(section[bodyOffset..]);
        ushort blockSize = BinaryPrimitives.ReadUInt16BigEndian(section[(bodyOffset + 4)..]);
        blockSizes[downloadId] = blockSize;
        int compatibilityLength = BinaryPrimitives.ReadUInt16BigEndian(section[(bodyOffset + 16)..]);
        int offset = bodyOffset + 18 + compatibilityLength;
        if (offset + 2 > section.Length - 4) return [];
        int count = BinaryPrimitives.ReadUInt16BigEndian(section[offset..]);
        offset += 2;
        var result = new List<OneSegDataModule>();
        for (int index = 0; index < count && offset + 8 <= section.Length - 4; index++)
        {
            ushort moduleId = BinaryPrimitives.ReadUInt16BigEndian(section[offset..]);
            int moduleSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(section[(offset + 2)..]));
            byte version = section[offset + 6];
            int infoLength = section[offset + 7];
            offset += 8;
            if (offset + infoLength > section.Length - 4) break;
            string info = DecodeModuleInfo(section.Slice(offset, infoLength));
            offset += infoLength;
            var key = (downloadId, moduleId, version);
            if (!modules.TryGetValue(key, out ModuleState? state))
                modules[key] = state = new(pid, downloadId, moduleId, version, moduleSize, info,
                    componentTags.GetValueOrDefault(pid));
            else state.Update(pid, moduleSize, info, componentTags.GetValueOrDefault(pid));
            result.Add(state.ToInfo());
        }
        return result;
    }

    private IReadOnlyList<OneSegDataModule> ParseDdb(ushort pid, uint downloadId,
        ReadOnlySpan<byte> section)
    {
        int bodyOffset = 20;
        if (bodyOffset + 6 > section.Length - 4) return [];
        ushort moduleId = BinaryPrimitives.ReadUInt16BigEndian(section[bodyOffset..]);
        byte version = section[bodyOffset + 2];
        ushort blockNumber = BinaryPrimitives.ReadUInt16BigEndian(section[(bodyOffset + 4)..]);
        int end = section.Length - 4;
        byte[] block = section.Slice(bodyOffset + 6, end - bodyOffset - 6).ToArray();
        var key = (downloadId, moduleId, version);
        // DDBs from other carousel transactions and control streams coexist on
        // the same PID.  Only modules announced by a valid DII belong here.
        if (!modules.TryGetValue(key, out ModuleState? state)) return [];
        state.Blocks[blockNumber] = block;
        state.TryComplete(blockSizes.GetValueOrDefault(downloadId));
        return [state.ToInfo()];
    }

    private static string DecodeModuleInfo(ReadOnlySpan<byte> value)
    {
        string ascii = Encoding.ASCII.GetString(value);
        int mime = ascii.IndexOf("text/", StringComparison.OrdinalIgnoreCase);
        return mime >= 0 ? ascii[mime..].Trim('\0', '\ufffd') : Convert.ToHexString(value);
    }

    private sealed class ModuleState(ushort pid, uint downloadId, ushort moduleId,
        byte version, int size, string info, byte componentTag)
    {
        public ushort Pid { get; private set; } = pid;
        public uint DownloadId { get; } = downloadId;
        public ushort ModuleId { get; } = moduleId;
        public byte Version { get; } = version;
        public int Size { get; private set; } = size;
        public string Info { get; private set; } = info;
        public byte ComponentTag { get; private set; } = componentTag;
        public SortedDictionary<ushort, byte[]> Blocks { get; } = [];
        public byte[]? Content { get; private set; }

        public void Update(ushort newPid, int newSize, string newInfo, byte newComponentTag)
        {
            Pid = newPid;
            Size = newSize;
            if (newComponentTag != 0) ComponentTag = newComponentTag;
            if (!string.IsNullOrWhiteSpace(newInfo)) Info = newInfo;
        }

        public void TryComplete(ushort blockSize)
        {
            if (Blocks.Count == 0) return;
            int expected = Size > 0 && blockSize > 0 ? (Size + blockSize - 1) / blockSize : -1;
            if (expected > 0 && (Blocks.Count < expected || Enumerable.Range(0, expected)
                .Any(number => !Blocks.ContainsKey((ushort)number)))) return;
            byte[] joined = Blocks.OrderBy(value => value.Key).SelectMany(value => value.Value).ToArray();
            if (Size > 0)
            {
                if (joined.Length < Size) return;
                if (joined.Length > Size) joined = joined[..Size];
            }
            Content = joined;
        }

        public OneSegDataModule ToInfo()
        {
            byte[] content = Content ?? [];
            byte[] display = TryInflate(content) ?? content;
            string contentType = DetectContentType(display, Info);
            string preview = display.Length == 0 ? $"ブロック {Blocks.Count}個受信" : TextPreview(display);
            IReadOnlyList<OneSegBmlResource> resources = display.Length == 0 ? [] :
                BmlResourceParser.Parse(display, ModuleId, contentType,
                    LooksLikeResourcePath(Info) ? Info : null);
            return new(Pid, DownloadId, ModuleId, Version, ModuleName(Info, display), contentType,
                Size > 0 ? Size : content.Length, Content is not null, preview, resources, ComponentTag);
        }

        private static byte[]? TryInflate(byte[] content)
        {
            if (content.Length < 2 || content[0] != 0x78) return null;
            try
            {
                using var input = new MemoryStream(content);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return output.ToArray();
            }
            catch (InvalidDataException) { return null; }
        }

        private static string DetectContentType(byte[] content, string info)
        {
            if (info.Contains("text/", StringComparison.OrdinalIgnoreCase)) return info;
            string head = Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 128));
            string? headerType = HeaderValue(head, "Content-Type");
            if (!string.IsNullOrWhiteSpace(headerType)) return headerType;
            if (head.Contains("<?xml", StringComparison.OrdinalIgnoreCase) ||
                head.Contains("<bml", StringComparison.OrdinalIgnoreCase)) return "application/bml+xml";
            if (head.Contains("<html", StringComparison.OrdinalIgnoreCase)) return "text/html";
            if (content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 &&
                content[2] == 0x4e && content[3] == 0x47) return "image/png";
            return "application/octet-stream";
        }

        private string ModuleName(string info, byte[] content)
        {
            string head = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 512));
            string? location = HeaderValue(head, "Content-Location");
            if (!string.IsNullOrWhiteSpace(location)) return location;
            if (string.IsNullOrWhiteSpace(info)) return $"Module {ModuleId}";
            return info.Length > 48 ? info[..48] : info;
        }

        private static string? HeaderValue(string text, string name)
        {
            string marker = name + ":";
            int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += marker.Length;
            int end = text.IndexOfAny(['\r','\n'], start);
            if (end < 0) end = text.Length;
            return text[start..end].Trim();
        }

        private static bool LooksLikeResourcePath(string value) =>
            value.IndexOfAny(['\r','\n',';']) < 0 &&
            Path.HasExtension(value) && !value.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

        private static string TextPreview(byte[] content)
        {
            string value = Encoding.UTF8.GetString(content.AsSpan(0, Math.Min(content.Length, 2048)));
            return new string(value.Select(character => char.IsControl(character) && character is not '\r' and not '\n'
                ? ' ' : character).ToArray()).Trim();
        }
    }
}
