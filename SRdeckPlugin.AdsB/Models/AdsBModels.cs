namespace SRdeckPlugin.AdsB.Models;

public sealed record ModeSFrame(
    byte[] Bytes,
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    long SamplePosition,
    double SignalQuality)
{
    public int DownlinkFormat => Bytes[0] >> 3;
    public string Icao => $"{Bytes[1]:X2}{Bytes[2]:X2}{Bytes[3]:X2}";
}

public sealed record AdsBMessage(
    string Icao,
    int TypeCode,
    string Kind,
    string Summary,
    string? Callsign = null,
    int? AltitudeFeet = null,
    bool? IsGeometricAltitude = null,
    double? GroundSpeedKnots = null,
    double? TrackDegrees = null,
    double? AirspeedKnots = null,
    double? HeadingDegrees = null,
    bool? IsTrueAirspeed = null,
    int? VerticalRateFeetPerMinute = null,
    int? SelectedAltitudeFeet = null,
    double? SelectedHeadingDegrees = null,
    bool? SelectedHeadingIsTrack = null,
    string? EmergencyState = null,
    string? Squawk = null,
    int? AdsBVersion = null,
    int? NacP = null,
    int? Sil = null,
    bool? NicA = null,
    bool? NicBaro = null,
    bool? IsOnGround = null,
    bool IsSurfacePosition = false,
    bool? IsOddCpr = null,
    int? CprLatitude = null,
    int? CprLongitude = null);

public sealed record AdsBPosition(double Latitude, double Longitude, DateTimeOffset ReceivedAt);

public sealed record AdsBMessageRow
{
    public DateTimeOffset ReceivedAt { get; init; }
    public string Icao { get; init; } = string.Empty;
    public string Callsign { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public int? AltitudeFeet { get; init; }
    public double? SpeedKnots { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? TrackDegrees { get; init; }
    public int? VerticalRateFeetPerMinute { get; init; }
    public string Squawk { get; init; } = string.Empty;

    public string AircraftDisplay => string.IsNullOrWhiteSpace(Callsign) ? Icao : Callsign;
    public string AircraftDetail => string.IsNullOrWhiteSpace(Callsign) ? "機名未取得" : Icao;
    public bool HasCallsign => !string.IsNullOrWhiteSpace(Callsign);

    public string KindDisplay => Kind switch
    {
        "airborne-position" => "空中位置",
        "surface-position" => "地上位置",
        "airborne-velocity" => "速度航法",
        "identification" => "機体識別",
        "emergency" => "緊急",
        "operational-status" => "運用状態",
        _ => string.IsNullOrWhiteSpace(Kind) ? "情報" : Kind
    };

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;

    public string PositionDisplay
    {
        get
        {
            if (Latitude.HasValue && Longitude.HasValue)
            {
                char latHemi = Latitude.Value >= 0 ? 'N' : 'S';
                char lonHemi = Longitude.Value >= 0 ? 'E' : 'W';
                return $"{Math.Abs(Latitude.Value):F4}°{latHemi}, {Math.Abs(Longitude.Value):F4}°{lonHemi}";
            }
            return string.Empty;
        }
    }

    public string CleanSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Summary)) return KindDisplay;
            if (!string.IsNullOrEmpty(Icao) && Summary.StartsWith(Icao, StringComparison.OrdinalIgnoreCase))
            {
                string stripped = Summary[Icao.Length..].Trim();
                if (!string.IsNullOrEmpty(stripped)) return stripped;
            }
            return Summary;
        }
    }

    public string PrimaryContentDisplay => HasPosition ? PositionDisplay : CleanSummary;

    public string KinematicsDisplay
    {
        get
        {
            var parts = new List<string>(3);
            if (AltitudeFeet.HasValue)
            {
                string alt = $"{AltitudeFeet.Value:N0} ft";
                if (VerticalRateFeetPerMinute.HasValue && Math.Abs(VerticalRateFeetPerMinute.Value) >= 64)
                {
                    string arrow = VerticalRateFeetPerMinute.Value > 0 ? "↑" : "↓";
                    alt += $" {arrow}{Math.Abs(VerticalRateFeetPerMinute.Value):N0} fpm";
                }
                parts.Add(alt);
            }
            if (SpeedKnots.HasValue)
            {
                string spd = $"{SpeedKnots.Value:F0} kt";
                if (TrackDegrees.HasValue) spd += $" {TrackDegrees.Value:F0}°";
                parts.Add(spd);
            }
            else if (TrackDegrees.HasValue)
            {
                parts.Add($"{TrackDegrees.Value:F0}°");
            }
            return string.Join("  ", parts);
        }
    }

    public string LatitudeDisplay => HasPosition
        ? $"緯度 {Math.Abs(Latitude!.Value):F4}°{(Latitude.Value >= 0 ? 'N' : 'S')}"
        : CleanSummary;
    public string LongitudeDisplay => HasPosition
        ? $"経度 {Math.Abs(Longitude!.Value):F4}°{(Longitude.Value >= 0 ? 'E' : 'W')}"
        : string.Empty;
    public string AltitudeDisplay => AltitudeFeet.HasValue ? $"高度 {AltitudeFeet.Value:N0} ft" : string.Empty;
    public string VerticalRateDisplay => VerticalRateFeetPerMinute.HasValue && Math.Abs(VerticalRateFeetPerMinute.Value) >= 64
        ? $"昇降 {(VerticalRateFeetPerMinute.Value > 0 ? "↑" : "↓")}{Math.Abs(VerticalRateFeetPerMinute.Value):N0} fpm"
        : string.Empty;
    public string SpeedDisplay => SpeedKnots.HasValue ? $"速度 {SpeedKnots.Value:F0} kt" : string.Empty;
    public string TrackDisplay => TrackDegrees.HasValue ? $"針路 {TrackDegrees.Value:F0}°" : string.Empty;

    public string SquawkDisplay => string.IsNullOrWhiteSpace(Squawk) ? string.Empty : $"SQ {Squawk}";
}

public sealed class AircraftState
{
    // ponytail: fields are values or immutable strings; deep-copy any future mutable references.
    internal AircraftState Copy() => (AircraftState)MemberwiseClone();

    public required string Icao { get; init; }
    public string Callsign { get; set; } = string.Empty;
    public int? AltitudeFeet { get; set; }
    public int? BarometricAltitudeFeet { get; set; }
    public int? GeometricAltitudeFeet { get; set; }
    public double? GroundSpeedKnots { get; set; }
    public double? TrackDegrees { get; set; }
    public double? AirspeedKnots { get; set; }
    public double? HeadingDegrees { get; set; }
    public int? VerticalRateFeetPerMinute { get; set; }
    public int? SelectedAltitudeFeet { get; set; }
    public double? SelectedHeadingDegrees { get; set; }
    public bool? SelectedHeadingIsTrack { get; set; }
    public string EmergencyState { get; set; } = string.Empty;
    public string Squawk { get; set; } = string.Empty;
    public int? AdsBVersion { get; set; }
    public int? NacP { get; set; }
    public int? Sil { get; set; }
    public bool? NicA { get; set; }
    public bool? NicBaro { get; set; }
    public bool IsOnGround { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long MessageCount { get; set; }
}
