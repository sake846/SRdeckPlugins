using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal static class IsdbTParameters
{
    public const int OutputSampleRateHz = 1_015_873;
    public const int OccupiedBandwidthHz = 428_572;
    public const int ChannelBandwidthHz = 500_000;
    public const int SymbolsPerFrame = 204;

    public static int FftSize(IsdbTMode mode) => mode switch
    {
        IsdbTMode.Mode1 => 256,
        IsdbTMode.Mode2 => 512,
        IsdbTMode.Mode3 => 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static int GuardSamples(IsdbTMode mode, IsdbTGuardInterval guard) =>
        FftSize(mode) / (int)guard;

    public static int ActiveCarriers(IsdbTMode mode) => 108 << ((int)mode - 1);
    public static int DataCarriers(IsdbTMode mode) => 96 << ((int)mode - 1);
    public static int ModeMultiplier(IsdbTMode mode) => 1 << ((int)mode - 1);

    public static ReadOnlySpan<int> TmccCarriers(IsdbTMode mode) => mode switch
    {
        IsdbTMode.Mode1 => [49],
        IsdbTMode.Mode2 => [23, 178],
        IsdbTMode.Mode3 => [101, 131, 286, 349],
        _ => []
    };

    public static ReadOnlySpan<int> AcCarriers(IsdbTMode mode) => mode switch
    {
        IsdbTMode.Mode1 => [35, 79],
        IsdbTMode.Mode2 => [98, 101, 118, 136],
        IsdbTMode.Mode3 => [7, 89, 206, 209, 226, 244, 377, 407],
        _ => []
    };

    public static int BitsPerCarrier(IsdbTModulation modulation) => modulation switch
    {
        IsdbTModulation.Dqpsk or IsdbTModulation.Qpsk => 2,
        IsdbTModulation.Qam16 => 4,
        IsdbTModulation.Qam64 => 6,
        _ => 0
    };

    public static int TimeInterleavingLength(IsdbTMode mode, int tmccCode) => tmccCode switch
    {
        0 => 0,
        1 => mode switch { IsdbTMode.Mode1 => 4, IsdbTMode.Mode2 => 2, _ => 1 },
        2 => mode switch { IsdbTMode.Mode1 => 8, IsdbTMode.Mode2 => 4, _ => 2 },
        3 => mode switch { IsdbTMode.Mode1 => 16, IsdbTMode.Mode2 => 8, _ => 4 },
        _ => -1
    };
}
