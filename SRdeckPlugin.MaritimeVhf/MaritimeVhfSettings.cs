using SRdeckPlugin.MaritimeVhf.Models;

namespace SRdeckPlugin.MaritimeVhf;

public sealed record MaritimeVhfSettings(
    string SelectedChannel = "16",
    MaritimeVhfReceiveSide ReceiveSide = MaritimeVhfReceiveSide.Ship,
    bool MonitorAudioEnabled = true,
    int MonitorAudioVolume = 80,
    bool SquelchEnabled = true,
    float SquelchThresholdDbm = -110f,
    bool DscWatchEnabled = true,
    bool AutoFollowEnabled = true,
    string OwnMmsi = "",
    int AutoReturnSeconds = 60,
    int MaximumHistory = 500)
{
    public MaritimeVhfSettings Normalize()
    {
        string selected = MaritimeVhfChannelPlan.Find(SelectedChannel)?.Number ?? "16";
        string mmsi = new((OwnMmsi ?? string.Empty).Where(char.IsAsciiDigit).Take(9).ToArray());
        return this with
        {
            SelectedChannel = selected,
            ReceiveSide = Enum.IsDefined(ReceiveSide) ? ReceiveSide : MaritimeVhfReceiveSide.Ship,
            MonitorAudioVolume = Math.Clamp(MonitorAudioVolume, 0, 100),
            SquelchThresholdDbm = Math.Clamp(SquelchThresholdDbm, -160f, -20f),
            OwnMmsi = mmsi,
            AutoReturnSeconds = Math.Clamp(AutoReturnSeconds, 0, 600),
            MaximumHistory = Math.Clamp(MaximumHistory, 50, 5_000)
        };
    }
}
