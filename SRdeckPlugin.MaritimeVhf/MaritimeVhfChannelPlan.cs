using SRdeckPlugin.MaritimeVhf.Models;

namespace SRdeckPlugin.MaritimeVhf;

/// <summary>
/// Voice-capable assignments from RR Appendix 18. Channel 70 is represented
/// separately by <see cref="DscFrequencyHz"/>; AIS 1/2 are intentionally absent.
/// </summary>
public static class MaritimeVhfChannelPlan
{
    public const long DscFrequencyHz = 156_525_000;
    public const int VoiceBandwidthHz = 16_000;

    public static IReadOnlyList<MaritimeVhfChannel> Channels { get; } = Build();

    public static MaritimeVhfChannel Channel16 { get; } =
        Channels.Single(channel => channel.Number == "16");

    public static MaritimeVhfChannel? Find(int channelNumber) =>
        Channels.FirstOrDefault(channel =>
            int.TryParse(channel.Number, out int value) && value == channelNumber);

    public static MaritimeVhfChannel? Find(string? channelNumber) =>
        Channels.FirstOrDefault(channel =>
            string.Equals(channel.Number, channelNumber, StringComparison.Ordinal));

    private static IReadOnlyList<MaritimeVhfChannel> Build()
    {
        var channels = new List<MaritimeVhfChannel>(55);
        for (int frequencyIndex = 0; frequencyIndex <= 29; frequencyIndex++)
        {
            int number = frequencyIndex % 2 == 0
                ? 60 + frequencyIndex / 2
                : 1 + frequencyIndex / 2;
            if (number == 70) continue;
            long ship = 156_025_000L + frequencyIndex * 25_000L;
            long coast = 160_625_000L + frequencyIndex * 25_000L;
            bool simplex = number is 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or
                15 or 16 or 17 or 67 or 68 or 69 or 71 or 72 or 73 or 74;
            channels.Add(new(number.ToString("00"), ship, simplex ? ship : coast,
                UsageFor(number)));
        }

        for (int frequencyIndex = 30; frequencyIndex <= 57; frequencyIndex++)
        {
            int number = frequencyIndex % 2 == 0
                ? 60 + frequencyIndex / 2
                : 1 + frequencyIndex / 2;
            long ship = 156_025_000L + frequencyIndex * 25_000L;
            long coast = 160_625_000L + frequencyIndex * 25_000L;
            bool simplex = number is 16 or 17 or 75 or 76 or 77 or 87 or 88;
            channels.Add(new(number.ToString("00"), ship, simplex ? ship : coast,
                UsageFor(number), number is 75 or 76));
        }

        return channels.OrderBy(channel => int.Parse(channel.Number)).ToArray();
    }

    private static string UsageFor(int channel) => channel switch
    {
        6 => "船間・安全",
        13 => "航行安全",
        15 or 17 => "船内通信（低出力）",
        16 => "遭難・安全・呼出",
        67 or 73 => "船間・港務",
        68 or 69 or 71 or 72 or 77 => "船間通信",
        75 or 76 => "CH16 保護",
        87 or 88 => "単信音声",
        _ => "港務・船舶通航・公衆通信"
    };
}
