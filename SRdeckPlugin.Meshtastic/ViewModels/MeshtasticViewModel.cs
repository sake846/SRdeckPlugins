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
    public uint PacketId { get; init; }
    public bool IsDecoded { get; init; }
    public string ModemPresetName { get; init; } = "-";
    public int RadioSlot { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public float? PreambleMarginDb { get; init; }
    public bool? PayloadCrcValid { get; init; }
    public int HopLimit { get; init; }
    public int HopStart { get; init; }
    public bool? WasRelayed { get; init; }
    public byte RelayNode { get; init; }

    public string PacketIdText => $"0x{PacketId:X8}";
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
                return HopStart > 0 ? $"中継 0x{RelayNode:X2} (Hop {HopLimit}/{HopStart})" : $"中継 0x{RelayNode:X2}";
            }
            return HopStart > 0 ? $"{Transmission} (Hop {HopLimit}/{HopStart})" : Transmission;
        }
    }

    public string SlotAndPresetText
    {
        get
        {
            string preset = !string.IsNullOrEmpty(ModemPresetName) && ModemPresetName != "-" ? ModemPresetName : "";
            string slot = RadioSlot > 0 ? $"Slot {RadioSlot}" : "";
            if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(preset)) return $"{slot} ({preset})";
            if (!string.IsNullOrEmpty(slot)) return slot;
            if (!string.IsNullOrEmpty(preset)) return preset;
            return "Slot -";
        }
    }

    public string SignalQualityText
    {
        get
        {
            if (PreambleMarginDb.HasValue)
            {
                return $"S/N {PreambleMarginDb.Value:+0.0;-0.0;0.0} dB";
            }
            if (!string.IsNullOrEmpty(Quality) && Quality != "-")
            {
                return Quality;
            }
            return "S/N -";
        }
    }
}

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
    string? LastReceivedContent = null, string? LongName = null, string? ShortName = null);

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
            true => $"中継 0x{reception.Packet.RelayNode:X2}",
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
    [ObservableProperty] private string _signalQuality = "Q - / CRC - / Corr 0";
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
    private DateTimeOffset? _firstSeenAt;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityStatus))]
    [NotifyPropertyChangedFor(nameof(LastSeenTime))]
    [NotifyPropertyChangedFor(nameof(LastSeenDateTimeText))]
    private DateTimeOffset? _lastSeenAt;

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
    public int PacketCount => _packetCount;
    public int ReceptionCount => _receptionCount;
    public int DirectReceptionCount => _directReceptionCount;
    public int RelayedReceptionCount => _relayedReceptionCount;
    public int UnknownRouteReceptionCount => _unknownRouteReceptionCount;
    public string RouteCounts => $"直接 {_directReceptionCount} / 中継 {_relayedReceptionCount} / 不明 {_unknownRouteReceptionCount}";
    private MeshtasticNodeInfo? _nodeInfo;
    private MeshtasticPosition? _latestPosition;
    private MeshtasticTelemetry? _latestTelemetry;
    private readonly Dictionary<uint, bool> _preferredDirectReceptionByPacket = new();

    private bool ShouldUpdateLatestReception(MeshtasticRadioPacket packet)
    {
        bool isDirect = packet.WasRelayed == false;
        if (!_preferredDirectReceptionByPacket.TryGetValue(packet.PacketId, out bool preferredIsDirect))
        {
            _preferredDirectReceptionByPacket[packet.PacketId] = isDirect;
            return true;
        }

        if (preferredIsDirect && !isDirect)
            return false;

        if (isDirect && !preferredIsDirect)
            _preferredDirectReceptionByPacket[packet.PacketId] = true;

        return true;
    }

    public void Update(MeshtasticDataReception reception, string? receivedContent = null)
    {
        _receptionCount++;
        if (!reception.IsDuplicate) _packetCount++;
        CountRoute(reception.Packet);

        RecordSeenAt(reception.Packet.ReceivedAt);
        bool updateLatestReception = ShouldUpdateLatestReception(reception.Packet);
        Counts = $"Packet {_packetCount} / Reception {_receptionCount}";
        if (!updateLatestReception)
        {
            Details = BuildDetails();
            return;
        }

        Route = $"{MeshtasticRouteDisplay.Format(reception)} / Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}";
        Mode = reception.Radio.Summary;
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
                Identity = $"{nodeInfo.HardwareName} / {nodeInfo.RoleName}";
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

    public void Observe(MeshtasticPacketReception reception, string? receivedContent = null)
    {
        _receptionCount++;
        if (!reception.IsDuplicate) _packetCount++;
        CountRoute(reception.Packet);
        RecordSeenAt(reception.Packet.ReceivedAt);
        bool updateLatestReception = ShouldUpdateLatestReception(reception.Packet);
        Counts = $"Packet {_packetCount} / Reception {_receptionCount}";
        if (!updateLatestReception)
        {
            Details = BuildDetails();
            return;
        }

        Route = $"{FormatPacketRoute(reception.Packet)} / Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}";
        Mode = reception.Radio.Summary;
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
        true => $"中継 0x{packet.RelayNode:X2}",
        null => "経路不明"
    };

    private void CountRoute(MeshtasticRadioPacket packet)
    {
        switch (packet.WasRelayed)
        {
            case false: _directReceptionCount++; break;
            case true: _relayedReceptionCount++; break;
            default: _unknownRouteReceptionCount++; break;
        }
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
        LastSeen = state.LastSeen;
        Route = state.Route;
        Mode = state.Mode ?? "-";
        Position = state.Position;
        Telemetry = state.Telemetry;
        LastMessage = state.LastMessage;
        LastReceivedContent = state.LastReceivedContent ?? "";
        SignalQuality = state.SignalQuality;
        Details = state.Details;
        Latitude = state.Latitude;
        Longitude = state.Longitude;
        AltitudeMeters = state.AltitudeMeters;
        _packetCount = state.PacketCount;
        _receptionCount = state.ReceptionCount;
        _directReceptionCount = state.DirectReceptionCount;
        _relayedReceptionCount = state.RelayedReceptionCount;
        _unknownRouteReceptionCount = state.UnknownRouteReceptionCount;
        FirstSeenAt = state.FirstSeenAt;
        LastSeenAt = state.LastSeenAt;
        Counts = $"Packet {_packetCount} / Reception {_receptionCount}";
        OnPropertyChanged(nameof(RouteCounts));
        OnPropertyChanged(nameof(HasPosition));
        OnPropertyChanged(nameof(Coordinates));
        OnPropertyChanged(nameof(LastSummaryText));
        OnPropertyChanged(nameof(LastSeenTime));
        OnPropertyChanged(nameof(LastSeenDateTimeText));
    }
}

/// <summary>Owns all Meshtastic-specific presentation state and receiver event handling.</summary>
public partial class MeshtasticViewModel : ObservableObject, IFrequencyOverlayProvider
{
}
