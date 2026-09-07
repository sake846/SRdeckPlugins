using System.Buffers.Binary;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Transport;

internal sealed partial class MpegTsAnalyzer
{
    private readonly Dictionary<ushort, SectionAssembler> sections = [];
    private readonly Dictionary<ushort, ushort> pmtPids = [];
    private readonly Dictionary<ushort, int> continuity = [];
    private readonly Dictionary<ushort, TimeSpan> lastPcrNotifications = [];
    private readonly Dictionary<ushort, OneSegProgramInfo> programs = [];
    private readonly Dictionary<ushort, ushort> adtsAudioServices = [];
    private readonly Dictionary<ushort, AdtsAudioModeDetector> adtsAudioDetectors = [];
    private readonly Dictionary<ushort, byte> adtsAudioComponentTypes = [];
    private readonly HashSet<ushort> captionPids = [];
    private readonly HashSet<ushort> dataPids = [];
    private readonly DsmccCarouselAnalyzer carousel = new();
    private readonly AribDrcsStore drcs = new();
    private readonly Dictionary<ushort, CaptionPesAssembler> captionPes = [];
    private readonly Dictionary<(ushort Pid, byte GroupId, byte Version), CaptionDataGroupAssembler> captionGroups = [];

    public event Action<IReadOnlyList<OneSegProgramInfo>>? ProgramsChanged;
    public event Action<OneSegEventInfo>? EventUpdated;
    public event Action<ushort, byte>? AudioComponentTypeDetected;
    public event Action<OneSegCaption>? CaptionReceived;
    internal event Action<ushort, IReadOnlyList<AribDrcsGlyph>>? DrcsReceived;
    public event Action<IReadOnlyList<OneSegConditionalAccessInfo>>? ConditionalAccessChanged;
    public event Action<OneSegNetworkInfo>? NetworkUpdated;
    public event Action<OneSegBroadcastClock>? BroadcastClockUpdated;
    public event Action<OneSegBroadcasterInfo>? BroadcasterUpdated;
    public event Action<OneSegTransportClock>? TransportClockUpdated;
    public event Action<OneSegDataModule>? DataModuleUpdated;
    internal event Action<ushort, byte[]>? CaptionStatementDataReceived;
    public long PacketCount { get; private set; }
    public long ContinuityErrors { get; private set; }
    public IReadOnlyList<OneSegProgramInfo> Programs => programs.Values.OrderBy(p => p.ServiceId).ToArray();
    public OneSegNetworkInfo? NetworkInfo { get; private set; }
    internal IReadOnlyDictionary<ushort, ushort> ProgramMap => pmtPids;

    public void Reset()
    {
        sections.Clear();
        pmtPids.Clear();
        continuity.Clear();
        lastPcrNotifications.Clear();
        programs.Clear();
        adtsAudioServices.Clear();
        adtsAudioDetectors.Clear();
        adtsAudioComponentTypes.Clear();
        NetworkInfo = null;
        captionPids.Clear();
        dataPids.Clear();
        carousel.Reset();
        drcs.Clear();
        captionPes.Clear();
        captionGroups.Clear();
        PacketCount = 0;
        ContinuityErrors = 0;
        ProgramsChanged?.Invoke([]);
        ConditionalAccessChanged?.Invoke([]);
    }

    public void Process(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 188 || packet[0] != 0x47 || (packet[1] & 0x80) != 0) return;
        PacketCount++;
        ushort pid = (ushort)(((packet[1] & 0x1f) << 8) | packet[2]);
        bool payloadStart = (packet[1] & 0x40) != 0;
        int adaptation = (packet[3] >> 4) & 3;
        int counter = packet[3] & 0x0f;
        bool hasPayload = adaptation is 1 or 3;
        if (hasPayload && pid != 0x1fff)
        {
            if (continuity.TryGetValue(pid, out int previous) && counter != ((previous + 1) & 0x0f)) ContinuityErrors++;
            continuity[pid] = counter;
        }
        if (adaptation is 2 or 3 && packet[4] >= 7 && (packet[5] & 0x10) != 0)
        {
            long pcrBase = ((long)packet[6] << 25) | ((long)packet[7] << 17) |
                ((long)packet[8] << 9) | ((long)packet[9] << 1) | (long)((packet[10] >> 7) & 1);
            int extension = ((packet[10] & 1) << 8) | packet[11];
            TimeSpan position = TimeSpan.FromSeconds((pcrBase * 300.0 + extension) / 27_000_000.0);
            if (!lastPcrNotifications.TryGetValue(pid, out TimeSpan previousPcr) ||
                position < previousPcr || position - previousPcr >= TimeSpan.FromMilliseconds(250))
            {
                lastPcrNotifications[pid] = position;
                TransportClockUpdated?.Invoke(new(pid, pcrBase, extension, position));
            }
        }
        if (!hasPayload) return;
        int offset = 4;
        if (adaptation == 3) offset += 1 + packet[4];
        if (offset >= packet.Length) return;
        ReadOnlySpan<byte> payload = packet[offset..];
        if (captionPids.Contains(pid)) ProcessCaptionPes(pid, payload, payloadStart);
        if (adtsAudioServices.TryGetValue(pid, out ushort audioService))
            ProcessAdtsAudio(pid, audioService, payload, payloadStart);
        // The partial-reception (one-seg) PMT is carried on the fixed PID 0x1FC8
        // and its segment may contain no PAT.  Generic MPEG-TS analyzers that only
        // follow PAT mappings therefore miss every elementary stream.
        // PID 0x0027 carries the partial-reception EIT used by one-seg.
        if (pid != 0 && pid != 0x01 && pid != 0x10 && pid != 0x11 && pid != 0x12 &&
            pid != 0x14 && pid != 0x24 && pid != 0x27 && pid != 0x1fc8 &&
            !pmtPids.ContainsValue(pid) && !dataPids.Contains(pid)) return;
        if (!sections.TryGetValue(pid, out SectionAssembler? assembler)) sections[pid] = assembler = new();
        foreach (byte[] section in assembler.Push(payload, payloadStart)) ParseSection(pid, section);
    }

    private void ParseSection(ushort pid, byte[] section)
    {
        if (section.Length < 3) return;
        bool hasCrc = section[0] != 0x70;
        if (hasCrc && (section.Length < 8 || !CrcIsValid(section))) return;
        switch (section[0])
        {
            case 0x00: ParsePat(section); break;
            case 0x01: ParseCat(section); break;
            case 0x02: ParsePmt(pid, section); break;
            case 0x40: case 0x41: ParseNit(section); break;
            case 0x42: case 0x46: ParseSdt(section); break;
            case >= 0x4e and <= 0x6f: ParseEit(section); break;
            case 0x70: case 0x73: ParseTime(section); break;
            case 0xc4: ParseBit(section); break;
            case 0x3b: case 0x3c: case 0x3d:
                foreach (OneSegDataModule module in carousel.Process(pid, section))
                    DataModuleUpdated?.Invoke(module);
                break;
        }
    }

    private void ParsePat(ReadOnlySpan<byte> section)
    {
        int end = section.Length - 4;
        for (int offset = 8; offset + 4 <= end; offset += 4)
        {
            ushort service = BinaryPrimitives.ReadUInt16BigEndian(section[offset..]);
            ushort pid = (ushort)(((section[offset + 2] & 0x1f) << 8) | section[offset + 3]);
            if (service != 0) pmtPids[service] = pid;
        }
    }

    private void ParsePmt(ushort pmtPid, ReadOnlySpan<byte> section)
    {
        ushort service = BinaryPrimitives.ReadUInt16BigEndian(section[3..]);
        pmtPids[service] = pmtPid;
        ushort pcr = (ushort)(((section[8] & 0x1f) << 8) | section[9]);
        int programInfo = ((section[10] & 0x0f) << 8) | section[11];
        int offset = 12 + programInfo, end = section.Length - 4;
        var streams = new List<OneSegElementaryStream>();
        var serviceAdtsPids = new HashSet<ushort>();
        while (offset + 5 <= end)
        {
            byte type = section[offset];
            ushort elementaryPid = (ushort)(((section[offset + 1] & 0x1f) << 8) | section[offset + 2]);
            int descriptorLength = ((section[offset + 3] & 0x0f) << 8) | section[offset + 4];
            if (offset + 5 + descriptorLength > end) break;
            ReadOnlySpan<byte> descriptors = section.Slice(offset + 5, descriptorLength);
            string language = DescriptorLanguage(descriptors);
            byte? tag = DescriptorComponentTag(descriptors);
            ushort? dataComponentId = DescriptorDataComponentId(descriptors);
            bool caption = type == 0x06 && (IsCaption(descriptors) || tag is >= 0x30 and <= 0x3f);
            bool data = type == 0x0d || dataComponentId is not null;
            string kind = type == 0x1b ? "映像" : type is 0x0f or 0x11 ? "音声" : caption ? "字幕" : data ? "データ放送" : "データ";
            string codec = type == 0x1b ? "H.264/AVC" : type == 0x0f ? "AAC" : type == 0x11 ? "HE-AAC" : caption ? "ARIB字幕" : $"0x{type:X2}";
            streams.Add(new(elementaryPid, type, kind, codec, language, tag, dataComponentId,
                string.Join(" / ", ReadDescriptors(descriptors).Select(value => value.DisplayText))));
            if (type == 0x0f)
            {
                serviceAdtsPids.Add(elementaryPid);
                adtsAudioServices[elementaryPid] = service;
                adtsAudioDetectors.TryAdd(elementaryPid, new AdtsAudioModeDetector());
            }
            if (caption) captionPids.Add(elementaryPid);
            if (data)
            {
                dataPids.Add(elementaryPid);
                carousel.RegisterStream(elementaryPid, tag);
            }
            offset += 5 + descriptorLength;
        }
        foreach (ushort oldPid in adtsAudioServices
            .Where(value => value.Value == service && !serviceAdtsPids.Contains(value.Key))
            .Select(value => value.Key).ToArray())
        {
            adtsAudioServices.Remove(oldPid);
            adtsAudioDetectors.Remove(oldPid);
            adtsAudioComponentTypes.Remove(oldPid);
        }
        programs.TryGetValue(service, out OneSegProgramInfo? old);
        programs[service] = new(service, old?.ServiceName ?? $"Service {service}",
            old?.ProviderName ?? "", pmtPid, pcr, streams);
        ProgramsChanged?.Invoke(Programs);
    }

    private void ProcessAdtsAudio(ushort pid, ushort service, ReadOnlySpan<byte> payload, bool start)
    {
        int offset = 0;
        if (start && payload.Length >= 9 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1)
        {
            offset = 9 + payload[8];
            if (offset > payload.Length) return;
        }
        if (!adtsAudioDetectors.TryGetValue(pid, out AdtsAudioModeDetector? detector))
            adtsAudioDetectors[pid] = detector = new AdtsAudioModeDetector();
        byte? componentType = detector.Push(payload[offset..]);
        if (componentType is not byte detected ||
            adtsAudioComponentTypes.GetValueOrDefault(pid) == detected) return;
        adtsAudioComponentTypes[pid] = detected;
        AudioComponentTypeDetected?.Invoke(service, detected);
    }

    private void ParseCat(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12) return;
        var values = new List<OneSegConditionalAccessInfo>();
        foreach (OneSegDescriptorInfo descriptor in ReadDescriptors(section.Slice(8, section.Length - 12)))
        {
            byte[] data = Convert.FromHexString(descriptor.RawHex);
            if (descriptor.Tag == 0x09 && data.Length >= 4)
                values.Add(new(BinaryPrimitives.ReadUInt16BigEndian(data),
                    (ushort)(((data[2] & 0x1f) << 8) | data[3]), "限定受信", Convert.ToHexString(data.AsSpan(4))));
            else if (descriptor.Tag == 0xf6 && data.Length >= 4)
                values.Add(new(BinaryPrimitives.ReadUInt16BigEndian(data),
                    (ushort)(((data[2] & 0x1f) << 8) | data[3]), "アクセス制御", Convert.ToHexString(data.AsSpan(4))));
        }
        ConditionalAccessChanged?.Invoke(values);
    }

    private void ParseNit(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16) return;
        ushort networkId = BinaryPrimitives.ReadUInt16BigEndian(section[3..]);
        int networkDescriptorLength = ((section[8] & 0x0f) << 8) | section[9];
        if (10 + networkDescriptorLength + 2 > section.Length - 4) return;
        IReadOnlyList<OneSegDescriptorInfo> networkDescriptors =
            ReadDescriptors(section.Slice(10, networkDescriptorLength));
        string networkName = networkDescriptors.FirstOrDefault(value => value.Tag == 0x40)?.Value ?? "";
        int offset = 10 + networkDescriptorLength;
        int loopLength = ((section[offset] & 0x0f) << 8) | section[offset + 1];
        offset += 2;
        int end = Math.Min(offset + loopLength, section.Length - 4);
        var transports = new List<OneSegTransportInfo>();
        while (offset + 6 <= end)
        {
            ushort tsid = BinaryPrimitives.ReadUInt16BigEndian(section[offset..]);
            ushort onid = BinaryPrimitives.ReadUInt16BigEndian(section[(offset + 2)..]);
            int length = ((section[offset + 4] & 0x0f) << 8) | section[offset + 5];
            if (offset + 6 + length > end) break;
            IReadOnlyList<OneSegDescriptorInfo> descriptors = ReadDescriptors(section.Slice(offset + 6, length));
            var services = new List<OneSegNetworkService>();
            string transportName = "";
            byte? remoteKey = null;
            ReadOnlySpan<byte> raw = section.Slice(offset + 6, length);
            for (int d = 0; d + 2 <= raw.Length;)
            {
                int size = raw[d + 1];
                if (d + 2 + size > raw.Length) break;
                ReadOnlySpan<byte> body = raw.Slice(d + 2, size);
                if (raw[d] == 0x41)
                    for (int item = 0; item + 3 <= body.Length; item += 3)
                        services.Add(new(BinaryPrimitives.ReadUInt16BigEndian(body[item..]), body[item + 2]));
                else if (raw[d] == 0xcd && body.Length >= 2)
                {
                    remoteKey = body[0];
                    int nameLength = body[1] >> 2;
                    if (2 + nameLength <= body.Length)
                        transportName = AribTextDecoder.DecodeSi(body.Slice(2, nameLength));
                }
                d += 2 + size;
            }
            transports.Add(new(tsid, onid, remoteKey, transportName, services, descriptors));
            offset += 6 + length;
        }
        OneSegNetworkInfo network = new(networkId, networkName, transports, networkDescriptors);
        NetworkInfo = network;
        NetworkUpdated?.Invoke(network);
    }

    private void ParseTime(ReadOnlySpan<byte> section)
    {
        if (section.Length < 8) return;
        // ARIB terrestrial SI carries the broadcast clock as Japan local time
        // (despite the inherited DVB field name UTC_time).
        DateTimeOffset? transmitted = DecodeMjd(section.Slice(3, 5), TimeSpan.FromHours(9));
        if (transmitted is null) return;
        string country = "JPN";
        int region = 0;
        TimeSpan currentOffset = TimeSpan.FromHours(9);
        DateTimeOffset? change = null;
        TimeSpan? nextOffset = null;
        if (section[0] == 0x73 && section.Length >= 14)
        {
            int descriptorLength = ((section[8] & 0x0f) << 8) | section[9];
            ReadOnlySpan<byte> descriptors = section.Slice(10, Math.Min(descriptorLength, section.Length - 14));
            for (int d = 0; d + 2 <= descriptors.Length;)
            {
                int size = descriptors[d + 1];
                if (d + 2 + size > descriptors.Length) break;
                if (descriptors[d] == 0x58 && size >= 13)
                {
                    ReadOnlySpan<byte> body = descriptors.Slice(d + 2, size);
                    country = System.Text.Encoding.ASCII.GetString(body[..3]);
                    region = body[3] >> 2;
                    int sign = (body[3] & 1) == 0 ? 1 : -1;
                    int h1 = Bcd(body[4]);
                    int m1 = Bcd(body[5]);
                    if (h1 >= 0 && m1 >= 0 && m1 < 60)
                    {
                        currentOffset = TimeSpan.FromMinutes(sign * (h1 * 60 + m1));
                    }
                    change = DecodeMjd(body.Slice(6, 5), currentOffset);
                    int h2 = Bcd(body[11]);
                    int m2 = Bcd(body[12]);
                    if (h2 >= 0 && m2 >= 0 && m2 < 60)
                    {
                        nextOffset = TimeSpan.FromMinutes(h2 * 60 + m2);
                    }
                    break;
                }
                d += 2 + size;
            }
        }
        try
        {
            DateTimeOffset local = new(transmitted.Value.DateTime, currentOffset);
            BroadcastClockUpdated?.Invoke(new(local.ToUniversalTime(), local, country,
                region, currentOffset, change, nextOffset));
        }
        catch
        {
        }
    }

    private void ParseBit(ReadOnlySpan<byte> section)
    {
        if (section.Length < 14) return;
        ushort onid = BinaryPrimitives.ReadUInt16BigEndian(section[3..]);
        bool view = (section[8] & 0x80) != 0;
        int commonLength = ((section[8] & 0x0f) << 8) | section[9];
        if (10 + commonLength > section.Length - 4) return;
        IReadOnlyList<OneSegDescriptorInfo> common = ReadDescriptors(section.Slice(10, commonLength));
        int offset = 10 + commonLength, end = section.Length - 4;
        var broadcasters = new List<OneSegBroadcasterEntry>();
        while (offset + 3 <= end)
        {
            byte id = section[offset++];
            int length = ((section[offset] & 0x0f) << 8) | section[offset + 1];
            offset += 2;
            if (offset + length > end) break;
            broadcasters.Add(new(id, ReadDescriptors(section.Slice(offset, length))));
            offset += length;
        }
        BroadcasterUpdated?.Invoke(new(onid, view, common, broadcasters));
    }

    private void ParseSdt(ReadOnlySpan<byte> section)
    {
        int offset = 11, end = section.Length - 4;
        while (offset + 5 <= end)
        {
            ushort service = BinaryPrimitives.ReadUInt16BigEndian(section[offset..]);
            int length = ((section[offset + 3] & 0x0f) << 8) | section[offset + 4];
            if (offset + 5 + length > end) break;
            string provider = "", name = "";
            ReadOnlySpan<byte> descriptors = section.Slice(offset + 5, length);
            for (int d = 0; d + 2 <= descriptors.Length;)
            {
                int size = descriptors[d + 1];
                if (d + 2 + size > descriptors.Length) break;
                if (descriptors[d] == 0x48 && size >= 3)
                {
                    int providerLength = descriptors[d + 3];
                    int nameLengthOffset = d + 4 + providerLength;
                    if (nameLengthOffset < d + 2 + size)
                    {
                        provider = AribTextDecoder.DecodeSi(descriptors.Slice(d + 4, providerLength));
                        int nameLength = descriptors[nameLengthOffset];
                        if (nameLengthOffset + 1 + nameLength <= d + 2 + size)
                            name = AribTextDecoder.DecodeSi(descriptors.Slice(nameLengthOffset + 1, nameLength));
                    }
                }
                d += 2 + size;
            }
            programs.TryGetValue(service, out OneSegProgramInfo? old);
            programs[service] = new(service, name, provider, old?.PmtPid ?? pmtPids.GetValueOrDefault(service), old?.PcrPid ?? 0, old?.Streams ?? []);
            offset += 5 + length;
        }
        ProgramsChanged?.Invoke(Programs);
    }

    private void ParseEit(ReadOnlySpan<byte> section)
    {
        ushort service = BinaryPrimitives.ReadUInt16BigEndian(section[3..]);
        bool isPresentSection = section[0] == 0x4e && section[6] == 0;
        int offset = 14, end = section.Length - 4;
        while (offset + 12 <= end)
        {
            bool isPresentEvent = isPresentSection && offset == 14;
            ushort eventId = BinaryPrimitives.ReadUInt16BigEndian(section[offset..]);
            DateTimeOffset? start = DecodeMjd(section.Slice(offset + 2, 5));
            TimeSpan? duration = DecodeDuration(section.Slice(offset + 7, 3));
            int length = ((section[offset + 10] & 0x0f) << 8) | section[offset + 11];
            if (offset + 12 + length > end) break;
            string title = "", description = "", extended = "", genre = "", series = "";
            string video = "", audio = "", parental = "", group = "", dataContent = "";
            byte? audioComponentType = null;
            bool mainAudioComponentFound = false;
            var descriptorSummary = new List<string>();
            var extendedParts = new SortedDictionary<byte, string>();
            ReadOnlySpan<byte> descriptors = section.Slice(offset + 12, length);
            for (int d = 0; d + 2 <= descriptors.Length;)
            {
                int size = descriptors[d + 1];
                if (d + 2 + size > descriptors.Length) break;
                byte descriptorTag = descriptors[d];
                ReadOnlySpan<byte> body = descriptors.Slice(d + 2, size);
                if (descriptorTag == 0x4d && size >= 5)
                {
                    int titleLength = body[3];
                    if (4 + titleLength < body.Length)
                    {
                        title = AribTextDecoder.DecodeSi(body.Slice(4, titleLength));
                        int textLength = body[4 + titleLength];
                        if (5 + titleLength + textLength <= body.Length)
                            description = AribTextDecoder.DecodeSi(body.Slice(5 + titleLength, textLength));
                    }
                }
                else if (descriptorTag == 0x4e && body.Length >= 6)
                {
                    byte number = (byte)(body[0] >> 4);
                    int itemsLength = body[4];
                    int textOffset = 5 + itemsLength;
                    var extendedText = new List<string>();
                    int itemOffset = 5, itemEnd = Math.Min(textOffset, body.Length);
                    while (itemOffset < itemEnd)
                    {
                        int labelLength = body[itemOffset++];
                        if (itemOffset + labelLength >= itemEnd) break;
                        string label = AribTextDecoder.DecodeSi(body.Slice(itemOffset, labelLength));
                        itemOffset += labelLength;
                        int valueLength = body[itemOffset++];
                        if (itemOffset + valueLength > itemEnd) break;
                        string itemValue = AribTextDecoder.DecodeSi(body.Slice(itemOffset, valueLength));
                        itemOffset += valueLength;
                        extendedText.Add(string.IsNullOrWhiteSpace(label) ? itemValue : $"{label}: {itemValue}");
                    }
                    if (textOffset < body.Length)
                    {
                        int textLength = body[textOffset];
                        if (textOffset + 1 + textLength <= body.Length)
                            extendedText.Add(AribTextDecoder.DecodeSi(body.Slice(textOffset + 1, textLength)));
                    }
                    extendedParts[number] = string.Join("\n", extendedText.Where(value => !string.IsNullOrWhiteSpace(value)));
                }
                else if (descriptorTag == 0x54)
                {
                    var genres = new HashSet<string>();
                    for (int item = 0; item + 1 < body.Length; item += 2)
                        genres.Add(ContentGenre(body[item] >> 4));
                    genre = string.Join("、", genres);
                }
                else if (descriptorTag == 0x50 && body.Length >= 6)
                    video = $"映像 component 0x{body[2]:X2} {AribTextDecoder.DecodeSi(body[6..])}".Trim();
                else if (descriptorTag == 0xc4 && body.Length >= 9)
                {
                    bool isMainComponent = (body[5] & 0x40) != 0;
                    if (audioComponentType is null || (isMainComponent && !mainAudioComponentFound))
                    {
                        audioComponentType = body[1];
                        audio = $"音声 component 0x{body[2]:X2} {System.Text.Encoding.ASCII.GetString(body.Slice(6, 3))}";
                    }
                    mainAudioComponentFound |= isMainComponent;
                }
                else if (descriptorTag == 0x55 && body.Length >= 4)
                    parental = $"年齢制限 {(body[3] == 0 ? "なし" : $"{body[3] + 3}歳以上")}";
                else if (descriptorTag == 0xd5 && body.Length >= 8)
                    series = $"シリーズ {BinaryPrimitives.ReadUInt16BigEndian(body):X4}";
                else if (descriptorTag == 0xd6 && body.Length >= 1)
                    group = $"イベントグループ {body[0] >> 4}";
                else if (descriptorTag == 0xc7 && body.Length >= 2)
                    dataContent = $"データ内容 0x{BinaryPrimitives.ReadUInt16BigEndian(body):X4}";
                descriptorSummary.Add($"0x{descriptorTag:X2}={Convert.ToHexString(body)}");
                d += 2 + size;
            }
            extended = string.Join("\n", extendedParts.Values.Where(value => !string.IsNullOrWhiteSpace(value)));
            EventUpdated?.Invoke(new(service, eventId, start, duration, title, description,
                extended, genre, series, video, audio, parental, group, dataContent,
                string.Join(" ", descriptorSummary), audioComponentType, isPresentEvent));
            offset += 12 + length;
        }
    }
}
