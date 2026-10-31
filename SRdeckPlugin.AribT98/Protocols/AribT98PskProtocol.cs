using System.Numerics;
using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Protocols;

/// <summary>STD-T98 v1.1 Part 1, sections 4.1.6-4.1.10. MSB-first air bits.</summary>
public static class AribT98PskProtocol
{
    public const int BitsPerFrame = 384;
    public const uint CommunicationSync = 0x1E56F;
    public const uint BurstSync = 0x2F94D06B;

    // Part 1 section 3.4: 00 +pi/4, 01 +3pi/4, 10 -pi/4, 11 -3pi/4.
    public static double DibitPhase(int dibit) => dibit switch
    {
        0 => Math.PI/4, 1 => 3*Math.PI/4, 2 => -Math.PI/4, 3 => -3*Math.PI/4,
        _ => throw new ArgumentOutOfRangeException(nameof(dibit))
    };

    public static bool TryDecode(ReadOnlySpan<float> bits, bool burst, int? arbitrarySeed, out AribT98Frame? frame)
    {
        frame = null;
        if (bits.Length != BitsPerFrame || arbitrarySeed is < 0 or > 511) return false;
        foreach (float bit in bits) if (!float.IsFinite(bit)) return false;
        int syncLength = burst ? 32 : 20, syncErrors = 0;
        uint sync = burst ? BurstSync : CommunicationSync;
        for (int i = 0; i < syncLength; i++)
            if ((bits[184+i] > 0 ? 1 : 0) != ((sync >> (syncLength-1-i)) & 1)) syncErrors++;
        if (syncErrors > (burst ? 3 : 2)) return false;

        Span<float> rich = stackalloc float[70];
        bits.Slice(128, 56).CopyTo(rich);
        bits.Slice(burst ? 216 : 204, 14).CopyTo(rich[56..]);
        byte[]? control = DecodeProtected(rich, false, out int corrections);
        if (control is null) return false;
        int f = AribT98Protocol.ReadBits(control, 2, 3);
        int mode = AribT98Protocol.ReadBits(control, 5, 3);
        if (f != (burst ? 7 : 3) || (burst ? mode != 4 : mode is 0 or 4)) return false;
        bool arbitrary = control[8] != 0;
        int uc = AribT98Protocol.ReadBits(control, 9, 9);
        byte[]? pich = null;
        string? callSign = null;
        byte[] raw = [], traffic = [];
        bool descrambled = !arbitrary || arbitrarySeed.HasValue;
        if (burst)
        {
            pich = DecodeProtected(bits.Slice(256, 120), true, out int pichCorrections);
            if (pich is not null)
            {
                corrections += pichCorrections;
                char[] digits = new char[9];
                bool valid = true;
                for (int i = 0; i < 9; i++)
                {
                    int digit = AribT98Protocol.ReadBits(pich, 4*i, 4);
                    valid &= digit <= 9;
                    digits[i] = (char)('0'+digit);
                }
                if (valid) callSign = new string(digits);
            }
        }
        else
        {
            raw = new byte[256];
            for (int i = 0; i < 256; i++) raw[i] = (byte)(bits[i < 96 ? 32+i : 224+i-96] > 0 ? 1 : 0);
            if (descrambled)
            {
                traffic = (byte[])raw.Clone();
                int pn = arbitrary ? arbitrarySeed!.Value : uc;
                for (int i = 0; i < 262; i++)
                {
                    // The undefined six bits between RI and TCH2 consume PN96..101.
                    if (i < 96) traffic[i] ^= (byte)(pn & 1);
                    else if (i >= 102) traffic[i-6] ^= (byte)(pn & 1);
                    pn = (pn >> 1) | (((pn ^ (pn >> 4)) & 1) << 8);
                }
            }
        }
        frame = new(!burst, mode, -1, false, 0, null, uc, null, control, traffic, pich, callSign, corrections)
        {
            Modulation = AribT98Modulation.PiOver4Qpsk, RawTrafficBits = raw,
            UsesArbitraryScrambler = arbitrary, TrafficDescrambled = descrambled
        };
        return true;
    }

    /// <summary>RICH: 10x7 / 24+6+5; PICH: 15x8 / 48+NULL+6+5. No puncturing.</summary>
    public static byte[]? DecodeProtected(ReadOnlySpan<float> air, bool pich, out int correctedBits)
    {
        correctedBits = 0;
        int length = pich ? 120 : 70, rows = pich ? 15 : 10;
        int payloadLength = pich ? 49 : 24, steps = length/2;
        if (air.Length != length) throw new ArgumentException("Invalid Part 1 protected field length.");
        foreach (float bit in air) if (!float.IsFinite(bit)) return null;
        Span<float> coded = stackalloc float[120];
        for (int i = 0; i < length; i++) coded[i] = air[(i % (length/rows))*rows + i/(length/rows)];
        Span<float> costs = stackalloc float[32], next = stackalloc float[32];
        Span<byte> trace = stackalloc byte[60*32], decoded = stackalloc byte[60];
        costs.Fill(float.PositiveInfinity); costs[0] = 0;
        for (int t = 0; t < steps; t++)
        {
            next.Fill(float.PositiveInfinity);
            for (int state = 0; state < 32; state++)
            for (int bit = 0; bit < 2; bit++)
            {
                int reg = (state << 1) | bit, target = reg & 31;
                int a = BitOperations.PopCount((uint)(reg & 0x2B)) & 1;
                int b = BitOperations.PopCount((uint)(reg & 0x3D)) & 1;
                float cost = costs[state] + (1-2*a)*coded[t*2] + (1-2*b)*coded[t*2+1];
                if (cost < next[target]) { next[target] = cost; trace[t*32+target] = (byte)state; }
            }
            next.CopyTo(costs);
        }
        int current = 0;
        for (int t = steps-1; t >= 0; t--) { decoded[t] = (byte)(current & 1); current = trace[t*32+current]; }
        if (AribT98Protocol.ReadBits(decoded, payloadLength+6, 5) != 0 ||
            (pich && decoded[48] != 0) ||
            ComputeCrc(decoded[..payloadLength]) != AribT98Protocol.ReadBits(decoded, payloadLength, 6)) return null;
        int encoder = 0;
        for (int t = 0; t < steps; t++)
        {
            encoder = ((encoder << 1) | decoded[t]) & 63;
            for (int j = 0; j < 2; j++)
            {
                int expected = BitOperations.PopCount((uint)(encoder & (j == 0 ? 0x2B : 0x3D))) & 1;
                if (coded[2*t+j] != 0 && expected != (coded[2*t+j] > 0 ? 1 : 0)) correctedBits++;
            }
        }
        return decoded[..(pich ? 48 : 24)].ToArray();
    }

    // Figure 4-6, zero-state polynomial division, S5 first. No final complement.
    // Part 1 supplies no interoperability vectors; this convention needs real-IQ verification.
    public static int ComputeCrc(ReadOnlySpan<byte> bits)
    {
        int crc = 0;
        foreach (byte bit in bits)
        {
            int feedback = ((crc >> 5) & 1) ^ bit;
            crc = (crc << 1) & 63;
            if (feedback != 0) crc ^= 3;
        }
        return crc;
    }
}
