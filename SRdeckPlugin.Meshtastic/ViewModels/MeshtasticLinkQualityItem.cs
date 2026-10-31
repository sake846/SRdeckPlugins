using System.Globalization;

namespace SRdeckPlugin.Meshtastic.ViewModels;

public sealed record MeshtasticLinkQualityItem(
    string Sender,
    string SenderName,
    string RadioCondition,
    int ReceptionCount,
    int DirectReceptionCount,
    int RelayedReceptionCount,
    int UnknownRouteReceptionCount,
    DateTimeOffset? FirstReceivedAt,
    DateTimeOffset? LastReceivedAt,
    MeshtasticSignalStatistics DirectSnr,
    MeshtasticSignalStatistics DirectRssiDbm,
    MeshtasticSignalStatistics DirectRssiDbfs)
{
    public string ReceptionSummary => $"受信 {ReceptionCount}\n直接 {DirectReceptionCount} / 中継 {RelayedReceptionCount}\n経路不明 {UnknownRouteReceptionCount}";
    public string SnrSummary => DirectSnr.Format("dB");
    public string RssiDbmSummary => DirectRssiDbm.Format("dBm");
    public string RssiDbfsSummary => DirectRssiDbfs.Format("dBFS");
    public string LastReceivedText => LastReceivedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "日時不明";
}

public sealed record MeshtasticSignalStatistics(int SampleCount, double? Average, float? Minimum, float? Maximum)
{
    internal static MeshtasticSignalStatistics From(IEnumerable<float?> values)
    {
        float[] samples = values.OfType<float>().Where(float.IsFinite).ToArray();
        return samples.Length == 0 ? new(0, null, null, null)
            : new(samples.Length, samples.Average(value => (double)value), samples.Min(), samples.Max());
    }

    public string Format(string unit) => SampleCount == 0 ? $"{unit} —（未計測）"
        : $"平均 {Average:F1} {unit}（n={SampleCount}）\n最小 {Minimum:F1} / 最大 {Maximum:F1}";
}
