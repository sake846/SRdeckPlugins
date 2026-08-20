using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.AdsB.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using System.Buffers;
using System.Runtime.InteropServices;

namespace SRdeckPlugin.AdsB.Dsp;

public sealed partial class ModeSReceiver
{
    private void RecoverBySuccessiveInterferenceCancellation(
        IqBlockMetadata metadata,
        double groupDelayInputSamples,
        int scanLimit,
        List<(int Offset, byte[] Bytes, double Quality)> candidates, HashSet<int> decodedOffsets,
        List<ModeSFrame> result)
    {
        if (candidates.Count == 0 || scanLimit <= 0) return;
        int sampleCount = pending.Count;
        float[] residualI = ArrayPool<float>.Shared.Rent(sampleCount);
        float[] residualQ = ArrayPool<float>.Shared.Rent(sampleCount);
        float[] residualPowerArray = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            for (int index = 0; index < sampleCount; index++)
            {
                residualI[index] = pendingI[index];
                residualQ[index] = pendingQ[index];
            }
            List<(int Offset, byte[] Bytes, double Quality)> toCancel =
                candidates.OrderByDescending(item => item.Quality).ToList();
            var knownPayloads = candidates.Select(item => Convert.ToHexString(item.Bytes))
                .ToHashSet(StringComparer.Ordinal);
            ReadOnlySpan<float> residualPower = residualPowerArray.AsSpan(0, sampleCount);

            for (int iteration = 0; iteration < 3 && toCancel.Count > 0; iteration++)
            {
                foreach ((int offset, byte[] bytes, _) in toCancel)
                    CancelKnownFrame(residualI, residualQ, sampleCount, offset, bytes);
                for (int index = 0; index < sampleCount; index++)
                    residualPowerArray[index] = residualI[index] * residualI[index] + residualQ[index] * residualQ[index];

                var recovered = new List<(int Offset, byte[] Bytes, double Quality)>();
                for (int scan = 0; scan < scanLimit && scan + LongFrameSamples <= sampleCount; scan++)
                {
                    if (decodedOffsets.Contains(scan)) continue;
                    if (!OverlapsCancelledFrame(candidates, scan)) continue;
                    bool hasPreamble = IsPreamble(residualPower, scan, noiseFloorPower,
                        out double quality, out double timingOffset);
                    byte[] bytes = hasPreamble
                        ? DecodeLongFrame(residualPower, scan + PreambleSamples + timingOffset,
                            noiseFloorPower, out _, out _)
                        : DecodeLongFrameFixed(residualPower, scan + PreambleSamples);
                    // Cancellation recovery is accepted only with an independent exact CRC.
                    string payloadKey = Convert.ToHexString(bytes);
                    if (!ModeSCrc.IsValidExtendedSquitter(bytes) || !knownPayloads.Add(payloadKey)) continue;

                    long workingPosition = pendingWorkingSampleStart + scan;
                    long sourceOffset = (long)Math.Round(workingPosition * (double)inputSampleRate /
                        DemodulationSampleRateHz - groupDelayInputSamples);
                    long sourcePosition = streamSampleOrigin + sourceOffset;
                    DateTimeOffset receivedAt = metadata.UtcTimestamp.AddSeconds(
                        (sourcePosition - metadata.AbsoluteSampleStart) / (double)inputSampleRate);
                    double recoveredQuality = hasPreamble ? quality * 0.8 : 0.1;
                    result.Add(new(bytes, receivedAt, metadata.StreamId, sourcePosition, recoveredQuality));
                    RecordSignalQuality(recoveredQuality);
                    recovered.Add((scan, bytes, recoveredQuality));
                    decodedOffsets.Add(scan);
                    ValidFrameCount++;
                    SicRecoveredFrameCount++;
                }
                candidates.AddRange(recovered);
                toCancel = recovered;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(residualI);
            ArrayPool<float>.Shared.Return(residualQ);
            ArrayPool<float>.Shared.Return(residualPowerArray);
        }
    }

    private void RecordSignalQuality(double quality)
    {
        lastSignalQuality = quality;
        signalQualitySum += quality;
        maximumSignalQuality = Math.Max(maximumSignalQuality, quality);
    }

    private static bool OverlapsCancelledFrame(
        List<(int Offset, byte[] Bytes, double Quality)> candidates, int scan)
    {
        foreach ((int offset, _, _) in candidates)
            if (scan < offset + LongFrameSamples && scan + PreambleSamples > offset) return true;
        return false;
    }

    private static void CancelKnownFrame(float[] valuesI, float[] valuesQ, int validLength,
        int offset, byte[] bytes)
    {
        const int channelRadius = 8;
        const int channelTaps = channelRadius * 2 + 1;
        double correlationI = 0;
        double correlationQ = 0;
        // A frame that passed preamble detection has a comparatively clean,
        // known training sequence. Estimate carrier and channel from that
        // preamble so an overlapping payload is not absorbed into the model.
        for (int relative = 0; relative + 1 < PreambleSamples; relative++)
        {
            if (!IsActivePulseSample(bytes, relative) || !IsActivePulseSample(bytes, relative + 1)) continue;
            int index = offset + relative;
            correlationI += valuesI[index + 1] * valuesI[index] + valuesQ[index + 1] * valuesQ[index];
            correlationQ += valuesQ[index + 1] * valuesI[index] - valuesI[index + 1] * valuesQ[index];
        }
        double phaseStep = Math.Atan2(correlationQ, correlationI);

        var normal = new double[channelTaps, channelTaps];
        var responseI = new double[channelTaps];
        var responseQ = new double[channelTaps];
        for (int relative = -channelRadius; relative < PreambleSamples + channelRadius; relative++)
        {
            int index = offset + relative;
            if ((uint)index >= (uint)validLength) continue;
            double phase = phaseStep * relative;
            double cosine = Math.Cos(phase);
            double sine = Math.Sin(phase);
            double observedI = valuesI[index] * cosine + valuesQ[index] * sine;
            double observedQ = valuesQ[index] * cosine - valuesI[index] * sine;
            for (int tap = 0; tap < channelTaps; tap++)
            {
                int lag = tap - channelRadius;
                if (!IsActivePulseSample(bytes, relative - lag)) continue;
                responseI[tap] += observedI;
                responseQ[tap] += observedQ;
                for (int other = 0; other < channelTaps; other++)
                {
                    int otherLag = other - channelRadius;
                    if (IsActivePulseSample(bytes, relative - otherLag)) normal[tap, other]++;
                }
            }
        }
        double trace = 0;
        for (int tap = 0; tap < channelTaps; tap++) trace += normal[tap, tap];
        double ridge = Math.Max(trace / channelTaps * 1e-5, 1e-9);
        for (int tap = 0; tap < channelTaps; tap++) normal[tap, tap] += ridge;
        double[] channelI = SolveNormalEquations(normal, responseI);
        double[] channelQ = SolveNormalEquations(normal, responseQ);

        for (int relative = -channelRadius; relative < LongFrameSamples + channelRadius; relative++)
        {
            int index = offset + relative;
            if ((uint)index >= (uint)validLength) continue;
            double modelI = 0;
            double modelQ = 0;
            for (int tap = 0; tap < channelTaps; tap++)
            {
                int lag = tap - channelRadius;
                if (!IsActivePulseSample(bytes, relative - lag)) continue;
                modelI += channelI[tap];
                modelQ += channelQ[tap];
            }
            double phase = phaseStep * relative;
            double cosine = Math.Cos(phase);
            double sine = Math.Sin(phase);
            valuesI[index] -= (float)(modelI * cosine - modelQ * sine);
            valuesQ[index] -= (float)(modelI * sine + modelQ * cosine);
        }
    }

    private static double[] SolveNormalEquations(double[,] matrix, double[] response)
    {
        int count = response.Length;
        var augmented = new double[count, count + 1];
        for (int row = 0; row < count; row++)
        {
            for (int column = 0; column < count; column++) augmented[row, column] = matrix[row, column];
            augmented[row, count] = response[row];
        }
        for (int pivot = 0; pivot < count; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < count; row++)
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
            if (best != pivot)
                for (int column = pivot; column <= count; column++)
                    (augmented[pivot, column], augmented[best, column]) =
                        (augmented[best, column], augmented[pivot, column]);
            double divisor = augmented[pivot, pivot];
            if (Math.Abs(divisor) < 1e-12) continue;
            for (int column = pivot; column <= count; column++) augmented[pivot, column] /= divisor;
            for (int row = 0; row < count; row++)
            {
                if (row == pivot) continue;
                double factor = augmented[row, pivot];
                for (int column = pivot; column <= count; column++)
                    augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        var result = new double[count];
        for (int row = 0; row < count; row++) result[row] = augmented[row, count];
        return result;
    }

    private static bool IsActivePulseSample(byte[] bytes, int relativeSample)
    {
        if ((uint)relativeSample >= LongFrameSamples) return false;
        int halfBit = relativeSample / SamplesPerHalfBit;
        if (halfBit < 16) return halfBit is 0 or 2 or 7 or 9;
        int dataHalfBit = halfBit - 16;
        int bit = dataHalfBit / 2;
        bool one = (bytes[bit / 8] & (1 << (7 - bit % 8))) != 0;
        return dataHalfBit % 2 == (one ? 0 : 1);
    }

    private static bool IsPreamble(ReadOnlySpan<float> values, int offset, float noiseFloor,
        out double quality, out double timingOffset)
    {
        quality = 0;
        timingOffset = 0;
        bool valid = false;
        ReadOnlySpan<double> phases = [-0.5, -0.25, 0, 0.25, 0.5];
        foreach (double phase in phases)
        {
            bool phaseValid = EvaluatePreamble(values, offset + phase, noiseFloor, out double phaseQuality);
            if (!phaseValid || phaseQuality <= quality) continue;
            valid = true;
            quality = phaseQuality;
            timingOffset = phase;
        }
        return valid;
    }

    private static bool HasFastPreambleCandidate(ReadOnlySpan<float> values, int offset, float noiseFloor)
    {
        float highMin = float.MaxValue;
        float high = 0;
        float lowMax = 0;
        float low = 0;
        for (int slot = 0; slot < 16; slot++)
        {
            int start = offset + slot * SamplesPerHalfBit;
            float energy = 0;
            for (int sample = 0; sample < SamplesPerHalfBit; sample++) energy += values[start + sample];
            if (slot is 0 or 2 or 7 or 9)
            {
                high += energy;
                highMin = Math.Min(highMin, energy);
            }
            else
            {
                low += energy;
                lowMax = Math.Max(lowMax, energy);
            }
        }
        float highAverage = high / 4;
        float lowAverage = low / 12;
        return highMin > Math.Max(lowMax * 1.15f, noiseFloor * SamplesPerHalfBit * 2) &&
               highAverage > Math.Max(lowAverage * 1.7f, noiseFloor * SamplesPerHalfBit * 3);
    }

    private static bool EvaluatePreamble(ReadOnlySpan<float> values, double offset,
        float noiseFloor, out double quality)
    {
        ReadOnlySpan<int> highIndices = [0, 2, 7, 9];
        Span<float> slots = stackalloc float[16];
        for (int slot = 0; slot < slots.Length; slot++)
            slots[slot] = Integrate(values, offset + slot * SamplesPerHalfBit, SamplesPerHalfBit);
        float highMin = float.MaxValue;
        float highSum = 0;
        foreach (int index in highIndices)
        {
            float value = slots[index];
            highMin = Math.Min(highMin, value);
            highSum += value;
        }
        float lowMax = 0;
        float lowSum = 0;
        for (int index = 0; index < slots.Length; index++)
        {
            if (index is 0 or 2 or 7 or 9) continue;
            float value = slots[index];
            lowMax = Math.Max(lowMax, value);
            lowSum += value;
        }
        float highAverage = highSum / highIndices.Length;
        float lowAverage = lowSum / 12;
        quality = Math.Clamp((highAverage - lowAverage) / Math.Max(highAverage + lowAverage, 1e-20f), 0, 1);
        float noiseEnergy = noiseFloor * SamplesPerHalfBit;
        return highMin > Math.Max(lowMax * 1.45f, noiseEnergy * 3f) &&
               highAverage > Math.Max(lowAverage * 2.4f, noiseEnergy * 5f);
    }

    private static byte[] DecodeLongFrame(ReadOnlySpan<float> values, double offset, float noiseFloor,
        out float[] confidence, out double timingAdjustment)
    {
        var bytes = new byte[14];
        confidence = new float[112];
        double timing = offset;
        for (int bit = 0; bit < 112; bit++)
        {
            double nominalStart = timing + bit * 2 * SamplesPerHalfBit;
            float first = Integrate(values, nominalStart, SamplesPerHalfBit);
            float second = Integrate(values, nominalStart + SamplesPerHalfBit, SamplesPerHalfBit);
            if (first > second)
                bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
            confidence[bit] = Math.Clamp(Math.Abs(first - second) /
                Math.Max(first + second + noiseFloor * SamplesPerHalfBit * 2, 1e-20f), 0, 1);

            float earlyFirst = Integrate(values, nominalStart - 0.25, SamplesPerHalfBit);
            float earlySecond = Integrate(values, nominalStart + SamplesPerHalfBit - 0.25, SamplesPerHalfBit);
            float lateFirst = Integrate(values, nominalStart + 0.25, SamplesPerHalfBit);
            float lateSecond = Integrate(values, nominalStart + SamplesPerHalfBit + 0.25, SamplesPerHalfBit);
            double earlyContrast = Math.Abs(earlyFirst - earlySecond);
            double lateContrast = Math.Abs(lateFirst - lateSecond);
            double error = (lateContrast - earlyContrast) /
                Math.Max(earlyContrast + lateContrast, 1e-20);
            timing += Math.Clamp(error * 0.04, -0.03, 0.03);
            timing = Math.Clamp(timing, offset - 0.75, offset + 0.75);
        }
        timingAdjustment = timing - offset;
        return bytes;
    }

    private static byte[] DecodeLongFrameFixed(ReadOnlySpan<float> values, int offset)
    {
        var bytes = new byte[14];
        for (int bit = 0; bit < 112; bit++)
        {
            int start = offset + bit * 2 * SamplesPerHalfBit;
            float first = 0;
            float second = 0;
            for (int sample = 0; sample < SamplesPerHalfBit; sample++)
            {
                first += values[start + sample];
                second += values[start + SamplesPerHalfBit + sample];
            }
            if (first > second) bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
        }
        return bytes;
    }

    private static float Integrate(ReadOnlySpan<float> values, double start, int count)
    {
        float sum = 0;
        for (int sample = 0; sample < count; sample++) sum += Interpolate(values, start + sample);
        return sum;
    }

    private static float Interpolate(ReadOnlySpan<float> values, double position)
    {
        int left = (int)Math.Floor(position);
        double fraction = position - left;
        if (left < 0) return values[0];
        if (left + 1 >= values.Length) return values[^1];
        return (float)(values[left] + (values[left + 1] - values[left]) * fraction);
    }

    internal static int SelectCoarseDecimationFactor(int sampleRateHz)
    {
        if (sampleRateHz < MinimumInputSampleRateHz)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        // Keep enough transition bandwidth for the final channel FIR and avoid
        // excessive CIC droop across the approximately 1.7 MHz ADS-B channel.
        int factor = Math.Max(1, (int)Math.Ceiling(sampleRateHz / 4_000_000d));
        return sampleRateHz / (double)factor >= 2_500_000 ? factor : Math.Max(1, factor - 1);
    }

}
