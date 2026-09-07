using System.Numerics;

namespace SRdeckPlugin.MaritimeVhf.Protocols;

/// <summary>ITU-R M.493 ten-bit character codec.</summary>
internal static class DscSymbolCodec
{
    public const int BitsPerSymbol = 10;

    public static bool TryDecode(ReadOnlySpan<bool> bits, out byte symbol)
    {
        symbol = 0;
        if (bits.Length < BitsPerSymbol) return false;
        for (int bit = 0; bit < 7; bit++)
            if (bits[bit]) symbol |= (byte)(1 << bit);

        int receivedZeroCount = (bits[7] ? 4 : 0) |
            (bits[8] ? 2 : 0) | (bits[9] ? 1 : 0);
        return receivedZeroCount == 7 - BitOperations.PopCount((uint)symbol);
    }

    public static bool[] Encode(byte symbol)
    {
        if (symbol > 127) throw new ArgumentOutOfRangeException(nameof(symbol));
        var bits = new bool[BitsPerSymbol];
        for (int bit = 0; bit < 7; bit++) bits[bit] = (symbol & (1 << bit)) != 0;
        int zeroCount = 7 - BitOperations.PopCount((uint)symbol);
        bits[7] = (zeroCount & 4) != 0;
        bits[8] = (zeroCount & 2) != 0;
        bits[9] = (zeroCount & 1) != 0;
        return bits;
    }
}
