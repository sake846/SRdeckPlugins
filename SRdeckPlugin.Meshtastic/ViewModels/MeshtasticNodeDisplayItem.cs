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

}
