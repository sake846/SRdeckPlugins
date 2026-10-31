using SRdeck.Codecs.Ambe;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Codec;

internal sealed record AribT98AmbeBlock(byte[] Parameters, int Errors, int C0Errors, int UnprotectedMinimum);

internal sealed class AribT98AmbeCache(byte[] bits, byte[]? reliability)
{
    internal readonly byte[] Bits = bits;
    internal readonly byte[]? Reliability = reliability;
    internal readonly AribT98AmbeBlock?[] Blocks = new AribT98AmbeBlock?[4];
}

/// <summary>AMBE channel FEC and privacy bits only; PCM synthesis requires a hardware dongle.</summary>
public static partial class AmbeDecoder
{
    public const int SamplesPerFrame = 160;
    public const int SampleRateHz = 8_000;
    // Weak ownership keeps the frame record's equality/hash immutable while
    // sharing FEC between playback, preset selection and manual analysis.
    private static readonly ConditionalWeakTable<AribT98Frame, AribT98AmbeCache> voiceCache = new();
    private static readonly Lazy<bool> available = new(() =>
    {
        try { Unpack(new byte[72], new byte[49], out _); return true; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return false; }
    });
    internal static bool IsFecAvailable => available.Value;

    public static unsafe int Unpack(ReadOnlySpan<byte> bits, Span<byte> parameters, out int c0Errors)
    {
        if (bits.Length != 72 || parameters.Length < 49) throw new ArgumentException("Expected 72 coded bits and 49 parameter bits.");
        int c0 = 0;
        int result;
        fixed (byte* input = bits)
        fixed (byte* output = parameters)
            result = Native.Unpack(input, bits.Length, output, parameters.Length, &c0);
        if (result < 0) throw new ArgumentException($"Invalid AMBE frame ({result}).");
        c0Errors = c0;
        return result;
    }

    public static unsafe int UnpackSoft(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> reliability,
        Span<byte> parameters, out int c0Errors, out int unprotectedMinimum, bool allowFastPath = true)
    {
        if (bits.Length != 72 || reliability.Length != 72 || parameters.Length < 49)
            throw new ArgumentException("Expected 72 coded bits/confidences and 49 parameter bits.");
        int c0 = 0, minimum = 0, result;
        fixed (byte* input = bits)
        fixed (byte* confidence = reliability)
        fixed (byte* output = parameters)
            result = Native.UnpackSoft(input, confidence, bits.Length, output, parameters.Length, &c0, &minimum, allowFastPath ? 1 : 0);
        if (result < 0) throw new ArgumentException($"Invalid soft AMBE frame ({result}).");
        c0Errors = c0;
        unprotectedMinimum = minimum;
        return result;
    }

    internal static int UnpackVoice(AribT98Frame frame, int slot, Span<byte> parameters,
        out int c0Errors, out bool lowConfidence)
    {
        if (slot is < 0 or > 3 || frame.TrafficBits.Length != 288 || parameters.Length < 49 ||
            (frame.TrafficReliability is { Length: not 288 })) throw new ArgumentException("Invalid voice frame.");
        var cache = voiceCache.GetValue(frame, static source => new(source.TrafficBits, source.TrafficReliability));
        // Frames can be read by the receive worker and a manual analysis task.
        // Cached parameters remain scrambled and are copied before PN15 removal.
        lock (cache)
        {
            if (cache.Blocks[slot] is not { } block)
            {
                byte[] decoded = new byte[49];
                int errors, c0, minimum = 255;
                if (!IsFecAvailable)
                {
                    errors = AmbeChannelFec.Unpack(cache.Bits.AsSpan(slot * 72, 72), decoded, out c0);
                    if (cache.Reliability is { } confidence)
                        minimum = AmbeChannelFec.UnprotectedMinimum(confidence.AsSpan(slot * 72, 72));
                }
                else if (cache.Reliability is { } reliability)
                    errors = UnpackSoft(cache.Bits.AsSpan(slot * 72, 72), reliability.AsSpan(slot * 72, 72), decoded, out c0, out minimum);
                else errors = Unpack(cache.Bits.AsSpan(slot * 72, 72), decoded, out c0);
                cache.Blocks[slot] = block = new(decoded, errors, c0, minimum);
            }
            block.Parameters.CopyTo(parameters);
            c0Errors = block.C0Errors;
            // At least one unprotected bit within two log-likelihood units of
            // its alternative: insufficient evidence for key inference.
            lowConfidence = block.UnprotectedMinimum < 32;
            return block.Errors;
        }
    }

    /// <summary>STD-T98 3/4.1.11: reset per 80 ms frame; mixed data slots still occupy their PN positions.</summary>
    public static void Descramble(Span<byte> parameters, int key, int voiceSlot)
    {
        if (parameters.Length != 49 || key is < 1 or > 32767 || voiceSlot is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(key));
        int state = key;
        for (int i = 0; i < (voiceSlot + 1) * 49; i++)
        {
            int bit = state & 1;
            state = (state >> 1) | (((state ^ (state >> 1)) & 1) << 14);
            if (i >= voiceSlot * 49) parameters[i - voiceSlot * 49] ^= (byte)bit;
        }
    }

    private static unsafe partial class Native
    {
        private const string Library = "arib_t98_fec_native";
        [LibraryImport(Library, EntryPoint = "arib_t98_unpack")]
        internal static partial int Unpack(byte* input, int count, byte* output, int size, int* c0);
        [LibraryImport(Library, EntryPoint = "arib_t98_unpack_soft")]
        internal static partial int UnpackSoft(byte* input, byte* reliability, int count, byte* output, int size, int* c0, int* minimum, int allowFastPath);
    }
}
