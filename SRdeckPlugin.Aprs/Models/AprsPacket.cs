namespace SRdeckPlugin.Aprs.Models;

public sealed record Ax25Frame(string Source, string Destination, string[] Path, string Information)
{
    public string MonitorText => $"{Source}>{Destination}{(Path.Length == 0 ? "" : "," + string.Join(',', Path))}:{Information}";
}

public sealed record AprsPacket(
    DateTimeOffset ReceivedAt, Guid StreamId, long FrequencyHz,
    string Source, string Destination, string Path, string Kind, string Information,
    double? Latitude = null, double? Longitude = null, string? Symbol = null,
    string? Addressee = null, string? Message = null, string? MessageId = null,
    string? EntityName = null, bool IsAlive = true,
    double? SpeedKnots = null, double? CourseDegrees = null,
    AprsWeather? Weather = null, AprsTelemetry? Telemetry = null)
{
    public string MonitorText => $"{Source}>{Destination}{(Path.Length == 0 ? "" : "," + Path)}:{Information}";
    // Object/item names belong to a reporting station; equal names from different
    // senders must not overwrite one another.
    public string EntityKey => EntityName is null ? Source : $"{Source}/{EntityName}";
    public string Label => EntityName ?? Source;
}

public sealed record AprsWeather(
    double? WindDirectionDegrees = null, double? WindSpeedMetersPerSecond = null,
    double? GustMetersPerSecond = null, double? TemperatureCelsius = null,
    double? RainLastHourMillimeters = null, double? RainLast24HoursMillimeters = null,
    double? RainSinceMidnightMillimeters = null, double? HumidityPercent = null,
    double? PressureHectopascals = null, double? LuminosityWattsPerSquareMeter = null);

// Values are the transmitted channel counts; without EQNS/UNIT definitions they
// must not be presented as physical quantities.
public sealed record AprsTelemetry(int? Sequence, double?[] Analog, string? Digital);
