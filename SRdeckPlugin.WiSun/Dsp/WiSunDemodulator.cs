using SRdeckPlugin.Contracts;
using SRdeckPlugin.WiSun.Models;

namespace SRdeckPlugin.WiSun.Dsp;

/// <summary>
/// Streaming IEEE 802.15.4 SUN-FSK receiver for JP FAN mode #1b and HAN/B Route.
/// The input must be a host-channelized complex baseband stream with 8 samples/bit.
/// </summary>
public sealed partial class WiSunDemodulator
{
    public const int FanWorkingSampleRateHz = 400_000;
    public const int HanWorkingSampleRateHz = 800_000;
    public const int FanBitRate = 50_000;
    public const int HanBitRate = 100_000;
    public const int SamplesPerBit = 8;
    public const int FanFrequencyDeviationHz = 25_000;
    public const int HanFrequencyDeviationHz = 50_000;
    public const int ChannelSpacingHz = 200_000;
    public const int FanChannelBandwidthHz = 120_000;
    public const int HanChannelBandwidthHz = 240_000;

    private const int MaximumCapturedPreambleBytes = 8;
    private const int RequiredPreambleBytes = 2;
    private const int RequiredPreambleBits = RequiredPreambleBytes * 8;
    private const int SfdBitCount = 16;
    private const int PhrBitCount = 16;
    private static readonly ushort[] UncodedSfds = [0x904E, 0x7A0E];
    private const int MaximumPsduLength = 2_047;
    private const int Pn9Seed = 0x1FF;
    private const double MinimumTrackedSamplesPerBit = 7.5;
    private const double MaximumTrackedSamplesPerBit = 8.5;
    private const double TimingPhaseGain = 0.35;
    private const double TimingRateGain = 0.015;
    public long TotalSyncAttempts { get; private set; }
    public long TotalRfBursts { get; private set; }
    public long TotalPreambleMatches { get; private set; }
    public long TotalSfdMatches { get; private set; }
    public long TotalPhrValid { get; private set; }
    public long TotalPayloadRead { get; private set; }
    public long TotalFramesPublished { get; private set; }
    public long TotalCrcOk { get; private set; }
    public long TotalCrcNg { get; private set; }
    public uint LastPreambleWord { get; private set; }
    public string LastPreambleRawHex { get; private set; } = string.Empty;
    public int LastPreambleByteCount { get; private set; }
    public ushort LastSfdWord { get; private set; }
    public double LastRecoveredSamplesPerBit { get; private set; } = SamplesPerBit;
    public double LastClockErrorPpm =>
        (LastRecoveredSamplesPerBit / SamplesPerBit - 1) * 1_000_000;
    public double LastInputLevelDbfs { get; private set; } = double.NaN;
    public double LastNoiseFloorDbfs { get; private set; } = double.NaN;
    public int LastInputSampleRateHz { get; private set; }
    public int LastSourceInputSampleRateHz { get; private set; }
    public double LastIntermediateSampleRateHz { get; private set; }
    public bool UsesHostChannelRateConversion { get; private set; }
    public long LastFrequencyHz { get; private set; }
    public DateTimeOffset? LastMeasuredAt { get; private set; }
    public bool RejectInvalidFcs { get; set; } = true;
    public ushort? CustomSfd { get; set; }
    public bool EnableRawBurstLog { get; set; }

    private readonly List<float> discriminator = new(HanWorkingSampleRateHz / 2);
    private readonly List<float> power = new(HanWorkingSampleRateHz / 2);
    private readonly List<double> discriminatorPrefix = new(HanWorkingSampleRateHz / 2) { 0 };
    private readonly List<double> powerPrefix = new(HanWorkingSampleRateHz / 2) { 0 };
    private Complex32 previousSample;
    private bool hasPreviousSample;
    private int scanSample;
    private long bufferSampleOffset;
    private long lastReportedSfdSample = long.MinValue;
    private bool inRfBurst;
    private int rfBurstSamples;
    private int rfBurstGapSamples;
    private int rfBurstStartSample = -1;
    private double noiseFloorPower;
    private long noiseFloorSamples;

    public event Action<WiSunPacketFrame>? OnPacketDecoded;
    public event Action<string>? OnDiagnosticLog;
    public event Action? OnDiagnosticCountersChanged;

    public void ResetCounters()
    {
        TotalSyncAttempts = 0;
        TotalRfBursts = 0;
        TotalPreambleMatches = 0;
        TotalSfdMatches = 0;
        TotalPhrValid = 0;
        TotalPayloadRead = 0;
        TotalFramesPublished = 0;
        TotalCrcOk = 0;
        TotalCrcNg = 0;
        LastPreambleWord = 0;
        LastPreambleRawHex = string.Empty;
        LastPreambleByteCount = 0;
        LastSfdWord = 0;
        LastRecoveredSamplesPerBit = SamplesPerBit;
        LastInputLevelDbfs = double.NaN;
        LastNoiseFloorDbfs = double.NaN;
        noiseFloorPower = 0;
        noiseFloorSamples = 0;
        LastInputSampleRateHz = 0;
        LastSourceInputSampleRateHz = 0;
        LastIntermediateSampleRateHz = 0;
        UsesHostChannelRateConversion = false;
        LastFrequencyHz = 0;
        LastMeasuredAt = null;
    }

    public static int WorkingSampleRateHz(WiSunPhyProfile profile, int customBitRateBps = 50_000) => profile switch
    {
        WiSunPhyProfile.FanMode1b => 400_000,
        WiSunPhyProfile.FanMode2 => 800_000,
        WiSunPhyProfile.FanMode3 => 1_200_000,
        WiSunPhyProfile.FanMode4 => 1_600_000,
        WiSunPhyProfile.FanMode5 => 2_400_000,
        WiSunPhyProfile.HanBRoute => 800_000,
        WiSunPhyProfile.Custom => Math.Clamp(customBitRateBps, 50_000, 300_000) * 8,
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public static int ChannelBandwidthHz(WiSunPhyProfile profile, int customBitRateBps = 50_000) => profile switch
    {
        WiSunPhyProfile.FanMode1b => 120_000,
        WiSunPhyProfile.FanMode2 => 240_000,
        WiSunPhyProfile.FanMode3 => 360_000,
        WiSunPhyProfile.FanMode4 => 480_000,
        WiSunPhyProfile.FanMode5 => 720_000,
        WiSunPhyProfile.HanBRoute => 240_000,
        WiSunPhyProfile.Custom => (int)(Math.Clamp(customBitRateBps, 50_000, 300_000) * 2.4),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public void Reset()
    {
        discriminator.Clear();
        power.Clear();
        discriminatorPrefix.Clear();
        discriminatorPrefix.Add(0);
        powerPrefix.Clear();
        powerPrefix.Add(0);
        previousSample = default;
        hasPreviousSample = false;
        scanSample = 0;
        bufferSampleOffset = 0;
        lastReportedSfdSample = long.MinValue;
        inRfBurst = false;
        rfBurstSamples = 0;
        rfBurstGapSamples = 0;
        rfBurstStartSample = -1;
        noiseFloorPower = 0;
        noiseFloorSamples = 0;
        LastNoiseFloorDbfs = double.NaN;
    }

    /// <summary>
    /// Clears all internal sample buffers and releases their backing arrays
    /// so the garbage collector can reclaim the memory without a large Gen 2
    /// collection on process exit.
    /// </summary>
    public void ReleaseBuffers()
    {
        Reset();
        discriminator.TrimExcess();
        power.TrimExcess();
        discriminatorPrefix.TrimExcess();
        powerPrefix.TrimExcess();
    }

    private static bool IsValidSampleRate(int sampleRateHz) =>
        sampleRateHz is 400_000 or 800_000 or 1_200_000 or 1_600_000 or 2_400_000;

}
