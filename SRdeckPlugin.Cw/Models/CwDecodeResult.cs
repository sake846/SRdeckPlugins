namespace SRdeckPlugin.Cw.Models;

public sealed record CwDecodeResult(
    long TrackId,
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    long FrequencyHz,
    double FrequencyOffsetHz,
    double SignalToNoiseDb,
    int SpeedWpm,
    string AppendedText,
    string Text,
    bool IsFinal = false,
    bool IsSentenceBoundary = false,
    CwLanguage Language = CwLanguage.Western,
    double Confidence = 1);
