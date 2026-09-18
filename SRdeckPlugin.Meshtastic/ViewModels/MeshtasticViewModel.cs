using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    private static readonly Regex DetailFieldPattern = new(
        @"(?<![A-Za-z0-9])(?<key>[A-Za-z][A-Za-z0-9]*)=(?<value>[^\s]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> DetailLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["latitude"] = "\u7DEF\u5EA6",
            ["longitude"] = "\u7D4C\u5EA6",
            ["altitudeM"] = "\u9AD8\u5EA6",
            ["altitudeSource"] = "\u9AD8\u5EA6\u60C5\u5831\u6E90",
            ["locationSource"] = "\u4F4D\u7F6E\u60C5\u5831\u6E90",
            ["time"] = "\u6E2C\u4F4D\u6642\u523B",
            ["pdop"] = "PDOP",
            ["hdop"] = "HDOP",
            ["vdop"] = "VDOP",
            ["gpsAccuracyMm"] = "GPS\u7CBE\u5EA6",
            ["groundSpeed"] = "\u5BFE\u5730\u901F\u5EA6",
            ["groundTrackDeg"] = "\u5BFE\u5730\u9032\u884C\u65B9\u5411",
            ["fixQuality"] = "\u6E2C\u4F4D\u54C1\u8CEA",
            ["fixType"] = "\u6E2C\u4F4D\u7A2E\u5225",
            ["satellites"] = "\u885B\u661F\u6570",
            ["precisionBits"] = "\u7CBE\u5EA6\u30D3\u30C3\u30C8\u6570",
            ["id"] = "ID",
            ["longName"] = "\u30CE\u30FC\u30C9\u540D",
            ["shortName"] = "\u77ED\u7E2E\u540D",
            ["mac"] = "MAC",
            ["hardware"] = "\u30CF\u30FC\u30C9\u30A6\u30A7\u30A2",
            ["role"] = "\u5F79\u5272",
            ["licensed"] = "\u514D\u8A31\u5C40",
            ["unmessagable"] = "\u30E1\u30C3\u30BB\u30FC\u30B8\u4E0D\u53EF",
            ["publicKey"] = "\u516C\u958B\u9375",
            ["variant"] = "\u7A2E\u5225",
            ["battery"] = "\u30D0\u30C3\u30C6\u30EA\u30FC",
            ["voltage"] = "\u96FB\u5727",
            ["channelUtilization"] = "\u30C1\u30E3\u30CD\u30EB\u5229\u7528\u7387",
            ["airUtilTx"] = "\u9001\u4FE1\u5229\u7528\u7387",
            ["uptime"] = "\u7A3C\u50CD\u6642\u9593",
            ["temperature"] = "\u6E29\u5EA6",
            ["humidity"] = "\u6E7F\u5EA6",
            ["pressure"] = "\u6C17\u5727",
            ["gasResistance"] = "\u30AC\u30B9\u62B5\u6297",
            ["current"] = "\u96FB\u6D41",
            ["iaq"] = "\u7A7A\u6C17\u8CEA\u6307\u6570",
            ["distance"] = "\u8DDD\u96E2",
            ["lux"] = "\u7167\u5EA6",
            ["whiteLux"] = "\u767D\u8272\u7167\u5EA6",
            ["irLux"] = "\u8D64\u5916\u7167\u5EA6",
            ["uvLux"] = "UV\u7167\u5EA6",
            ["windDirection"] = "\u98A8\u5411",
            ["windSpeed"] = "\u98A8\u901F",
            ["weight"] = "\u91CD\u91CF",
            ["windGust"] = "\u6700\u5927\u77AC\u9593\u98A8\u901F",
            ["windLull"] = "\u6700\u5C0F\u98A8\u901F",
            ["radiation"] = "\u653E\u5C04\u7DDA\u91CF",
            ["rainfall1h"] = "1\u6642\u9593\u96E8\u91CF",
            ["rainfall24h"] = "24\u6642\u9593\u96E8\u91CF",
            ["soilMoisture"] = "\u571F\u58CC\u6C34\u5206",
            ["soilTemperature"] = "\u571F\u58CC\u6E29\u5EA6",
            ["oneWireTemperature"] = "1-Wire\u6E29\u5EA6",
            ["packetsTx"] = "\u9001\u4FE1\u30D1\u30B1\u30C3\u30C8\u6570",
            ["packetsRx"] = "\u53D7\u4FE1\u30D1\u30B1\u30C3\u30C8\u6570",
            ["packetsRxBad"] = "\u30A8\u30E9\u30FC\u30D1\u30B1\u30C3\u30C8\u6570",
            ["onlineNodes"] = "\u30AA\u30F3\u30E9\u30A4\u30F3\u30CE\u30FC\u30C9\u6570",
            ["totalNodes"] = "\u7DCF\u30CE\u30FC\u30C9\u6570",
            ["rxDuplicates"] = "\u91CD\u8907\u53D7\u4FE1\u6570",
            ["txRelay"] = "\u4E2D\u7D99\u9001\u4FE1\u6570",
            ["txRelayCanceled"] = "\u4E2D\u7D99\u53D6\u6D88\u6570",
            ["heapTotal"] = "\u7DCF\u30D2\u30FC\u30D7",
            ["heapFree"] = "\u7A7A\u304D\u30D2\u30FC\u30D7",
            ["txDropped"] = "\u7834\u68C4\u9001\u4FE1\u6570",
            ["noiseFloor"] = "\u30CE\u30A4\u30BA\u30D5\u30ED\u30A2",
            ["notification"] = "\u901A\u77E5",
            ["errorReason"] = "\u30A8\u30E9\u30FC\u7406\u7531",
            ["routeTowards"] = "\u5F80\u8DEF",
            ["routeBack"] = "\u5FA9\u8DEF",
            ["nodeId"] = "\u30CE\u30FC\u30C9ID",
            ["lastSentBy"] = "\u9001\u4FE1\u5143",
            ["interval"] = "\u9001\u4FE1\u9593\u9694",
            ["neighbors"] = "\u30CD\u30A4\u30D0\u30FC",
            ["lastRx"] = "\u53D7\u4FE1\u6642\u523B",
            ["snr"] = "SNR"
        };

    private static string FormatDetailsJapanese(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return "-";
        return DetailFieldPattern.Replace(details, TranslateDetailField);
    }

    private static string TranslateDetailField(Match match)
    {
        string key = match.Groups["key"].Value;
        string value = match.Groups["value"].Value;
        if (key is "altitudeSource" or "locationSource")
        {
            value = value switch
            {
                "MANUAL" => "\u624B\u52D5",
                "INTERNAL" => "\u5185\u8535",
                "EXTERNAL" => "\u5916\u90E8",
                "BAROMETRIC" => "\u6C17\u5727\u8A08",
                "UNSET" => "\u672A\u8A2D\u5B9A",
                _ => value
            };
        }
        else if (key == "licensed")
        {
            value = value switch { "True" => "\u6709", "False" => "\u7121", _ => value };
        }
        else if (key == "unmessagable")
        {
            value = value switch { "True" => "\u306F\u3044", "False" => "\u3044\u3044\u3048", _ => value };
        }
        return DetailLabels.TryGetValue(key, out string? label)
            ? $"{label}={value}"
            : match.Value;
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

    private readonly HashSet<string> _receivedAppTypes = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<MeshtasticApplicationDisplayItem> ApplicationTypes =>
        MeshtasticDataParser.KnownApplicationNames
            .Select(name => new MeshtasticApplicationDisplayItem(name, _receivedAppTypes.Contains(name)))
            .ToArray();
    public int PacketCount { get; private set; }
    public int ReceptionCount { get; private set; }
    public int DirectReceptionCount { get; private set; }
    public int RelayedReceptionCount { get; private set; }
    public int UnknownRouteReceptionCount { get; private set; }
    public string RouteCounts => $"直接 {DirectReceptionCount} / 中継 {RelayedReceptionCount} / 不明 {UnknownRouteReceptionCount}";
    public float? MinimumRssiDbfs { get; private set; }
    public float? MaximumRssiDbfs { get; private set; }
    public float? MinimumRssiDbm { get; private set; }
    public float? MaximumRssiDbm { get; private set; }
    public float? MinimumSnrDb { get; private set; }
    public float? MaximumSnrDb { get; private set; }
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
        ReceptionCount++;
        if (!reception.IsDuplicate) PacketCount++;
        RecordSeenAt(reception.Packet.ReceivedAt);
        UpdateSignalRanges(reception.Quality);
        bool updateLatestReception = TrackPreferredPacketRoute(reception.Packet);
        RecordReceivedAppType(MeshtasticDataParser.GetNodeListApplicationName(reception.Data.PortNumber));
        Counts = $"Packet {PacketCount} / Reception {ReceptionCount}";
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
        ReceptionCount++;
        if (!reception.IsDuplicate) PacketCount++;
        RecordSeenAt(reception.Packet.ReceivedAt);
        UpdateSignalRanges(reception.Quality);
        bool updateLatestReception = TrackPreferredPacketRoute(reception.Packet);
        Counts = $"Packet {PacketCount} / Reception {ReceptionCount}";
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
        (MinimumRssiDbfs, MaximumRssiDbfs) = UpdateRange(MinimumRssiDbfs, MaximumRssiDbfs,
            quality.EstimatedRssiDbfs);
        (MinimumRssiDbm, MaximumRssiDbm) = UpdateRange(MinimumRssiDbm, MaximumRssiDbm,
            quality.EstimatedRssiDbm);
        (MinimumSnrDb, MaximumSnrDb) = UpdateRange(MinimumSnrDb, MaximumSnrDb,
            quality.EstimatedSnrDb);
        OnPropertyChanged(nameof(RssiRangeText));
        OnPropertyChanged(nameof(SnrRangeText));
        OnPropertyChanged(nameof(MinimumRssiDbfs));
        OnPropertyChanged(nameof(MaximumRssiDbfs));
        OnPropertyChanged(nameof(MinimumRssiDbm));
        OnPropertyChanged(nameof(MaximumRssiDbm));
        OnPropertyChanged(nameof(MinimumSnrDb));
        OnPropertyChanged(nameof(MaximumSnrDb));
    }

    private static (float? Minimum, float? Maximum) UpdateRange(float? minimum, float? maximum, float? value)
    {
        if (value is not float number || !float.IsFinite(number)) return (minimum, maximum);
        return (
            minimum is float currentMinimum && currentMinimum < number ? currentMinimum : number,
            maximum is float currentMaximum && currentMaximum > number ? currentMaximum : number);
    }

    private void AdjustPreferredRouteCount(bool? wasRelayed, int amount)
    {
        switch (wasRelayed)
        {
            case false: DirectReceptionCount += amount; break;
            case true: RelayedReceptionCount += amount; break;
            default: UnknownRouteReceptionCount += amount; break;
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
        PacketCount = state.PacketCount;
        ReceptionCount = state.ReceptionCount;
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
            DirectReceptionCount = _preferredReceptionRouteByPacket.Values.Count(route => route == false);
            RelayedReceptionCount = _preferredReceptionRouteByPacket.Values.Count(route => route == true);
            UnknownRouteReceptionCount = _preferredReceptionRouteByPacket.Values.Count(route => route is null);
        }
        else
        {
            DirectReceptionCount = state.DirectReceptionCount;
            RelayedReceptionCount = state.RelayedReceptionCount;
            UnknownRouteReceptionCount = state.UnknownRouteReceptionCount;
        }
        FirstSeenAt = state.FirstSeenAt;
        LastSeenAt = state.LastSeenAt;
        RadioSlot = state.RadioSlot ?? ParseRadioSlot(state.Mode);
        ModemPresetName = string.IsNullOrWhiteSpace(state.ModemPresetName) ? "-" : state.ModemPresetName;
        RegionName = string.IsNullOrWhiteSpace(state.RegionName) ? ParseRegionName(state.Mode) : state.RegionName;
        MinimumRssiDbfs = state.MinimumRssiDbfs;
        MaximumRssiDbfs = state.MaximumRssiDbfs;
        MinimumRssiDbm = state.MinimumRssiDbm;
        MaximumRssiDbm = state.MaximumRssiDbm;
        MinimumSnrDb = state.MinimumSnrDb;
        MaximumSnrDb = state.MaximumSnrDb;
        RestoreReceivedAppTypes(state.ReceivedAppTypes);
        Counts = $"Packet {PacketCount} / Reception {ReceptionCount}";
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
