using System;
using System.Linq;

namespace SRdeckPlugin.WiSun;

public enum WiSunPhyProfile
{
    FanMode1b,
    FanMode2,
    FanMode3,
    FanMode4,
    FanMode5,
    HanBRoute,
    Custom
}

public sealed record WiSunSettings
{
    public long FrequencyHz { get; init; } = 922_400_000;
    public long FrequencyStepHz { get; init; } = 200_000;
    public float SquelchThresholdDbm { get; init; } = -125.0f;
    public float SquelchThresholdDbfs
    {
        get => SquelchThresholdDbm;
        init => SquelchThresholdDbm = value <= -60.0f ? value : (value - 80.0f);
    }
    public bool IsReceiverEnabled { get; init; } = true;
    public WiSunPhyProfile PhyProfile { get; init; } = WiSunPhyProfile.FanMode1b;
    public int[] FanChannels { get; init; } = [9];
    public int[] HanChannels { get; init; } = [4];
    public long CustomFrequencyHz { get; init; } = 922_400_000;
    public int CustomBitRateBps { get; init; } = 50_000;
    public string CustomSfdHex { get; init; } = "904E";
    public bool EnableRawBurstLog { get; init; } = false;

    public WiSunSettings Normalize()
    {
        WiSunPhyProfile profile = Enum.IsDefined(PhyProfile)
            ? PhyProfile
            : WiSunPhyProfile.FanMode1b;
        int fanStep = profile switch
        {
            WiSunPhyProfile.FanMode5 => 4,
            WiSunPhyProfile.FanMode2 or WiSunPhyProfile.FanMode3 or WiSunPhyProfile.FanMode4 => 2,
            _ => 1
        };
        int[] fanChannels = NormalizeChannels(FanChannels, 9, 37, 9, fanStep);
        int[] hanChannels = NormalizeChannels(HanChannels, 4, 17, 4, 1);
        int primaryChannel = profile == WiSunPhyProfile.HanBRoute
            ? hanChannels[0]
            : fanChannels[0];
        long customFrequencyHz = Math.Clamp(CustomFrequencyHz, 100_000_000L, 2_500_000_000L);
        int customBitRateBps = NormalizeBitRate(CustomBitRateBps);
        string customSfdHex = NormalizeSfdHex(CustomSfdHex);

        long stepHz = profile switch
        {
            WiSunPhyProfile.FanMode5 => 800_000L,
            WiSunPhyProfile.HanBRoute or WiSunPhyProfile.FanMode2 or WiSunPhyProfile.FanMode3 or WiSunPhyProfile.FanMode4 => 400_000L,
            WiSunPhyProfile.Custom => 100_000L,
            _ => 200_000L
        };

        long primaryFrequencyHz = profile switch
        {
            WiSunPhyProfile.Custom => customFrequencyHz,
            WiSunPhyProfile.HanBRoute => 922_500_000L + (primaryChannel - 4) * 400_000L,
            _ => 920_600_000L + primaryChannel * 200_000L
        };

        return this with
        {
            FrequencyHz = primaryFrequencyHz,
            FrequencyStepHz = stepHz,
            SquelchThresholdDbm = Math.Clamp(SquelchThresholdDbm, -160.0f, 0.0f),
            PhyProfile = profile,
            FanChannels = fanChannels,
            HanChannels = hanChannels,
            CustomFrequencyHz = customFrequencyHz,
            CustomBitRateBps = customBitRateBps,
            CustomSfdHex = customSfdHex
        };
    }

    public static long StepHzForBitRate(int bitRateBps) => bitRateBps switch
    {
        >= 300_000 => 800_000L,
        >= 100_000 => 400_000L,
        _ => 200_000L
    };

    private static int NormalizeBitRate(int bitRate) => bitRate switch
    {
        100_000 or 150_000 or 200_000 or 300_000 => bitRate,
        _ => 50_000
    };

    private static string NormalizeSfdHex(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            string cleaned = hex.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (ushort.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out _))
            {
                return cleaned.PadLeft(4, '0').ToUpperInvariant();
            }
        }
        return "904E";
    }

    private static int[] NormalizeChannels(
        int[]? channels,
        int minimum,
        int maximum,
        int fallback,
        int step = 1)
    {
        int[] normalized = channels?
            .Where(channel => channel >= minimum && channel <= maximum && (channel - minimum) % step == 0)
            .Distinct()
            .Order()
            .ToArray() ?? [];
        return normalized.Length == 0 ? [fallback] : normalized;
    }
}
