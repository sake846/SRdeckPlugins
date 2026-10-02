using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.AdsB.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SRdeckPlugin.AdsB.Dsp;

public sealed partial class ModeSReceiver
{
    private const int SicChannelRadius = 8;
    private const int SicChannelTaps = SicChannelRadius * 2 + 1;
    private readonly SicWorkspace sicWorkspace = new();
    private readonly List<(int Offset, byte[] Bytes, double Quality)> pendingCancellations = new();
    private readonly List<(int Offset, ModeSPayloadKey Payload)> knownSicFrames = new();
    private readonly List<SampleRange> cancellationScanRanges = new();

    private void QueueSicFrame(int offset, byte[] bytes, double quality)
    {
        if (!enableSuccessiveInterferenceCancellation) return;
        pendingCancellations.Add((offset, bytes, quality));
        knownSicFrames.Add((offset, new(bytes)));
    }

    private void RecoverBySuccessiveInterferenceCancellation(
        IqBlockMetadata metadata,
        double groupDelayInputSamples,
        int scanLimit,
        List<ModeSFrame> result)
    {
        if (!enableSuccessiveInterferenceCancellation || scanLimit <= 0 ||
            (pendingCancellations.Count == 0 && cancellationScanRanges.Count == 0)) return;
        SicCancellationRunCount++;
        int sampleCount = pending.Count;
        // Keep the residual IQ across calls; pending power remains untouched for
        // the normal demodulator. Each accepted frame is subtracted only once.
        Span<float> residualI = CollectionsMarshal.AsSpan(pendingI);
        Span<float> residualQ = CollectionsMarshal.AsSpan(pendingQ);
        float[] residualPowerArray = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            var decodedOffsets = knownSicFrames.Select(item => item.Offset).ToHashSet();
            ReadOnlySpan<float> residualPower = residualPowerArray.AsSpan(0, sampleCount);
            for (int index = 0; index < sampleCount; index++)
                residualPowerArray[index] = residualI[index] * residualI[index] + residualQ[index] * residualQ[index];
            Span<byte> decodedBytes = stackalloc byte[14];
            Span<float> bitConfidence = stackalloc float[112];

            for (int iteration = 0; iteration <= 3; iteration++)
            {
                // Wait for the filter tail too, so a boundary never leaves a
                // partially subtracted frame in the next block.
                var toCancel = pendingCancellations
                    .Where(item => item.Offset + LongFrameSamples + SicChannelRadius <= sampleCount)
                    .OrderByDescending(item => item.Quality).ToList();
                if (toCancel.Count == 0 && (iteration > 0 || cancellationScanRanges.Count == 0)) break;
                pendingCancellations.RemoveAll(item =>
                    item.Offset + LongFrameSamples + SicChannelRadius <= sampleCount);
                sicWorkspace.AffectedRanges.Clear();
                foreach ((int offset, byte[] bytes, _) in toCancel)
                {
                    SampleRange affected = CancelKnownFrame(residualI, residualQ, sampleCount, offset, bytes, sicWorkspace);
                    sicWorkspace.AffectedRanges.Add(affected);
                    // Either the preamble OR the payload can overlap the cancelled frame.
                    cancellationScanRanges.Add(new(Math.Max(0, affected.Start - LongFrameSamples + 1), affected.End));
                }
                // Three recovery passes; also subtract the last recovered frames
                // so their residual tails are ready for the following block.
                if (iteration == 3) break;
                MergeRanges(sicWorkspace.AffectedRanges, 0, sampleCount);
                foreach (SampleRange range in sicWorkspace.AffectedRanges)
                    for (int index = range.Start; index < range.End; index++)
                        residualPowerArray[index] = residualI[index] * residualI[index] + residualQ[index] * residualQ[index];

                MergeRanges(cancellationScanRanges, 0, sampleCount);
                foreach (SampleRange range in cancellationScanRanges)
                {
                    int end = Math.Min(range.End, Math.Min(scanLimit, sampleCount - LongFrameSamples + 1));
                    for (int scan = range.Start; scan < end; scan++)
                    {
                        if (decodedOffsets.Contains(scan)) continue;
                        bool hasPreamble = IsPreamble(residualPower, scan, noiseFloorPower,
                            out double quality, out double timingOffset);
                        if (hasPreamble)
                            DecodeLongFrame(residualPower, scan + PreambleSamples + timingOffset,
                                noiseFloorPower, decodedBytes, bitConfidence, out _);
                        else if (!DecodeLongFrameFixed(residualPower, scan + PreambleSamples, decodedBytes))
                            continue;
                        // Cancellation recovery is accepted only with an independent exact CRC.
                        if (!ModeSCrc.IsValidExtendedSquitter(decodedBytes)) continue;
                        var payloadKey = new ModeSPayloadKey(decodedBytes);
                        if (IsKnownSicFrame(scan, payloadKey)) continue;

                        byte[] acceptedBytes = decodedBytes.ToArray();
                        long workingPosition = pendingWorkingSampleStart + scan;
                        long sourceOffset = (long)Math.Round(workingPosition * (double)inputSampleRate /
                            DemodulationSampleRateHz - groupDelayInputSamples);
                        long sourcePosition = streamSampleOrigin + sourceOffset;
                        DateTimeOffset receivedAt = metadata.UtcTimestamp.AddSeconds(
                            (sourcePosition - metadata.AbsoluteSampleStart) / (double)inputSampleRate);
                        double recoveredQuality = hasPreamble ? quality * 0.8 : 0.1;
                        result.Add(new(acceptedBytes, receivedAt, metadata.StreamId, sourcePosition, recoveredQuality));
                        RecordSignalQuality(recoveredQuality);
                        QueueSicFrame(scan, acceptedBytes, recoveredQuality);
                        decodedOffsets.Add(scan);
                        ValidFrameCount++;
                        SicRecoveredFrameCount++;
                    }
                }
            }
            // Only unfinished frame starts need rescanning when more IQ arrives.
            MergeRanges(cancellationScanRanges, scanLimit, sampleCount);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(residualPowerArray);
        }
    }

    private bool IsKnownSicFrame(int offset, ModeSPayloadKey payload)
    {
        foreach (var frame in knownSicFrames)
            if (frame.Payload == payload && Math.Abs(frame.Offset - offset) < LongFrameSamples)
                return true;
        return false;
    }

    private void RecordSignalQuality(double quality)
    {
        lastSignalQuality = quality;
        signalQualitySum += quality;
        maximumSignalQuality = Math.Max(maximumSignalQuality, quality);
    }

    private static SampleRange CancelKnownFrame(Span<float> valuesI, Span<float> valuesQ, int validLength,
        int offset, ReadOnlySpan<byte> bytes, SicWorkspace workspace)
    {
        workspace.ClearEstimation();
        double correlationI = 0;
        double correlationQ = 0;
        // Average carrier rotation over all known pulses. Four preamble pairs
        // alone are easily biased by noise or an earlier overlapping frame.
        for (int relative = 0; relative + 1 < LongFrameSamples; relative++)
        {
            if (!IsActivePulseSample(bytes, relative) || !IsActivePulseSample(bytes, relative + 1)) continue;
            int index = offset + relative;
            correlationI += valuesI[index + 1] * valuesI[index] + valuesQ[index + 1] * valuesQ[index];
            correlationQ += valuesQ[index + 1] * valuesI[index] - valuesI[index + 1] * valuesQ[index];
        }
        double phaseStep = Math.Atan2(correlationQ, correlationI);

        // Adjacent-sample rotation can still be pulled toward the weaker
        // transmitter's carrier. Fit phase over the full known pulse train;
        // the initial estimate lets us unwrap even large carrier offsets.
        PhaseRotator carrier = new(phaseStep, 0);
        double count = 0, sumTime = 0, sumTimeSquared = 0, sumPhase = 0, sumTimePhase = 0;
        double previousPhase = 0;
        for (int relative = 0; relative < LongFrameSamples; relative++)
        {
            int index = offset + relative;
            if (IsActivePulseSample(bytes, relative))
            {
                double i = valuesI[index] * carrier.Cosine + valuesQ[index] * carrier.Sine;
                double q = valuesQ[index] * carrier.Cosine - valuesI[index] * carrier.Sine;
                double phase = Math.Atan2(q, i);
                phase = previousPhase + Math.IEEERemainder(phase - previousPhase, 2 * Math.PI);
                previousPhase = phase;
                count++;
                sumTime += relative;
                sumTimeSquared += relative * relative;
                sumPhase += phase;
                sumTimePhase += relative * phase;
            }
            carrier.MoveNext();
        }
        phaseStep += (count * sumTimePhase - sumTime * sumPhase) /
            (count * sumTimeSquared - sumTime * sumTime);

        // Use the CRC-validated waveform, including its payload, for channel
        // training. An earlier weak frame can corrupt the entire preamble.
        PhaseRotator rotation = new(phaseStep, -SicChannelRadius);
        for (int relative = -SicChannelRadius; relative < LongFrameSamples + SicChannelRadius; relative++)
        {
            int index = offset + relative;
            if ((uint)index < (uint)validLength)
            {
                double observedI = valuesI[index] * rotation.Cosine + valuesQ[index] * rotation.Sine;
                double observedQ = valuesQ[index] * rotation.Cosine - valuesI[index] * rotation.Sine;
                for (int tap = 0; tap < SicChannelTaps; tap++)
                {
                    int lag = tap - SicChannelRadius;
                    if (!IsActivePulseSample(bytes, relative - lag)) continue;
                    workspace.ResponseI[tap] += observedI;
                    workspace.ResponseQ[tap] += observedQ;
                    for (int other = 0; other < SicChannelTaps; other++)
                    {
                        int otherLag = other - SicChannelRadius;
                        if (IsActivePulseSample(bytes, relative - otherLag)) workspace.Normal[tap, other]++;
                    }
                }
            }
            rotation.MoveNext();
        }
        double trace = 0;
        for (int tap = 0; tap < SicChannelTaps; tap++) trace += workspace.Normal[tap, tap];
        double ridge = Math.Max(trace / SicChannelTaps * 1e-5, 1e-9);
        for (int tap = 0; tap < SicChannelTaps; tap++) workspace.Normal[tap, tap] += ridge;
        SolveNormalEquations(workspace);

        rotation = new PhaseRotator(phaseStep, -SicChannelRadius);
        for (int relative = -SicChannelRadius; relative < LongFrameSamples + SicChannelRadius; relative++)
        {
            int index = offset + relative;
            if ((uint)index < (uint)validLength)
            {
                double modelI = 0;
                double modelQ = 0;
                for (int tap = 0; tap < SicChannelTaps; tap++)
                {
                    int lag = tap - SicChannelRadius;
                    if (!IsActivePulseSample(bytes, relative - lag)) continue;
                    modelI += workspace.ChannelI[tap];
                    modelQ += workspace.ChannelQ[tap];
                }
                valuesI[index] -= (float)(modelI * rotation.Cosine - modelQ * rotation.Sine);
                valuesQ[index] -= (float)(modelI * rotation.Sine + modelQ * rotation.Cosine);
            }
            rotation.MoveNext();
        }
        return new(Math.Max(0, offset - SicChannelRadius),
            Math.Min(validLength, offset + LongFrameSamples + SicChannelRadius));
    }

    private static void SolveNormalEquations(SicWorkspace workspace)
    {
        const int count = SicChannelTaps;
        const int firstResponseColumn = count;
        const int secondResponseColumn = count + 1;
        double[,] augmented = workspace.Augmented;
        for (int row = 0; row < count; row++)
        {
            for (int column = 0; column < count; column++)
                augmented[row, column] = workspace.Normal[row, column];
            augmented[row, firstResponseColumn] = workspace.ResponseI[row];
            augmented[row, secondResponseColumn] = workspace.ResponseQ[row];
        }
        for (int pivot = 0; pivot < count; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < count; row++)
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
            if (best != pivot)
                for (int column = pivot; column <= secondResponseColumn; column++)
                    (augmented[pivot, column], augmented[best, column]) =
                        (augmented[best, column], augmented[pivot, column]);
            double divisor = augmented[pivot, pivot];
            if (Math.Abs(divisor) < 1e-12) continue;
            for (int column = pivot; column <= secondResponseColumn; column++) augmented[pivot, column] /= divisor;
            for (int row = 0; row < count; row++)
            {
                if (row == pivot) continue;
                double factor = augmented[row, pivot];
                for (int column = pivot; column <= secondResponseColumn; column++)
                    augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        for (int row = 0; row < count; row++)
        {
            workspace.ChannelI[row] = augmented[row, firstResponseColumn];
            workspace.ChannelQ[row] = augmented[row, secondResponseColumn];
        }
    }

    private static bool IsActivePulseSample(ReadOnlySpan<byte> bytes, int relativeSample)
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
        => IsPreamble(values, offset, noiseFloor, ReadOnlySpan<float>.Empty,
            out quality, out timingOffset);

    private static bool IsPreamble(ReadOnlySpan<float> values, int offset, float noiseFloor,
        ReadOnlySpan<float> integerSlots, out double quality, out double timingOffset)
    {
        quality = 0;
        timingOffset = 0;
        bool valid = false;
        ReadOnlySpan<double> phases = [-0.5, -0.25, 0, 0.25, 0.5];
        foreach (double phase in phases)
        {
            bool phaseValid = phase == 0 && integerSlots.Length == 16
                ? EvaluatePreambleSlots(integerSlots, noiseFloor, out double phaseQuality)
                : EvaluatePreamble(values, offset + phase, noiseFloor, out phaseQuality);
            if (!phaseValid || phaseQuality <= quality) continue;
            valid = true;
            quality = phaseQuality;
            timingOffset = phase;
        }
        return valid;
    }

    private static void FillIntegerPreambleSlots(
        ReadOnlySpan<float> power,
        int offset,
        Span<float> slots)
    {
        for (int slot = 0; slot < slots.Length; slot++)
        {
            int start = offset + slot * SamplesPerHalfBit;
            slots[slot] = (float)((double)power[start] + power[start + 1]);
        }
    }

    internal static bool HasFastPreambleCandidate(
        ReadOnlySpan<float> power, int offset, float noiseFloor, Span<float> slots)
    {
        // Necessary conditions from the full gate below: the first pulse must
        // clear both the noise threshold and the following quiet half-bit.
        // Reject before computing all 16 slots on the per-sample scan path.
        float first = (float)((double)power[offset] + power[offset + 1]);
        float quiet = (float)((double)power[offset + 2] + power[offset + 3]);
        if (!(first > Math.Max(quiet * 1.15f, noiseFloor * SamplesPerHalfBit * 2)))
            return false;

        FillIntegerPreambleSlots(power, offset, slots);
        float highMin = float.MaxValue;
        float high = 0;
        float lowMax = 0;
        float low = 0;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            float energy = slots[slot];
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
        Span<float> slots = stackalloc float[16];
        for (int slot = 0; slot < slots.Length; slot++)
            slots[slot] = IntegrateTwoSamples(values, offset + slot * SamplesPerHalfBit);
        return EvaluatePreambleSlots(slots, noiseFloor, out quality);
    }

    private static bool EvaluatePreambleSlots(ReadOnlySpan<float> slots,
        float noiseFloor, out double quality)
    {
        ReadOnlySpan<int> highIndices = [0, 2, 7, 9];
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

    private void DecodeLongFrame(ReadOnlySpan<float> values, double offset, float noiseFloor,
        Span<byte> bytes, Span<float> confidence, out double timingAdjustment)
    {
        bytes.Clear();
        double timing = offset;
        for (int bit = 0; bit < 112; bit++)
        {
            double nominalStart = timing + bit * 2 * SamplesPerHalfBit;
            float first = IntegrateTwoSamples(values, nominalStart);
            float second = IntegrateTwoSamples(values, nominalStart + SamplesPerHalfBit);
            if (first > second)
                bytes[bit / 8] |= (byte)(1 << (7 - bit % 8));
            confidence[bit] = Math.Clamp(Math.Abs(first - second) /
                Math.Max(first + second + noiseFloor * SamplesPerHalfBit * 2, 1e-20f), 0, 1);

            if (bit % timingGradientInterval == 0)
            {
                timingGradientEvaluationCount++;
                float earlyFirst = IntegrateTwoSamples(values, nominalStart - 0.25);
                float earlySecond = IntegrateTwoSamples(values, nominalStart + SamplesPerHalfBit - 0.25);
                float lateFirst = IntegrateTwoSamples(values, nominalStart + 0.25);
                float lateSecond = IntegrateTwoSamples(values, nominalStart + SamplesPerHalfBit + 0.25);
                double earlyContrast = Math.Abs(earlyFirst - earlySecond);
                double lateContrast = Math.Abs(lateFirst - lateSecond);
                double error = (lateContrast - earlyContrast) /
                    Math.Max(earlyContrast + lateContrast, 1e-20);
                timing += Math.Clamp(error * 0.04, -0.03, 0.03);
                timing = Math.Clamp(timing, offset - 0.75, offset + 0.75);
            }
        }
        timingAdjustment = timing - offset;
    }

    private static bool DecodeLongFrameFixed(ReadOnlySpan<float> values, int offset, Span<byte> bytes)
    {
        bytes.Clear();
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
            if (bit == 4 && bytes[0] >> 3 is not (17 or 18)) return false;
        }
        return true;
    }

    internal static float IntegrateTwoSamples(ReadOnlySpan<float> values, double start)
    {
        int left = (int)Math.Floor(start);
        double fraction = start - left;
        if (fraction == 0) return IntegrateInteger2(values, left);
        if (left >= 0 && left + 2 < values.Length)
            return (float)((1 - fraction) * values[left] + values[left + 1] +
                fraction * values[left + 2]);

        float first = Interpolate(values, left, fraction);
        float second = Interpolate(values, left + 1, fraction);
        return first + second;
    }

    private static float IntegrateInteger2(ReadOnlySpan<float> values, int start)
    {
        if ((uint)start < (uint)(values.Length - 1)) return values[start] + values[start + 1];
        return SampleClamped(values, start) + SampleClamped(values, start + 1);
    }

    private static float Interpolate(ReadOnlySpan<float> values, int left, double fraction)
    {
        if (left < 0) return values[0];
        if (left + 1 >= values.Length) return values[^1];
        return (float)(values[left] + (values[left + 1] - values[left]) * fraction);
    }

    private static float SampleClamped(ReadOnlySpan<float> values, int index) =>
        values[Math.Clamp(index, 0, values.Length - 1)];

    private static void MergeRanges(List<SampleRange> ranges, int minimum, int maximumExclusive)
    {
        if (ranges.Count == 0) return;
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        int sourceCount = ranges.Count;
        int write = 0;
        for (int read = 0; read < sourceCount; read++)
        {
            SampleRange source = ranges[read];
            var range = new SampleRange(
                Math.Max(minimum, source.Start),
                Math.Min(maximumExclusive, source.End));
            if (range.Start >= range.End) continue;
            if (write > 0 && range.Start <= ranges[write - 1].End)
            {
                SampleRange previous = ranges[write - 1];
                ranges[write - 1] = previous with { End = Math.Max(previous.End, range.End) };
            }
            else
            {
                ranges[write++] = range;
            }
        }
        if (write < ranges.Count) ranges.RemoveRange(write, ranges.Count - write);
    }

    private readonly record struct ModeSPayloadKey(ulong First8, uint Next4, ushort Last2)
    {
        public ModeSPayloadKey(ReadOnlySpan<byte> bytes) : this(
            bytes.Length == 14 ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : throw new ArgumentException(
                "A Mode S payload key requires exactly 14 bytes.", nameof(bytes)),
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]))
        {
        }
    }

    private readonly record struct SampleRange(int Start, int End);

    private struct PhaseRotator
    {
        private const int ReanchorInterval = 64;
        private readonly double phaseStep;
        private readonly double stepCosine;
        private readonly double stepSine;
        private int nextRelative;
        private int samplesSinceAnchor;

        public PhaseRotator(double phaseStep, int relative)
        {
            this.phaseStep = phaseStep;
            stepCosine = Math.Cos(phaseStep);
            stepSine = Math.Sin(phaseStep);
            Cosine = Math.Cos(phaseStep * relative);
            Sine = Math.Sin(phaseStep * relative);
            nextRelative = relative + 1;
            samplesSinceAnchor = 0;
        }

        public double Cosine { get; private set; }
        public double Sine { get; private set; }

        public void MoveNext()
        {
            samplesSinceAnchor++;
            if (samplesSinceAnchor == ReanchorInterval)
            {
                Cosine = Math.Cos(phaseStep * nextRelative);
                Sine = Math.Sin(phaseStep * nextRelative);
                samplesSinceAnchor = 0;
            }
            else
            {
                double nextCosine = Cosine * stepCosine - Sine * stepSine;
                Sine = Sine * stepCosine + Cosine * stepSine;
                Cosine = nextCosine;
            }
            nextRelative++;
        }
    }

    private sealed class SicWorkspace
    {
        public double[,] Normal { get; } = new double[SicChannelTaps, SicChannelTaps];
        public double[] ResponseI { get; } = new double[SicChannelTaps];
        public double[] ResponseQ { get; } = new double[SicChannelTaps];
        public double[] ChannelI { get; } = new double[SicChannelTaps];
        public double[] ChannelQ { get; } = new double[SicChannelTaps];
        public double[,] Augmented { get; } = new double[SicChannelTaps, SicChannelTaps + 2];
        public List<SampleRange> AffectedRanges { get; } = new(8);

        public void ClearEstimation()
        {
            Array.Clear(Normal);
            Array.Clear(ResponseI);
            Array.Clear(ResponseQ);
            Array.Clear(ChannelI);
            Array.Clear(ChannelQ);
        }
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
