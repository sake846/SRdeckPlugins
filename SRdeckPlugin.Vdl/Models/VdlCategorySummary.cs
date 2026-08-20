namespace SRdeckPlugin.Vdl.Models;

public sealed record VdlCategorySummary(
    string Key,
    int Count,
    DateTimeOffset LastReceivedAt,
    string Callsign,
    string Protocol,
    string FrameType,
    string LatestText,
    IReadOnlyList<VdlDecodedFrame> History);
