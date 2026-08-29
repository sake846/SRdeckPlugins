namespace SRdeckPlugin.Hfdl.Dsp;

public readonly record struct HfdlPhysicalMode(int DataRate, bool LongInterleaver, int M1Shift)
{
    public int FrameCount => LongInterleaver ? 168 : 72;
    public int InterleaverColumns => (DataRate, LongInterleaver) switch
    {
        (1800, false) => 162, (1800, true) => 378,
        (1200, false) => 108, (1200, true) => 252,
        (600, false) => 54, (600, true) => 126,
        (300, false) => 54, (300, true) => 126,
        _ => throw new ArgumentOutOfRangeException(nameof(DataRate))
    };
    public int ModulationOrder => DataRate switch { 1800 => 8, 1200 => 4, _ => 2 };
    public int UserBitCount => DataRate * (LongInterleaver ? 42 : 18) / 10;

    public static IReadOnlyList<HfdlPhysicalMode> All { get; } =
    [
        new(300, false, 72), new(600, false, 82), new(1200, false, 113), new(1800, false, 123),
        new(300, true, 61), new(600, true, 103), new(1200, true, 93), new(1800, true, 9)
    ];
}

/// <summary>ARINC 635/ICAO HFDL physical-layer constants and shared sequences.</summary>
public static partial class HfdlPhysicalLayer
{
    public const int SymbolRate = 1_800;
    public const int PrekeySymbols = 448;
    public const int PreambleSymbols = 531;
    public const int DataSymbolsPerFrame = 30;
    public const int ProbeSymbolsPerFrame = 15;
    public const int SymbolsPerFrame = 45;

    private const string AText =
        "010 1101 1101 1110 0011 1010 0010 1011 1000 0001 1110 1100 1100 0100 1001 1100 " +
        "1111 1001 0000 0100 0110 1010 1001 1011 0100 1010 0001 0110 0001 1001 0111 1111";
    private const string M1Text =
        "011 1011 0111 1010 0010 1100 1011 1110 0010 0000 0110 0110 1100 0111 0011 1010 " +
        "1110 0001 0011 0000 0101 0101 1010 0100 1010 0111 1001 0001 1010 1000 0111 1111";
    private const string ScramblerHex = "131BC4250F8C15EFCD6AEC996E2368";
    private static readonly bool[] ASequence = ParseBits(AText);
    private static readonly bool[] M1Sequence = ParseBits(M1Text);
    private static readonly bool[] TSequence = ParseBits(
        string.Concat(Enumerable.Repeat("000100110101111", 9)));
    private static readonly bool[] Scrambler = ParseHexBits(ScramblerHex);
    private static readonly bool[][] ShiftedM1Sequences = CreateShiftedM1Sequences();

    public static ReadOnlySpan<bool> A => ASequence;
    public static ReadOnlySpan<bool> M1 => M1Sequence;
    public static ReadOnlySpan<bool> T => TSequence;

    /// <summary>Returns a cached, read-only cyclic shift of the M1 sequence.</summary>
    public static ReadOnlySpan<bool> ShiftedM1(int shift) =>
        ShiftedM1Sequences[Mod(shift, M1Sequence.Length)];

    private static bool[] ParseBits(string value) => value
        .Where(character => character is '0' or '1')
        .Select(character => character == '1').ToArray();

    private static bool[] ParseHexBits(string value) => value.SelectMany(character =>
        Enumerable.Range(0, 4).Select(bit =>
            ((Convert.ToInt32(character.ToString(), 16) >> (3 - bit)) & 1) != 0)).ToArray();

    private static bool[][] CreateShiftedM1Sequences()
    {
        var sequences = new bool[M1Sequence.Length][];
        for (int shift = 0; shift < sequences.Length; shift++)
        {
            sequences[shift] = new bool[M1Sequence.Length];
            for (int index = 0; index < M1Sequence.Length; index++)
                sequences[shift][index] = M1Sequence[(index + shift) % M1Sequence.Length];
        }
        return sequences;
    }

    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
}
