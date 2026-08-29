using System.Numerics;
using System.Runtime.CompilerServices;

namespace SRdeckPlugin.Acars.Dsp;

public sealed partial class AcarsReceiver
{
    /// <summary>
    /// Measures the 1,200/2,400 Hz correlations used by the frame decoder.
    /// Tone power is the squared magnitude of the unnormalized I/Q correlation.
    /// </summary>
    internal static void MeasureMskTones(ReadOnlySpan<float> window,
        out ToneCorrelation lowTone, out ToneCorrelation highTone)
    {
        lowTone = CorrelateMskTone(window, LowTone);
        highTone = CorrelateMskTone(window, HighTone);
    }

    /// <summary>
    /// Measures the same correlations over a logical window stored in a ring.
    /// The first logical sample is at <paramref name="start"/>.
    /// </summary>
    internal static void MeasureMskTonesRing(ReadOnlySpan<float> ring, int start,
        out ToneCorrelation lowTone, out ToneCorrelation highTone)
    {
        ReadOnlySpan<float> first = ring[start..];
        ReadOnlySpan<float> second = ring[..start];
        lowTone = CorrelateMskToneRing(first, second, LowTone);
        highTone = CorrelateMskToneRing(first, second, HighTone);
    }

    /// <summary>
    /// Returns normalized tone separation: zero for equal tone powers and one
    /// when all correlated power is in one tone.
    /// </summary>
    internal static float CalculateMskToneSeparation(double lowPower, double highPower) =>
        (float)(Math.Abs(lowPower - highPower) /
            Math.Max(lowPower + highPower, 1e-20));

    private static ToneCorrelation CorrelateMskTone(ReadOnlySpan<float> window,
        (float[] I, float[] Q) correlator)
    {
        float i = DotProduct(window, correlator.I);
        float q = DotProduct(window, correlator.Q);
        return new(i, q, i * i + q * q);
    }

    private static ToneCorrelation CorrelateMskToneRing(
        ReadOnlySpan<float> first, ReadOnlySpan<float> second,
        (float[] I, float[] Q) correlator)
    {
        float i = DotProduct(first, correlator.I.AsSpan(0, first.Length)) +
                  DotProduct(second, correlator.I.AsSpan(first.Length));
        float q = DotProduct(first, correlator.Q.AsSpan(0, first.Length)) +
                  DotProduct(second, correlator.Q.AsSpan(first.Length));
        return new(i, q, i * i + q * q);
    }

    private static (float[] I, float[] Q) CreateCorrelator(int frequencyHz)
    {
        var i = new float[SamplesPerBit];
        var q = new float[SamplesPerBit];
        double step = 2 * Math.PI * frequencyHz / DemodulationSampleRateHz;
        for (int n = 0; n < SamplesPerBit; n++)
        {
            i[n] = (float)Math.Cos(step * n);
            q[n] = (float)Math.Sin(step * n);
        }
        return (i, q);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProduct(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        float sum = 0f;
        int index = 0;
        if (Vector.IsHardwareAccelerated && left.Length >= Vector<float>.Count)
        {
            Vector<float> vsum = Vector<float>.Zero;
            int vectorEnd = left.Length - (left.Length % Vector<float>.Count);
            for (; index < vectorEnd; index += Vector<float>.Count)
            {
                Vector<float> vl = new(left.Slice(index, Vector<float>.Count));
                Vector<float> vr = new(right.Slice(index, Vector<float>.Count));
                vsum += vl * vr;
            }
            sum = Vector.Dot(vsum, Vector<float>.One);
        }
        for (; index < left.Length; index++)
        {
            sum += left[index] * right[index];
        }
        return sum;
    }
}
