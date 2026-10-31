using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SRdeckPlugin.AribT98.Dsp;

internal static class AribT98FirKernel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static double Dot(ReadOnlySpan<double> samples, ReadOnlySpan<double> coefficients)
    {
        // Validate once; all vector loads below stay within this common length.
        coefficients = coefficients[..samples.Length];
        ref double source = ref MemoryMarshal.GetReference(samples);
        ref double taps = ref MemoryMarshal.GetReference(coefficients);
        int index = 0;
        Vector<double> sum = Vector<double>.Zero;
        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<double>.Count;
            // Independent sums let the CPU overlap the long 161/321-tap FIRs.
            Vector<double> sum1 = Vector<double>.Zero, sum2 = Vector<double>.Zero, sum3 = Vector<double>.Zero;
            for (; index <= samples.Length - 4 * width; index += 4 * width)
            {
                sum += Vector.LoadUnsafe(ref source, (nuint)index) * Vector.LoadUnsafe(ref taps, (nuint)index);
                sum1 += Vector.LoadUnsafe(ref source, (nuint)(index + width)) * Vector.LoadUnsafe(ref taps, (nuint)(index + width));
                sum2 += Vector.LoadUnsafe(ref source, (nuint)(index + 2 * width)) * Vector.LoadUnsafe(ref taps, (nuint)(index + 2 * width));
                sum3 += Vector.LoadUnsafe(ref source, (nuint)(index + 3 * width)) * Vector.LoadUnsafe(ref taps, (nuint)(index + 3 * width));
            }
            sum = (sum + sum1) + (sum2 + sum3);
            int limit = samples.Length - width;
            for (; index <= limit; index += width)
                sum += Vector.LoadUnsafe(ref source, (nuint)index) * Vector.LoadUnsafe(ref taps, (nuint)index);
        }

        double result = Vector.Sum(sum);
        for (; index < samples.Length; index++) result += samples[index] * coefficients[index];
        return result;
    }

    public static double[] Reverse(ReadOnlySpan<float> coefficients)
    {
        var reversed = new double[coefficients.Length];
        for (int index = 0; index < coefficients.Length; index++)
            reversed[index] = coefficients[coefficients.Length - 1 - index];
        return reversed;
    }

    public static double[] Reverse(ReadOnlySpan<double> coefficients)
    {
        var reversed = new double[coefficients.Length];
        for (int index = 0; index < coefficients.Length; index++)
            reversed[index] = coefficients[coefficients.Length - 1 - index];
        return reversed;
    }
}
