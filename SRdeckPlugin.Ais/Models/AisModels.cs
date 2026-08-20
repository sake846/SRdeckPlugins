namespace SRdeckPlugin.Ais.Models;

public sealed record AisFrame(
    byte[] Payload,
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    long SamplePosition,
    string Channel,
    long FrequencyHz,
    double SignalQuality);

public sealed record AisMessage(
    int MessageType,
    int RepeatIndicator,
    uint Mmsi,
    string Kind,
    string Summary,
    double? Latitude = null,
    double? Longitude = null,
    double? SpeedOverGroundKnots = null,
    double? CourseOverGroundDegrees = null,
    int? TrueHeadingDegrees = null,
    double? RateOfTurnDegreesPerMinute = null,
    int? NavigationStatus = null,
    string VesselName = "",
    string CallSign = "",
    int? ImoNumber = null,
    int? ShipType = null,
    string Destination = "",
    double? DraughtMetres = null,
    int? DimensionToBowMetres = null,
    int? DimensionToSternMetres = null,
    int? DimensionToPortMetres = null,
    int? DimensionToStarboardMetres = null,
    bool PositionAccurate = false,
    int? UtcSecond = null,
    string AidType = "");

public sealed class AisTargetState(uint mmsi)
{
    public uint Mmsi { get; } = mmsi;
    public string VesselName { get; set; } = string.Empty;
    public string CallSign { get; set; } = string.Empty;
    public int? ImoNumber { get; set; }
    public int? ShipType { get; set; }
    public string Destination { get; set; } = string.Empty;
    public double? DraughtMetres { get; set; }
    public int? DimensionToBowMetres { get; set; }
    public int? DimensionToSternMetres { get; set; }
    public int? DimensionToPortMetres { get; set; }
    public int? DimensionToStarboardMetres { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? SpeedOverGroundKnots { get; set; }
    public double? CourseOverGroundDegrees { get; set; }
    public int? TrueHeadingDegrees { get; set; }
    public double? RateOfTurnDegreesPerMinute { get; set; }
    public int? NavigationStatus { get; set; }
    public bool PositionAccurate { get; set; }
    public string AidType { get; set; } = string.Empty;
    public bool IsBaseStation { get; set; }
    public string LastChannel { get; set; } = string.Empty;
    public DateTimeOffset LastSeen { get; set; }
    public long MessageCount { get; set; }

    public void Apply(AisMessage message, AisFrame frame)
    {
        if (!string.IsNullOrWhiteSpace(message.VesselName)) VesselName = message.VesselName;
        if (!string.IsNullOrWhiteSpace(message.CallSign)) CallSign = message.CallSign;
        if (message.ImoNumber is not null) ImoNumber = message.ImoNumber;
        if (message.ShipType is not null) ShipType = message.ShipType;
        if (!string.IsNullOrWhiteSpace(message.Destination)) Destination = message.Destination;
        if (message.DraughtMetres is not null) DraughtMetres = message.DraughtMetres;
        if (message.DimensionToBowMetres is not null) DimensionToBowMetres = message.DimensionToBowMetres;
        if (message.DimensionToSternMetres is not null) DimensionToSternMetres = message.DimensionToSternMetres;
        if (message.DimensionToPortMetres is not null) DimensionToPortMetres = message.DimensionToPortMetres;
        if (message.DimensionToStarboardMetres is not null) DimensionToStarboardMetres = message.DimensionToStarboardMetres;
        if (message.Latitude is not null) Latitude = message.Latitude;
        if (message.Longitude is not null) Longitude = message.Longitude;
        if (message.SpeedOverGroundKnots is not null) SpeedOverGroundKnots = message.SpeedOverGroundKnots;
        if (message.CourseOverGroundDegrees is not null) CourseOverGroundDegrees = message.CourseOverGroundDegrees;
        if (message.TrueHeadingDegrees is not null) TrueHeadingDegrees = message.TrueHeadingDegrees;
        if (message.RateOfTurnDegreesPerMinute is not null) RateOfTurnDegreesPerMinute = message.RateOfTurnDegreesPerMinute;
        if (message.NavigationStatus is not null) NavigationStatus = message.NavigationStatus;
        if (!string.IsNullOrWhiteSpace(message.AidType)) AidType = message.AidType;
        if (message.MessageType == 4) IsBaseStation = true;
        if (message.Latitude is not null || message.Longitude is not null)
            PositionAccurate = message.PositionAccurate;
        LastChannel = frame.Channel;
        LastSeen = frame.ReceivedAt;
        MessageCount++;
    }
}

public sealed record AisMessageRow(
    DateTimeOffset ReceivedAt,
    string Channel,
    uint Mmsi,
    int MessageType,
    string Kind,
    string Name,
    string Summary,
    double? Latitude,
    double? Longitude,
    double? SpeedKnots,
    double? CourseDegrees);
