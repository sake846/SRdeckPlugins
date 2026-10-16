using SRdeckPlugin.Hfdl.Protocols;
using System.Buffers;
using System.Collections.Concurrent;

namespace SRdeckPlugin.Hfdl.Dsp;

public static partial class HfdlPhysicalLayer
{
    private static readonly ConcurrentDictionary<(int Columns, bool LongInterleaver), InterleaverPermutation>
        InterleaverPermutations = CreateInterleaverPermutations();
    private static readonly sbyte[] ViterbiFirstSigns = CreateViterbiSigns(0x5b);
    private static readonly sbyte[] ViterbiSecondSigns = CreateViterbiSigns(0x79);

    /// <summary>Interleaves coded chips using the HFDL physical-layer permutation.</summary>
    public static bool[] Interleave(ReadOnlySpan<bool> input, int columns, bool longInterleaver)
    {
        if (input.Length != 40 * columns) throw new ArgumentException("Invalid HFDL interleaver input length.", nameof(input));
        InterleaverPermutation permutation = GetInterleaverPermutation(columns, longInterleaver);
        var output = new bool[input.Length];
        for (int index = 0; index < output.Length; index++)
            output[index] = input[permutation.InterleaveOutputToInput[index]];
        return output;
    }

    /// <summary>Deinterleaves hard-decision chips using the HFDL physical-layer permutation.</summary>
    public static bool[] Deinterleave(ReadOnlySpan<bool> input, int columns, bool longInterleaver)
    {
        if (input.Length != 40 * columns) throw new ArgumentException("Invalid HFDL interleaver input length.", nameof(input));
        InterleaverPermutation permutation = GetInterleaverPermutation(columns, longInterleaver);
        var output = new bool[input.Length];
        for (int index = 0; index < output.Length; index++)
            output[index] = input[permutation.DeinterleaveOutputToInput[index]];
        return output;
    }

    private static float[] Deinterleave(ReadOnlySpan<float> input, int columns, bool longInterleaver)
    {
        if (input.Length != 40 * columns) throw new ArgumentException("Invalid HFDL interleaver input length.", nameof(input));
        InterleaverPermutation permutation = GetInterleaverPermutation(columns, longInterleaver);
        var output = new float[input.Length];
        for (int index = 0; index < output.Length; index++)
            output[index] = input[permutation.DeinterleaveOutputToInput[index]];
        return output;
    }

    /// <summary>Applies the rate-1/2 HFDL convolutional encoder.</summary>
    public static bool[] ConvolutionalEncode(ReadOnlySpan<bool> input)
    {
        var output = new bool[input.Length * 2];
        int state = 0;
        for (int index = 0; index < input.Length; index++)
        {
            int bit = input[index] ? 1 : 0;
            int full = (bit << 6) | state;
            output[index * 2] = Parity(full & 0x5b);
            output[index * 2 + 1] = Parity(full & 0x79);
            state = (bit << 5) | (state >> 1);
        }
        return output;
    }

    /// <summary>Decodes hard-decision HFDL convolutional-code chips.</summary>
    public static bool[] ViterbiDecode(ReadOnlySpan<bool> chips, int outputBits)
    {
        float[] soft = ArrayPool<float>.Shared.Rent(chips.Length);
        try
        {
            for (int index = 0; index < chips.Length; index++) soft[index] = chips[index] ? 1 : -1;
            return ViterbiDecode(soft.AsSpan(0, chips.Length), outputBits);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(soft);
        }
    }

    /// <summary>Decodes soft-decision HFDL convolutional-code chips.</summary>
    public static bool[] ViterbiDecode(ReadOnlySpan<float> chips, int outputBits)
    {
        if (chips.Length < outputBits * 2) throw new ArgumentException("Insufficient HFDL FEC chips.", nameof(chips));
        const int states = 64;
        const double infinity = 1e100;
        Span<double> metrics = stackalloc double[states];
        Span<double> next = stackalloc double[states];
        metrics.Fill(infinity);
        metrics[0] = 0;
        ulong[] survivors = ArrayPool<ulong>.Shared.Rent(outputBits);
        try
        {
            for (int time = 0; time < outputBits; time++)
            {
                ulong decisions = 0;
                float first = chips[time * 2], second = chips[time * 2 + 1];
                for (int pair = 0; pair < states / 2; pair++)
                {
                    // Both polynomials have their low and high bits set:
                    // toggling the predecessor's low bit or the input bit
                    // negates both branch signs. Share that distance across
                    // the four transitions, retaining the even path on ties.
                    double distance = ViterbiFirstSigns[pair * 4] * first +
                                      ViterbiSecondSigns[pair * 4] * second;
                    double even = metrics[pair * 2], odd = metrics[pair * 2 + 1];
                    double zeroEven = even + distance, zeroOdd = odd - distance;
                    double oneEven = even - distance, oneOdd = odd + distance;
                    if (zeroOdd < zeroEven)
                    {
                        next[pair] = zeroOdd;
                        decisions |= 1UL << pair;
                    }
                    else next[pair] = zeroEven;
                    if (oneOdd < oneEven)
                    {
                        next[pair + 32] = oneOdd;
                        decisions |= 1UL << (pair + 32);
                    }
                    else next[pair + 32] = oneEven;
                }
                survivors[time] = decisions;
                Span<double> swap = metrics;
                metrics = next;
                next = swap;
            }

            int finalState = 0;
            for (int state = 1; state < states; state++)
                if (metrics[state] < metrics[finalState]) finalState = state;
            var output = new bool[outputBits];
            for (int time = outputBits - 1; time >= 0; time--)
            {
                output[time] = (finalState & 32) != 0;
                bool oddPredecessor = (survivors[time] & (1UL << finalState)) != 0;
                finalState = ((finalState << 1) & 0x3f) | (oddPredecessor ? 1 : 0);
            }
            return output;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(survivors);
        }
    }

    /// <summary>Extracts a complete HFDL PDU with a valid header FCS, or returns an empty array.</summary>
    public static byte[] ExtractMpdu(ReadOnlySpan<bool> bits)
        => TryExtractMpdu(bits, out byte[] mpdu) ? mpdu : [];

    /// <summary>Retains all declared LPDUs after the MPDU header, or the fixed-size SPDU.</summary>
    public static bool TryExtractMpdu(ReadOnlySpan<bool> bits, out byte[] mpdu)
    {
        byte[] bytes = BitsToBytesLsbFirst(bits);
        bool valid = HfdlPdu.TryReadLengths(bytes, out _, out int length) && length <= bytes.Length;
        mpdu = valid ? bytes[..length] : [];
        return valid;
    }

    private static byte[] BitsToBytesLsbFirst(ReadOnlySpan<bool> bits)
    {
        var output = new byte[bits.Length / 8];
        for (int index = 0; index < output.Length * 8; index++) if (bits[index]) output[index / 8] |= (byte)(1 << (index % 8));
        return output;
    }

    private sealed record InterleaverPermutation(int[] InterleaveOutputToInput, int[] DeinterleaveOutputToInput);

    private static ConcurrentDictionary<(int Columns, bool LongInterleaver), InterleaverPermutation>
        CreateInterleaverPermutations()
    {
        var permutations = new ConcurrentDictionary<(int, bool), InterleaverPermutation>();
        foreach (HfdlPhysicalMode mode in HfdlPhysicalMode.All)
            permutations.TryAdd((mode.InterleaverColumns, mode.LongInterleaver),
                CreateInterleaverPermutation(mode.InterleaverColumns, mode.LongInterleaver));
        return permutations;
    }

    private static InterleaverPermutation GetInterleaverPermutation(int columns, bool longInterleaver)
        => InterleaverPermutations.GetOrAdd((columns, longInterleaver),
            static key => CreateInterleaverPermutation(key.Columns, key.LongInterleaver));

    private static InterleaverPermutation CreateInterleaverPermutation(int columns, bool longInterleaver)
    {
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        int length = checked(40 * columns);
        var interleave = new int[length];
        var deinterleave = new int[length];
        int row = 0, column = 0, step = longInterleaver ? 23 : 17;
        for (int outputIndex = 0; outputIndex < length; outputIndex++)
        {
            int inputIndex = column * 40 + (row * 9) % 40;
            interleave[outputIndex] = inputIndex;
            deinterleave[inputIndex] = outputIndex;
            if (row == 39) { row = 0; column = Mod(column + 1 - step, columns); }
            else { row++; column = Mod(column - step, columns); }
        }
        return new(interleave, deinterleave);
    }

    private static sbyte[] CreateViterbiSigns(int polynomial)
    {
        var output = new sbyte[128];
        for (int state = 0; state < 64; state++)
            for (int bit = 0; bit < 2; bit++)
            {
                int register = (bit << 6) | state;
                output[state * 2 + bit] = Parity(register & polynomial) ? (sbyte)-1 : (sbyte)1;
            }
        return output;
    }

    private static bool Parity(int value) => (System.Numerics.BitOperations.PopCount((uint)value) & 1) != 0;
}
