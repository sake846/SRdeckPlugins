// Channel decoding conventions and Golay generator adapted from mbelib-neo:
// Copyright (C) 2025 arancormonk; Copyright (C) 2010 mbelib Author.
// GPL-2.0-or-later; combined plugin GPL-3.0-only. See THIRD-PARTY-NOTICES.md.
using System.Numerics;

namespace SRdeck.Codecs.Ambe;

/// <summary>Channel FEC only; never synthesizes audio or loads a codec DLL.</summary>
internal static class AmbeChannelFec
{
    // DSD's ISC-licensed NXDN bitplane schedule; notice in THIRD-PARTY-NOTICES.md.
    private static ReadOnlySpan<int> W => [0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,2,0,2,0,2,0,2,0,2,0,2,0,2];
    private static ReadOnlySpan<int> X => [23,10,22,9,21,8,20,7,19,6,18,5,17,4,16,3,15,2,14,1,13,0,12,10,11,9,10,8,9,7,8,6,7,5,6,4];
    private static ReadOnlySpan<int> Y => [0,2,0,2,0,2,0,2,0,3,0,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3];
    private static ReadOnlySpan<int> Z => [5,3,4,2,3,1,2,0,1,13,0,12,22,11,21,10,20,9,19,8,18,7,17,6,16,5,15,4,14,3,13,2,12,1,11,0];
    private static readonly uint[] codewords = BuildCodewords();

    internal static int UnprotectedMinimum(ReadOnlySpan<byte> reliability)
    {
        if (reliability.Length != 72) throw new ArgumentException("Expected 72 channel-bit confidences.");
        int minimum = 255;
        for (int i = 0; i < 36; i++)
        {
            if (W[i] >= 2) minimum = Math.Min(minimum, reliability[2 * i]);
            if (Y[i] >= 2) minimum = Math.Min(minimum, reliability[2 * i + 1]);
        }
        return minimum;
    }

    internal static int Unpack(ReadOnlySpan<byte> bits, Span<byte> parameters, out int c0Errors)
    {
        if (bits.Length != 72 || parameters.Length < 49 || bits.IndexOfAnyExcept((byte)0, (byte)1) >= 0)
            throw new ArgumentException("Expected 72 binary channel bits and 49 parameter bits.");
        Span<uint> planes = stackalloc uint[4];
        planes.Clear();
        for (int i = 0; i < 36; i++)
        {
            planes[W[i]] |= (uint)bits[2 * i] << X[i];
            planes[Y[i]] |= (uint)bits[2 * i + 1] << Z[i];
        }
        uint c0 = Correct(planes[0] >> 1, out c0Errors);
        // Match mbelib's corrected-data count and extra C0 parity check.
        if (c0Errors == 0 && (BitOperations.PopCount(planes[0]) & 1) != 0) c0Errors = 1;
        int state = (int)(c0 >> 11) * 16;
        for (int i = 22; i >= 0; i--)
        {
            state = (173 * state + 13849) & 0xffff;
            planes[1] ^= (uint)(state >> 15) << i;
        }
        uint c1 = Correct(planes[1], out int c1Errors);
        int at = 0;
        Copy(c0 >> 11, 12, parameters, ref at);
        Copy(c1 >> 11, 12, parameters, ref at);
        Copy(planes[2], 11, parameters, ref at);
        Copy(planes[3], 14, parameters, ref at);
        return c0Errors + c1Errors;
    }

    private static void Copy(uint word, int count, Span<byte> output, ref int at)
    {
        for (int i = count - 1; i >= 0; i--) output[at++] = (byte)((word >> i) & 1);
    }

    private static uint Correct(uint received, out int errors)
    {
        // Hard FEC scans 4096 Golay codewords; T98 uses native soft FEC for key analysis.
        uint best = 0;
        int distance = 24;
        foreach (uint word in codewords)
        {
            int candidate = BitOperations.PopCount(received ^ word);
            if (candidate >= distance) continue;
            best = word; distance = candidate;
            if (distance <= 3) break; // Golay(23,12) has a unique radius-three result.
        }
        errors = BitOperations.PopCount((received ^ best) >> 11);
        return best;
    }

    private static uint[] BuildCodewords()
    {
        // Golay generator matrix used by the pinned mbelib-neo ECC implementation.
        ReadOnlySpan<uint> generator = [0x63a,0x31d,0x7b4,0x3da,0x1ed,0x6cc,0x366,0x1b3,0x6e3,0x54b,0x49f,0x475];
        var words = new uint[4096];
        for (uint data = 0; data < words.Length; data++)
        {
            uint parity = 0;
            for (int i = 0; i < 12; i++)
                if ((data & (1u << (11 - i))) != 0) parity ^= generator[i];
            words[data] = (data << 11) | parity;
        }
        return words;
    }
}
