namespace SRdeckPlugin.Hfdl.Models;

public sealed record HfdlCategorySummary(
    string Key,
    int Count,
    DateTimeOffset LastReceivedAt,
    string FlightId,
    string Kind,
    string LatestPayload,
    IReadOnlyList<HfdlReception> History);
