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
        Span<double> lanes = stackalloc double[width];

        // Keep one tone group's oscillator and sums in registers throughout the
        // sample loop instead of reading/writing six scratch arrays per sample.
        for (int firstTone = 0; firstTone < powers.Length; firstTone += width)
        {
            for (int lane = 0; lane < width; lane++)
            {
                int tone = firstTone + lane;
                double frequency = firstFrequencyHz + tone * toneSpacingHz;
                double angle = -2 * Math.PI * frequency * stride / sampleRateHz;
                lanes[lane] = Math.Cos(angle);
            }
            var rotationReal = new Vector<double>(lanes);
            for (int lane = 0; lane < width; lane++)
            {
                int tone = firstTone + lane;
                double frequency = firstFrequencyHz + tone * toneSpacingHz;
                double angle = -2 * Math.PI * frequency * stride / sampleRateHz;
                lanes[lane] = Math.Sin(angle);
            }
            var rotationImaginary = new Vector<double>(lanes);
            var oscillatorReal = Vector<double>.One;
            var oscillatorImaginary = Vector<double>.Zero;
            var sumReal = Vector<double>.Zero;
            var sumImaginary = Vector<double>.Zero;
            for (int index = 0; index < sampleCount; index += stride)
            {
                Complex32 sample = samples[start + index];
                var sampleI = new Vector<double>(sample.I);
                var sampleQ = new Vector<double>(sample.Q);
                var weight = new Vector<double>(window[index]);
                Vector<double> currentReal = oscillatorReal;
                Vector<double> currentImaginary = oscillatorImaginary;
                sumReal += weight *
                    (sampleI * currentReal - sampleQ * currentImaginary);
                sumImaginary += weight *
                    (sampleI * currentImaginary + sampleQ * currentReal);
                oscillatorReal = currentReal * rotationReal - currentImaginary * rotationImaginary;
                oscillatorImaginary = currentImaginary * rotationReal + currentReal * rotationImaginary;
            }

            for (int lane = 0; lane < Math.Min(width, powers.Length - firstTone); lane++)
            {
                double real = sumReal[lane];
                double imaginary = sumImaginary[lane];
                powers[firstTone + lane] = real * real + imaginary * imaginary;
            }
        }
    }

    private static void MeasureScalar(ReadOnlySpan<Complex32> samples,
        ReadOnlySpan<float> window, int start, int sampleCount, int stride,
        int sampleRateHz, double firstFrequencyHz, double toneSpacingHz,
        Span<double> powers)
    {
        for (int tone = 0; tone < powers.Length; tone++)
        {
            double frequency = firstFrequencyHz + tone * toneSpacingHz;
            double angle = -2 * Math.PI * frequency * stride / sampleRateHz;
            double rotationReal = Math.Cos(angle);
            double rotationImaginary = Math.Sin(angle);
            double oscillatorReal = 1;
            double oscillatorImaginary = 0;
            double sumReal = 0;
            double sumImaginary = 0;
            for (int index = 0; index < sampleCount; index += stride)
            {
                Complex32 sample = samples[start + index];
                double weight = window[index];
                double currentReal = oscillatorReal;
                double currentImaginary = oscillatorImaginary;
                sumReal += weight *
                    (sample.I * currentReal - sample.Q * currentImaginary);
                sumImaginary += weight *
                    (sample.I * currentImaginary + sample.Q * currentReal);
                oscillatorReal = currentReal * rotationReal - currentImaginary * rotationImaginary;
                oscillatorImaginary = currentImaginary * rotationReal + currentReal * rotationImaginary;
            }
            powers[tone] = sumReal * sumReal + sumImaginary * sumImaginary;
        }
    }
}
