using System.Numerics;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Ft8.Dsp;

internal static class TonePowerBank
{
    private const int MaximumTones = 8;

    public static void Measure(ReadOnlySpan<Complex32> samples, ReadOnlySpan<float> window,
        int start, int sampleCount, int stride, int sampleRateHz,
        double firstFrequencyHz, double toneSpacingHz, Span<double> powers)
    {
        if (powers.IsEmpty) return;
        if (powers.Length > MaximumTones)
            throw new ArgumentOutOfRangeException(nameof(powers));
        if (sampleCount < 1 || sampleCount > samples.Length || sampleCount > window.Length)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (stride < 1) throw new ArgumentOutOfRangeException(nameof(stride));
        if (sampleRateHz < 1) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (start < 0 || start > samples.Length - sampleCount)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (!Vector.IsHardwareAccelerated)
        {
            MeasureScalar(samples, window, start, sampleCount, stride, sampleRateHz,
                firstFrequencyHz, toneSpacingHz, powers);
            return;
        }

        int width = Vector<double>.Count;
        int groupCount = (powers.Length + width - 1) / width;
        Span<Vector<double>> rotationReal = stackalloc Vector<double>[groupCount];
        Span<Vector<double>> rotationImaginary = stackalloc Vector<double>[groupCount];
        Span<Vector<double>> oscillatorReal = stackalloc Vector<double>[groupCount];
        Span<Vector<double>> oscillatorImaginary = stackalloc Vector<double>[groupCount];
        Span<Vector<double>> sumReal = stackalloc Vector<double>[groupCount];
        Span<Vector<double>> sumImaginary = stackalloc Vector<double>[groupCount];
        Span<double> lanes = stackalloc double[width];

        for (int group = 0; group < groupCount; group++)
        {
            for (int lane = 0; lane < width; lane++)
            {
                int tone = group * width + lane;
                double frequency = firstFrequencyHz + tone * toneSpacingHz;
                double angle = -2 * Math.PI * frequency * stride / sampleRateHz;
                lanes[lane] = Math.Cos(angle);
            }
            rotationReal[group] = new Vector<double>(lanes);
            for (int lane = 0; lane < width; lane++)
            {
                int tone = group * width + lane;
                double frequency = firstFrequencyHz + tone * toneSpacingHz;
                double angle = -2 * Math.PI * frequency * stride / sampleRateHz;
                lanes[lane] = Math.Sin(angle);
            }
            rotationImaginary[group] = new Vector<double>(lanes);
            oscillatorReal[group] = Vector<double>.One;
            oscillatorImaginary[group] = Vector<double>.Zero;
            sumReal[group] = Vector<double>.Zero;
            sumImaginary[group] = Vector<double>.Zero;
        }

        for (int index = 0; index < sampleCount; index += stride)
        {
            Complex32 sample = samples[start + index];
            var sampleI = new Vector<double>(sample.I);
            var sampleQ = new Vector<double>(sample.Q);
            var weight = new Vector<double>(window[index]);
            for (int group = 0; group < groupCount; group++)
            {
                Vector<double> currentReal = oscillatorReal[group];
                Vector<double> currentImaginary = oscillatorImaginary[group];
                sumReal[group] += weight *
                    (sampleI * currentReal - sampleQ * currentImaginary);
                sumImaginary[group] += weight *
                    (sampleI * currentImaginary + sampleQ * currentReal);
                oscillatorReal[group] = currentReal * rotationReal[group] -
                                        currentImaginary * rotationImaginary[group];
                oscillatorImaginary[group] = currentImaginary * rotationReal[group] +
                                             currentReal * rotationImaginary[group];
            }
        }

        for (int tone = 0; tone < powers.Length; tone++)
        {
            int group = tone / width;
            int lane = tone % width;
            double real = sumReal[group][lane];
            double imaginary = sumImaginary[group][lane];
            powers[tone] = real * real + imaginary * imaginary;
        }
    }

    private static void MeasureScalar(ReadOnlySpan<Complex32> samples,
        ReadOnlySpan<float> window, int start, int sampleCount, int stride,
        int sampleRateHz, double firstFrequencyHz, double toneSpacingHz,
        Span<double> powers)
    {
        Span<double> rotationReal = stackalloc double[powers.Length];
        Span<double> rotationImaginary = stackalloc double[powers.Length];
        Span<double> oscillatorReal = stackalloc double[powers.Length];
        Span<double> oscillatorImaginary = stackalloc double[powers.Length];
        Span<double> sumReal = stackalloc double[powers.Length];
        Span<double> sumImaginary = stackalloc double[powers.Length];
        oscillatorReal.Fill(1);
        oscillatorImaginary.Clear();
        sumReal.Clear();
        sumImaginary.Clear();
        for (int tone = 0; tone < powers.Length; tone++)
        {
            double frequency = firstFrequencyHz + tone * toneSpacingHz;
            double angle = -2 * Math.PI * frequency * stride / sampleRateHz;
            rotationReal[tone] = Math.Cos(angle);
            rotationImaginary[tone] = Math.Sin(angle);
        }

        for (int index = 0; index < sampleCount; index += stride)
        {
            Complex32 sample = samples[start + index];
            double weight = window[index];
            for (int tone = 0; tone < powers.Length; tone++)
            {
                double currentReal = oscillatorReal[tone];
                double currentImaginary = oscillatorImaginary[tone];
                sumReal[tone] += weight *
                    (sample.I * currentReal - sample.Q * currentImaginary);
                sumImaginary[tone] += weight *
                    (sample.I * currentImaginary + sample.Q * currentReal);
                oscillatorReal[tone] = currentReal * rotationReal[tone] -
                                       currentImaginary * rotationImaginary[tone];
                oscillatorImaginary[tone] = currentImaginary * rotationReal[tone] +
                                            currentReal * rotationImaginary[tone];
            }
        }

        for (int tone = 0; tone < powers.Length; tone++)
            powers[tone] = sumReal[tone] * sumReal[tone] +
                           sumImaginary[tone] * sumImaginary[tone];
    }
}
