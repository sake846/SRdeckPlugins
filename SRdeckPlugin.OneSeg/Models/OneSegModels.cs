using System.Text.Json.Serialization;

namespace SRdeckPlugin.OneSeg.Models;

public sealed record OneSegChannel(int Number, long CenterFrequencyHz)
{
    public string Id => $"uhf-{Number}";
    public string DisplayName => $"UHF {Number}ch";
    public string FrequencyText => $"{CenterFrequencyHz / 1_000_000.0:F6} MHz";

    public static IReadOnlyList<OneSegChannel> JapaneseUhfChannels { get; } =
        Enumerable.Range(13, 40)
            .Select(channel => new OneSegChannel(channel,
                473_142_857L + (channel - 13L) * 6_000_000L))
            .ToArray();
}

public sealed record OneSegStation(
    ushort ServiceId,
    string ServiceName,
    string ProviderName,
    int ChannelNumber,
    double? MerDb = null,
    long TransportPackets = 0,
    long ReedSolomonPackets = 0,
    long ReedSolomonRejected = 0,
    DateTimeOffset? ScannedAt = null)
{
    // NIT's extended broadcaster descriptor. Older saved stations may not have this value.
    public byte? RemoteControlKeyId { get; init; }

    [JsonIgnore]
    public string RemoteControlKeyText => RemoteControlKeyId is byte key ? $"{key}ch" : "--";
    [JsonIgnore]
    public string DisplayName => $"{ServiceName}（UHF {ChannelNumber}ch）";
    [JsonIgnore]
    public string QualityText => MerDb is double mer
        ? $"MER {mer:F1} dB / TS {TransportPackets:N0} / RS棄却 {ReedSolomonRejected:N0}"
        : $"MER -- / TS {TransportPackets:N0} / RS棄却 {ReedSolomonRejected:N0}";
    [JsonIgnore]
    public double QualityScore => (MerDb ?? -30) -
        (ReedSolomonPackets + ReedSolomonRejected <= 0 ? 0 :
            ReedSolomonRejected * 30.0 / (ReedSolomonPackets + ReedSolomonRejected));

    public OneSegStation UpdateInfo(
        string? serviceName = null,
        string? providerName = null,
        byte? remoteControlKeyId = null,
        double? merDb = null,
        long? transportPackets = null,
        long? reedSolomonPackets = null,
        long? reedSolomonRejected = null,
        DateTimeOffset? scannedAt = null)
    {
        return this with
        {
            ServiceName = !string.IsNullOrWhiteSpace(serviceName) ? serviceName : ServiceName,
            ProviderName = !string.IsNullOrWhiteSpace(providerName) ? providerName : ProviderName,
            RemoteControlKeyId = remoteControlKeyId ?? RemoteControlKeyId,
            MerDb = merDb ?? MerDb,
            TransportPackets = transportPackets ?? TransportPackets,
            ReedSolomonPackets = reedSolomonPackets ?? ReedSolomonPackets,
            ReedSolomonRejected = reedSolomonRejected ?? ReedSolomonRejected,
            ScannedAt = scannedAt ?? ScannedAt
        };
    }
}

public sealed record OneSegAudioTrackOption(int Id, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"音声 {Id}" : Name;
}

public sealed record OneSegCaptionPositionOption(string Id, string DisplayName);

public sealed record OneSegPrefecture(
    string Id,
    string DisplayName,
    IReadOnlyList<int> CandidateChannelNumbers)
{
    public IReadOnlyList<OneSegChannel> CandidateChannels => CandidateChannelNumbers
        .Select(number => OneSegChannel.JapaneseUhfChannels.First(channel => channel.Number == number))
        .ToArray();

    public static IReadOnlyList<OneSegPrefecture> All { get; } =
    [
        new("hokkaido", "北海道", [13,14,15,17,19,21,23,25]),
        new("aomori", "青森県", [16,18,21,26,28]),
        new("iwate", "岩手県", [14,15,17,18,19,20]),
        new("miyagi", "宮城県", [17,19,21,23,24,26]),
        new("akita", "秋田県", [17,21,24,25,28]),
        new("yamagata", "山形県", [16,18,20,22,24,26]),
        new("fukushima", "福島県", [15,17,19,21,23,25]),
        new("ibaraki", "茨城県", [13,14,15,17,18,19,20]),
        new("tochigi", "栃木県", [15,17,18,29,34,35,39,47]),
        new("gunma", "群馬県", [19,21,28,31,33,36,38,42]),
        new("saitama", "埼玉県", [18,21,22,23,24,25,26,27,32]),
        new("chiba", "千葉県", [16,21,22,23,24,25,26,27,30]),
        new("tokyo", "東京都", [16,21,22,23,24,25,26,27]),
        new("kanagawa", "神奈川県", [18,21,22,23,24,25,26,27]),
        new("niigata", "新潟県", [17,19,23,26,29,35]),
        new("toyama", "富山県", [18,20,22,24,28]),
        new("ishikawa", "石川県", [15,17,19,21,23,25]),
        new("fukui", "福井県", [22,24,25,26]),
        new("yamanashi", "山梨県", [21,23,25,27,29]),
        new("nagano", "長野県", [13,14,15,16,17,18]),
        new("gifu", "岐阜県", [18,20,21,22,23,24,25,26,27]),
        new("shizuoka", "静岡県", [15,17,19,20,21,22]),
        new("aichi", "愛知県", [19,20,21,22,23,25,26]),
        new("mie", "三重県", [13,15,17,19,21,22,23]),
        new("shiga", "滋賀県", [14,16,18,20,22,24,26]),
        new("kyoto", "京都府", [23,24,25,26,27,28]),
        new("osaka", "大阪府", [13,14,15,16,17,18,19,20,22,23,24]),
        new("hyogo", "兵庫県", [13,14,15,16,17,18,19,20,22]),
        new("nara", "奈良県", [13,14,15,16,17,18,19,20,22]),
        new("wakayama", "和歌山県", [14,15,16,17,18,19,20]),
        new("tottori", "鳥取県", [17,19,20,21,22]),
        new("shimane", "島根県", [17,19,20,21,22]),
        new("okayama", "岡山県", [14,15,16,17,18,19,20]),
        new("hiroshima", "広島県", [14,15,16,17,18,19,20]),
        new("yamaguchi", "山口県", [15,16,17,18,19]),
        new("tokushima", "徳島県", [16,17,18,19]),
        new("kagawa", "香川県", [14,15,16,17,18,19,20]),
        new("ehime", "愛媛県", [16,17,18,19,20,21]),
        new("kochi", "高知県", [17,19,21,23,25]),
        new("fukuoka", "福岡県", [22,23,24,25,26,27,28,29,30]),
        new("saga", "佐賀県", [33,36,40,41]),
        new("nagasaki", "長崎県", [14,15,16,17,18,19]),
        new("kumamoto", "熊本県", [15,16,17,18,19,20]),
        new("oita", "大分県", [14,15,16,17,18]),
        new("miyazaki", "宮崎県", [15,16,17,18]),
        new("kagoshima", "鹿児島県", [15,16,17,18,19,20]),
        new("okinawa", "沖縄県", [13,14,15,16,17])
    ];

    public static OneSegPrefecture Find(string? id) => All.FirstOrDefault(item =>
        string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        All.First(item => item.Id == "tokyo");
}

public sealed record OneSegSettings(
    string PrefectureId = "tokyo",
    int Channel = 27,
    bool RecordTransportStream = false,
    bool AutoRecordOnStart = false,
    string? RecordingDirectory = null,
    int RecordingSegmentMinutes = 0,
    long RecordingMaxBytes = 0,
    long RecordingStorageLimitBytes = 0,
    bool RecordingSidecarEnabled = true,
    string? IqCaptureDirectory = null,
    int IqCaptureSeconds = 5,
    bool StartPlaybackAutomatically = true,
    int Volume = 100,
    IReadOnlyList<OneSegStation>? ScannedStations = null,
    ushort? LastServiceId = null,
    int AudioTrackId = -1,
    bool CaptionsEnabled = true,
    double CaptionFontSize = 16,
    bool CaptionBackgroundEnabled = true,
    string CaptionPosition = "Bottom",
    bool EmergencyNotificationsEnabled = true)
{
    public OneSegSettings Normalize()
    {
        OneSegPrefecture prefecture = OneSegPrefecture.Find(PrefectureId);
        return this with
        {
            PrefectureId = prefecture.Id,
            Channel = OneSegChannel.JapaneseUhfChannels.Any(item => item.Number == Channel)
                ? Channel : prefecture.CandidateChannelNumbers[0],
            RecordingDirectory = string.IsNullOrWhiteSpace(RecordingDirectory)
                ? null : RecordingDirectory.Trim(),
            AutoRecordOnStart = AutoRecordOnStart,
            RecordingSegmentMinutes = Math.Clamp(RecordingSegmentMinutes, 0, 24 * 60),
            RecordingMaxBytes = Math.Clamp(RecordingMaxBytes, 0, 1_000_000_000_000L),
            RecordingStorageLimitBytes = Math.Clamp(RecordingStorageLimitBytes, 0, 10_000_000_000_000L),
            IqCaptureDirectory = string.IsNullOrWhiteSpace(IqCaptureDirectory) ? null : IqCaptureDirectory.Trim(),
            IqCaptureSeconds = Math.Clamp(IqCaptureSeconds, 1, 60),
            Volume = 100,
            CaptionFontSize = 16,
            CaptionPosition = CaptionPosition is "Top" or "Center" or "Bottom"
                ? CaptionPosition : "Bottom",
            EmergencyNotificationsEnabled = true,
            ScannedStations = (ScannedStations ?? []).Where(station =>
                    OneSegChannel.JapaneseUhfChannels.Any(channel =>
                        channel.Number == station.ChannelNumber) &&
                    !string.IsNullOrWhiteSpace(station.ServiceName))
                .DistinctBy(station => (station.ServiceId, station.ChannelNumber))
                .Select(station => station with
                {
                    ServiceName = station.ServiceName.Trim(),
                    ProviderName = (station.ProviderName ?? "").Trim()
                }).ToArray()
        };
    }
}

public enum IsdbTMode
{
    Unknown = 0,
    Mode1 = 1,
    Mode2 = 2,
    Mode3 = 3
}

public enum IsdbTGuardInterval
{
    Unknown = 0,
    OneQuarter = 4,
    OneEighth = 8,
    OneSixteenth = 16,
    OneThirtySecond = 32
}

public enum IsdbTModulation
{
    Dqpsk = 0,
    Qpsk = 1,
    Qam16 = 2,
    Qam64 = 3,
    Unused = 7
}

public enum IsdbTCodeRate
{
    Rate1Over2 = 0,
    Rate2Over3 = 1,
    Rate3Over4 = 2,
    Rate5Over6 = 3,
    Rate7Over8 = 4,
    Unused = 7
}

public sealed record TmccLayerParameters(
    IsdbTModulation Modulation,
    IsdbTCodeRate CodeRate,
    int TimeInterleavingLength,
    int SegmentCount);

public sealed record TmccParameters(
    bool PartialReception,
    bool EmergencyAlarm,
    int SwitchingCountdown,
    TmccLayerParameters LayerA,
    TmccLayerParameters LayerB,
    TmccLayerParameters LayerC,
    bool IsParityValid);

public sealed record OneSegDiagnostics(
    DateTimeOffset MeasuredAt,
    IsdbTMode Mode,
    IsdbTGuardInterval GuardInterval,
    bool OfdmLocked,
    bool TmccLocked,
    double FrequencyOffsetHz,
    double MerDb,
    long OfdmSymbols,
    long TmccFrames,
    long ViterbiBytes,
    long ReedSolomonPackets,
    long ReedSolomonRejected,
    long TransportPackets,
    long ContinuityErrors,
    TmccParameters? Tmcc,
    double ChannelLevelDbfs = double.NegativeInfinity)
{
    public static OneSegDiagnostics Empty { get; } = new(
        DateTimeOffset.MinValue, IsdbTMode.Unknown, IsdbTGuardInterval.Unknown,
        false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
}

public sealed record OneSegElementaryStream(
    ushort Pid,
    byte StreamType,
    string Kind,
    string Codec,
    string Language,
    byte? ComponentTag,
    ushort? DataComponentId = null,
    string DescriptorSummary = "");

public sealed record OneSegProgramInfo(
    ushort ServiceId,
    string ServiceName,
    string ProviderName,
    ushort PmtPid,
    ushort PcrPid,
    IReadOnlyList<OneSegElementaryStream> Streams);

public sealed record OneSegEventInfo(
    ushort ServiceId,
    ushort EventId,
    DateTimeOffset? StartTime,
    TimeSpan? Duration,
    string Title,
    string Description,
    string ExtendedDescription = "",
    string Genre = "",
    string Series = "",
    string VideoComponent = "",
    string AudioComponent = "",
    string ParentalRating = "",
    string EventGroup = "",
    string DataContent = "",
    string DescriptorSummary = "",
    byte? AudioComponentType = null,
    bool IsPresentEvent = false)
{
    public bool IsMonoAudio => AudioComponentType == 0x01;
    public bool IsDualMonoAudio => AudioComponentType == 0x02;
    public bool IsStereoAudio => AudioComponentType == 0x03;
    public bool IsActiveAt(DateTimeOffset value) =>
        StartTime is DateTimeOffset start && EndTime is DateTimeOffset end &&
        start <= value && value < end;
    public DateTimeOffset? EndTime => StartTime is DateTimeOffset start && Duration is TimeSpan duration
        ? start + duration : null;
    public string TimeRangeText => StartTime is DateTimeOffset start
        ? EndTime is DateTimeOffset end
            ? $"{start:HH:mm} ～ {end:HH:mm}"
            : $"{start:HH:mm} ～ --:--"
        : "--:-- ～ --:--";
    public string FullDescription => string.Join("\n", new[] { Description, ExtendedDescription }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Attributes => string.Join(" / ", new[] { Genre, Series, VideoComponent,
        AudioComponent, ParentalRating, EventGroup, DataContent }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed record OneSegCaption(
    DateTimeOffset ReceivedAt,
    ushort Pid,
    string Language,
    string Text);

public sealed record OneSegDescriptorInfo(byte Tag, string Name, string Value, string RawHex)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Value) ? Name : $"{Name}: {Value}";
}

public sealed record OneSegConditionalAccessInfo(
    ushort SystemId,
    ushort Pid,
    string Kind,
    string PrivateData)
{
    public string DisplayText => $"{Kind} / System 0x{SystemId:X4} / PID 0x{Pid:X4}";
}

public sealed record OneSegNetworkService(ushort ServiceId, byte ServiceType)
{
    public string DisplayText => $"Service {ServiceId} / 種別 0x{ServiceType:X2}";
}

public sealed record OneSegTransportInfo(
    ushort TransportStreamId,
    ushort OriginalNetworkId,
    byte? RemoteControlKeyId,
    string TransportName,
    IReadOnlyList<OneSegNetworkService> Services,
    IReadOnlyList<OneSegDescriptorInfo> Descriptors)
{
    public string DisplayText => $"TS {TransportStreamId} / ONID {OriginalNetworkId}" +
        (RemoteControlKeyId is byte key ? $" / リモコン {key}" : "");
}

public sealed record OneSegNetworkInfo(
    ushort NetworkId,
    string NetworkName,
    IReadOnlyList<OneSegTransportInfo> Transports,
    IReadOnlyList<OneSegDescriptorInfo> Descriptors)
{
    public string DisplayName => string.IsNullOrWhiteSpace(NetworkName)
        ? $"Network {NetworkId}" : NetworkName;
}

public sealed record OneSegBroadcastClock(
    DateTimeOffset UtcTime,
    DateTimeOffset LocalTime,
    string CountryCode,
    int RegionId,
    TimeSpan CurrentOffset,
    DateTimeOffset? ChangeTime,
    TimeSpan? NextOffset)
{
    public string DisplayText => $"放送時刻 {LocalTime:yyyy/MM/dd HH:mm:ss zzz}";
}

public sealed record OneSegBroadcasterEntry(
    byte BroadcasterId,
    IReadOnlyList<OneSegDescriptorInfo> Descriptors)
{
    public string DisplayText => $"Broadcaster {BroadcasterId}: " +
        string.Join(" / ", Descriptors.Select(value => value.DisplayText));
}

public sealed record OneSegBroadcasterInfo(
    ushort OriginalNetworkId,
    bool BroadcastViewPropriety,
    IReadOnlyList<OneSegDescriptorInfo> CommonDescriptors,
    IReadOnlyList<OneSegBroadcasterEntry> Broadcasters);

public sealed record OneSegTransportClock(
    ushort Pid,
    long PcrBase,
    int PcrExtension,
    TimeSpan Position)
{
    public string DisplayText => $"PCR PID 0x{Pid:X4} / {Position:hh\\:mm\\:ss\\.fff}";
}

public sealed record OneSegBmlResource(
    string Path,
    string ContentType,
    byte[] Content)
{
    public int Size => Content.Length;
    public string DisplayText => $"{Path} ({ContentType}, {Size:N0} bytes)";
}

public sealed record OneSegDataModule(
    ushort Pid,
    uint DownloadId,
    ushort ModuleId,
    byte Version,
    string Name,
    string ContentType,
    int Size,
    bool Complete,
    string Preview,
    IReadOnlyList<OneSegBmlResource>? Resources = null,
    byte? ComponentTag = null)
{
    public string PidText => $"0x{Pid:X4}";
    public string Status => Complete ? "完了" : "受信中";
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Module {ModuleId}" : Name;
    public int ResourceCount => Resources?.Count ?? 0;
}
