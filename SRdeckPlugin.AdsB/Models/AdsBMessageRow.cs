namespace SRdeckPlugin.AdsB.Models;

public sealed record AdsBMessageRow
{
    public DateTimeOffset ReceivedAt { get; init; }
    public string Icao { get; init; } = string.Empty;
    public string Callsign { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public int? AltitudeFeet { get; init; }
    public double? SpeedKnots { get; init; }
}
