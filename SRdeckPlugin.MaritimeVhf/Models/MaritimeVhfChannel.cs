namespace SRdeckPlugin.MaritimeVhf.Models;

public enum MaritimeVhfReceiveSide
{
    Ship,
    Coast
}

public sealed record MaritimeVhfChannel(
    string Number,
    long ShipFrequencyHz,
    long CoastFrequencyHz,
    string Usage,
    bool IsGuardChannel = false)
{
    public bool IsSimplex => CoastFrequencyHz == 0 || CoastFrequencyHz == ShipFrequencyHz;

    public long FrequencyFor(MaritimeVhfReceiveSide side) =>
        side == MaritimeVhfReceiveSide.Coast && CoastFrequencyHz > 0
            ? CoastFrequencyHz
            : ShipFrequencyHz;

    public string DuplexText => IsSimplex ? "単信" : "複信";
    public string DisplayName => $"CH {Number}  {ShipFrequencyHz / 1_000_000.0:F3} MHz  {Usage}";
}

public sealed record MaritimeVhfReceiveSideOption(
    MaritimeVhfReceiveSide Value,
    string DisplayName);
