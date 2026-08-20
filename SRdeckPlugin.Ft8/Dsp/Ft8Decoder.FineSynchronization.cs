using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.Protocols;

namespace SRdeckPlugin.Ft8.Dsp;

public sealed partial class Ft8Decoder
{
    private bool TryFineDecode(ReadOnlySpan<Complex32> samples, Waterfall waterfall,
        Candidate candidate, Span<float> likelihood, int ldpcIterations,
        out Ft8Codec.DecodedMessage? message, out int parityErrors, out bool crcValid,
        out double basebandFrequency, out double timeOffset)
    {
        double coarseFrequency = (waterfall.MinimumBin + candidate.FrequencyOffset +
                                  candidate.FrequencySub / (double)FrequencyOversampling) * 6.25;
        int coarseStart = (candidate.TimeOffset * TimeOversampling + candidate.TimeSub) *
                          HopSamples + SymbolSamples;
        (int refinedStart, double refinedFrequency) =
            RefineSynchronization(samples, coarseStart, coarseFrequency);
        ExtractFineLikelihood(samples, refinedStart, refinedFrequency, likelihood);
        basebandFrequency = refinedFrequency;
        timeOffset = refinedStart / (double)SampleRateHz;
        if (!Normalize(likelihood))
        {
            message = null;
            parityErrors = int.MaxValue;
            crcValid = false;
            return false;
        }
        return codec.TryDecode(likelihood, ldpcIterations, out message,
            out parityErrors, out crcValid);
    }

    private (int Start, double Frequency) RefineSynchronization(
        ReadOnlySpan<Complex32> samples, int coarseStart, double coarseFrequency)
    {
        int bestStart = coarseStart;
        double bestFrequency = coarseFrequency;
        double bestScore = double.NegativeInfinity;
        for (int timeAdjustment = -512; timeAdjustment <= 512; timeAdjustment += 256)
        {
            int start = coarseStart + timeAdjustment;
            if (start < 0 || start + 79 * SymbolSamples > samples.Length) continue;
            for (int frequencyStep = -2; frequencyStep <= 2; frequencyStep++)
            {
                double frequency = coarseFrequency + frequencyStep * 0.78125;
                double score = FineSyncScore(samples, start, frequency);
                if (score <= bestScore) continue;
                bestScore = score;
                bestStart = start;
                bestFrequency = frequency;
            }
        }
        return (bestStart, bestFrequency);
    }

    private double FineSyncScore(ReadOnlySpan<Complex32> samples,
        int start, double basebandFrequency)
    {
        double score = 0;
        for (int group = 0; group < 3; group++)
        for (int index = 0; index < Costas.Length; index++)
        {
            int symbol = group * 36 + index;
            double frequency = basebandFrequency + Costas[index] * 6.25;
            int symbolStart = start + symbol * SymbolSamples;
            double expected = TonePower(samples, symbolStart, frequency, 2);
            double lower = TonePower(samples, symbolStart, frequency - 6.25, 2);
            double upper = TonePower(samples, symbolStart, frequency + 6.25, 2);
            score += Math.Log(Math.Max(expected, 1e-20)) -
                     0.5 * (Math.Log(Math.Max(lower, 1e-20)) +
                            Math.Log(Math.Max(upper, 1e-20)));
        }
        return score;
    }

    private void ExtractFineLikelihood(ReadOnlySpan<Complex32> samples,
        int start, double basebandFrequency, Span<float> likelihood)
    {
        Span<float> mapped = stackalloc float[8];
        Span<float> toneDb = stackalloc float[8];
        for (int symbol = 0; symbol < 58; symbol++)
        {
            int channelSymbol = symbol + (symbol < 29 ? 7 : 14);
            int symbolStart = start + channelSymbol * SymbolSamples;
            int bit = symbol * 3;
            if (symbolStart < 0 || symbolStart + SymbolSamples > samples.Length)
            {
                likelihood.Slice(bit, 3).Clear();
                continue;
            }
            for (int tone = 0; tone < 8; tone++)
            {
                double power = TonePower(samples, symbolStart,
                    basebandFrequency + tone * 6.25, 1);
                toneDb[tone] = (float)(10 * Math.Log10(Math.Max(power, 1e-20)));
            }
            for (int value = 0; value < 8; value++) mapped[value] = toneDb[Gray[value]];
            likelihood[bit] = Max4(mapped[4], mapped[5], mapped[6], mapped[7]) -
                              Max4(mapped[0], mapped[1], mapped[2], mapped[3]);
            likelihood[bit + 1] = Max4(mapped[2], mapped[3], mapped[6], mapped[7]) -
                                  Max4(mapped[0], mapped[1], mapped[4], mapped[5]);
            likelihood[bit + 2] = Max4(mapped[1], mapped[3], mapped[5], mapped[7]) -
                                  Max4(mapped[0], mapped[2], mapped[4], mapped[6]);
        }
    }

    private double TonePower(ReadOnlySpan<Complex32> samples,
        int start, double frequency, int stride)
    {
        double angle = -2 * Math.PI * frequency * stride / SampleRateHz;
        double rotationReal = Math.Cos(angle);
        double rotationImaginary = Math.Sin(angle);
        double oscillatorReal = 1;
        double oscillatorImaginary = 0;
        double sumReal = 0;
        double sumImaginary = 0;
        for (int index = 0; index < SymbolSamples; index += stride)
        {
            Complex32 sample = samples[start + index];
            double weight = fineWindow[index];
            sumReal += weight * (sample.I * oscillatorReal - sample.Q * oscillatorImaginary);
            sumImaginary += weight * (sample.I * oscillatorImaginary + sample.Q * oscillatorReal);
            double nextReal = oscillatorReal * rotationReal - oscillatorImaginary * rotationImaginary;
            oscillatorImaginary = oscillatorImaginary * rotationReal + oscillatorReal * rotationImaginary;
            oscillatorReal = nextReal;
        }
        return sumReal * sumReal + sumImaginary * sumImaginary;
    }

    private int CalculateSnr(ReadOnlySpan<Complex32> samples, double basebandFrequency,
        double timeOffsetSeconds, byte[] payload, WsjtNoiseBaseline noiseBaseline)
    {
        byte[] codeword = Ft8Codec.EncodeCodeword(payload);
        Span<int> tones = stackalloc int[79];
        for (int group = 0; group < 3; group++)
            Costas.AsSpan().CopyTo(tones[(group * 36)..]);
        for (int symbol = 0; symbol < 58; symbol++)
        {
            int bit = symbol * 3;
            int value = (codeword[bit] << 2) | (codeword[bit + 1] << 1) | codeword[bit + 2];
            int channelSymbol = symbol + (symbol < 29 ? 7 : 14);
            tones[channelSymbol] = Gray[value];
        }

        int startSample = (int)Math.Round(timeOffsetSeconds * SampleRateHz);
        double activePowerSum = 0;
        int evaluatedSymbols = 0;

        for (int symbol = 0; symbol < tones.Length; symbol++)
        {
            int symbolStart = startSample + symbol * SymbolSamples;
            if (symbolStart < 0 || symbolStart + SymbolSamples > samples.Length)
                continue;

            int activeTone = tones[symbol];
            activePowerSum += TonePower(samples, symbolStart,
                basebandFrequency + activeTone * 6.25, 1);
            evaluatedSymbols++;
        }

        if (evaluatedSymbols == 0)
            return WsjtMinimumSnrDb;

        // Compare the decoded tone-power sum with a lower-envelope spectrum
        // baseline, then convert to the FT8 2500 Hz reference bandwidth.
        double avgActivePower = activePowerSum / evaluatedSymbols;
        double noisePower = noiseBaseline.GetTonePower(basebandFrequency,
            fineWindowEnergy / snrBaselineWindowEnergy);
        double signalToNoise = Math.Max(0.001, avgActivePower / noisePower - 1.0);
        double snrDb = 10 * Math.Log10(signalToNoise) - WsjtSnrCalibrationDb;
        return Math.Max(WsjtMinimumSnrDb, (int)Math.Round(snrDb));
    }

    private WsjtNoiseBaseline CreateWsjtNoiseBaseline(ReadOnlySpan<Complex32> samples)
    {
        const int minimumBin = AudioMinimumHz - AudioCenterHz;
        const int maximumBin = AudioMaximumHz - AudioCenterHz;
        int firstBin = (int)Math.Ceiling(minimumBin / SnrBaselineBinWidthHz);
        int lastBin = (int)Math.Floor(maximumBin / SnrBaselineBinWidthHz);
        int binCount = lastBin - firstBin + 1;
        var averagePower = new double[binCount];
        int frameCount = 0;
        int usableSamples = Math.Min(samples.Length, SlotSamples);

        for (int start = 0; start + SnrBaselineFftSize <= usableSamples; start += SymbolSamples)
        {
            for (int index = 0; index < SnrBaselineFftSize; index++)
            {
                Complex32 sample = samples[start + index];
                float weight = snrBaselineWindow[index];
                snrBaselineReal[index] = sample.I * weight;
                snrBaselineImaginary[index] = sample.Q * weight;
            }
            snrBaselineFft.Transform(snrBaselineReal, snrBaselineImaginary);
            for (int index = 0; index < binCount; index++)
            {
                int bin = firstBin + index;
                int fftBin = bin < 0 ? SnrBaselineFftSize + bin : bin;
                double realValue = snrBaselineReal[fftBin];
                double imaginaryValue = snrBaselineImaginary[fftBin];
                averagePower[index] += realValue * realValue + imaginaryValue * imaginaryValue;
            }
            frameCount++;
        }

        if (frameCount == 0)
            return new(firstBin, SnrBaselineBinWidthHz, [1e-12]);

        for (int index = 0; index < averagePower.Length; index++)
            averagePower[index] /= frameCount;
        return new(firstBin, SnrBaselineBinWidthHz,
            FitWsjtLowerEnvelope(averagePower));
    }

    private static double[] FitWsjtLowerEnvelope(double[] averagePower)
    {
        const int segments = 10;
        var x = new List<double>();
        var y = new List<double>();
        double center = (averagePower.Length - 1) * 0.5;
        for (int segment = 0; segment < segments; segment++)
        {
            int start = segment * averagePower.Length / segments;
            int end = (segment + 1) * averagePower.Length / segments;
            if (end <= start) continue;
            double[] values = averagePower[start..end]
                .Select(power => 10 * Math.Log10(Math.Max(power, 1e-20))).ToArray();
            Array.Sort(values);
            double threshold = values[Math.Min(values.Length - 1, values.Length / 10)];
            for (int index = start; index < end; index++)
            {
                double value = 10 * Math.Log10(Math.Max(averagePower[index], 1e-20));
                if (value > threshold) continue;
                x.Add((index - center) / center);
                y.Add(value);
            }
        }

        double[] coefficients = FitPolynomial(x, y);
        var baseline = new double[averagePower.Length];
        for (int index = 0; index < baseline.Length; index++)
        {
            double normalizedIndex = (index - center) / center;
            double value = coefficients[4];
            for (int degree = 3; degree >= 0; degree--)
                value = value * normalizedIndex + coefficients[degree];
            baseline[index] = Math.Pow(10, (value + 0.65) / 10.0);
        }
        return baseline;
    }

    private static double[] FitPolynomial(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        const int terms = 5;
        var matrix = new double[terms, terms + 1];
        Span<double> powers = stackalloc double[terms];
        for (int row = 0; row < x.Count; row++)
        {
            powers[0] = 1;
            for (int degree = 1; degree < terms; degree++) powers[degree] = powers[degree - 1] * x[row];
            for (int left = 0; left < terms; left++)
            {
                matrix[left, terms] += powers[left] * y[row];
                for (int right = 0; right < terms; right++)
                    matrix[left, right] += powers[left] * powers[right];
            }
        }
        for (int column = 0; column < terms; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < terms; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column])) pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-12)
                return [0, 0, 0, 0, 0];
            for (int index = column; index <= terms; index++)
                (matrix[column, index], matrix[pivot, index]) = (matrix[pivot, index], matrix[column, index]);
            double divisor = matrix[column, column];
            for (int index = column; index <= terms; index++) matrix[column, index] /= divisor;
            for (int row = 0; row < terms; row++)
            {
                if (row == column) continue;
                double factor = matrix[row, column];
                for (int index = column; index <= terms; index++)
                    matrix[row, index] -= factor * matrix[column, index];
            }
        }
        return Enumerable.Range(0, terms).Select(row => matrix[row, terms]).ToArray();
    }
}
