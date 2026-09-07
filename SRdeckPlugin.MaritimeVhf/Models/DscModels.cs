namespace SRdeckPlugin.MaritimeVhf.Models;

public enum DscPriority
{
    Routine,
    Safety,
    Urgency,
    Distress
}

public sealed record DscWorkingChannel(
    int ChannelNumber,
    MaritimeVhfReceiveSide? ForcedSide = null)
{
    public MaritimeVhfChannel? Channel => MaritimeVhfChannelPlan.Find(ChannelNumber);
    public string DisplayName => Channel is null
        ? $"CH {ChannelNumber:00}"
        : $"CH {Channel.Number}" + (ForcedSide switch
        {
            MaritimeVhfReceiveSide.Ship => "（船舶側単信）",
            MaritimeVhfReceiveSide.Coast => "（海岸局側単信）",
            _ => string.Empty
        });
}

public sealed record DscMessage(
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    long SamplePosition,
    byte FormatSpecifier,
    DscPriority Priority,
    string SourceMmsi,
    string DestinationMmsi,
    string Kind,
    string Summary,
    DscWorkingChannel? WorkingChannel,
    int? NatureOfDistress,
    double? Latitude,
    double? Longitude,
    TimeOnly? PositionTimeUtc,
    byte EndOfSequence,
    IReadOnlyList<byte> Symbols)
{
    public bool IsAddressedTo(string ownMmsi) =>
        string.IsNullOrWhiteSpace(ownMmsi) ||
        string.IsNullOrWhiteSpace(DestinationMmsi) ||
        string.Equals(DestinationMmsi, ownMmsi, StringComparison.Ordinal);
}

public sealed record DscMessageRow(
    DateTimeOffset ReceivedAt,
    string Priority,
    string SourceMmsi,
    string DestinationMmsi,
    string WorkingChannel,
    string Summary);
