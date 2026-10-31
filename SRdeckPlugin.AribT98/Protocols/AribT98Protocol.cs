using System.Numerics;
using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Protocols;

/// <summary>ARIB STD-T98 v1.1, Part 3, sections 4.1.6-4.1.10. Air bits are MSB first.</summary>
public static class AribT98Protocol
{
    public const int SymbolsPerFrame = 192;
    public const uint SyncWord = 0xCDF59;
    public static ReadOnlySpan<int> SyncSymbols => [-3, 1, -3, 3, -3, -3, 3, 3, -1, 3];

    public static int DibitToSymbol(int dibit) => dibit switch { 0 => 1, 1 => 3, 2 => -1, 3 => -3, _ => throw new ArgumentOutOfRangeException(nameof(dibit)) };

    public static bool TryDecode(ReadOnlySpan<float> symbols, out AribT98Frame? frame,
        bool preserveSoftInformation = true)
    {
        frame = null;
        if (symbols.Length != SymbolsPerFrame) return false;
        Span<float> bits = stackalloc float[364];
        int pn = 0x0e4;
        for (int i = 0; i < 182; i++)
        {
            float symbol = symbols[i + 10];
            if (!float.IsFinite(symbol)) return false;
            if ((pn & 1) != 0) symbol = -symbol;
            pn = (pn >> 1) | (((pn ^ (pn >> 4)) & 1) << 8);
            // Signed max-log metrics: positive favours a one.
            bits[i * 2] = -symbol;
            bits[i * 2 + 1] = Math.Abs(symbol) - 2;
        }
        int rich = 0;
        for (int i = 0; i < 8; i++) rich = (rich << 1) | (bits[2*i] > 0 ? 1 : 0);
        // RICH ignores magnitude by design (figure 4-10); check parity,
        // reserved bits and direct mode before accepting a channel layout.
        if ((BitOperations.PopCount((uint)rich) & 1) != 0 || (rich & 0x62) != 0) return false;
        bool communication = (rich & 0x80) != 0;
        int mode = (rich >> 2) & 7;
        if ((!communication && mode != 4) || (communication && mode > 5)) return false;
        byte[]? control = DecodeProtected(bits.Slice(16, 60), true, out int corrected);
        if (control is null) return false;
        int header = ReadBits(control, 0, 8);
        bool first = (header & 0x80) != 0;
        byte[] traffic = new byte[288];
        for (int i = 0; i < traffic.Length; i++) traffic[i] = (byte)(bits[i + 76] > 0 ? 1 : 0);
        byte[]? reliability = null;
        if (preserveSoftInformation && communication && mode is 1 or 2 or 3)
        {
            // Decision-directed residual variance in normalized symbol units.
            // This is an AWGN distance proxy, not a calibrated BER probability.
            double variance = 0;
            for (int i = 10; i < symbols.Length; i++)
            {
                double value = symbols[i];
                double nearest = value < -2 ? -3 : value < 0 ? -1 : value < 2 ? 1 : 3;
                variance += (value - nearest) * (value - nearest);
            }
            variance = Math.Max(0.04, variance / 182);
            reliability = new byte[288];
            for (int i = 0; i < 144; i++)
            {
                // PN9 only changes the sign; distances/confidence are invariant.
                double value = symbols[i + 48];
                double positive = Math.Min(Square(value - 1), Square(value - 3));
                double negative = Math.Min(Square(value + 1), Square(value + 3));
                double inner = Math.Min(Square(value - 1), Square(value + 1));
                double outer = Math.Min(Square(value - 3), Square(value + 3));
                reliability[2*i] = QuantizeConfidence(positive - negative, variance);
                reliability[2*i+1] = QuantizeConfidence(inner - outer, variance);
            }
        }
        byte[]? parameters = null;
        string? callSign = null;
        if (!communication)
        {
            parameters = DecodeProtected(bits.Slice(76, 144), false, out int pichCorrections);
            corrected += pichCorrections;
            if (parameters is not null)
            {
                char[] digits = new char[9];
                bool bcd = true;
                for (int i = 0; i < 9; i++)
                {
                    int digit = ReadBits(parameters, i*4, 4);
                    bcd &= digit <= 9;
                    digits[i] = (char)('0' + digit);
                }
                if (bcd) callSign = new string(digits);
            }
        }
        byte[]? data1 = null, data2 = null;
        if (communication && mode is 1 or 4) data1 = DecodeProtected(bits.Slice(76, 144), false, out _);
        if (communication && mode is 2 or 4) data2 = DecodeProtected(bits.Slice(220, 144), false, out _);
        frame = new(communication, mode, header & 31, first, (header >> 5) & 3,
            first ? ReadBits(control, 8, 2) : null,
            first ? ReadBits(control, 10, 9) : null,
            first ? ReadBits(control, 19, 7) : null,
            control, traffic, parameters, callSign, corrected)
            { Data1 = data1, Data2 = data2, TrafficReliability = reliability };
        return true;
    }

    private static double Square(double value) => value * value;
    // 16 units per absolute max-log likelihood, saturated at 255; 0 is erasure.
    private static byte QuantizeConfidence(double distanceDifference, double variance) =>
        (byte)Math.Clamp(Math.Round(Math.Abs(distanceDifference) / (2 * variance) * 16), 0, 255);

    /// <summary>Depuncture as erasures, soft K=5 Viterbi, then CRC and zero-tail validation.</summary>
    public static byte[]? DecodeProtected(ReadOnlySpan<float> airBits, bool sacch, out int correctedBits)
    {
        int length = sacch ? 60 : 144;
        int rows = sacch ? 5 : 9;
        int steps = sacch ? 36 : 96;
        int payloadLength = sacch ? 26 : 80;
        int width = sacch ? 6 : 12;
        correctedBits = 0;
        if (airBits.Length != length) throw new ArgumentException("Invalid protected field length.");
        foreach (float bit in airBits) if (!float.IsFinite(bit)) return null;
        Span<float> punctured = stackalloc float[144];
        for (int i = 0; i < length; i++) punctured[i] = airBits[(i % (length / rows)) * rows + i / (length / rows)];
        Span<float> coded = stackalloc float[192];
        coded.Clear();
        int source = 0;
        for (int step = 0; step < steps; step++)
        {
            coded[2*step] = punctured[source++];
            if (sacch ? step % 3 != 2 : step % 2 == 1) coded[2*step+1] = punctured[source++];
        }
        Span<float> costs = stackalloc float[16];
        Span<float> next = stackalloc float[16];
        Span<byte> trace = stackalloc byte[96 * 16];
        costs.Fill(float.PositiveInfinity);
        costs[0] = 0;
        for (int t = 0; t < steps; t++)
        {
            next.Fill(float.PositiveInfinity);
            for (int state = 0; state < 16; state++)
            for (int bit = 0; bit < 2; bit++)
            {
                int reg = (state << 1) | bit;
                int a = BitOperations.PopCount((uint)(reg & 0x19)) & 1;
                int b = BitOperations.PopCount((uint)(reg & 0x17)) & 1;
                int target = reg & 15;
                float cost = costs[state] + (1 - 2*a)*coded[2*t] + (1 - 2*b)*coded[2*t+1];
                if (cost < next[target]) { next[target] = cost; trace[t*16+target] = (byte)state; }
            }
            next.CopyTo(costs);
        }
        Span<byte> decoded = stackalloc byte[96];
        int current = 0;
        for (int t = steps-1; t >= 0; t--) { decoded[t] = (byte)(current & 1); current = trace[t*16+current]; }
        if (ReadBits(decoded, payloadLength + width, 4) != 0 ||
            ComputeCrc(decoded[..payloadLength], width, sacch ? 0x27 : 0x80f) != ReadBits(decoded, payloadLength, width)) return null;
        int encoder = 0;
        for (int t = 0; t < steps; t++)
        {
            encoder = ((encoder << 1) | decoded[t]) & 31;
            for (int j = 0; j < 2; j++)
            {
                float received = coded[2*t+j];
                int expected = BitOperations.PopCount((uint)(encoder & (j == 0 ? 0x19 : 0x17))) & 1;
                if (received != 0 && expected != (received > 0 ? 1 : 0)) correctedBits++;
            }
        }
        return decoded[..payloadLength].ToArray();
    }

    public static int ComputeCrc(ReadOnlySpan<byte> bits, int width, int polynomial)
    {
        int mask = (1 << width) - 1, crc = mask;
        foreach (byte bit in bits)
        {
            int feedback = ((crc >> (width - 1)) & 1) ^ bit;
            crc = (crc << 1) & mask;
            if (feedback != 0) crc ^= polynomial;
        }
        return crc;
    }

    public static int ReadBits(ReadOnlySpan<byte> bits, int start, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++) value = (value << 1) | bits[start+i];
        return value;
    }
}
