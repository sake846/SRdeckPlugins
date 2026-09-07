using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public sealed record MeshtasticDisplayItem(
    string ReceivedTime,
    string Sender,
    string SenderName,
    string Transmission,
    string Route,
    string Port,
    string Quality,
    string Summary,
    string Details,
    string PayloadHex)
{
    public string Radio { get; init; } = "";
    public string RegionName { get; init; } = "";
    public uint PacketId { get; init; }
    public bool IsDecoded { get; init; }
    public string ModemPresetName { get; init; } = "-";
    public int RadioSlot { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public float? EstimatedSnrDb { get; init; }
    public float? EstimatedRssiDbfs { get; init; }
    public float? EstimatedRssiDbm { get; init; }
    public bool? PayloadCrcValid { get; init; }
    public int HopLimit { get; init; }
    public int HopStart { get; init; }
    public bool? WasRelayed { get; init; }
    public byte RelayNode { get; init; }

    public string PacketIdText => $"{PacketId:X8}";
    [JsonIgnore]
    public string ReceivedDateTimeText => ReceivedAt == default
        ? ReceivedTime
        : ReceivedAt.ToLocalTime().ToString("yyyy/MM/dd\nHH:mm:ss", CultureInfo.InvariantCulture);
    [JsonIgnore]
    public string ReceivedDateTimeSingleLineText => ReceivedAt == default
        ? ReceivedTime
        : ReceivedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    public bool IsTextMessage => string.Equals(Port, "TEXT_MESSAGE_APP", StringComparison.OrdinalIgnoreCase);

    public bool IsDirect => WasRelayed == false || string.Equals(Transmission, "直接", StringComparison.OrdinalIgnoreCase);

    public string RelayText
    {
        get
        {
            if (IsDirect)
            {
                return HopStart > 0 ? $"直接 (Hop {HopLimit}/{HopStart})" : "直接";
            }
            if (RelayNode != 0)
            {
                return HopStart > 0 ? $"中継 {RelayNode:x2} (Hop {HopLimit}/{HopStart})" : $"中継 {RelayNode:x2}";
            }
            return HopStart > 0 ? $"{Transmission} (Hop {HopLimit}/{HopStart})" : Transmission;
        }
    }

    public string SlotAndPresetText
    {
        get
        {
            string region = !string.IsNullOrEmpty(RegionName) && RegionName != "None" ? $"{RegionName} " : "";
            string preset = !string.IsNullOrEmpty(ModemPresetName) && ModemPresetName != "-" ? ModemPresetName : "";
            string slot = RadioSlot > 0 ? $"Slot {RadioSlot}" : "";
            if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(preset)) return $"{region}{slot} ({preset})";
            if (!string.IsNullOrEmpty(slot)) return $"{region}{slot}";
            if (!string.IsNullOrEmpty(preset)) return $"{region}{preset}".Trim();
            return $"{region}Slot -".Trim();
        }
    }

    public string SignalQualityText => MeshtasticLoRaReceptionQuality.FormatSignal(EstimatedSnrDb, EstimatedRssiDbfs, EstimatedRssiDbm);

    [JsonIgnore]
    public string QualityToolTip
    {
        get
        {
            int crcStart = Quality?.IndexOf(" / CRC ", StringComparison.Ordinal) ?? -1;
            return MeshtasticLoRaReceptionQuality.SignalDescription + Environment.NewLine +
                SignalQualityText + (crcStart >= 0 ? Quality![crcStart..] : "");
        }
    }

    public string PortPadded => (Port ?? string.Empty).PadRight(20);

    public string QualityText => SignalQualityText.PadRight(12);

    [JsonIgnore]
    public string DisplayDetails => FormatDetailsJapanese(Details);

    private static string FormatDetailsJapanese(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return "-";
        
        string text = details;
        
        // Position
        text = text.Replace("latitude=", "緯度=", StringComparison.Ordinal);
        text = text.Replace("longitude=", "経度=", StringComparison.Ordinal);
        text = text.Replace("altitudeM=", "高度=", StringComparison.Ordinal);
        text = text.Replace("altitudeSource=MANUAL", "高度情報源=手動", StringComparison.Ordinal);
        text = text.Replace("altitudeSource=INTERNAL", "高度情報源=内蔵", StringComparison.Ordinal);
        text = text.Replace("altitudeSource=EXTERNAL", "高度情報源=外部", StringComparison.Ordinal);
        text = text.Replace("altitudeSource=BAROMETRIC", "高度情報源=気圧計", StringComparison.Ordinal);
        text = text.Replace("altitudeSource=UNSET", "高度情報源=未設定", StringComparison.Ordinal);
        text = text.Replace("altitudeSource=", "高度情報源=", StringComparison.Ordinal);
        text = text.Replace("locationSource=MANUAL", "位置情報源=手動", StringComparison.Ordinal);
        text = text.Replace("locationSource=INTERNAL", "位置情報源=内蔵", StringComparison.Ordinal);
        text = text.Replace("locationSource=EXTERNAL", "位置情報源=外部", StringComparison.Ordinal);
        text = text.Replace("locationSource=UNSET", "位置情報源=未設定", StringComparison.Ordinal);
        text = text.Replace("locationSource=", "位置情報源=", StringComparison.Ordinal);
        text = text.Replace("time=", "測位時刻=", StringComparison.Ordinal);
        text = text.Replace("pdop=", "PDOP=", StringComparison.Ordinal);
        text = text.Replace("hdop=", "HDOP=", StringComparison.Ordinal);
        text = text.Replace("vdop=", "VDOP=", StringComparison.Ordinal);
        text = text.Replace("gpsAccuracyMm=", "GPS精度=", StringComparison.Ordinal);
        text = text.Replace("groundSpeed=", "対地速度=", StringComparison.Ordinal);
        text = text.Replace("groundTrackDeg=", "対地進行方向=", StringComparison.Ordinal);
        text = text.Replace("fixQuality=", "測位品質=", StringComparison.Ordinal);
        text = text.Replace("fixType=", "測位種別=", StringComparison.Ordinal);
        text = text.Replace("satellites=", "衛星数=", StringComparison.Ordinal);
        text = text.Replace("precisionBits=", "精度ビット数=", StringComparison.Ordinal);
        
        // NodeInfo
        text = text.Replace("id=", "ID=", StringComparison.Ordinal);
        text = text.Replace("longName=", "ノード名=", StringComparison.Ordinal);
        text = text.Replace("shortName=", "短縮名=", StringComparison.Ordinal);
        text = text.Replace("mac=", "MAC=", StringComparison.Ordinal);
        text = text.Replace("hardware=", "ハードウェア=", StringComparison.Ordinal);
        text = text.Replace("role=", "役割=", StringComparison.Ordinal);
        text = text.Replace("licensed=True", "免許局=有", StringComparison.Ordinal);
        text = text.Replace("licensed=False", "免許局=無", StringComparison.Ordinal);
        text = text.Replace("licensed=", "免許局=", StringComparison.Ordinal);
        text = text.Replace("unmessagable=True", "メッセージ不可=はい", StringComparison.Ordinal);
        text = text.Replace("unmessagable=False", "メッセージ不可=いいえ", StringComparison.Ordinal);
        text = text.Replace("unmessagable=", "メッセージ不可=", StringComparison.Ordinal);
        text = text.Replace("publicKey=", "公開鍵=", StringComparison.Ordinal);
        
        // Telemetry
        text = text.Replace("variant=", "種別=", StringComparison.Ordinal);
        text = text.Replace("battery=", "バッテリー=", StringComparison.Ordinal);
        text = text.Replace("voltage=", "電圧=", StringComparison.Ordinal);
        text = text.Replace("channelUtilization=", "チャネル利用率=", StringComparison.Ordinal);
        text = text.Replace("airUtilTx=", "送信利用率=", StringComparison.Ordinal);
        text = text.Replace("uptime=", "稼働時間=", StringComparison.Ordinal);
        text = text.Replace("temperature=", "温度=", StringComparison.Ordinal);
        text = text.Replace("humidity=", "湿度=", StringComparison.Ordinal);
        text = text.Replace("pressure=", "気圧=", StringComparison.Ordinal);
        text = text.Replace("gasResistance=", "ガス抵抗=", StringComparison.Ordinal);
        text = text.Replace("current=", "電流=", StringComparison.Ordinal);
        text = text.Replace("iaq=", "空気質指数=", StringComparison.Ordinal);
        text = text.Replace("distance=", "距離=", StringComparison.Ordinal);
        text = text.Replace("lux=", "照度=", StringComparison.Ordinal);
        text = text.Replace("whiteLux=", "白色照度=", StringComparison.Ordinal);
        text = text.Replace("irLux=", "赤外照度=", StringComparison.Ordinal);
        text = text.Replace("uvLux=", "UV照度=", StringComparison.Ordinal);
        text = text.Replace("windDirection=", "風向=", StringComparison.Ordinal);
        text = text.Replace("windSpeed=", "風速=", StringComparison.Ordinal);
        text = text.Replace("weight=", "重量=", StringComparison.Ordinal);
        text = text.Replace("windGust=", "最大瞬間風速=", StringComparison.Ordinal);
        text = text.Replace("windLull=", "最小風速=", StringComparison.Ordinal);
        text = text.Replace("radiation=", "放射線量=", StringComparison.Ordinal);
        text = text.Replace("rainfall1h=", "1時間雨量=", StringComparison.Ordinal);
        text = text.Replace("rainfall24h=", "24時間雨量=", StringComparison.Ordinal);
        text = text.Replace("soilMoisture=", "土壌水分=", StringComparison.Ordinal);
        text = text.Replace("soilTemperature=", "土壌温度=", StringComparison.Ordinal);
        text = text.Replace("oneWireTemperature=", "1-Wire温度=", StringComparison.Ordinal);
        text = text.Replace("packetsTx=", "送信パケット数=", StringComparison.Ordinal);
        text = text.Replace("packetsRx=", "受信パケット数=", StringComparison.Ordinal);
        text = text.Replace("packetsRxBad=", "エラーパケット数=", StringComparison.Ordinal);
        text = text.Replace("onlineNodes=", "オンラインノード数=", StringComparison.Ordinal);
        text = text.Replace("totalNodes=", "総ノード数=", StringComparison.Ordinal);
        text = text.Replace("rxDuplicates=", "重複受信数=", StringComparison.Ordinal);
        text = text.Replace("txRelay=", "中継送信数=", StringComparison.Ordinal);
        text = text.Replace("txRelayCanceled=", "中継取消数=", StringComparison.Ordinal);
        text = text.Replace("heapTotal=", "総ヒープ=", StringComparison.Ordinal);
        text = text.Replace("heapFree=", "空きヒープ=", StringComparison.Ordinal);
        text = text.Replace("txDropped=", "破棄送信数=", StringComparison.Ordinal);
        text = text.Replace("noiseFloor=", "ノイズフロア=", StringComparison.Ordinal);
        
        // Other
        text = text.Replace("notification=", "通知=", StringComparison.Ordinal);
        text = text.Replace("errorReason=", "エラー理由=", StringComparison.Ordinal);
        text = text.Replace("routeTowards=", "往路=", StringComparison.Ordinal);
        text = text.Replace("routeBack=", "復路=", StringComparison.Ordinal);
        text = text.Replace("nodeId=", "ノードID=", StringComparison.Ordinal);
        text = text.Replace("lastSentBy=", "送信元=", StringComparison.Ordinal);
        text = text.Replace("interval=", "送信間隔=", StringComparison.Ordinal);
        text = text.Replace("neighbors=", "ネイバー=", StringComparison.Ordinal);
        text = text.Replace("lastRx=", "受信時刻=", StringComparison.Ordinal);
        text = text.Replace("snr=", "SNR=", StringComparison.Ordinal);

        return text;
    }

    [JsonIgnore]
    public IReadOnlyList<MeshtasticDetailField> DisplayFields => ParseDisplayFields(DisplayDetails);

    private static IReadOnlyList<MeshtasticDetailField> ParseDisplayFields(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text == "-") return [new MeshtasticDetailField(string.Empty, "-")];

        var list = new List<MeshtasticDetailField>();
        string[] tokens = text.Contains("  ", StringComparison.Ordinal)
            ? text.Split(["  "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            int eq = token.IndexOf('=');
            if (eq > 0)
            {
                string key = token[..eq].Trim();
                string val = token[(eq + 1)..].Trim();
                list.Add(new MeshtasticDetailField(key, val));
            }
            else
            {
                list.Add(new MeshtasticDetailField(string.Empty, token));
            }
        }

        if (list.Count == 0)
        {
            list.Add(new MeshtasticDetailField(string.Empty, text));
        }

        return list;
    }
}

public sealed record MeshtasticDetailField(string Name, string Value)
{
    public bool HasName => !string.IsNullOrEmpty(Name);
    public string Separator => HasName ? " " : string.Empty;
}

public sealed record MeshtasticApplicationDisplayItem(string Name, bool IsReceived);

public sealed record MeshtasticPacketGroupItem(
    string Sender,
    string SenderName,
    string PacketIdText,
    string Port,
    string Summary,
    IReadOnlyList<MeshtasticDisplayItem> Receptions)
{
    public int ReceptionCount => Receptions.Count;
    public string ReceptionCountText => $"{ReceptionCount}回受信";
    public string LatestReceivedDateTime => Receptions.Count == 0
        ? "-"
        : Receptions.Max(item => item.ReceivedAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
}
public sealed record MeshtasticReceptionAggregateItem(
    string Label,
    int ReceptionCount,
    int PacketCount,
    int NodeCount)
{
    public string Summary => $"受信 {ReceptionCount} / Packet {PacketCount} / Node {NodeCount}";
}

public sealed record MeshtasticHistoryRankingItem(string Label, string SubLabel, int ReceptionCount, int PacketCount)
{
    public string Summary => $"受信 {ReceptionCount} / Packet {PacketCount}";
}

internal sealed record MeshtasticPersistedNode(
    uint NodeNumber, string DisplayName, string Identity, string LastSeen, string Route,
    string Position, string Telemetry, string LastMessage, string SignalQuality, string Details,
    double? Latitude, double? Longitude, int? AltitudeMeters, int PacketCount, int ReceptionCount,
    int DirectReceptionCount, int RelayedReceptionCount, int UnknownRouteReceptionCount,
    DateTimeOffset? FirstSeenAt, DateTimeOffset? LastSeenAt, string? Mode = "-",
    string? LastReceivedContent = null, string? LongName = null, string? ShortName = null,
    Dictionary<uint, bool?>? PreferredReceptionRoutes = null,
    bool? HasCompletePreferredReceptionRoutes = null,
    string? HardwareName = null, string? RoleName = null, int? RadioSlot = null,
    string? ModemPresetName = null, float? MinimumRssiDbfs = null, float? MaximumRssiDbfs = null,
    float? MinimumRssiDbm = null, float? MaximumRssiDbm = null, float? MinimumSnrDb = null,
    float? MaximumSnrDb = null, string? ReceivedAppTypes = null, string? RegionName = null);

internal sealed record MeshtasticPersistedState(
    DateTimeOffset SavedAt,
    List<MeshtasticPersistedNode> Nodes,
    string? Mode = null);

internal static class MeshtasticRouteDisplay
{
    public static string Format(MeshtasticDataReception reception)
    {
        return reception.Packet.WasRelayed switch
        {
            false => "直接",
            true => $"中継 {reception.Packet.RelayNode:x2}",
            null => "経路不明"
        };
    }
}

public sealed partial class MeshtasticMapPoint : ObservableObject
{
    public MeshtasticMapPoint(uint nodeNumber) => NodeNumber = nodeNumber;
    public uint NodeNumber { get; }
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _coordinates = "";
    [ObservableProperty] private string _activityStatus = "不明";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasDirectReception;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public sealed partial class MeshtasticSlotSelectionItem : ObservableObject
{
    public MeshtasticSlotSelectionItem(int slot, bool isSelected)
    {
        Slot = slot;
        _isSelected = isSelected;
    }

    public int Slot { get; }
    public string Label => Slot.ToString();
    [ObservableProperty] private bool _isSelected;
}

public sealed class MeshtasticSpecifiedSlotOption
{
    public int Slot { get; }
    public int FrequencyHz { get; }
    public string DisplayName { get; }

    public MeshtasticSpecifiedSlotOption(int slot, int frequencyHz)
    {
        Slot = slot;
        FrequencyHz = frequencyHz;
        DisplayName = $"slot {slot} ({frequencyHz / 1_000_000.0:0.###} MHz)";
    }
}

public sealed partial class MeshtasticNodeDisplayItem : ObservableObject
{
    public MeshtasticNodeDisplayItem(uint nodeNumber)
    {
        NodeNumber = nodeNumber;
        NodeId = $"!{nodeNumber:x8}";
        DisplayName = NodeId;
    }

    public uint NodeNumber { get; }
    public string NodeId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private string _displayName;
    [ObservableProperty] private string _longName = "";
    [ObservableProperty] private string _shortName = "";
    [ObservableProperty] private string _hardwareName = "-";
    [ObservableProperty] private string _roleName = "-";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlotAndPresetText))]
    private string _regionName = "";
    [ObservableProperty] private string _receivedAppTypesText = "-";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private string _identity = "NodeInfo 未受信";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSeenTime))]
    [NotifyPropertyChangedFor(nameof(LastSeenDateTimeText))]
    private string _lastSeen = "-";
    [ObservableProperty] private string _route = "-";
    [ObservableProperty] private string _mode = "-";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private string _position = "Position 未受信";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private string _telemetry = "Telemetry 未受信";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private string _lastMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private string _lastReceivedContent = "";
    [ObservableProperty] private string _counts = "Packet 0 / Reception 0";
    [ObservableProperty] private string _signalQuality = "SNR — / RSSI — / CRC - / Corr 0";
    public string SignalQualityToolTip => MeshtasticLoRaReceptionQuality.SignalDescription;
    [ObservableProperty] private string _details = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosition))]
    [NotifyPropertyChangedFor(nameof(Coordinates))]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private double? _latitude;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPosition))]
    [NotifyPropertyChangedFor(nameof(Coordinates))]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private double? _longitude;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Coordinates))]
    [NotifyPropertyChangedFor(nameof(LastSummaryText))]
    private int? _altitudeMeters;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityStatus))]
    [NotifyPropertyChangedFor(nameof(FirstSeenDateTimeText))]
    private DateTimeOffset? _firstSeenAt;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityStatus))]
    [NotifyPropertyChangedFor(nameof(LastSeenTime))]
    [NotifyPropertyChangedFor(nameof(LastSeenDateTimeText))]
    private DateTimeOffset? _lastSeenAt;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlotAndPresetText))]
    private int _radioSlot;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlotAndPresetText))]
    private string _modemPresetName = "-";

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
    public string Coordinates => HasPosition
        ? $"{Latitude!.Value:F6}, {Longitude!.Value:F6}" + (AltitudeMeters.HasValue ? $" / {AltitudeMeters.Value} m" : "")
        : "Position 未受信";
    public string ActivityStatus
    {
        get
        {
            if (!LastSeenAt.HasValue) return "不明";
            TimeSpan sinceLast = DateTimeOffset.Now - LastSeenAt.Value;
            if (FirstSeenAt.HasValue && DateTimeOffset.Now - FirstSeenAt.Value <= TimeSpan.FromHours(1)) return "新規";
            if (sinceLast <= TimeSpan.FromHours(2)) return "活動中";
            if (sinceLast <= TimeSpan.FromHours(24)) return "最近";
            return "休止";
        }
    }

    public string LastSeenTime => LastSeenAt.HasValue ? LastSeenAt.Value.ToLocalTime().ToString("HH:mm:ss") : (LastSeen != "-" ? LastSeen : "");
    public string LastSeenDateTimeText => LastSeenAt.HasValue
        ? LastSeenAt.Value.ToLocalTime().ToString("yyyy/MM/dd\nHH:mm:ss", CultureInfo.InvariantCulture)
        : (LastSeen != "-" ? LastSeen : "");
    public string FirstSeenDateTimeText => FirstSeenAt.HasValue
        ? FirstSeenAt.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "—";

    public string SlotAndPresetText
    {
        get
        {
            string region = !string.IsNullOrEmpty(RegionName) && RegionName != "None" ? $"{RegionName} " : "";
            string preset = !string.IsNullOrEmpty(ModemPresetName) && ModemPresetName != "-" ? ModemPresetName : "";
            string slot = RadioSlot > 0 ? $"Slot {RadioSlot}" : "";
            if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(preset)) return $"{region}{slot} ({preset})";
            if (!string.IsNullOrEmpty(slot)) return $"{region}{slot}";
            if (!string.IsNullOrEmpty(preset)) return $"{region}{preset}".Trim();
            return $"{region}Slot -".Trim();
        }
    }

    public string RssiRangeText => FormatRange(
        MinimumRssiDbm, MaximumRssiDbm, "dBm") ??
        FormatRange(MinimumRssiDbfs, MaximumRssiDbfs, "dBFS") ?? "—";

    public string SnrRangeText => FormatRange(MinimumSnrDb, MaximumSnrDb, "dB") ?? "—";

    private static string? FormatRange(float? minimum, float? maximum, string unit)
    {
        float? validMinimum = IsFinite(minimum) ? minimum : null;
        float? validMaximum = IsFinite(maximum) ? maximum : null;
        if (!validMinimum.HasValue && !validMaximum.HasValue) return null;

        float low = validMinimum ?? validMaximum!.Value;
        float high = validMaximum ?? validMinimum!.Value;
        return $"最大 {high.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} / " +
               $"最小 {low.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)} {unit}";
    }

    private static bool IsFinite(float? value) => value is float number && float.IsFinite(number);

    public string LastSummaryText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LastReceivedContent)) return LastReceivedContent;
            if (!string.IsNullOrWhiteSpace(LastMessage)) return LastMessage;
            if (HasPosition) return $"位置: {Coordinates}";
            if (!string.IsNullOrWhiteSpace(Telemetry) && Telemetry != "Telemetry 未受信") return $"テレメトリ: {Telemetry}";
            if (!string.IsNullOrWhiteSpace(Identity) && Identity != "NodeInfo 未受信") return Identity;
            return "データ未受信";
        }
    }

    private int _packetCount;
    private int _receptionCount;
    private int _directReceptionCount;
    private int _relayedReceptionCount;
    private int _unknownRouteReceptionCount;
    private float? _minimumRssiDbfs;
    private float? _maximumRssiDbfs;
    private float? _minimumRssiDbm;
    private float? _maximumRssiDbm;
    private float? _minimumSnrDb;
    private float? _maximumSnrDb;
    private readonly HashSet<string> _receivedAppTypes = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<MeshtasticApplicationDisplayItem> ApplicationTypes =>
        MeshtasticDataParser.KnownApplicationNames
            .Select(name => new MeshtasticApplicationDisplayItem(name, _receivedAppTypes.Contains(name)))
            .ToArray();
    public int PacketCount => _packetCount;
    public int ReceptionCount => _receptionCount;
    public int DirectReceptionCount => _directReceptionCount;
    public int RelayedReceptionCount => _relayedReceptionCount;
    public int UnknownRouteReceptionCount => _unknownRouteReceptionCount;
    public string RouteCounts => $"直接 {_directReceptionCount} / 中継 {_relayedReceptionCount} / 不明 {_unknownRouteReceptionCount}";
    public float? MinimumRssiDbfs => _minimumRssiDbfs;
    public float? MaximumRssiDbfs => _maximumRssiDbfs;
    public float? MinimumRssiDbm => _minimumRssiDbm;
    public float? MaximumRssiDbm => _maximumRssiDbm;
    public float? MinimumSnrDb => _minimumSnrDb;
    public float? MaximumSnrDb => _maximumSnrDb;
    private MeshtasticNodeInfo? _nodeInfo;
    private MeshtasticPosition? _latestPosition;
    private MeshtasticTelemetry? _latestTelemetry;
    private readonly Dictionary<uint, bool?> _preferredReceptionRouteByPacket = new();
    private bool _hasCompletePreferredReceptionRoutes = true;
    internal Dictionary<uint, bool?> PreferredReceptionRoutes => new(_preferredReceptionRouteByPacket);
    internal bool HasCompletePreferredReceptionRoutes => _hasCompletePreferredReceptionRoutes;

    private bool TrackPreferredPacketRoute(MeshtasticRadioPacket packet)
    {
        bool? route = packet.WasRelayed;
        if (!_preferredReceptionRouteByPacket.TryGetValue(packet.PacketId, out bool? preferredRoute))
        {
            _preferredReceptionRouteByPacket[packet.PacketId] = route;
            AdjustPreferredRouteCount(route, 1);
            NotifyRouteCountsChanged();
            return true;
        }

        int preferredRank = RoutePreferenceRank(preferredRoute);
        int newRank = RoutePreferenceRank(route);
        if (newRank < preferredRank)
            return false;

        if (newRank > preferredRank)
        {
            AdjustPreferredRouteCount(preferredRoute, -1);
            _preferredReceptionRouteByPacket[packet.PacketId] = route;
            AdjustPreferredRouteCount(route, 1);
            NotifyRouteCountsChanged();
        }

        return true;
    }

    private static int RoutePreferenceRank(bool? wasRelayed) => wasRelayed switch
    {
        false => 2,
        true => 1,
        null => 0
    };

    public void Update(MeshtasticDataReception reception, string? receivedContent = null, string? modemPresetName = null,
        string? regionName = null)
    {
        _receptionCount++;
        if (!reception.IsDuplicate) _packetCount++;
        RecordSeenAt(reception.Packet.ReceivedAt);
        UpdateSignalRanges(reception.Quality);
        bool updateLatestReception = TrackPreferredPacketRoute(reception.Packet);
        RecordReceivedAppType(MeshtasticDataParser.GetNodeListApplicationName(reception.Data.PortNumber));
        Counts = $"Packet {_packetCount} / Reception {_receptionCount}";
        if (!updateLatestReception)
        {
            Details = BuildDetails();
            return;
        }

        Route = $"{MeshtasticRouteDisplay.Format(reception)} / Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}";
        Mode = reception.Radio.Summary;
        UpdateRadio(reception.Radio, modemPresetName, regionName);
        SignalQuality = reception.Quality.Summary;

        switch (reception.Data.DecodedPayload)
        {
            case MeshtasticNodeInfo nodeInfo:
                _nodeInfo = nodeInfo;
                LongName = nodeInfo.LongName ?? "";
                ShortName = nodeInfo.ShortName ?? "";
                string baseName = !string.IsNullOrWhiteSpace(nodeInfo.LongName)
                    ? nodeInfo.LongName.Trim()
                    : (!string.IsNullOrWhiteSpace(nodeInfo.Id) ? nodeInfo.Id.Trim() : NodeId);
                DisplayName = FormatDisplayName(baseName, nodeInfo.ShortName);
                HardwareName = nodeInfo.HardwareName;
                RoleName = nodeInfo.RoleName;
                Identity = $"{HardwareName} / {RoleName}";
                break;
            case MeshtasticPosition position:
                _latestPosition = position;
                Position = position.Summary;
                Latitude = position.Latitude;
                Longitude = position.Longitude;
                AltitudeMeters = position.AltitudeMeters;
                OnPropertyChanged(nameof(HasPosition));
                OnPropertyChanged(nameof(Coordinates));
                break;
            case MeshtasticTelemetry telemetry:
                _latestTelemetry = telemetry;
                Telemetry = telemetry.Summary;
                break;
        }

        if (reception.Data.Text is not null)
        {
            LastMessage = reception.Data.Text;
        }

        LastReceivedContent = receivedContent ?? reception.Data.DecodedPayload?.Summary ?? reception.Data.Text ??
            (reception.Data.Payload.Length == 0
                ? "(ペイロードなし)"
                : $"{reception.Data.PortName} / {reception.Data.Payload.Length} bytes");

        _lastQualityDetails = reception.Quality.Details;
        Details = BuildDetails();
        OnPropertyChanged(nameof(LastSummaryText));
        OnPropertyChanged(nameof(LastSeenTime));
        OnPropertyChanged(nameof(LastSeenDateTimeText));
    }

    public void Observe(MeshtasticPacketReception reception, string? receivedContent = null, string? modemPresetName = null)
    {
        _receptionCount++;
        if (!reception.IsDuplicate) _packetCount++;
        RecordSeenAt(reception.Packet.ReceivedAt);
        UpdateSignalRanges(reception.Quality);
        bool updateLatestReception = TrackPreferredPacketRoute(reception.Packet);
        Counts = $"Packet {_packetCount} / Reception {_receptionCount}";
        if (!updateLatestReception)
        {
            Details = BuildDetails();
            return;
        }

        Route = $"{FormatPacketRoute(reception.Packet)} / Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}";
        Mode = reception.Radio.Summary;
        UpdateRadio(reception.Radio, modemPresetName);
        SignalQuality = reception.Quality.Summary;
        LastReceivedContent = receivedContent ??
            $"復号できないパケット / Channel 0x{reception.Packet.ChannelHash:X2}";
        _lastQualityDetails = reception.Quality.Details;
        Details = BuildDetails();
        OnPropertyChanged(nameof(LastSummaryText));
        OnPropertyChanged(nameof(LastSeenTime));
        OnPropertyChanged(nameof(LastSeenDateTimeText));
    }

    private static string FormatPacketRoute(MeshtasticRadioPacket packet) => packet.WasRelayed switch
    {
        false => "直接",
        true => $"中継 {packet.RelayNode:x2}",
        null => "経路不明"
    };

    private void UpdateRadio(MeshtasticRadioReception radio, string? modemPresetName, string? regionName = null)
    {
        RadioSlot = radio.RadioChannel;
        if (!string.IsNullOrWhiteSpace(modemPresetName) && modemPresetName != "-")
            ModemPresetName = modemPresetName;
        if (!string.IsNullOrWhiteSpace(regionName))
            RegionName = regionName;
        else if (radio.Region != default)
            RegionName = radio.Region.ToString();
    }

    private void UpdateSignalRanges(MeshtasticLoRaReceptionQuality quality)
    {
        UpdateRange(ref _minimumRssiDbfs, ref _maximumRssiDbfs, quality.EstimatedRssiDbfs);
        UpdateRange(ref _minimumRssiDbm, ref _maximumRssiDbm, quality.EstimatedRssiDbm);
        UpdateRange(ref _minimumSnrDb, ref _maximumSnrDb, quality.EstimatedSnrDb);
        OnPropertyChanged(nameof(RssiRangeText));
        OnPropertyChanged(nameof(SnrRangeText));
        OnPropertyChanged(nameof(MinimumRssiDbfs));
        OnPropertyChanged(nameof(MaximumRssiDbfs));
        OnPropertyChanged(nameof(MinimumRssiDbm));
        OnPropertyChanged(nameof(MaximumRssiDbm));
        OnPropertyChanged(nameof(MinimumSnrDb));
        OnPropertyChanged(nameof(MaximumSnrDb));
    }

    private static void UpdateRange(ref float? minimum, ref float? maximum, float? value)
    {
        if (value is not float number || !float.IsFinite(number)) return;
        minimum = minimum is float currentMinimum && currentMinimum < number ? currentMinimum : number;
        maximum = maximum is float currentMaximum && currentMaximum > number ? currentMaximum : number;
    }

    private void AdjustPreferredRouteCount(bool? wasRelayed, int amount)
    {
        switch (wasRelayed)
        {
            case false: _directReceptionCount += amount; break;
            case true: _relayedReceptionCount += amount; break;
            default: _unknownRouteReceptionCount += amount; break;
        }
    }

    private void NotifyRouteCountsChanged()
    {
        OnPropertyChanged(nameof(DirectReceptionCount));
        OnPropertyChanged(nameof(RelayedReceptionCount));
        OnPropertyChanged(nameof(UnknownRouteReceptionCount));
        OnPropertyChanged(nameof(RouteCounts));
    }

    private void RecordSeenAt(DateTimeOffset receivedAt)
    {
        FirstSeenAt ??= receivedAt;
        LastSeenAt = receivedAt;
        LastSeen = receivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        OnPropertyChanged(nameof(ActivityStatus));
        OnPropertyChanged(nameof(LastSeenTime));
        OnPropertyChanged(nameof(LastSeenDateTimeText));
    }

    internal void RecordReceivedAppType(string? appType)
    {
        string normalized = NormalizeReceivedAppType(appType);
        if (string.IsNullOrWhiteSpace(normalized) || !_receivedAppTypes.Add(normalized)) return;
        ReceivedAppTypesText = FormatReceivedAppTypes(_receivedAppTypes);
        OnPropertyChanged(nameof(ApplicationTypes));
    }

    private void RestoreReceivedAppTypes(string? appTypes)
    {
        _receivedAppTypes.Clear();
        if (!string.IsNullOrWhiteSpace(appTypes) && appTypes != "-")
        {
            foreach (string appType in appTypes.Split(
                         [' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string normalized = NormalizeReceivedAppType(appType);
                if (!string.IsNullOrWhiteSpace(normalized))
                    _receivedAppTypes.Add(normalized);
            }
        }

        ReceivedAppTypesText = _receivedAppTypes.Count == 0
            ? "-"
            : FormatReceivedAppTypes(_receivedAppTypes);
        OnPropertyChanged(nameof(ApplicationTypes));
    }

    private static string NormalizeReceivedAppType(string? appType)
    {
        return MeshtasticDataParser.NormalizeNodeListApplicationName(appType);
    }

    private static string FormatReceivedAppTypes(IEnumerable<string> appTypes) =>
        string.Join(" ", appTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private string BuildDetails()
    {
        var parts = new List<string>
        {
            $"Node: {NodeId}",
            $"Last seen: {LastSeen}",
            $"Route: {Route}",
            $"Mode: {Mode}",
            Counts,
            $"Reception route: {RouteCounts}"
        };
        parts.Add($"Quality: {SignalQuality} ({_lastQualityDetails})");
        parts.Add($"RSSI range: {RssiRangeText} / SNR range: {SnrRangeText}");
        if (_nodeInfo is not null) parts.Add(_nodeInfo.Details);
        if (_latestPosition is not null) parts.Add(_latestPosition.Details);
        if (_latestTelemetry is not null) parts.Add(_latestTelemetry.Details);
        if (!string.IsNullOrWhiteSpace(LastMessage)) parts.Add($"Last message: {LastMessage}");
        return string.Join(Environment.NewLine, parts);
    }

    public static string FormatDisplayName(string? baseName, string? shortName)
    {
        string trimmedBase = baseName?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedBase)) return string.Empty;

        if (!string.IsNullOrWhiteSpace(shortName))
        {
            string trimmedShort = shortName.Trim();
            if (!string.Equals(trimmedBase, trimmedShort, StringComparison.OrdinalIgnoreCase) &&
                !trimmedBase.EndsWith($"[{trimmedShort}]", StringComparison.OrdinalIgnoreCase) &&
                !trimmedBase.EndsWith($"({trimmedShort})", StringComparison.OrdinalIgnoreCase))
            {
                return $"{trimmedBase} [{trimmedShort}]";
            }
        }

        return trimmedBase;
    }

    private string _lastQualityDetails = "-";

    internal void Restore(MeshtasticPersistedNode state)
    {
        DisplayName = state.DisplayName;
        Identity = state.Identity;
        LongName = state.LongName ?? "";
        ShortName = state.ShortName ?? "";
        (HardwareName, RoleName) = ResolveHardwareAndRole(state);
        if (!string.IsNullOrWhiteSpace(state.HardwareName) || !string.IsNullOrWhiteSpace(state.RoleName))
            Identity = $"{HardwareName} / {RoleName}";
        LastSeen = state.LastSeen;
        Route = state.Route;
        Mode = state.Mode ?? "-";
        Position = state.Position;
        Telemetry = state.Telemetry;
        LastMessage = state.LastMessage;
        LastReceivedContent = state.LastReceivedContent ?? "";
        SignalQuality = MeshtasticLoRaReceptionQuality.HideLegacyQuality(state.SignalQuality);
        // Migrate old Q/S/N presentation while preserving unrelated details.
        // Never relabel an old FFT margin as an SNR or invent a missing RSSI.
        Details = SignalQuality != state.SignalQuality
            ? string.Join(Environment.NewLine, state.Details.Split('\n').Select(line =>
                line.TrimStart().StartsWith("Quality:", StringComparison.Ordinal)
                    ? $"Quality: {SignalQuality}" : line.TrimEnd('\r')))
            : state.Details;
        Latitude = state.Latitude;
        Longitude = state.Longitude;
        AltitudeMeters = state.AltitudeMeters;
        _packetCount = state.PacketCount;
        _receptionCount = state.ReceptionCount;
        _preferredReceptionRouteByPacket.Clear();
        if (state.PreferredReceptionRoutes is { } preferredRoutes)
        {
            foreach ((uint packetId, bool? route) in preferredRoutes)
                _preferredReceptionRouteByPacket[packetId] = route;
        }
        _hasCompletePreferredReceptionRoutes = state.HasCompletePreferredReceptionRoutes == true &&
                                                state.PreferredReceptionRoutes is not null;
        if (_hasCompletePreferredReceptionRoutes)
        {
            _directReceptionCount = _preferredReceptionRouteByPacket.Values.Count(route => route == false);
            _relayedReceptionCount = _preferredReceptionRouteByPacket.Values.Count(route => route == true);
            _unknownRouteReceptionCount = _preferredReceptionRouteByPacket.Values.Count(route => route is null);
        }
        else
        {
            _directReceptionCount = state.DirectReceptionCount;
            _relayedReceptionCount = state.RelayedReceptionCount;
            _unknownRouteReceptionCount = state.UnknownRouteReceptionCount;
        }
        FirstSeenAt = state.FirstSeenAt;
        LastSeenAt = state.LastSeenAt;
        RadioSlot = state.RadioSlot ?? ParseRadioSlot(state.Mode);
        ModemPresetName = string.IsNullOrWhiteSpace(state.ModemPresetName) ? "-" : state.ModemPresetName;
        RegionName = string.IsNullOrWhiteSpace(state.RegionName) ? ParseRegionName(state.Mode) : state.RegionName;
        _minimumRssiDbfs = state.MinimumRssiDbfs;
        _maximumRssiDbfs = state.MaximumRssiDbfs;
        _minimumRssiDbm = state.MinimumRssiDbm;
        _maximumRssiDbm = state.MaximumRssiDbm;
        _minimumSnrDb = state.MinimumSnrDb;
        _maximumSnrDb = state.MaximumSnrDb;
        RestoreReceivedAppTypes(state.ReceivedAppTypes);
        Counts = $"Packet {_packetCount} / Reception {_receptionCount}";
        NotifyRouteCountsChanged();
        OnPropertyChanged(nameof(HasPosition));
        OnPropertyChanged(nameof(Coordinates));
        OnPropertyChanged(nameof(LastSummaryText));
        OnPropertyChanged(nameof(LastSeenTime));
        OnPropertyChanged(nameof(LastSeenDateTimeText));
        OnPropertyChanged(nameof(RssiRangeText));
        OnPropertyChanged(nameof(SnrRangeText));
    }

    private static (string HardwareName, string RoleName) ResolveHardwareAndRole(MeshtasticPersistedNode state)
    {
        if (!string.IsNullOrWhiteSpace(state.HardwareName) || !string.IsNullOrWhiteSpace(state.RoleName))
            return (MeshtasticApplicationPayloadParser.GetHardwareName(state.HardwareName), state.RoleName ?? "-");

        string identity = state.Identity;
        int separator = identity.IndexOf(" / ", StringComparison.Ordinal);
        return separator > 0
            ? (identity[..separator], identity[(separator + 3)..])
            : ("-", "-");
    }

    private static int ParseRadioSlot(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return 0;
        int marker = mode.IndexOf(" slot ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return 0;
        marker += 6;
        int end = marker;
        while (end < mode.Length && char.IsDigit(mode[end])) end++;
        return int.TryParse(mode[marker..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)
            ? slot
            : 0;
    }

    private static string ParseRegionName(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return "";
        int marker = mode.IndexOf(" slot ", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? mode[..marker].Trim() : "";
    }
}

/// <summary>Owns all Meshtastic-specific presentation state and receiver event handling.</summary>
public partial class MeshtasticViewModel : ObservableObject, IFrequencyOverlayProvider
{
}
