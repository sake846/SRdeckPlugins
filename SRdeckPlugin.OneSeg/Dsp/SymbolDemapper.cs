using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal static class SymbolDemapper
{
    internal const int MaximumReliability = 127;
    private const float QpskAxis = 0.707106781f;
    private const float Qam16AxisStep = 0.316227766f;
    private const float Qam16InnerThreshold = 0.632455532f;
    private const float Qam64AxisStep = 0.15430335f;
    private const float Qam64InnerThreshold = 0.3086067f;
    private const float Qam64OuterThreshold = 0.6172134f;

    public static byte[] Demap(ReadOnlySpan<ComplexF> symbols, IsdbTModulation modulation)
    {
        int width = BitsPerSymbol(modulation);
        var bits = new byte[symbols.Length * width];
        Demap(symbols, modulation, bits);
        return bits;
    }

    public static int Demap(ReadOnlySpan<ComplexF> symbols, IsdbTModulation modulation,
        Span<byte> bits)
    {
        int width = BitsPerSymbol(modulation);
        int required = symbols.Length * width;
        if (bits.Length < required)
            throw new ArgumentException("The output buffer is too small.", nameof(bits));
        int output = 0;
        foreach (ComplexF symbol in symbols)
        {
            int word = modulation switch
            {
                IsdbTModulation.Dqpsk or IsdbTModulation.Qpsk =>
                    (symbol.Real < 0 ? 2 : 0) | (symbol.Imaginary < 0 ? 1 : 0),
                IsdbTModulation.Qam16 => Demap16(symbol),
                IsdbTModulation.Qam64 => Demap64(symbol),
                _ => 0
            };
            for (int bit = width - 1; bit >= 0; bit--)
                bits[output++] = (byte)((word >> bit) & 1);
        }
        return output;
    }

    public static int DemapSoft(
        ReadOnlySpan<ComplexF> symbols,
        IsdbTModulation modulation,
        Span<sbyte> reliabilities)
    {
        // Negative values support bit 0, zero is an erasure, and positive values support bit 1.
        int width = BitsPerSymbol(modulation);
        int required = symbols.Length * width;
        if (reliabilities.Length < required)
            throw new ArgumentException("The output buffer is too small.",
                nameof(reliabilities));
        int output = 0;
        foreach (ComplexF symbol in symbols)
        {
            switch (modulation)
            {
                case IsdbTModulation.Dqpsk:
                case IsdbTModulation.Qpsk:
                    reliabilities[output++] = Quantize(-symbol.Real / QpskAxis);
                    reliabilities[output++] = Quantize(-symbol.Imaginary / QpskAxis);
                    break;
                case IsdbTModulation.Qam16:
                    reliabilities[output++] = Quantize(-symbol.Real / Qam16AxisStep);
                    reliabilities[output++] = Quantize(-symbol.Imaginary / Qam16AxisStep);
                    reliabilities[output++] = Quantize(
                        (Qam16InnerThreshold - MathF.Abs(symbol.Real)) / Qam16AxisStep);
                    reliabilities[output++] = Quantize(
                        (Qam16InnerThreshold - MathF.Abs(symbol.Imaginary)) / Qam16AxisStep);
                    break;
                case IsdbTModulation.Qam64:
                    reliabilities[output++] = Quantize(-symbol.Real / Qam64AxisStep);
                    reliabilities[output++] = Quantize(-symbol.Imaginary / Qam64AxisStep);
                    reliabilities[output++] = Quantize(
                        (Qam64OuterThreshold - MathF.Abs(symbol.Real)) / Qam64AxisStep);
                    reliabilities[output++] = Quantize(
                        (Qam64OuterThreshold - MathF.Abs(symbol.Imaginary)) / Qam64AxisStep);
                    reliabilities[output++] = Quantize(Qam64InnerReliability(symbol.Real));
                    reliabilities[output++] = Quantize(Qam64InnerReliability(symbol.Imaginary));
                    break;
                default:
                    throw new NotSupportedException($"Unsupported modulation {modulation}.");
            }
        }
        return output;
    }

    internal static int BitsPerSymbol(IsdbTModulation modulation) =>
        modulation switch
        {
            IsdbTModulation.Dqpsk or IsdbTModulation.Qpsk => 2,
            IsdbTModulation.Qam16 => 4,
            IsdbTModulation.Qam64 => 6,
            _ => throw new NotSupportedException($"Unsupported modulation {modulation}.")
        };

    private static int Demap16(ComplexF value)
    {
        return (value.Real < 0 ? 8 : 0) | (value.Imaginary < 0 ? 4 : 0) |
               (Math.Abs(value.Real) < Qam16InnerThreshold ? 2 : 0) |
               (Math.Abs(value.Imaginary) < Qam16InnerThreshold ? 1 : 0);
    }

    private static int Demap64(ComplexF value)
    {
        float real = Math.Abs(value.Real), imaginary = Math.Abs(value.Imaginary);
        return (value.Real < 0 ? 32 : 0) | (value.Imaginary < 0 ? 16 : 0) |
               (real < Qam64OuterThreshold ? 8 : 0) |
               (imaginary < Qam64OuterThreshold ? 4 : 0) |
               (real < Qam64InnerThreshold || real > 3 * Qam64InnerThreshold ? 2 : 0) |
               (imaginary < Qam64InnerThreshold || imaginary > 3 * Qam64InnerThreshold ? 1 : 0);
    }

    private static float Qam64InnerReliability(float coordinate)
    {
        float magnitude = MathF.Abs(coordinate);
        float upper = 3 * Qam64InnerThreshold;
        if (magnitude < Qam64InnerThreshold)
            return (Qam64InnerThreshold - magnitude) / Qam64AxisStep;
        if (magnitude > upper)
            return (magnitude - upper) / Qam64AxisStep;
        return -MathF.Min(magnitude - Qam64InnerThreshold, upper - magnitude) /
               Qam64AxisStep;
    }

    private static sbyte Quantize(float normalized) => (sbyte)Math.Clamp(
        (int)MathF.Round(normalized * MaximumReliability),
        -MaximumReliability,
        MaximumReliability);
}
