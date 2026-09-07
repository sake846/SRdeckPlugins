using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Hfdl.Dsp;

public static partial class HfdlPhysicalLayer
{
    private static readonly ConstellationPoint[][] Constellations =
    [[], [], CreateConstellation(2), [], CreateConstellation(4), [], [], [], CreateConstellation(8)];

    /// <summary>Demodulates and decodes the data frames from a received HFDL burst.</summary>
    public static bool[] DecodeDataSymbols(ReadOnlySpan<Complex32> symbols, HfdlPhysicalMode mode,
        ReadOnlySpan<bool> probe, out double quality)
    {
        int expected = mode.FrameCount * SymbolsPerFrame;
        if (symbols.Length < expected) throw new ArgumentException("The HFDL data segment is incomplete.", nameof(symbols));
        var chips = new float[mode.InterleaverColumns * 40];
        int chipPosition = 0;
        double qualitySum = 0;
        var equalizer = new ProbeAdaptiveEqualizer(7);
        var corrected = new Complex32[SymbolsPerFrame];
        for (int frame = 0; frame < mode.FrameCount; frame++)
        {
            int offset = frame * SymbolsPerFrame;
            ReadOnlySpan<Complex32> source = symbols.Slice(offset, SymbolsPerFrame);
            ReadOnlySpan<Complex32> receivedProbe = source.Slice(DataSymbolsPerFrame, ProbeSymbolsPerFrame);
            double residualCarrierStep = EstimateCarrierStep(receivedProbe, probe);
            var rotator = new LinearPhaseRotator(
                residualCarrierStep * DataSymbolsPerFrame, -residualCarrierStep);
            for (int index = 0; index < corrected.Length; index++)
                corrected[index] = rotator.RotateNext(source[index]);
            Complex32 channel = EstimateChannel(corrected.AsSpan(DataSymbolsPerFrame, ProbeSymbolsPerFrame), probe);
            double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
            for (int index = 0; index < corrected.Length; index++)
                corrected[index] = Equalize(corrected[index], channel, inverse);
            equalizer.Train(corrected, DataSymbolsPerFrame, probe);
            qualitySum += equalizer.MeasureQuality(corrected, DataSymbolsPerFrame, probe);
            for (int index = 0; index < DataSymbolsPerFrame; index++)
            {
                Complex32 value = equalizer.Process(corrected, index);
                int dataIndex = frame * DataSymbolsPerFrame + index;
                if (Scrambler[dataIndex % Scrambler.Length]) value = new(-value.I, -value.Q);
                AppendMappedSoftChips(chips, ref chipPosition, value, mode.ModulationOrder);
            }
        }
        quality = qualitySum / mode.FrameCount;
        float[] deinterleaved = Deinterleave(chips, mode.InterleaverColumns, mode.LongInterleaver);
        if (mode.DataRate == 300)
        {
            int collapsedLength = deinterleaved.Length / 2;
            for (int index = 0; index < collapsedLength; index++)
                deinterleaved[index] = deinterleaved[index * 2] + deinterleaved[index * 2 + 1];
            return ViterbiDecode(deinterleaved.AsSpan(0, collapsedLength), mode.UserBitCount);
        }
        return ViterbiDecode(deinterleaved, mode.UserBitCount);
    }

    /// <summary>
    /// Demodulates a complete synchronized burst, using the known preamble tail
    /// to converge equalization before data begins and tracking residual carrier
    /// phase continuously through data and embedded probe segments.
    /// </summary>
    public static bool[] DecodeBurstSymbols(ReadOnlySpan<Complex32> symbols, HfdlPhysicalMode mode,
        ReadOnlySpan<bool> probe, out double quality)
    {
        int expected = PreambleSymbols + mode.FrameCount * SymbolsPerFrame;
        if (symbols.Length < expected)
            throw new ArgumentException("The HFDL burst is incomplete.", nameof(symbols));
        if (probe.Length < ProbeSymbolsPerFrame)
            throw new ArgumentException("The HFDL probe sequence is incomplete.", nameof(probe));

        Complex32[] burst = symbols[..expected].ToArray();
        var chips = new float[mode.InterleaverColumns * 40];
        int chipPosition = 0;
        var equalizer = new CenteredProbeAdaptiveEqualizer(7, 0.10);
        const int centerTap = 3;
        const int preambleTrainingStart = PreambleSymbols - 9 * ProbeSymbolsPerFrame;
        int nextInput = preambleTrainingStart - centerTap;
        for (int tap = 0; tap < 7; tap++) equalizer.Push(burst[nextInput++]);

        double carrierPhase = 0;
        double carrierFrequency = 0;
        double signalEnergy = 0;
        double errorEnergy = 0;
        double constellationStep = 2 * Math.PI / mode.ModulationOrder;

        Complex32 ProcessSymbol(bool? trainingBit, bool measureQuality)
        {
            Complex32 value = equalizer.Process();
            double phaseError;
            if (trainingBit is { } bit)
            {
                float target = bit ? -1 : 1;
                if (measureQuality)
                {
                    double di = value.I - target;
                    signalEnergy += 1;
                    errorEnergy += di * di + value.Q * value.Q;
                }
                equalizer.Train(new(target, 0), value);
                phaseError = Math.Atan2(value.Q * target, value.I * target);
            }
            else
            {
                double angle = Math.Atan2(value.Q, value.I);
                phaseError = WrapPhase(angle - Math.Round(angle / constellationStep) * constellationStep);
            }

            carrierFrequency += 0.002 * phaseError;
            carrierPhase += carrierFrequency + 0.08 * phaseError;
            if (nextInput < expected)
                equalizer.Push(RotatePhase(burst[nextInput++], -carrierPhase));
            return value;
        }

        for (int segment = 0; segment < 9; segment++)
            for (int item = 0; item < ProbeSymbolsPerFrame; item++)
                ProcessSymbol(probe[item], false);

        for (int frame = 0; frame < mode.FrameCount; frame++)
        {
            for (int item = 0; item < DataSymbolsPerFrame; item++)
            {
                Complex32 value = ProcessSymbol(null, false);
                int dataIndex = frame * DataSymbolsPerFrame + item;
                if (Scrambler[dataIndex % Scrambler.Length]) value = new(-value.I, -value.Q);
                AppendMappedSoftChips(chips, ref chipPosition, value, mode.ModulationOrder);
            }

            int trainCount = frame + 1 < mode.FrameCount
                ? ProbeSymbolsPerFrame
                : ProbeSymbolsPerFrame - centerTap;
            for (int item = 0; item < trainCount; item++)
                ProcessSymbol(probe[item], true);
        }

        quality = signalEnergy <= 0
            ? 0
            : Math.Clamp(1 - Math.Sqrt(errorEnergy / signalEnergy), 0, 1);
        float[] deinterleaved = Deinterleave(chips, mode.InterleaverColumns, mode.LongInterleaver);
        if (mode.DataRate == 300)
        {
            int collapsedLength = deinterleaved.Length / 2;
            for (int index = 0; index < collapsedLength; index++)
                deinterleaved[index] = deinterleaved[index * 2] + deinterleaved[index * 2 + 1];
            return ViterbiDecode(deinterleaved.AsSpan(0, collapsedLength), mode.UserBitCount);
        }
        return ViterbiDecode(deinterleaved, mode.UserBitCount);
    }

    private static double WrapPhase(double phase)
    {
        while (phase > Math.PI) phase -= 2 * Math.PI;
        while (phase < -Math.PI) phase += 2 * Math.PI;
        return phase;
    }

    private static Complex32 RotatePhase(Complex32 value, double phase)
    {
        double cosine = Math.Cos(phase);
        double sine = Math.Sin(phase);
        return new(
            (float)(value.I * cosine - value.Q * sine),
            (float)(value.I * sine + value.Q * cosine));
    }

    private static void AppendMappedSoftChips(Span<float> output, ref int position, Complex32 value, int order)
    {
        int count = order switch { 8 => 3, 4 => 2, _ => 1 };
        Span<double> zeroDistance = stackalloc double[3];
        Span<double> oneDistance = stackalloc double[3];
        zeroDistance.Fill(double.PositiveInfinity);
        oneDistance.Fill(double.PositiveInfinity);
        foreach (ConstellationPoint point in Constellations[order])
        {
            double di = value.I - point.I, dq = value.Q - point.Q;
            double distance = di * di + dq * dq;
            for (int bit = 0; bit < count; bit++)
            {
                ref double minimum = ref ((point.Gray & (1 << bit)) != 0 ? ref oneDistance[bit] : ref zeroDistance[bit]);
                minimum = Math.Min(minimum, distance);
            }
        }
        for (int bit = 0; bit < count; bit++)
            output[position++] = (float)Math.Clamp(zeroDistance[bit] - oneDistance[bit], -8, 8);
    }

    private static double EstimateCarrierStep(ReadOnlySpan<Complex32> received, ReadOnlySpan<bool> expected)
    {
        double real = 0, imaginary = 0;
        for (int index = 1; index < received.Length; index++)
        {
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            Complex32 previous = received[index - 1], current = received[index];
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
        }
        return Math.Atan2(imaginary, real);
    }

    private static ConstellationPoint[] CreateConstellation(int order)
    {
        var output = new ConstellationPoint[order];
        for (int sector = 0; sector < order; sector++)
        {
            double phase = 2 * Math.PI * sector / order;
            output[sector] = new((float)Math.Cos(phase), (float)Math.Sin(phase),
                sector ^ (sector >> 1));
        }
        return output;
    }

    internal struct LinearPhaseRotator
    {
        private readonly double stepI;
        private readonly double stepQ;
        private double phasorI;
        private double phasorQ;
        private int normalizationCounter;

        public LinearPhaseRotator(double initialPhase, double phaseStep)
        {
            phasorI = Math.Cos(initialPhase);
            phasorQ = Math.Sin(initialPhase);
            stepI = Math.Cos(phaseStep);
            stepQ = Math.Sin(phaseStep);
            normalizationCounter = 0;
        }

        public Complex32 RotateNext(Complex32 value)
        {
            var output = new Complex32(
                (float)(value.I * phasorI - value.Q * phasorQ),
                (float)(value.I * phasorQ + value.Q * phasorI));
            double nextI = phasorI * stepI - phasorQ * stepQ;
            phasorQ = phasorI * stepQ + phasorQ * stepI;
            phasorI = nextI;
            if (++normalizationCounter == 256)
            {
                double inverseMagnitude = 1 / Math.Sqrt(phasorI * phasorI + phasorQ * phasorQ);
                phasorI *= inverseMagnitude;
                phasorQ *= inverseMagnitude;
                normalizationCounter = 0;
            }
            return output;
        }
    }

    private sealed class ProbeAdaptiveEqualizer
    {
        private readonly Complex32[] weights;

        public ProbeAdaptiveEqualizer(int taps)
        {
            weights = new Complex32[taps];
            weights[0] = new(1, 0);
        }

        public Complex32 Process(ReadOnlySpan<Complex32> samples, int index)
        {
            double i = 0, q = 0;
            for (int tap = 0; tap < weights.Length; tap++)
            {
                int source = index - tap;
                if (source < 0) break;
                Complex32 x = samples[source], w = weights[tap];
                i += w.I * x.I - w.Q * x.Q;
                q += w.I * x.Q + w.Q * x.I;
            }
            return new((float)i, (float)q);
        }

        public void Train(ReadOnlySpan<Complex32> samples, int offset, ReadOnlySpan<bool> expected)
        {
            const double step = 0.18;
            for (int epoch = 0; epoch < 5; epoch++)
            {
                for (int item = weights.Length - 1; item < expected.Length; item++)
                {
                    int index = offset + item;
                    Complex32 value = Process(samples, index);
                    double target = expected[item] ? -1 : 1;
                    double errorI = target - value.I, errorQ = -value.Q;
                    double power = 1e-4;
                    for (int tap = 0; tap < weights.Length; tap++)
                    {
                        Complex32 x = samples[index - tap];
                        power += x.I * x.I + x.Q * x.Q;
                    }
                    double gain = step / power;
                    for (int tap = 0; tap < weights.Length; tap++)
                    {
                        Complex32 x = samples[index - tap], w = weights[tap];
                        weights[tap] = new(
                            (float)(w.I + gain * (errorI * x.I + errorQ * x.Q)),
                            (float)(w.Q + gain * (errorQ * x.I - errorI * x.Q)));
                    }
                }
            }
        }

        public double MeasureQuality(ReadOnlySpan<Complex32> samples, int offset, ReadOnlySpan<bool> expected)
        {
            double error = 0, reference = 0;
            for (int item = weights.Length - 1; item < expected.Length; item++)
            {
                Complex32 value = Process(samples, offset + item);
                double target = expected[item] ? -1 : 1;
                double di = value.I - target;
                error += di * di + value.Q * value.Q;
                reference += target * target;
            }
            double evm = Math.Sqrt(error / Math.Max(reference, 1e-12));
            return Math.Clamp(1 - evm, 0, 1);
        }
    }

    private sealed class CenteredProbeAdaptiveEqualizer
    {
        private readonly Complex32[] weights;
        private readonly Complex32[] samples;
        private readonly double step;
        private int position;

        public CenteredProbeAdaptiveEqualizer(int taps, double step)
        {
            weights = new Complex32[taps];
            samples = new Complex32[taps];
            weights[taps / 2] = new(1, 0);
            this.step = step;
        }

        public void Push(Complex32 sample)
        {
            position = (position + 1) % samples.Length;
            samples[position] = sample;
        }

        public Complex32 Process()
        {
            double i = 0;
            double q = 0;
            for (int tap = 0; tap < weights.Length; tap++)
            {
                Complex32 x = samples[(position + samples.Length - tap) % samples.Length];
                Complex32 w = weights[tap];
                i += w.I * x.I - w.Q * x.Q;
                q += w.I * x.Q + w.Q * x.I;
            }
            return new((float)i, (float)q);
        }

        public void Train(Complex32 target, Complex32 value)
        {
            double errorI = target.I - value.I;
            double errorQ = target.Q - value.Q;
            double energy = 1e-9;
            foreach (Complex32 sample in samples)
                energy += sample.I * sample.I + sample.Q * sample.Q;
            double gain = step / energy;
            for (int tap = 0; tap < weights.Length; tap++)
            {
                Complex32 x = samples[(position + samples.Length - tap) % samples.Length];
                Complex32 w = weights[tap];
                weights[tap] = new(
                    (float)(w.I + gain * (errorI * x.I + errorQ * x.Q)),
                    (float)(w.Q + gain * (errorQ * x.I - errorI * x.Q)));
            }
        }
    }

    private readonly record struct ConstellationPoint(float I, float Q, int Gray);

    private static Complex32 EstimateChannel(ReadOnlySpan<Complex32> received, ReadOnlySpan<bool> expected)
    {
        float i = 0, q = 0;
        for (int index = 0; index < received.Length; index++)
        {
            float sign = expected[index] ? -1 : 1;
            i += received[index].I * sign; q += received[index].Q * sign;
        }
        return new(i / received.Length, q / received.Length);
    }

    private static Complex32 Equalize(Complex32 value, Complex32 channel, double inverse) => new(
        (float)((value.I * channel.I + value.Q * channel.Q) * inverse),
        (float)((value.Q * channel.I - value.I * channel.Q) * inverse));
}
