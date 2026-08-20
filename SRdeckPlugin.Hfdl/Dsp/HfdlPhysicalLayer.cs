using SRdeckPlugin.Contracts;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Hfdl.Protocols;

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

/// <summary>ARINC 635/ICAO HFDL physical-layer constants and coding transforms.</summary>
public static class HfdlPhysicalLayer
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
    private static readonly bool[] TSequence = ParseBits(string.Concat(Enumerable.Repeat("000100110101111", 9)));
    private static readonly bool[] Scrambler = ParseHexBits(ScramblerHex);
    private static readonly ConstellationPoint[][] Constellations =
    [[], [], CreateConstellation(2), [], CreateConstellation(4), [], [], [], CreateConstellation(8)];

    public static ReadOnlySpan<bool> A => ASequence;
    public static ReadOnlySpan<bool> M1 => M1Sequence;
    public static ReadOnlySpan<bool> T => TSequence;

    public static bool[] ShiftedM1(int shift)
    {
        var output = new bool[M1Sequence.Length];
        for (int index = 0; index < output.Length; index++) output[index] = M1Sequence[(index + shift) % output.Length];
        return output;
    }

    public static Complex32[] BuildBurst(ReadOnlySpan<byte> mpdu, HfdlPhysicalMode mode)
    {
        bool[] userBits = BytesToBitsLsbFirst(mpdu, mode.UserBitCount);
        bool[] coded = ConvolutionalEncode(userBits);
        if (mode.DataRate == 300) coded = coded.SelectMany(bit => new[] { bit, bit }).ToArray();
        bool[] interleaved = Interleave(coded, mode.InterleaverColumns, mode.LongInterleaver);
        Complex32[] dataSymbols = MapAndScramble(interleaved, mode.ModulationOrder);
        bool[] shiftedM1 = ShiftedM1(mode.M1Shift);

        var symbols = new List<Complex32>(PrekeySymbols + PreambleSymbols + mode.FrameCount * SymbolsPerFrame);
        for (int index = 0; index < PrekeySymbols; index++) symbols.Add(Bpsk((index & 1) != 0));
        AppendBpsk(symbols, ASequence);
        AppendBpsk(symbols, ASequence);
        AppendBpsk(symbols, shiftedM1);
        AppendBpsk(symbols, shiftedM1.AsSpan(0, ProbeSymbolsPerFrame));
        AppendBpsk(symbols, TSequence);
        for (int frame = 0; frame < mode.FrameCount; frame++)
        {
            symbols.AddRange(dataSymbols.AsSpan(frame * DataSymbolsPerFrame, DataSymbolsPerFrame).ToArray());
            AppendBpsk(symbols, shiftedM1.AsSpan(0, ProbeSymbolsPerFrame));
        }
        return symbols.ToArray();
    }

    public static bool[] DecodeDataSymbols(ReadOnlySpan<Complex32> symbols, HfdlPhysicalMode mode,
        ReadOnlySpan<bool> probe, out double quality)
    {
        int expected = mode.FrameCount * SymbolsPerFrame;
        if (symbols.Length < expected) throw new ArgumentException("The HFDL data segment is incomplete.", nameof(symbols));
        var chips = new List<float>(mode.InterleaverColumns * 40);
        double qualitySum = 0;
        var equalizer = new ProbeAdaptiveEqualizer(3);
        for (int frame = 0; frame < mode.FrameCount; frame++)
        {
            int offset = frame * SymbolsPerFrame;
            ReadOnlySpan<Complex32> source = symbols.Slice(offset, SymbolsPerFrame);
            ReadOnlySpan<Complex32> receivedProbe = source.Slice(DataSymbolsPerFrame, ProbeSymbolsPerFrame);
            double residualCarrierStep = EstimateCarrierStep(receivedProbe, probe);
            var corrected = new Complex32[SymbolsPerFrame];
            for (int index = 0; index < corrected.Length; index++)
                corrected[index] = Rotate(source[index], -residualCarrierStep * (index - DataSymbolsPerFrame));
            Complex32 channel = EstimateChannel(corrected.AsSpan(DataSymbolsPerFrame, ProbeSymbolsPerFrame), probe);
            double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
            for (int index = 0; index < corrected.Length; index++)
                corrected[index] = Equalize(corrected[index], channel, inverse);
            equalizer.Train(corrected, DataSymbolsPerFrame, probe);
            qualitySum += equalizer.MeasureQuality(corrected, DataSymbolsPerFrame, probe);
            for (int index = 0; index < DataSymbolsPerFrame; index++)
            {
                Complex32 value = equalizer.Process(corrected, index);
                int dataIndex = frame * DataSymbolsPerFrame + index;
                if (Scrambler[dataIndex % Scrambler.Length]) value = new(-value.I, -value.Q);
                AppendMappedSoftChips(chips, value, mode.ModulationOrder);
            }
        }
        quality = qualitySum / mode.FrameCount;
        float[] deinterleaved = Deinterleave(chips.ToArray(), mode.InterleaverColumns, mode.LongInterleaver);
        if (mode.DataRate == 300)
        {
            var collapsed = new float[deinterleaved.Length / 2];
            for (int index = 0; index < collapsed.Length; index++)
                collapsed[index] = deinterleaved[index * 2] + deinterleaved[index * 2 + 1];
            deinterleaved = collapsed;
        }
        return ViterbiDecode(deinterleaved, mode.UserBitCount);
    }

    public static bool[] Interleave(ReadOnlySpan<bool> input, int columns, bool longInterleaver)
    {
        if (input.Length != 40 * columns) throw new ArgumentException("Invalid HFDL interleaver input length.", nameof(input));
        var matrix = new bool[40, columns];
        int position = 0;
        for (int column = 0; column < columns; column++)
            for (int item = 0; item < 40; item++) matrix[(item * 9) % 40, column] = input[position++];
        var output = new bool[input.Length];
        int row = 0, col = 0, step = longInterleaver ? 23 : 17;
        for (int index = 0; index < output.Length; index++)
        {
            output[index] = matrix[row, col];
            if (row == 39) { row = 0; col = Mod(col + 1 - step, columns); }
            else { row++; col = Mod(col - step, columns); }
        }
        return output;
    }

    public static bool[] Deinterleave(ReadOnlySpan<bool> input, int columns, bool longInterleaver)
    {
        if (input.Length != 40 * columns) throw new ArgumentException("Invalid HFDL interleaver input length.", nameof(input));
        var matrix = new bool[40, columns];
        int row = 0, col = 0, step = longInterleaver ? 23 : 17;
        for (int index = 0; index < input.Length; index++)
        {
            matrix[row, col] = input[index];
            if (row == 39) { row = 0; col = Mod(col + 1 - step, columns); }
            else { row++; col = Mod(col - step, columns); }
        }
        var output = new bool[input.Length];
        int position = 0;
        for (int column = 0; column < columns; column++)
            for (int item = 0; item < 40; item++) output[position++] = matrix[(item * 9) % 40, column];
        return output;
    }

    private static float[] Deinterleave(ReadOnlySpan<float> input, int columns, bool longInterleaver)
    {
        if (input.Length != 40 * columns) throw new ArgumentException("Invalid HFDL interleaver input length.", nameof(input));
        var matrix = new float[40, columns];
        int row = 0, col = 0, step = longInterleaver ? 23 : 17;
        for (int index = 0; index < input.Length; index++)
        {
            matrix[row, col] = input[index];
            if (row == 39) { row = 0; col = Mod(col + 1 - step, columns); }
            else { row++; col = Mod(col - step, columns); }
        }
        var output = new float[input.Length];
        int position = 0;
        for (int column = 0; column < columns; column++)
            for (int item = 0; item < 40; item++) output[position++] = matrix[(item * 9) % 40, column];
        return output;
    }

    public static bool[] ConvolutionalEncode(ReadOnlySpan<bool> input)
    {
        var output = new bool[input.Length * 2];
        int state = 0;
        for (int index = 0; index < input.Length; index++)
        {
            int full = ((state << 1) | (input[index] ? 1 : 0)) & 0x7f;
            output[index * 2] = Parity(full & 0x5b);
            output[index * 2 + 1] = Parity(full & 0x79);
            state = full & 0x3f;
        }
        return output;
    }

    public static bool[] ViterbiDecode(ReadOnlySpan<bool> chips, int outputBits)
    {
        var soft = new float[chips.Length];
        for (int index = 0; index < chips.Length; index++) soft[index] = chips[index] ? 1 : -1;
        return ViterbiDecode(soft, outputBits);
    }

    public static bool[] ViterbiDecode(ReadOnlySpan<float> chips, int outputBits)
    {
        if (chips.Length < outputBits * 2) throw new ArgumentException("Insufficient HFDL FEC chips.", nameof(chips));
        const int states = 64;
        const double infinity = 1e100;
        double[] metrics = Enumerable.Repeat(infinity, states).ToArray();
        metrics[0] = 0;
        var previousState = new byte[outputBits, states];
        var previousBit = new bool[outputBits, states];
        var next = new double[states];
        for (int time = 0; time < outputBits; time++)
        {
            Array.Fill(next, infinity);
            for (int state = 0; state < states; state++)
            {
                if (metrics[state] >= infinity * 0.5) continue;
                for (int bit = 0; bit < 2; bit++)
                {
                    int full = ((state << 1) | bit) & 0x7f;
                    int nextState = full & 0x3f;
                    bool first = Parity(full & 0x5b), second = Parity(full & 0x79);
                    double distance = (first ? -chips[time * 2] : chips[time * 2]) +
                                      (second ? -chips[time * 2 + 1] : chips[time * 2 + 1]);
                    double candidate = metrics[state] + distance;
                    if (candidate >= next[nextState]) continue;
                    next[nextState] = candidate;
                    previousState[time, nextState] = (byte)state;
                    previousBit[time, nextState] = bit != 0;
                }
            }
            (metrics, next) = (next, metrics);
        }
        int finalState = Array.IndexOf(metrics, metrics.Min());
        var output = new bool[outputBits];
        for (int time = outputBits - 1; time >= 0; time--)
        {
            output[time] = previousBit[time, finalState];
            finalState = previousState[time, finalState];
        }
        return output;
    }

    public static byte[] ExtractMpdu(ReadOnlySpan<bool> bits)
        => TryExtractMpdu(bits, out byte[] mpdu) ? mpdu : [];

    public static bool TryExtractMpdu(ReadOnlySpan<bool> bits, out byte[] mpdu)
    {
        byte[] bytes = BitsToBytesLsbFirst(bits);
        for (int length = bytes.Length; length >= 5; length--)
        {
            if (!HfdlCrc.IsValid(bytes.AsSpan(0, length))) continue;
            mpdu = bytes[..length];
            return true;
        }
        mpdu = [];
        return false;
    }

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

    private static void AppendMappedSoftChips(List<float> output, Complex32 value, int order)
    {
        int count = order switch { 8 => 3, 4 => 2, _ => 1 };
        Span<double> zeroDistance = stackalloc double[3];
        Span<double> oneDistance = stackalloc double[3];
        zeroDistance.Fill(double.PositiveInfinity);
        oneDistance.Fill(double.PositiveInfinity);
        foreach (ConstellationPoint point in Constellations[order])
        {
            double di = value.I - point.I, dq = value.Q - point.Q;
            double distance = di * di + dq * dq;
            for (int bit = 0; bit < count; bit++)
            {
                ref double minimum = ref ((point.Gray & (1 << bit)) != 0 ? ref oneDistance[bit] : ref zeroDistance[bit]);
                minimum = Math.Min(minimum, distance);
            }
        }
        for (int bit = 0; bit < count; bit++)
            output.Add((float)Math.Clamp(zeroDistance[bit] - oneDistance[bit], -8, 8));
    }

    private static double EstimateCarrierStep(ReadOnlySpan<Complex32> received, ReadOnlySpan<bool> expected)
    {
        double real = 0, imaginary = 0;
        for (int index = 1; index < received.Length; index++)
        {
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            Complex32 previous = received[index - 1], current = received[index];
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
        }
        return Math.Atan2(imaginary, real);
    }

    private static ConstellationPoint[] CreateConstellation(int order)
    {
        var output = new ConstellationPoint[order];
        for (int sector = 0; sector < order; sector++)
        {
            double phase = 2 * Math.PI * sector / order;
            output[sector] = new((float)Math.Cos(phase), (float)Math.Sin(phase),
                sector ^ (sector >> 1));
        }
        return output;
    }

    private static Complex32 Rotate(Complex32 value, double phase)
    {
        double cosine = Math.Cos(phase), sine = Math.Sin(phase);
        return new((float)(value.I * cosine - value.Q * sine),
            (float)(value.I * sine + value.Q * cosine));
    }

    private sealed class ProbeAdaptiveEqualizer
    {
        private readonly Complex32[] weights;

        public ProbeAdaptiveEqualizer(int taps)
        {
            weights = new Complex32[taps];
            weights[0] = new(1, 0);
        }

        public Complex32 Process(ReadOnlySpan<Complex32> samples, int index)
        {
            double i = 0, q = 0;
            for (int tap = 0; tap < weights.Length; tap++)
            {
                int source = index - tap;
                if (source < 0) break;
                Complex32 x = samples[source], w = weights[tap];
                i += w.I * x.I - w.Q * x.Q;
                q += w.I * x.Q + w.Q * x.I;
            }
            return new((float)i, (float)q);
        }

        public void Train(ReadOnlySpan<Complex32> samples, int offset, ReadOnlySpan<bool> expected)
        {
            const double step = 0.18;
            for (int epoch = 0; epoch < 5; epoch++)
            {
                for (int item = weights.Length - 1; item < expected.Length; item++)
                {
                    int index = offset + item;
                    Complex32 value = Process(samples, index);
                    double target = expected[item] ? -1 : 1;
                    double errorI = target - value.I, errorQ = -value.Q;
                    double power = 1e-4;
                    for (int tap = 0; tap < weights.Length; tap++)
                    {
                        Complex32 x = samples[index - tap];
                        power += x.I * x.I + x.Q * x.Q;
                    }
                    double gain = step / power;
                    for (int tap = 0; tap < weights.Length; tap++)
                    {
                        Complex32 x = samples[index - tap], w = weights[tap];
                        weights[tap] = new(
                            (float)(w.I + gain * (errorI * x.I + errorQ * x.Q)),
                            (float)(w.Q + gain * (errorQ * x.I - errorI * x.Q)));
                    }
                }
            }
        }

        public double MeasureQuality(ReadOnlySpan<Complex32> samples, int offset, ReadOnlySpan<bool> expected)
        {
            double error = 0, reference = 0;
            for (int item = weights.Length - 1; item < expected.Length; item++)
            {
                Complex32 value = Process(samples, offset + item);
                double target = expected[item] ? -1 : 1;
                double di = value.I - target;
                error += di * di + value.Q * value.Q;
                reference += target * target;
            }
            double evm = Math.Sqrt(error / Math.Max(reference, 1e-12));
            return Math.Clamp(1 - evm, 0, 1);
        }
    }

    private readonly record struct ConstellationPoint(float I, float Q, int Gray);

    private static Complex32 EstimateChannel(ReadOnlySpan<Complex32> received, ReadOnlySpan<bool> expected)
    {
        float i = 0, q = 0;
        for (int index = 0; index < received.Length; index++)
        {
            float sign = expected[index] ? -1 : 1;
            i += received[index].I * sign; q += received[index].Q * sign;
        }
        return new(i / received.Length, q / received.Length);
    }
    private static Complex32 Equalize(Complex32 value, Complex32 channel, double inverse) => new(
        (float)((value.I * channel.I + value.Q * channel.Q) * inverse),
        (float)((value.Q * channel.I - value.I * channel.Q) * inverse));
    private static void AppendBpsk(List<Complex32> output, ReadOnlySpan<bool> bits)
    { foreach (bool bit in bits) output.Add(Bpsk(bit)); }
    private static Complex32 Bpsk(bool bit) => new(bit ? -1 : 1, 0);
    private static bool[] BytesToBitsLsbFirst(ReadOnlySpan<byte> bytes, int count)
    {
        var output = new bool[count];
        for (int index = 0; index < Math.Min(count, bytes.Length * 8); index++) output[index] = (bytes[index / 8] & (1 << (index % 8))) != 0;
        return output;
    }
    private static byte[] BitsToBytesLsbFirst(ReadOnlySpan<bool> bits)
    {
        var output = new byte[bits.Length / 8];
        for (int index = 0; index < output.Length * 8; index++) if (bits[index]) output[index / 8] |= (byte)(1 << (index % 8));
        return output;
    }
    private static bool[] ParseBits(string value) => value.Where(character => character is '0' or '1')
        .Select(character => character == '1').ToArray();
    private static bool[] ParseHexBits(string value) => value.SelectMany(character =>
        Enumerable.Range(0, 4).Select(bit => ((Convert.ToInt32(character.ToString(), 16) >> (3 - bit)) & 1) != 0)).ToArray();
    private static int GrayToBinary(int gray) { int value = gray; for (int shift = 1; shift < 8; shift <<= 1) value ^= value >> shift; return value; }
    private static bool Parity(int value) => (System.Numerics.BitOperations.PopCount((uint)value) & 1) != 0;
    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
}
