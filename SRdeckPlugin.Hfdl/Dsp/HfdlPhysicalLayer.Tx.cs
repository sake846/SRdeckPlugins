using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Hfdl.Dsp;

public static partial class HfdlPhysicalLayer
{
    private static Complex32[] MapAndScramble(bool[] chips, int order)
    {
        int perSymbol = order switch { 8 => 3, 4 => 2, _ => 1 };
        var output = new Complex32[chips.Length / perSymbol];
        for (int index = 0; index < output.Length; index++)
        {
            int gray = 0;
            for (int bit = 0; bit < perSymbol; bit++) if (chips[index * perSymbol + bit]) gray |= 1 << bit;
            int sector = GrayToBinary(gray) & (order - 1);
            double phase = 2 * Math.PI * sector / order;
            Complex32 value = new((float)Math.Cos(phase), (float)Math.Sin(phase));
            if (Scrambler[index % Scrambler.Length]) value = new(-value.I, -value.Q);
            output[index] = value;
        }
        return output;
    }

    private static void AppendBpsk(List<Complex32> output, ReadOnlySpan<bool> bits)
    { foreach (bool bit in bits) output.Add(Bpsk(bit)); }

    private static Complex32 Bpsk(bool bit) => new(bit ? -1 : 1, 0);

    private static bool[] BytesToBitsLsbFirst(ReadOnlySpan<byte> bytes, int count)
    {
        var output = new bool[count];
        for (int index = 0; index < Math.Min(count, bytes.Length * 8); index++) output[index] = (bytes[index / 8] & (1 << (index % 8))) != 0;
        return output;
    }

    private static int GrayToBinary(int gray) { int value = gray; for (int shift = 1; shift < 8; shift <<= 1) value ^= value >> shift; return value; }
}
