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

public sealed partial class MeshtasticNodeDisplayItem
{
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
        OnPropertyChanged(nameof(LastSeenDateTimeSingleLineText));
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
        OnPropertyChanged(nameof(LastSeenDateTimeSingleLineText));
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
