using SRdeckPlugin.AdsB.Models;
using SRdeckPlugin.AdsB.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using System.Buffers;
using System.Buffers.Binary;

namespace SRdeckPlugin.AdsB.Dsp;

public sealed partial class ModeSReceiver
{
    private const int SicChannelRadius = 8;
    private const int SicChannelTaps = SicChannelRadius * 2 + 1;
    private readonly SicWorkspace sicWorkspace = new();

    private void RecoverBySuccessiveInterferenceCancellation(
        IqBlockMetadata metadata,
        double groupDelayInputSamples,
        int scanLimit,
        List<(int Offset, byte[] Bytes, double Quality)> candidates, HashSet<int> decodedOffsets,
        List<ModeSFrame> result)
    {
        if (!enableSuccessiveInterferenceCancellation || candidates.Count == 0 || scanLimit <= 0) return;
        SicCancellationRunCount++;
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
            var knownPayloads = candidates.Select(item => new ModeSPayloadKey(item.Bytes)).ToHashSet();
            ReadOnlySpan<float> residualPower = residualPowerArray.AsSpan(0, sampleCount);
            for (int index = 0; index < sampleCount; index++)
                residualPowerArray[index] = residualI[index] * residualI[index] + residualQ[index] * residualQ[index];
            Span<byte> decodedBytes = stackalloc byte[14];
            Span<float> bitConfidence = stackalloc float[112];

            for (int iteration = 0; iteration < 3 && toCancel.Count > 0; iteration++)
            {
                sicWorkspace.AffectedRanges.Clear();
                foreach ((int offset, byte[] bytes, _) in toCancel)
                    sicWorkspace.AffectedRanges.Add(
                        CancelKnownFrame(residualI, residualQ, sampleCount, offset, bytes, sicWorkspace));
                MergeRanges(sicWorkspace.AffectedRanges, 0, sampleCount);
                foreach (SampleRange range in sicWorkspace.AffectedRanges)
                    for (int index = range.Start; index < range.End; index++)
                        residualPowerArray[index] = residualI[index] * residualI[index] + residualQ[index] * residualQ[index];

                var recovered = new List<(int Offset, byte[] Bytes, double Quality)>();
                BuildCancellationScanRanges(candidates, scanLimit, sampleCount, sicWorkspace.ScanRanges);
                foreach (SampleRange range in sicWorkspace.ScanRanges)
                {
                    for (int scan = range.Start; scan < range.End; scan++)
                    {
                        if (decodedOffsets.Contains(scan)) continue;
                        bool hasPreamble = IsPreamble(residualPower, scan, noiseFloorPower,
                            out double quality, out double timingOffset);
                        if (hasPreamble)
                            DecodeLongFrame(residualPower, scan + PreambleSamples + timingOffset,
                                noiseFloorPower, decodedBytes, bitConfidence, out _);
                        else
                            DecodeLongFrameFixed(residualPower, scan + PreambleSamples, decodedBytes);
                        // Cancellation recovery is accepted only with an independent exact CRC.
                        if (!ModeSCrc.IsValidExtendedSquitter(decodedBytes)) continue;
                        var payloadKey = new ModeSPayloadKey(decodedBytes);
                        if (!knownPayloads.Add(payloadKey)) continue;

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
                        recovered.Add((scan, acceptedBytes, recoveredQuality));
                        decodedOffsets.Add(scan);
                        ValidFrameCount++;
                        SicRecoveredFrameCount++;
                    }
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

    private static void BuildCancellationScanRanges(
        List<(int Offset, byte[] Bytes, double Quality)> candidates,
        int scanLimit,
        int sampleCount,
        List<SampleRange> ranges)
    {
        ranges.Clear();
        int maximumExclusive = Math.Min(scanLimit, sampleCount - LongFrameSamples + 1);
        foreach ((int offset, _, _) in candidates)
        {
            int start = Math.Max(0, offset - PreambleSamples + 1);
            int end = Math.Min(maximumExclusive, offset + LongFrameSamples);
            if (start < end) ranges.Add(new(start, end));
        }
        MergeRanges(ranges, 0, maximumExclusive);
    }

    private static SampleRange CancelKnownFrame(float[] valuesI, float[] valuesQ, int validLength,
        int offset, ReadOnlySpan<byte> bytes, SicWorkspace workspace)
    {
        workspace.ClearEstimation();
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

        PhaseRotator rotation = new(phaseStep, -SicChannelRadius);
        for (int relative = -SicChannelRadius; relative < PreambleSamples + SicChannelRadius; relative++)
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
        ReadOnlySpan<double> powerPrefix,
        int offset,
        Span<float> slots)
    {
        for (int slot = 0; slot < slots.Length; slot++)
        {
            int start = offset + slot * SamplesPerHalfBit;
            slots[slot] = (float)(powerPrefix[start + SamplesPerHalfBit] - powerPrefix[start]);
        }
    }

    private static bool HasFastPreambleCandidate(ReadOnlySpan<float> slots, float noiseFloor)
    {
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

    private static void DecodeLongFrameFixed(ReadOnlySpan<float> values, int offset, Span<byte> bytes)
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
        }
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
        public List<SampleRange> ScanRanges { get; } = new(8);

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
