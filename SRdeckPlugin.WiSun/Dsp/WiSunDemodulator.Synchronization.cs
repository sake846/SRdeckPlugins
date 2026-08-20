using SRdeckPlugin.Contracts;
using SRdeckPlugin.WiSun.Models;

namespace SRdeckPlugin.WiSun.Dsp;

/// <summary>
/// Streaming IEEE 802.15.4 SUN-FSK receiver for JP FAN mode #1b and HAN/B Route.
/// The input must be a host-channelized complex baseband stream with 8 samples/bit.
/// </summary>
public sealed partial class WiSunDemodulator
{
    private bool TrySynchronize(
        int start,
        float squelchPower,
        DateTimeOffset timestamp,
        out int polarity,
        out float discriminatorBias,
        out int sfdErrors,
        out ushort matchedSfd,
        out bool newSfd)
    {
        TotalSyncAttempts++;
        polarity = 1;
        discriminatorBias = 0;
        sfdErrors = int.MaxValue;
        matchedSfd = 0;
        newSfd = false;

        int requiredPreambleSamples = RequiredPreambleBits * SamplesPerBit;
        if (start < requiredPreambleSamples) return false;

        int totalSfdSamples = SfdBitCount * SamplesPerBit;
        if (start + totalSfdSamples >= powerPrefix.Count) return false;
        double sfdPower = (powerPrefix[start + totalSfdSamples] - powerPrefix[start]) / totalSfdSamples;
        if (sfdPower < squelchPower) return false;

        // 1. Preamble Lock: Verify preceding 2-byte alternating preamble before evaluating SFD candidate
        int requiredPreambleStart = start - requiredPreambleSamples;
        Span<float> preambleMetrics = stackalloc float[RequiredPreambleBits];
        float preambleSum = 0;
        for (int bit = 0; bit < RequiredPreambleBits; bit++)
        {
            preambleMetrics[bit] = SymbolMetric(requiredPreambleStart + bit * SamplesPerBit);
            preambleSum += preambleMetrics[bit];
        }
        float candidateBias = preambleSum / RequiredPreambleBits;
        int candidatePolarity = preambleMetrics[^1] >= candidateBias ? 1 : -1;
        if (AlternatingSuffixLength(requiredPreambleStart, RequiredPreambleBits, candidatePolarity, candidateBias) < RequiredPreambleBits)
            return false;

        // 2. Synchronized SFD Search: Preamble locked! Test SFD match using locked timing parameters.
        Span<float> metrics = stackalloc float[SfdBitCount];
        for (int bit = 0; bit < SfdBitCount; bit++)
        {
            metrics[bit] = SymbolMetric(start + bit * SamplesPerBit);
        }

        ushort[] sfdsToTest = CustomSfd.HasValue
            ? [CustomSfd.Value, 0x904E, 0x7A0E]
            : UncodedSfds;

        foreach (ushort expectedSfd in sfdsToTest)
        {
            EstimateSfdDecision(metrics, expectedSfd,
                out int testPolarity, out float testBias);
            int errors = CountWordErrorsMsbFirst(
                metrics, expectedSfd, testPolarity, testBias);
            if (errors < sfdErrors)
            {
                polarity = testPolarity;
                discriminatorBias = testBias;
                sfdErrors = errors;
                matchedSfd = expectedSfd;
            }
        }

        ushort observedSfd = ReadWordMsbFirst(metrics, polarity, discriminatorBias);
        if (sfdErrors != 0) return false;

        long absoluteSfdSample = bufferSampleOffset + start;
        newSfd = lastReportedSfdSample == long.MinValue ||
            absoluteSfdSample - lastReportedSfdSample > SamplesPerBit;
        if (!newSfd) return true;
        lastReportedSfdSample = absoluteSfdSample;

        int preambleBytes = Math.Min(
            MaximumCapturedPreambleBytes, start / (8 * SamplesPerBit));
        int preambleBits = preambleBytes * 8;
        int preambleStart = start - preambleBits * SamplesPerBit;
        ulong observedPreamble = ReadBitsMsbFirst(
            preambleStart, preambleBits, polarity, discriminatorBias);
        string preambleHex = preambleBytes == 0
            ? string.Empty
            : observedPreamble.ToString($"X{preambleBytes * 2}");
        string preambleRawBits = ReadBitsStringMsbFirst(
            preambleStart, preambleBits, polarity, discriminatorBias);
        string sfdRawBits = ReadBitsStringMsbFirst(
            start, SfdBitCount, polarity, discriminatorBias);

        LastPreambleWord = (uint)observedPreamble;
        LastPreambleRawHex = preambleHex;
        LastPreambleByteCount = preambleBytes;
        LastSfdWord = observedSfd;
        TotalPreambleMatches++;

        OnDiagnosticLog?.Invoke(
            $"[{timestamp:HH:mm:ss.fff}] [SFD RAW] PRE[{preambleBytes}B]:" +
            $"{(preambleBytes == 0 ? "--" : preambleHex)} | " +
            $"SFD:0x{observedSfd:X4} | RAW:{preambleHex}{observedSfd:X4} | " +
            $"bits:{preambleRawBits}{sfdRawBits} | " +
            $"Polarity:{(polarity > 0 ? "+" : "-")}");
        return true;
    }

    private struct SymbolTimingState
    {
        public double NextSymbolStart;
        public double SamplesPerBit;
        public float Bias;
        public int Polarity;
        public bool HasPreviousDecision;
        public bool PreviousDecision;
        public float ZeroLevel;
        public float OneLevel;
    }

    private SymbolTimingState CreateTimingState(
        int sfdStart,
        ushort matchedSfd,
        int polarity,
        float bias)
    {
        Span<double> transitionBits = stackalloc double[SfdBitCount - 1];
        Span<double> transitionSamples = stackalloc double[SfdBitCount - 1];
        int transitionCount = 0;
        bool previous = (matchedSfd & 0x8000) != 0;
        for (int bit = 1; bit < SfdBitCount; bit++)
        {
            bool current = ((matchedSfd >> (SfdBitCount - 1 - bit)) & 1) != 0;
            if (current != previous &&
                TryFindTransition(sfdStart + bit * SamplesPerBit,
                    SamplesPerBit, polarity, bias, out double crossing))
            {
                transitionBits[transitionCount] = bit;
                transitionSamples[transitionCount] = crossing;
                transitionCount++;
            }
            previous = current;
        }

        double samplesPerBit = SamplesPerBit;
        double alignedSfdStart = sfdStart;
        if (transitionCount >= 2)
        {
            double meanBit = 0;
            double meanSample = 0;
            for (int index = 0; index < transitionCount; index++)
            {
                meanBit += transitionBits[index];
                meanSample += transitionSamples[index];
            }
            meanBit /= transitionCount;
            meanSample /= transitionCount;
            double numerator = 0;
            double denominator = 0;
            for (int index = 0; index < transitionCount; index++)
            {
                double centeredBit = transitionBits[index] - meanBit;
                numerator += centeredBit * (transitionSamples[index] - meanSample);
                denominator += centeredBit * centeredBit;
            }
            if (denominator > 0)
            {
                samplesPerBit = Math.Clamp(numerator / denominator,
                    MinimumTrackedSamplesPerBit, MaximumTrackedSamplesPerBit);
                alignedSfdStart = meanSample - meanBit * samplesPerBit;
            }
        }

        float zeroSum = 0;
        float oneSum = 0;
        int zeroCount = 0;
        int oneCount = 0;
        for (int bit = 0; bit < SfdBitCount; bit++)
        {
            float metric = FractionalSymbolMetric(
                alignedSfdStart + bit * samplesPerBit, samplesPerBit);
            bool expected = ((matchedSfd >> (SfdBitCount - 1 - bit)) & 1) != 0;
            if (expected)
            {
                oneSum += metric;
                oneCount++;
            }
            else
            {
                zeroSum += metric;
                zeroCount++;
            }
        }
        float zeroLevel = zeroCount > 0 ? zeroSum / zeroCount : bias - polarity;
        float oneLevel = oneCount > 0 ? oneSum / oneCount : bias + polarity;

        return new SymbolTimingState
        {
            NextSymbolStart = alignedSfdStart + SfdBitCount * samplesPerBit,
            SamplesPerBit = samplesPerBit,
            Bias = bias,
            Polarity = polarity,
            HasPreviousDecision = true,
            PreviousDecision = (matchedSfd & 1) != 0,
            ZeroLevel = zeroLevel,
            OneLevel = oneLevel
        };
    }

    private bool TryReadTrackedWordMsbFirst(
        ref SymbolTimingState timing,
        out ushort value)
    {
        value = 0;
        for (int bit = 0; bit < PhrBitCount; bit++)
        {
            if (!TryReadTrackedBit(ref timing, out bool decision)) return false;
            value <<= 1;
            if (decision) value |= 1;
        }
        return true;
    }

    private bool TryReadTrackedBytesLsbFirst(
        ref SymbolTimingState timing,
        int count,
        Span<byte> result)
    {
        for (int byteIndex = 0; byteIndex < count; byteIndex++)
            for (int bit = 0; bit < 8; bit++)
            {
                if (!TryReadTrackedBit(ref timing, out bool decision)) return false;
                if (decision)
                    result[byteIndex] |= (byte)(1 << bit);
            }
        return true;
    }

    private bool TryReadTrackedBit(
        ref SymbolTimingState timing,
        out bool decision)
    {
        decision = false;
        if (timing.NextSymbolStart < 0 ||
            timing.NextSymbolStart + timing.SamplesPerBit >= discriminator.Count)
            return false;

        float metric = FractionalSymbolMetric(
            timing.NextSymbolStart, timing.SamplesPerBit);
        decision = timing.Polarity * (metric - timing.Bias) >= 0;

        if (timing.HasPreviousDecision && decision != timing.PreviousDecision &&
            TryFindTransition(timing.NextSymbolStart, timing.SamplesPerBit,
                timing.Polarity, timing.Bias, out double crossing))
        {
            double error = Math.Clamp(
                crossing - timing.NextSymbolStart,
                -timing.SamplesPerBit / 3,
                timing.SamplesPerBit / 3);
            timing.NextSymbolStart += TimingPhaseGain * error;
            timing.SamplesPerBit = Math.Clamp(
                timing.SamplesPerBit + TimingRateGain * error,
                MinimumTrackedSamplesPerBit, MaximumTrackedSamplesPerBit);
        }

        const float levelGain = 0.025f;
        if (decision)
            timing.OneLevel += levelGain * (metric - timing.OneLevel);
        else
            timing.ZeroLevel += levelGain * (metric - timing.ZeroLevel);
        timing.Bias = (timing.OneLevel + timing.ZeroLevel) / 2;
        timing.PreviousDecision = decision;
        timing.HasPreviousDecision = true;
        timing.NextSymbolStart += timing.SamplesPerBit;
        return true;
    }

    private float FractionalSymbolMetric(double start, double samplesPerBit)
    {
        const int integrationPoints = 8;
        float sum = 0;
        for (int point = 0; point < integrationPoints; point++)
        {
            double position = start +
                (point + 0.5) * samplesPerBit / integrationPoints;
            sum += InterpolatedDiscriminator(position);
        }
        return sum / integrationPoints;
    }

    private float InterpolatedDiscriminator(double position)
    {
        int lower = Math.Clamp((int)Math.Floor(position), 0, discriminator.Count - 1);
        int upper = Math.Min(lower + 1, discriminator.Count - 1);
        float fraction = (float)(position - Math.Floor(position));
        return discriminator[lower] +
            fraction * (discriminator[upper] - discriminator[lower]);
    }

    private bool TryFindTransition(
        double predictedBoundary,
        double samplesPerBit,
        int polarity,
        float bias,
        out double crossing)
    {
        crossing = predictedBoundary;
        double radius = Math.Min(3.0, samplesPerBit * 0.4);
        double from = Math.Max(0, predictedBoundary - radius);
        double to = Math.Min(discriminator.Count - 1.001, predictedBoundary + radius);
        bool found = false;
        double bestDistance = double.MaxValue;
        float previous = polarity * (InterpolatedDiscriminator(from) - bias);
        const double step = 0.25;
        for (double position = from + step; position <= to; position += step)
        {
            float current = polarity * (InterpolatedDiscriminator(position) - bias);
            if ((previous < 0 && current >= 0) || (previous >= 0 && current < 0))
            {
                double denominator = Math.Abs(previous) + Math.Abs(current);
                double candidate = denominator > 1e-9
                    ? position - step * Math.Abs(current) / denominator
                    : position - step / 2;
                double distance = Math.Abs(candidate - predictedBoundary);
                if (distance < bestDistance)
                {
                    crossing = candidate;
                    bestDistance = distance;
                    found = true;
                }
            }
            previous = current;
        }
        return found;
    }

    private static void EstimateSfdDecision(
        ReadOnlySpan<float> metrics,
        ushort expected,
        out int polarity,
        out float bias)
    {
        float zeroSum = 0;
        float oneSum = 0;
        int zeroCount = 0;
        int oneCount = 0;
        for (int bit = 0; bit < SfdBitCount; bit++)
        {
            if (((expected >> (SfdBitCount - 1 - bit)) & 1) != 0)
            {
                oneSum += metrics[bit];
                oneCount++;
            }
            else
            {
                zeroSum += metrics[bit];
                zeroCount++;
            }
        }
        float zeroMean = zeroSum / zeroCount;
        float oneMean = oneSum / oneCount;
        polarity = oneMean >= zeroMean ? 1 : -1;
        bias = (zeroMean + oneMean) / 2;
    }


    private float SymbolMetric(int start)
    {
        return (float)((discriminatorPrefix[start + SamplesPerBit] -
            discriminatorPrefix[start]) / SamplesPerBit);
    }

    private float SymbolPower(int start)
    {
        return (float)((powerPrefix[start + SamplesPerBit] -
            powerPrefix[start]) / SamplesPerBit);
    }

    private static int CountWordErrorsMsbFirst(
        ReadOnlySpan<float> metrics,
        ushort expected,
        int polarity,
        float bias)
    {
        int errors = 0;
        for (int bit = 0; bit < SfdBitCount; bit++)
        {
            bool actual = polarity * (metrics[bit] - bias) >= 0;
            bool wanted = ((expected >> (SfdBitCount - 1 - bit)) & 1) != 0;
            if (actual != wanted) errors++;
        }
        return errors;
    }

    private static ushort ReadWordMsbFirst(
        ReadOnlySpan<float> metrics,
        int polarity,
        float bias)
    {
        ushort value = 0;
        for (int bit = 0; bit < SfdBitCount; bit++)
        {
            value <<= 1;
            if (polarity * (metrics[bit] - bias) >= 0) value |= 1;
        }
        return value;
    }

    private ulong ReadBitsMsbFirst(int start, int bitCount, int polarity, float bias)
    {
        ulong value = 0;
        for (int bit = 0; bit < bitCount; bit++)
        {
            value <<= 1;
            if (polarity * (SymbolMetric(start + bit * SamplesPerBit) - bias) >= 0) value |= 1UL;
        }
        return value;
    }

    private string ReadBitsStringMsbFirst(int start, int bitCount, int polarity, float bias)
    {
        var result = new char[bitCount];
        for (int bit = 0; bit < bitCount; bit++)
            result[bit] = polarity *
                (SymbolMetric(start + bit * SamplesPerBit) - bias) >= 0 ? '1' : '0';
        return new string(result);
    }

    private int AlternatingSuffixLength(int start, int bitCount, int polarity, float bias)
    {
        if (bitCount == 0) return 0;
        bool previous = polarity *
            (SymbolMetric(start + (bitCount - 1) * SamplesPerBit) - bias) >= 0;
        int length = 1;
        for (int bit = bitCount - 2; bit >= 0; bit--)
        {
            bool current = polarity *
                (SymbolMetric(start + bit * SamplesPerBit) - bias) >= 0;
            if (current == previous) break;
            previous = current;
            length++;
        }
        return length;
    }

    private byte[] ReadBytesLsbFirst(int start, int count, int polarity, float bias)
    {
        var result = new byte[count];
        for (int byteIndex = 0; byteIndex < count; byteIndex++)
            for (int bit = 0; bit < 8; bit++)
                if (polarity * (SymbolMetric(start + (byteIndex * 8 + bit) * SamplesPerBit) - bias) >= 0)
                    result[byteIndex] |= (byte)(1 << bit);
        return result;
    }
}
