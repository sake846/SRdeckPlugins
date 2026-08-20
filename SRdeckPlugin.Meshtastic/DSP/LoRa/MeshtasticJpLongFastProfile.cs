// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

public enum MeshtasticModemPreset
{
    LongFast = 0,
    LongModerate = 1,
    LongSlow = 2,
    MediumSlow = 3,
    MediumFast = 4,
    ShortSlow = 5,
    ShortFast = 6,
    AutoSf250 = 100,
    AutoSf125 = 101,
    AutoSf250And125 = 102
}

public enum MeshtasticRegion
{
    JP,
    US,
    EU_433,
    EU_868,
    CN,
    ANZ,
    ANZ_433,
    RU,
    KR,
    TW,
    IN,
    NZ_865,
    TH,
    UA_433,
    UA_868,
    MY_433,
    MY_919,
    SG_923,
    PH_433,
    PH_868,
    PH_915,
    KZ_433,
    KZ_863,
    NP_865,
    BR_902
}

public sealed record MeshtasticRegionProfile(
    MeshtasticRegion Region,
    int StartHz,
    int EndHz)
{
    public string Name => Region.ToString();

    public int GetMaximumChannel(int bandwidthHz) => (EndHz - StartHz) / bandwidthHz;

    public int CalculateChannelFrequencyHz(int channel, int bandwidthHz)
    {
        int maximumChannel = GetMaximumChannel(bandwidthHz);
        if (channel < 1 || channel > maximumChannel)
            throw new ArgumentOutOfRangeException(nameof(channel), channel,
                $"{Name} channel must be between 1 and {maximumChannel} for {bandwidthHz / 1000.0:0.###} kHz bandwidth.");
        return StartHz + (bandwidthHz / 2) + ((channel - 1) * bandwidthHz);
    }
}

public sealed record MeshtasticLoRaProfile(
    MeshtasticModemPreset Preset,
    string Name,
    int BandwidthHz,
    int SpreadingFactor,
    int CodingRateDenominator);

/// <summary>
/// Meshtastic sub-GHz regional and modem parameters. Region values mirror the
/// official firmware RegionInfo definitions (checked 2026-07-11).
/// </summary>
public static class MeshtasticJpLongFastProfile
{
    public const int DefaultChannel = 12;
    public const int Channel = DefaultChannel;
    public const int FrequencyHz = 923_375_000;
    public const int BandwidthHz = 250_000;
    public const int SpreadingFactor = 11;
    public const int CodingRateDenominator = 5;
    public const int PreambleSymbols = 16;
    public const byte SyncWord = 0x2B;
    public const int DecoderSampleRateHz = 500_000;
    public const int RegionStartHz = 920_500_000;
    public const int RegionEndHz = 923_500_000;
    public const int MinimumChannel = 1;
    public const int MaximumChannel = (RegionEndHz - RegionStartHz) / BandwidthHz;

    private static readonly IReadOnlyDictionary<MeshtasticRegion, MeshtasticRegionProfile> Regions =
        new Dictionary<MeshtasticRegion, MeshtasticRegionProfile>
        {
            [MeshtasticRegion.JP] = new(MeshtasticRegion.JP, 920_500_000, 923_500_000),
            [MeshtasticRegion.US] = new(MeshtasticRegion.US, 902_000_000, 928_000_000),
            [MeshtasticRegion.EU_433] = new(MeshtasticRegion.EU_433, 433_000_000, 434_000_000),
            [MeshtasticRegion.EU_868] = new(MeshtasticRegion.EU_868, 869_400_000, 869_650_000),
            [MeshtasticRegion.CN] = new(MeshtasticRegion.CN, 470_000_000, 510_000_000),
            [MeshtasticRegion.ANZ] = new(MeshtasticRegion.ANZ, 915_000_000, 928_000_000),
            [MeshtasticRegion.ANZ_433] = new(MeshtasticRegion.ANZ_433, 433_050_000, 434_790_000),
            [MeshtasticRegion.RU] = new(MeshtasticRegion.RU, 868_700_000, 869_200_000),
            [MeshtasticRegion.KR] = new(MeshtasticRegion.KR, 920_000_000, 923_000_000),
            [MeshtasticRegion.TW] = new(MeshtasticRegion.TW, 920_000_000, 925_000_000),
            [MeshtasticRegion.IN] = new(MeshtasticRegion.IN, 865_000_000, 867_000_000),
            [MeshtasticRegion.NZ_865] = new(MeshtasticRegion.NZ_865, 864_000_000, 868_000_000),
            [MeshtasticRegion.TH] = new(MeshtasticRegion.TH, 920_000_000, 925_000_000),
            [MeshtasticRegion.UA_433] = new(MeshtasticRegion.UA_433, 433_000_000, 434_700_000),
            [MeshtasticRegion.UA_868] = new(MeshtasticRegion.UA_868, 868_000_000, 868_600_000),
            [MeshtasticRegion.MY_433] = new(MeshtasticRegion.MY_433, 433_000_000, 435_000_000),
            [MeshtasticRegion.MY_919] = new(MeshtasticRegion.MY_919, 919_000_000, 924_000_000),
            [MeshtasticRegion.SG_923] = new(MeshtasticRegion.SG_923, 917_000_000, 925_000_000),
            [MeshtasticRegion.PH_433] = new(MeshtasticRegion.PH_433, 433_000_000, 434_700_000),
            [MeshtasticRegion.PH_868] = new(MeshtasticRegion.PH_868, 868_000_000, 869_400_000),
            [MeshtasticRegion.PH_915] = new(MeshtasticRegion.PH_915, 915_000_000, 918_000_000),
            [MeshtasticRegion.KZ_433] = new(MeshtasticRegion.KZ_433, 433_075_000, 434_775_000),
            [MeshtasticRegion.KZ_863] = new(MeshtasticRegion.KZ_863, 863_000_000, 868_000_000),
            [MeshtasticRegion.NP_865] = new(MeshtasticRegion.NP_865, 865_000_000, 868_000_000),
            [MeshtasticRegion.BR_902] = new(MeshtasticRegion.BR_902, 902_000_000, 907_500_000)
        };

    public static MeshtasticRegionProfile GetRegion(MeshtasticRegion region) => Regions[region];

    public static int CalculateChannelFrequencyHz(int channel) =>
        GetRegion(MeshtasticRegion.JP).CalculateChannelFrequencyHz(channel, BandwidthHz);

    public static MeshtasticLoRaProfile GetProfile(MeshtasticModemPreset preset) => preset switch
    {
        MeshtasticModemPreset.AutoSf250And125 => new(preset, "AutoSF-250+125", 250_000, 0, 0),
        MeshtasticModemPreset.AutoSf250 => new(preset, "AutoSF-250", 250_000, 0, 0),
        MeshtasticModemPreset.AutoSf125 => new(preset, "AutoSF-125", 125_000, 0, 0),
        MeshtasticModemPreset.LongModerate => new(preset, "LongModerate", 125_000, 11, 8),
        MeshtasticModemPreset.LongSlow => new(preset, "LongSlow", 125_000, 12, 8),
        MeshtasticModemPreset.MediumSlow => new(preset, "MediumSlow", 250_000, 10, 5),
        MeshtasticModemPreset.MediumFast => new(preset, "MediumFast", 250_000, 9, 5),
        MeshtasticModemPreset.ShortSlow => new(preset, "ShortSlow", 250_000, 8, 5),
        MeshtasticModemPreset.ShortFast => new(preset, "ShortFast", 250_000, 7, 5),
        _ => new(MeshtasticModemPreset.LongFast, "LongFast", BandwidthHz, SpreadingFactor, CodingRateDenominator)
    };

    public static bool IsAutoSf(MeshtasticModemPreset preset) =>
        preset is MeshtasticModemPreset.AutoSf250And125 or MeshtasticModemPreset.AutoSf250 or MeshtasticModemPreset.AutoSf125;

    public static string GetEffectiveChannelName(
        MeshtasticModemPreset selectedPreset,
        MeshtasticLoRaProfile detectedProfile,
        string configuredChannelName) =>
        IsAutoSf(selectedPreset) ? detectedProfile.Name : configuredChannelName;

    public static IReadOnlyList<MeshtasticLoRaProfile> GetChannelProfiles(MeshtasticModemPreset preset) => preset switch
    {
        MeshtasticModemPreset.AutoSf250And125 =>
        [
            GetProfile(MeshtasticModemPreset.AutoSf250),
            GetProfile(MeshtasticModemPreset.AutoSf125)
        ],
        _ => [GetProfile(preset)]
    };

    public static IReadOnlyList<MeshtasticLoRaProfile> GetDetectionProfiles(MeshtasticModemPreset preset)
    {
        if (preset == MeshtasticModemPreset.AutoSf250And125)
        {
            return
            [
                .. GetDetectionProfiles(MeshtasticModemPreset.AutoSf250),
                .. GetDetectionProfiles(MeshtasticModemPreset.AutoSf125)
            ];
        }
        if (preset == MeshtasticModemPreset.AutoSf250)
        {
            return
            [
                GetProfile(MeshtasticModemPreset.ShortFast),
                GetProfile(MeshtasticModemPreset.ShortSlow),
                GetProfile(MeshtasticModemPreset.MediumFast),
                GetProfile(MeshtasticModemPreset.MediumSlow),
                GetProfile(MeshtasticModemPreset.LongFast)
            ];
        }
        if (preset == MeshtasticModemPreset.AutoSf125)
        {
            return
            [
                GetProfile(MeshtasticModemPreset.LongModerate),
                GetProfile(MeshtasticModemPreset.LongSlow)
            ];
        }
        return [GetProfile(preset)];
    }
}
