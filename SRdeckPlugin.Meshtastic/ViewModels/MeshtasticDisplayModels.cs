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

