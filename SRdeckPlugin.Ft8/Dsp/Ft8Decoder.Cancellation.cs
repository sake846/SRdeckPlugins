using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.Protocols;

namespace SRdeckPlugin.Ft8.Dsp;

public sealed partial class Ft8Decoder
{
    private void CancelDecodedSignal(Span<Complex32> residual,
        Ft8Reception reception, long channelCenterFrequencyHz)
    {
        byte[] codeword = Ft8Codec.EncodeCodeword(reception.Payload);
        Span<int> tones = stackalloc int[79];
        for (int group = 0; group < 3; group++)
            Costas.AsSpan().CopyTo(tones[(group * 36)..]);
        for (int symbol = 0; symbol < 58; symbol++)
        {
            int bit = symbol * 3;
            int value = (codeword[bit] << 2) |
                        (codeword[bit + 1] << 1) | codeword[bit + 2];
            int channelSymbol = symbol + (symbol < 29 ? 7 : 14);
            tones[channelSymbol] = Gray[value];
        }

        int start = (int)Math.Round(reception.TimeOffsetSeconds * SampleRateHz);
        double basebandFrequency = reception.FrequencyHz - channelCenterFrequencyHz;
        (start, basebandFrequency) = RefineCancellationSynchronization(
            residual, start, basebandFrequency, tones);
        double windowSum = fineWindow.Sum(value => (double)value);
        for (int symbol = 0; symbol < tones.Length; symbol++)
        {
            int symbolStart = start + symbol * SymbolSamples;
            if (symbolStart < 0 || symbolStart + SymbolSamples > residual.Length) continue;
            double frequency = basebandFrequency + tones[symbol] * 6.25;
            double angle = -2 * Math.PI * frequency / SampleRateHz;
            double rotationReal = Math.Cos(angle);
            double rotationImaginary = Math.Sin(angle);
            double oscillatorReal = 1;
            double oscillatorImaginary = 0;
            double amplitudeReal = 0;
            double amplitudeImaginary = 0;
            for (int index = 0; index < SymbolSamples; index++)
            {
                Complex32 sample = residual[symbolStart + index];
                double weight = fineWindow[index];
                amplitudeReal += weight *
                                 (sample.I * oscillatorReal - sample.Q * oscillatorImaginary);
                amplitudeImaginary += weight *
                                      (sample.I * oscillatorImaginary + sample.Q * oscillatorReal);
                double nextReal = oscillatorReal * rotationReal -
                                  oscillatorImaginary * rotationImaginary;
                oscillatorImaginary = oscillatorImaginary * rotationReal +
                                      oscillatorReal * rotationImaginary;
                oscillatorReal = nextReal;
            }
            amplitudeReal /= windowSum;
            amplitudeImaginary /= windowSum;

            angle = 2 * Math.PI * frequency / SampleRateHz;
            rotationReal = Math.Cos(angle);
            rotationImaginary = Math.Sin(angle);
            oscillatorReal = 1;
            oscillatorImaginary = 0;
            for (int index = 0; index < SymbolSamples; index++)
            {
                int destination = symbolStart + index;
                Complex32 sample = residual[destination];
                // The estimated phasor already represents the unwindowed
                // symbol amplitude.  A tapered, 90%-scaled subtraction leaves
                // a strong copy of an overlapping FT8 signal in the residual.
                float subtractI = (float)(
                    (amplitudeReal * oscillatorReal - amplitudeImaginary * oscillatorImaginary));
                float subtractQ = (float)(
                    (amplitudeReal * oscillatorImaginary + amplitudeImaginary * oscillatorReal));
                residual[destination] = new Complex32(
                    sample.I - subtractI, sample.Q - subtractQ);
                double nextReal = oscillatorReal * rotationReal -
                                  oscillatorImaginary * rotationImaginary;
                oscillatorImaginary = oscillatorImaginary * rotationReal +
                                      oscillatorReal * rotationImaginary;
                oscillatorReal = nextReal;
            }
        }
    }

    private (int Start, double Frequency) RefineCancellationSynchronization(
        ReadOnlySpan<Complex32> samples, int coarseStart, double coarseFrequency,
        ReadOnlySpan<int> tones)
    {
        int bestStart = coarseStart;
        double bestFrequency = coarseFrequency;
        double bestScore = double.NegativeInfinity;

        // Fine reporting synchronization is deliberately cheap and uses only
        // Costas symbols.  Cancellation needs a phase-accurate waveform, so
        // refine against all decoded tones before reconstructing the signal.
        for (int timeAdjustment = -512; timeAdjustment <= 512; timeAdjustment += 128)
        {
            int start = coarseStart + timeAdjustment;
            if (start < 0 || start + tones.Length * SymbolSamples > samples.Length) continue;
            for (int frequencyStep = -2; frequencyStep <= 2; frequencyStep++)
            {
                double frequency = coarseFrequency + frequencyStep * 0.78125;
                double score = 0;
                for (int symbol = 0; symbol < tones.Length; symbol++)
                {
                    int symbolStart = start + symbol * SymbolSamples;
                    double expected = TonePower(samples, symbolStart,
                        frequency + tones[symbol] * 6.25, 2);
                    score += Math.Log(Math.Max(expected, 1e-20));
                }
                if (score <= bestScore) continue;
                bestScore = score;
                bestStart = start;
                bestFrequency = frequency;
            }
        }
        return (bestStart, bestFrequency);
    }

    private static float Max4(float a, float b, float c, float d) =>
        Math.Max(Math.Max(a, b), Math.Max(c, d));

    private static float[] CreateWindow()
    {
        var result = new float[FftSize];
        for (int index = 0; index < result.Length; index++)
        {
            float sine = MathF.Sin(MathF.PI * index / result.Length);
            result[index] = sine * sine;
        }
        return result;
    }

    private static float[] CreateFineWindow()
    {
        var result = new float[SymbolSamples];
        for (int index = 0; index < result.Length; index++)
        {
            float sine = MathF.Sin(MathF.PI * (index + 0.5f) / result.Length);
            result[index] = sine * sine;
        }
        return result;
    }

    private static float[] CreateSnrBaselineWindow()
    {
        var result = new float[SnrBaselineFftSize];
        for (int index = 0; index < result.Length; index++)
        {
            double phase = 2 * Math.PI * index / result.Length;
            result[index] = (float)(0.3635819 - 0.4891775 * Math.Cos(phase) +
                                    0.1365995 * Math.Cos(2 * phase) -
                                    0.0106411 * Math.Cos(3 * phase));
        }
        return result;
    }

    private sealed record WsjtNoiseBaseline(int FirstBin, double BinWidthHz, double[] Power)
    {
        public double GetTonePower(double frequencyHz, double detectorToFftNoiseScale)
        {
            int index = (int)Math.Round(frequencyHz / BinWidthHz) - FirstBin;
            index = Math.Clamp(index, 0, Power.Length - 1);
            return Math.Max(1e-12, Power[index] * detectorToFftNoiseScale);
        }
    }

    private readonly record struct Candidate(
        int Score, int TimeOffset, int FrequencyOffset, int TimeSub, int FrequencySub);

    private sealed record Waterfall(byte[] Magnitude, int ActualBlockCount, int BinCount, int MinimumBin)
    {
        public int BlockCount => ActualBlockCount / TimeOversampling;

        public int Get(int block, int timeSub, int frequencySub, int bin)
        {
            // Each FFT block already represents one half-symbol time step.
            int actualBlock = block * TimeOversampling + timeSub;
            if (actualBlock < 0 || actualBlock >= ActualBlockCount || bin < 0 || bin >= BinCount) return 0;
            return Magnitude[(actualBlock * FrequencyOversampling + frequencySub) * BinCount + bin];
        }
    }
}
