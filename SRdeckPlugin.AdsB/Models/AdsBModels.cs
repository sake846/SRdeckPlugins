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

public sealed class AircraftState
{
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
    public DateTimeOffset LastSeen { get; set; }
    public long MessageCount { get; set; }
}
