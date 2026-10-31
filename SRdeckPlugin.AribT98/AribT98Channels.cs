namespace SRdeckPlugin.AribT98;

public enum AribT98ChannelPlan { Registered, LicensedUhf, LicensedVhf, RelayUplink, RelayDownlink }
public sealed record AribT98ChannelPlanChoice(AribT98ChannelPlan Value, string Label);

public sealed record AribT98Channel(int Number, long FrequencyHz, bool IsAirborne = false,
    AribT98ChannelPlan Plan = AribT98ChannelPlan.Registered)
{
    public bool IsManual => Number == 0;
    public bool IsDataOnly => Plan == AribT98ChannelPlan.LicensedVhf && Number >= 20;
    public bool IsRelay => Plan is AribT98ChannelPlan.RelayUplink or AribT98ChannelPlan.RelayDownlink;
    public string NumberText => IsManual ? "—" : Number.ToString("D2");
    public string Prefix => IsRelay ? "D" : IsAirborne ? "S" : "CH";
    public string Display => IsManual ? "手動選局" : Plan switch
    {
        AribT98ChannelPlan.LicensedUhf => $"免許UHF CH {NumberText}",
        AribT98ChannelPlan.LicensedVhf => $"免許VHF CH {NumberText}",
        AribT98ChannelPlan.RelayUplink => $"中継 D{NumberText} 上り",
        AribT98ChannelPlan.RelayDownlink => $"中継 D{NumberText} 下り",
        _ => $"{Prefix} {NumberText}"
    };
    public string Description => IsManual ? "詳細設定の周波数を使用" : Plan switch
    {
        AribT98ChannelPlan.LicensedUhf => "免許局・UHF",
        AribT98ChannelPlan.LicensedVhf => IsDataOnly ? "免許局・VHF（データ用）" : "免許局・VHF",
        AribT98ChannelPlan.RelayUplink => "免許局・中継上り（端末→中継局）",
        AribT98ChannelPlan.RelayDownlink => "免許局・中継下り（中継局→端末）",
        _ => IsAirborne ? "上空用チャネル・受信専用" : Number == 15 ? "呼出チャネル" : "登録局・地上用"
    };
    public string Label => IsManual ? "手動選局（詳細設定）" : IsAirborne ? $"S {NumberText}　上空用" :
        IsRelay ? $"D {NumberText}" : IsDataOnly ? $"CH {NumberText}　データ用" :
        Plan == AribT98ChannelPlan.Registered && Number == 15 ? "CH 15　呼出" : $"CH {NumberText}";
}

/// <summary>STD-T98 v2.0-E5 Part 3, Tables 3-4/5/6 (PDF pp.177-183). Order is channel number, not frequency.</summary>
public static class AribT98Channels
{
    public static IReadOnlyList<AribT98ChannelPlanChoice> Plans { get; } = Array.AsReadOnly(new AribT98ChannelPlanChoice[]
    {
        new(AribT98ChannelPlan.Registered, "登録局 351 MHz"),
        new(AribT98ChannelPlan.LicensedUhf, "免許局 UHF（75ch）"),
        new(AribT98ChannelPlan.LicensedVhf, "免許局 VHF（28ch）"),
        new(AribT98ChannelPlan.RelayUplink, "免許局 中継・上り（465 MHz）"),
        new(AribT98ChannelPlan.RelayDownlink, "免許局 中継・下り（468 MHz）")
    });
    public static AribT98Channel Manual { get; } = new(0, 0);
    public static IReadOnlyList<AribT98Channel> Registered { get; } = Array.AsReadOnly(
        Enumerable.Range(1, 82).Select(n => new AribT98Channel(n,
            n <= 70 ? 351_200_000L + (n - 1) * 6_250 : 351_031_250L + (n - 71) * 6_250))
        .Concat(Enumerable.Range(1, 15).Select(n => new AribT98Channel(n,
            n <= 5 ? 351_168_750L + (n - 1) * 6_250 : 351_106_250L + (n - 6) * 6_250, true)))
        .ToArray());
    public static IReadOnlyList<AribT98Channel> LicensedUhf { get; } = Array.AsReadOnly(
        Enumerable.Range(1, 75).Select(n => new AribT98Channel(n,
            n <= 65 ? 467_000_000L + (n - 1) * 6_250 : 465_096_875L + (n - 66) * 6_250,
            Plan: AribT98ChannelPlan.LicensedUhf)).ToArray());
    public static IReadOnlyList<AribT98Channel> LicensedVhf { get; } = Array.AsReadOnly(
        Enumerable.Range(1, 28).Select(n => new AribT98Channel(n, 154_443_750L + (n - 1) * 6_250,
            Plan: AribT98ChannelPlan.LicensedVhf)).ToArray());
    public static IReadOnlyList<AribT98Channel> RelayUplink { get; } = Array.AsReadOnly(
        Enumerable.Range(1, 10).Select(n => new AribT98Channel(n, 465_034_375L + (n - 1) * 6_250,
            Plan: AribT98ChannelPlan.RelayUplink)).ToArray());
    public static IReadOnlyList<AribT98Channel> RelayDownlink { get; } = Array.AsReadOnly(
        Enumerable.Range(1, 10).Select(n => new AribT98Channel(n, 468_796_875L + (n - 1) * 6_250,
            Plan: AribT98ChannelPlan.RelayDownlink)).ToArray());
    public static IReadOnlyList<AribT98Channel> All { get; } = Array.AsReadOnly(
        Registered.Concat(LicensedUhf).Concat(LicensedVhf).Concat(RelayUplink).Concat(RelayDownlink).ToArray());
    public static IReadOnlyList<AribT98Channel> Choices { get; } = Array.AsReadOnly(Registered.Append(Manual).ToArray());
    private static readonly IReadOnlyDictionary<AribT98ChannelPlan, IReadOnlyList<AribT98Channel>> ChoicesByPlan =
        Plans.ToDictionary(p => p.Value, p => (IReadOnlyList<AribT98Channel>)Array.AsReadOnly(ForPlan(p.Value).Append(Manual).ToArray()));
    public static IReadOnlyList<AribT98Channel> ForPlan(AribT98ChannelPlan plan) => plan switch
    {
        AribT98ChannelPlan.Registered => Registered,
        AribT98ChannelPlan.LicensedUhf => LicensedUhf,
        AribT98ChannelPlan.LicensedVhf => LicensedVhf,
        AribT98ChannelPlan.RelayUplink => RelayUplink,
        AribT98ChannelPlan.RelayDownlink => RelayDownlink,
        _ => throw new ArgumentOutOfRangeException(nameof(plan))
    };
    public static IReadOnlyList<AribT98Channel> ChoicesFor(AribT98ChannelPlan plan) => ChoicesByPlan[plan];
    public static AribT98Channel Find(long frequencyHz) => All.FirstOrDefault(c => c.FrequencyHz == frequencyHz) ?? Manual;
    public static AribT98Channel FindAt(long frequencyHz)
    {
        AribT98Channel nearest = All.MinBy(c => Math.Abs(c.FrequencyHz - frequencyHz))!;
        return Math.Abs(nearest.FrequencyHz - frequencyHz) < 3_125 ? nearest : Manual;
    }
    public static AribT98Channel Calling => Registered[14];
}
