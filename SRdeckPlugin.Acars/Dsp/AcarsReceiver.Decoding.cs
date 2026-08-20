using System.Numerics;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Acars.Dsp;

/// <summary>Streaming VHF ACARS AM-MSK receiver.</summary>
public sealed partial class AcarsReceiver
{
    private IReadOnlyList<AcarsFrame> Decode(IqBlockMetadata metadata)
    {
        var result = new List<AcarsFrame>();
        float bestRecentMeanConfidence = 0;
        float bestRecentSquelchMetric = 0;
        ReadOnlySpan<float> audioSpan = audioBuffer.AsSpan(0, audioCount);
        for (int phase = 0; phase < SamplesPerBit; phase++)
        {
            int symbolCount = (audioCount - phase) / SamplesPerBit;
            if (symbolCount < 8 * 18) continue;
            EnsureCapacity(symbolCount);
            for (int symbol = 0; symbol < symbolCount; symbol++)
            {
                MeasureTones(audioSpan, phase + symbol * SamplesPerBit,
                    out lowCorrelations[symbol], out highCorrelations[symbol],
                    out confidenceBuffer[symbol]);
            }
            int recentSymbolCount = Math.Min(symbolCount,
                DecodeIntervalSamples / SamplesPerBit);
            double recentConfidenceSum = 0;
            for (int index = symbolCount - recentSymbolCount;
                 index < symbolCount; index++)
            {
                recentConfidenceSum += confidenceBuffer[index];
            }
            float recentMeanConfidence = recentSymbolCount == 0 ? 0 :
                (float)(recentConfidenceSum / recentSymbolCount);
            bestRecentMeanConfidence = MathF.Max(
                bestRecentMeanConfidence, recentMeanConfidence);
            double recentSquelchSum = 0;
            double recentLowPower = 0;
            double recentHighPower = 0;
            for (int index = symbolCount - recentSymbolCount;
                 index < symbolCount; index++)
            {
                recentSquelchSum += MeasureSquelchMetric(
                    audioSpan,
                    phase + index * SamplesPerBit,
                    lowCorrelations[index].Power,
                    highCorrelations[index].Power,
                    confidenceBuffer[index]);
                recentLowPower += lowCorrelations[index].Power;
                recentHighPower += highCorrelations[index].Power;
            }
            float recentMeanSquelchMetric = recentSymbolCount == 0 ? 0 :
                (float)(recentSquelchSum / recentSymbolCount);
            double toneBalance = 2 * Math.Min(recentLowPower, recentHighPower) /
                Math.Max(recentLowPower + recentHighPower, 1e-20);
            double toneDiversity = Math.Clamp(toneBalance / 0.25, 0, 1);
            recentMeanSquelchMetric *= (float)toneDiversity;
            bestRecentSquelchMetric = MathF.Max(
                bestRecentSquelchMetric, recentMeanSquelchMetric);

            // Use a soft sequence detector which rewards both tone energy and
            // the continuous MSK phase trajectory.
            ReadOnlySpan<bool> coherentTones = DecodeCoherentToneSequence(
                lowCorrelations.AsSpan(0, symbolCount),
                highCorrelations.AsSpan(0, symbolCount),
                predecessorsBuffer, tonesBuffer);
            DecodeToneSequence(coherentTones, confidenceBuffer.AsSpan(0, symbolCount), phase, metadata, result);
        }
        lastToneConfidence = bestRecentMeanConfidence;
        lastMskSquelchMetric = bestRecentSquelchMetric;
        UpdateMskSquelch(bestRecentSquelchMetric);
        decodePassCount++;
        return result;
    }

    private void EnsureCapacity(int symbolCount)
    {
        if (symbolCount > lowCorrelations.Length)
        {
            Array.Resize(ref lowCorrelations, symbolCount * 2);
            Array.Resize(ref highCorrelations, symbolCount * 2);
            Array.Resize(ref confidenceBuffer, symbolCount * 2);
            Array.Resize(ref predecessorsBuffer, symbolCount * 4);
            Array.Resize(ref tonesBuffer, symbolCount * 2);
            Array.Resize(ref bitsBuffer, symbolCount * 2);
        }
    }

    private void UpdateMskSquelch(float separation)
    {
        if (separation < MskSquelchOpenThreshold) return;
        isMskSquelchOpen = true;
        mskSquelchHoldSamples = MskSquelchHoldSamples;
    }

    private void ProcessFastMskSquelch(float sample)
    {
        if (mskSquelchHoldSamples > 0 && --mskSquelchHoldSamples == 0)
            isMskSquelchOpen = false;

        // Measure half-cycle lengths before the correlation hop. At 48 ksps
        // they are 20 samples for 1,200 Hz and 10 samples for 2,400 Hz.
        // Requiring both lengths rejects a continuous tone without delaying
        // a real alternating MSK preamble until the 100 ms decoder pass.
        fastMskLowToneAge = Math.Min(10_000, fastMskLowToneAge + 1);
        fastMskHighToneAge = Math.Min(10_000, fastMskHighToneAge + 1);
        fastMskCrossingInterval = Math.Min(10_000, fastMskCrossingInterval + 1);
        const float crossingThreshold = 0.002f;
        int crossingSign = sample > crossingThreshold ? 1 :
            sample < -crossingThreshold ? -1 : 0;
        if (crossingSign != 0)
        {
            if (fastMskCrossingSign == 0)
            {
                fastMskCrossingSign = crossingSign;
                fastMskCrossingInterval = 0;
            }
            else if (crossingSign != fastMskCrossingSign)
            {
                int interval = fastMskCrossingInterval;
                fastMskCrossingSign = crossingSign;
                fastMskCrossingInterval = 0;
                if (interval is >= 16 and <= 24) fastMskLowToneAge = 0;
                else if (interval is >= 7 and <= 13) fastMskHighToneAge = 0;
            }
        }

        float previousSample = fastMskWindow[fastMskWindowPosition];
        fastMskWindowPower += sample * sample - previousSample * previousSample;
        fastMskWindow[fastMskWindowPosition] = sample;
        if (++fastMskWindowPosition == SamplesPerBit) fastMskWindowPosition = 0;
        if (fastMskWindowCount < SamplesPerBit)
        {
            fastMskWindowCount++;
            if (fastMskWindowCount < SamplesPerBit) return;
        }
        if (++fastMskHopCount < 10) return;
        fastMskHopCount = 0;

        ReadOnlySpan<float> p1 = fastMskWindow.AsSpan(fastMskWindowPosition);
        ReadOnlySpan<float> p2 = fastMskWindow.AsSpan(0, fastMskWindowPosition);
        float i1200 = DotProduct(p1, LowTone.I.AsSpan(0, p1.Length)) +
                      DotProduct(p2, LowTone.I.AsSpan(p1.Length));
        float q1200 = DotProduct(p1, LowTone.Q.AsSpan(0, p1.Length)) +
                      DotProduct(p2, LowTone.Q.AsSpan(p1.Length));
        float i2400 = DotProduct(p1, HighTone.I.AsSpan(0, p1.Length)) +
                      DotProduct(p2, HighTone.I.AsSpan(p1.Length));
        float q2400 = DotProduct(p1, HighTone.Q.AsSpan(0, p1.Length)) +
                      DotProduct(p2, HighTone.Q.AsSpan(p1.Length));
        double lowPower = i1200 * i1200 + q1200 * q1200;
        double highPower = i2400 * i2400 + q2400 * q2400;
        float separation = (float)(Math.Abs(lowPower - highPower) /
            Math.Max(lowPower + highPower, 1e-20));
        float metric = CalculateSquelchMetric(
            lowPower, highPower, separation, Math.Max(fastMskWindowPower, 0));
        lastToneConfidence = MathF.Max(lastToneConfidence, separation);
        lastMskSquelchMetric = MathF.Max(lastMskSquelchMetric, metric);
        fastMskMetricSum -= fastMskMetrics[fastMskMetricPosition];
        fastMskMetrics[fastMskMetricPosition] = metric;
        fastMskMetricSum += metric;
        if (++fastMskMetricPosition == FastMskMetricWindowHops)
            fastMskMetricPosition = 0;
        if (fastMskMetricCount < FastMskMetricWindowHops)
            fastMskMetricCount++;
        float averageMetric = fastMskMetricSum / fastMskMetricCount;
        // Both MSK tones must occur within about 20 ms. A continuous birdie at
        // either 1,200 or 2,400 Hz must not hold the squelch open. Requiring an
        // 8 ms average also prevents isolated noise peaks from refreshing the
        // hang timer indefinitely.
        if (fastMskMetricCount < FastMskMetricWindowHops ||
            averageMetric < FastMskAverageOpenThreshold ||
            fastMskLowToneAge > 960 || fastMskHighToneAge > 960)
            return;
        isMskSquelchOpen = true;
        mskSquelchHoldSamples = MskSquelchHoldSamples;
    }

    private void DecodeToneSequence(ReadOnlySpan<bool> tones, ReadOnlySpan<float> confidence, int phase,
        IqBlockMetadata metadata, List<AcarsFrame> result)
    {
        if (bitsBuffer.Length < tones.Length)
            Array.Resize(ref bitsBuffer, tones.Length * 2);
        Span<bool> bits = bitsBuffer.AsSpan(0, tones.Length);
        // ACARS is NRZI. Different SDR I/Q conventions and AM detector
        // polarity can interchange the apparent 1200/2400 Hz states.
        for (int transitionTonePolarity = 0; transitionTonePolarity < 2; transitionTonePolarity++)
        {
            bool state = false;
            for (int symbol = 0; symbol < tones.Length; symbol++)
            {
                if (tones[symbol] ^ (transitionTonePolarity != 0)) state = !state;
                bits[symbol] = state;
            }
            for (int bitOffset = 0; bitOffset < 8; bitOffset++)
            {
                byte[] bytes = Pack(bits, bitOffset, invert: false);
                Scan(bytes, confidence, phase, bitOffset, metadata, result);
                bytes = Pack(bits, bitOffset, invert: true);
                Scan(bytes, confidence, phase, bitOffset, metadata, result);
            }
        }
    }

    internal readonly record struct ToneCorrelation(double I, double Q, double Power);

    internal static bool[] DecodeCoherentToneSequence(
        ReadOnlySpan<ToneCorrelation> low, ReadOnlySpan<ToneCorrelation> high)
    {
        if (low.Length != high.Length) throw new ArgumentException("Tone correlation lengths differ.");
        if (low.IsEmpty) return [];
        var predecessors = new byte[low.Length * 2];
        var tones = new bool[low.Length];
        DecodeCoherentToneSequence(low, high, predecessors, tones);
        return tones;
    }

    internal static Span<bool> DecodeCoherentToneSequence(
        ReadOnlySpan<ToneCorrelation> low, ReadOnlySpan<ToneCorrelation> high,
        Span<byte> predecessors, Span<bool> tones)
    {
        if (low.Length != high.Length) throw new ArgumentException("Tone correlation lengths differ.");
        if (low.IsEmpty) return Span<bool>.Empty;
        double highScore = Emission(high[0], low[0].Power + high[0].Power);
        double lowScore = Emission(low[0], low[0].Power + high[0].Power);
        for (int symbol = 1; symbol < low.Length; symbol++)
        {
            double totalPower = low[symbol].Power + high[symbol].Power;
            double highEmission = Emission(high[symbol], totalPower);
            double lowEmission = Emission(low[symbol], totalPower);
            double highFromHigh = highScore + PhaseScore(high[symbol - 1], high[symbol], false);
            double highFromLow = lowScore + PhaseScore(low[symbol - 1], high[symbol], true);
            double lowFromHigh = highScore + PhaseScore(high[symbol - 1], low[symbol], false);
            double lowFromLow = lowScore + PhaseScore(low[symbol - 1], low[symbol], true);
            bool highPreviousLow = highFromLow > highFromHigh;
            bool lowPreviousLow = lowFromLow > lowFromHigh;
            predecessors[symbol * 2] = highPreviousLow ? (byte)1 : (byte)0;
            predecessors[symbol * 2 + 1] = lowPreviousLow ? (byte)1 : (byte)0;
            highScore = Math.Max(highFromHigh, highFromLow) + highEmission;
            lowScore = Math.Max(lowFromHigh, lowFromLow) + lowEmission;
        }

        int state = lowScore > highScore ? 1 : 0;
        for (int symbol = low.Length - 1; symbol >= 0; symbol--)
        {
            tones[symbol] = state != 0;
            if (symbol > 0) state = predecessors[symbol * 2 + state];
        }
        return tones.Slice(0, low.Length);

        static double Emission(ToneCorrelation correlation, double totalPower) =>
            Math.Log((correlation.Power + 1e-20) / Math.Max(totalPower, 1e-20));

        static double PhaseScore(ToneCorrelation previous, ToneCorrelation current, bool previousWasLow)
        {
            double magnitude = Math.Sqrt(previous.Power * current.Power);
            if (magnitude < 1e-20) return 0;
            double phaseCoherence = (previous.I * current.I + previous.Q * current.Q) / magnitude;
            // Over one 2400-baud symbol, 1200 Hz advances by pi while
            // 2400 Hz advances by 2*pi. Correlation phase therefore flips only
            // after the low tone.
            if (previousWasLow) phaseCoherence = -phaseCoherence;
            return 0.75 * Math.Clamp(phaseCoherence, -1, 1);
        }
    }

    private void Scan(byte[] bytes, ReadOnlySpan<float> confidence, int phase, int bitOffset,
        IqBlockMetadata metadata, List<AcarsFrame> result)
    {
        for (int index = 0; index + 18 <= bytes.Length; index++)
        {
            if ((bytes[index] & 0x7f) != 0x16 || (bytes[index + 1] & 0x7f) != 0x16 ||
                (bytes[index + 2] & 0x7f) != 0x01) continue;
            int end = index + 15;
            while (end < bytes.Length && (bytes[end] & 0x7f) is not (0x03 or 0x17)) end++;
            if (end + 2 >= bytes.Length) continue;
            byte[] raw = bytes[index..(end + 3)];
            if (!AcarsMessageParser.TryParse(raw, out AcarsMessage? parsed) || parsed is null) continue;
            long demodOffset = phase + ((long)bitOffset + index * 8) * SamplesPerBit;
            long position = audioSampleStart + (long)Math.Round(demodOffset * (double)inputSampleRate / DemodulationSampleRateHz);
            if (position <= lastFramePosition + inputSampleRate / 20) continue;
            ReadOnlySpan<float> confidenceSlice = confidence.Slice(bitOffset + index * 8, raw.Length * 8);
            double quality = confidenceSlice.IsEmpty ? 0 : Average(confidenceSlice);
            if (!parsed.IsBlockCheckValid &&
                TryCorrectSingleBit(raw, out byte[] corrected, out AcarsMessage? correctedMessage))
            {
                raw = corrected;
                parsed = correctedMessage!;
            }
            if (!parsed.IsBlockCheckValid)
            {
                if (position > lastCandidatePosition + inputSampleRate / 20)
                {
                    lastCandidatePosition = position;
                    RejectedFrameCount++;
                }
                continue;
            }
            // All twenty sampling phases inspect the same transmission. A
            // marginal phase can produce a parseable header with a bad BCS
            // before the optimum phase is visited. Do not count that one
            // over-the-air frame as both rejected and valid.
            long duplicateWindow = inputSampleRate / 20;
            if (RejectedFrameCount > 0 &&
                lastCandidatePosition > lastFramePosition + duplicateWindow &&
                Math.Abs(position - lastCandidatePosition) <= duplicateWindow)
            {
                RejectedFrameCount--;
            }
            // A decoded BCS is required on air. Parity is retained as a
            // diagnostic because some SDR recordings have already stripped it.
            result.Add(new(raw, metadata.UtcTimestamp, metadata.StreamId, position,
                targetFrequencyHz, Math.Clamp(quality, 0, 1)));
            lastFramePosition = position;
            lastCandidatePosition = Math.Max(lastCandidatePosition, position);
            ValidFrameCount++;
        }
    }

    private void MeasureTones(ReadOnlySpan<float> audioSpan, int offset, out ToneCorrelation lowTone,
        out ToneCorrelation highTone, out float confidence)
    {
        (float i1200, float q1200) = Correlate(audioSpan, offset, LowTone);
        (float i2400, float q2400) = Correlate(audioSpan, offset, HighTone);
        float low = i1200 * i1200 + q1200 * q1200;
        float high = i2400 * i2400 + q2400 * q2400;
        lowTone = new(i1200, q1200, low);
        highTone = new(i2400, q2400, high);
        confidence = Math.Abs(low - high) / Math.Max(low + high, 1e-20f);
    }

    private float MeasureSquelchMetric(
        ReadOnlySpan<float> audioSpan, int offset, double lowPower, double highPower, float confidence)
    {
        ReadOnlySpan<float> window = audioSpan.Slice(offset, SamplesPerBit);
        float windowPower = SumOfSquares(window);
        return CalculateSquelchMetric(lowPower, highPower, confidence, windowPower);
    }

    private static float CalculateSquelchMetric(
        double lowPower, double highPower, float confidence, double windowPower)
    {
        double tonePresence = (lowPower + highPower) /
            Math.Max(windowPower * SamplesPerBit * 0.5, 1e-20);
        const double minimumFullScaleToneRms = 0.015;
        double windowRms = Math.Sqrt(windowPower / SamplesPerBit);
        double amplitudePresence = Math.Clamp(windowRms / minimumFullScaleToneRms, 0, 1);
        return confidence * (float)(
            Math.Clamp(tonePresence, 0, 1) * amplitudePresence);
    }

    private static (float I, float Q) Correlate(ReadOnlySpan<float> audioSpan, int offset, (float[] I, float[] Q) correlator)
    {
        ReadOnlySpan<float> window = audioSpan.Slice(offset, SamplesPerBit);
        float i = DotProduct(window, correlator.I);
        float q = DotProduct(window, correlator.Q);
        return (i, q);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SumOfSquares(ReadOnlySpan<float> span)
    {
        float sum = 0f;
        int index = 0;
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            Vector<float> vsum = Vector<float>.Zero;
            int vectorEnd = span.Length - (span.Length % Vector<float>.Count);
            for (; index < vectorEnd; index += Vector<float>.Count)
            {
                Vector<float> v = new(span.Slice(index, Vector<float>.Count));
                vsum += v * v;
            }
            sum = Vector.Dot(vsum, Vector<float>.One);
        }
        for (; index < span.Length; index++)
        {
            float val = span[index];
            sum += val * val;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Average(ReadOnlySpan<float> span)
    {
        if (span.IsEmpty) return 0f;
        float sum = 0f;
        int index = 0;
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            Vector<float> vsum = Vector<float>.Zero;
            int vectorEnd = span.Length - (span.Length % Vector<float>.Count);
            for (; index < vectorEnd; index += Vector<float>.Count)
            {
                Vector<float> v = new(span.Slice(index, Vector<float>.Count));
                vsum += v;
            }
            sum = Vector.Dot(vsum, Vector<float>.One);
        }
        for (; index < span.Length; index++)
        {
            sum += span[index];
        }
        return sum / span.Length;
    }

    /// <summary>
    /// Corrects one damaged over-the-air bit when odd character parity identifies
    /// the affected byte and the BCS uniquely identifies the bit. If all character
    /// parity is intact, only the two unprotected BCS bytes are considered.
    /// </summary>
}
