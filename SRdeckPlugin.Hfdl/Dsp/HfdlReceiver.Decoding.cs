using SRdeckPlugin.Contracts;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Hfdl.Protocols;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Hfdl.Dsp;

public sealed partial class HfdlReceiver
{
    private List<HfdlFrame> DecodeAvailable(IqBlockMetadata metadata)
    {
        var output = new List<HfdlFrame>();
        int minimumSamples = (HfdlPhysicalLayer.PreambleSymbols + 72 * HfdlPhysicalLayer.SymbolsPerFrame) * SamplesPerSymbol;
        while (working.Count >= minimumSamples)
        {
            BurstCandidate? candidate = pendingCandidate ?? FindPreamble();
            if (candidate is null) break;
            pendingCandidate = candidate;
            int requiredSymbols = HfdlPhysicalLayer.PreambleSymbols + candidate.Value.Mode.FrameCount * HfdlPhysicalLayer.SymbolsPerFrame;
            int requiredSamples = checked((int)Math.Ceiling(candidate.Value.SampleOffset +
                requiredSymbols * SamplesPerSymbol));
            if (working.Count < requiredSamples) break;

            BurstCandidate acceptedCandidate = candidate.Value;
            double dataQuality = 0;
            byte[] mpdu = [];
            double carrierTrialStep = 2 * Math.PI * 2 / SymbolRate;
            (double Timing, double Carrier)[] retries =
            [
                (0, 0), (-0.5, 0), (0.5, 0), (0, -carrierTrialStep), (0, carrierTrialStep)
            ];
            foreach ((double timing, double carrier) in retries)
            {
                BurstCandidate trial = candidate.Value with
                {
                    SampleOffset = candidate.Value.SampleOffset + timing,
                    CarrierStep = candidate.Value.CarrierStep + carrier
                };
                Complex32[] symbols = SliceTrackedSymbols(trial, requiredSymbols);
                CorrectCarrier(symbols, trial.CarrierStep, trial.Channel);
                ReadOnlySpan<Complex32> data = symbols.AsSpan(HfdlPhysicalLayer.PreambleSymbols);
                bool[] shiftedM1 = HfdlPhysicalLayer.ShiftedM1(trial.Mode.M1Shift);
                bool[] decoded = HfdlPhysicalLayer.DecodeDataSymbols(data, trial.Mode,
                    shiftedM1.AsSpan(0, HfdlPhysicalLayer.ProbeSymbolsPerFrame), out double trialQuality);
                if (!HfdlPhysicalLayer.TryExtractMpdu(decoded, out byte[] trialMpdu)) continue;
                acceptedCandidate = trial;
                dataQuality = trialQuality;
                mpdu = trialMpdu;
                break;
            }
            lastPreambleCorrelation = acceptedCandidate.Correlation;
            lastDataQuality = dataQuality;
            lastCarrierOffsetHz = acceptedCandidate.CarrierStep * SymbolRate / (2 * Math.PI);
            lastDataRate = acceptedCandidate.Mode.DataRate;
            long sourcePosition = workingSampleStart + (long)Math.Round(candidate.Value.SampleOffset *
                (double)inputSampleRate / WorkingSampleRate);
            if (mpdu.Length >= 5 && HfdlCrc.IsValid(mpdu))
            {
                HfdlModulation modulation = acceptedCandidate.Mode.DataRate switch
                { 1800 => HfdlModulation.EightPsk, 1200 => HfdlModulation.Qpsk, _ => HfdlModulation.Bpsk };
                output.Add(new(mpdu, metadata.UtcTimestamp, metadata.StreamId, sourcePosition,
                    metadata.CenterFrequencyHz, modulation,
                    Math.Clamp((acceptedCandidate.Correlation + dataQuality) * 0.5, 0, 1)));
                ValidFrameCount++;
            }
            else RejectedFrameCount++;
            pendingCandidate = null;
            RemoveWorking(requiredSamples);
        }
        return output;
    }

    private BurstCandidate? FindPreamble()
    {
        preambleSearchPassCount++;
        BurstCandidate? best = null;
        double scanBestCorrelation = 0;
        for (int timing = 0; timing < SamplesPerSymbol; timing++)
        {
            Complex32[] symbols = SliceSymbols(timing, (working.Count - timing) / SamplesPerSymbol);
            int maximumStart = symbols.Length - HfdlPhysicalLayer.PreambleSymbols;
            for (int start = 0; start <= maximumStart; start++)
            {
                // Reject noise with a sparse differential probe before paying
                // for the complete 127-symbol correlation. Genuine A sequences
                // remain well above this deliberately conservative gate.
                if (SparseDifferentialCorrelation(symbols, start, HfdlPhysicalLayer.A) < 0.40) continue;
                double first = DifferentialCorrelation(symbols, start, HfdlPhysicalLayer.A);
                scanBestCorrelation = Math.Max(scanBestCorrelation, first);
                if (first < MinimumPreambleCorrelation) continue;
                preambleCandidateCount++;
                double second = DifferentialCorrelation(symbols, start + 127, HfdlPhysicalLayer.A);
                double correlation = (first + second) * 0.5;
                if (second < MinimumPreambleCorrelation || (best is not null && correlation <= best.Value.Correlation)) continue;

                double carrierStep = EstimateCarrierStep(symbols, start, HfdlPhysicalLayer.A);
                Complex32 channel = EstimateChannel(symbols, start, HfdlPhysicalLayer.A, carrierStep);
                HfdlPhysicalMode? mode = IdentifyMode(symbols, start + 254, carrierStep, channel, out double modeCorrelation);
                if (mode is null || modeCorrelation < MinimumPreambleCorrelation) continue;
                bool[] shiftedM1 = HfdlPhysicalLayer.ShiftedM1(mode.Value.M1Shift);
                double m2Correlation = AbsoluteCorrelation(symbols, start + 381,
                    shiftedM1.AsSpan(0, HfdlPhysicalLayer.ProbeSymbolsPerFrame), carrierStep, channel);
                double tailCorrelation = AbsoluteCorrelation(symbols, start + 396,
                    HfdlPhysicalLayer.T, carrierStep, channel);
                if (m2Correlation < MinimumPreambleCorrelation || tailCorrelation < MinimumPreambleCorrelation) continue;
                Complex32 burstRelativeChannel = Rotate(channel, carrierStep * start);
                best = new(timing + start * SamplesPerSymbol, carrierStep, burstRelativeChannel, mode.Value,
                    (correlation + modeCorrelation + m2Correlation + tailCorrelation) * 0.25);
            }
        }
        lastSearchBestCorrelation = scanBestCorrelation;
        if (best is not null)
        {
            best = RefineCandidate(best.Value);
            synchronizationCount++;
        }
        return best;
    }

    private BurstCandidate RefineCandidate(BurstCandidate initial)
    {
        BurstCandidate best = initial;
        for (int quarter = -3; quarter <= 3; quarter++)
        {
            double sampleOffset = initial.SampleOffset + quarter * 0.25;
            if (sampleOffset < 0 || sampleOffset + HfdlPhysicalLayer.PreambleSymbols * SamplesPerSymbol + 1 >= working.Count)
                continue;
            Complex32[] symbols = SliceSymbols(sampleOffset, HfdlPhysicalLayer.PreambleSymbols);
            double first = DifferentialCorrelation(symbols, 0, HfdlPhysicalLayer.A);
            double second = DifferentialCorrelation(symbols, 127, HfdlPhysicalLayer.A);
            if (first < MinimumPreambleCorrelation || second < MinimumPreambleCorrelation) continue;
            double carrierStep = EstimateCarrierStep(symbols, 0, HfdlPhysicalLayer.A);
            Complex32 channel = EstimateChannel(symbols, 0, HfdlPhysicalLayer.A, carrierStep);
            HfdlPhysicalMode? mode = IdentifyMode(symbols, 254, carrierStep, channel, out double modeCorrelation);
            if (mode is null) continue;
            bool[] shiftedM1 = HfdlPhysicalLayer.ShiftedM1(mode.Value.M1Shift);
            double m2 = AbsoluteCorrelation(symbols, 381,
                shiftedM1.AsSpan(0, HfdlPhysicalLayer.ProbeSymbolsPerFrame), carrierStep, channel);
            double tail = AbsoluteCorrelation(symbols, 396, HfdlPhysicalLayer.T, carrierStep, channel);
            double score = ((first + second) * 0.5 + modeCorrelation + m2 + tail) * 0.25;
            if (score <= best.Correlation) continue;
            best = new(sampleOffset, carrierStep, channel, mode.Value, score);
        }
        return best;
    }

    private static HfdlPhysicalMode? IdentifyMode(ReadOnlySpan<Complex32> symbols, int offset,
        double carrierStep, Complex32 channel, out double bestCorrelation)
    {
        HfdlPhysicalMode? best = null;
        bestCorrelation = 0;
        double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
        foreach (HfdlPhysicalMode mode in HfdlPhysicalMode.All)
        {
            bool[] sequence = HfdlPhysicalLayer.ShiftedM1(mode.M1Shift);
            double real = 0, imaginary = 0, energy = 0;
            for (int index = 0; index < sequence.Length; index++)
            {
                Complex32 value = Rotate(symbols[offset + index], -carrierStep * (offset + index));
                value = Equalize(value, channel, inverse);
                float sign = sequence[index] ? -1 : 1;
                real += value.I * sign; imaginary += value.Q * sign;
                energy += Math.Sqrt(value.I * value.I + value.Q * value.Q);
            }
            double correlation = Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);
            if (correlation <= bestCorrelation) continue;
            bestCorrelation = correlation; best = mode;
        }
        return best;
    }

    private static double DifferentialCorrelation(ReadOnlySpan<Complex32> symbols, int offset, ReadOnlySpan<bool> expected)
    {
        if (offset < 0 || offset + expected.Length > symbols.Length) return 0;
        double real = 0, imaginary = 0, energy = 0;
        for (int index = 1; index < expected.Length; index++)
        {
            Complex32 previous = symbols[offset + index - 1], current = symbols[offset + index];
            double i = previous.I * current.I + previous.Q * current.Q;
            double q = previous.I * current.Q - previous.Q * current.I;
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += i * sign; imaginary += q * sign;
            energy += Math.Sqrt((previous.I * previous.I + previous.Q * previous.Q) *
                                (current.I * current.I + current.Q * current.Q));
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);
    }

    private static double SparseDifferentialCorrelation(ReadOnlySpan<Complex32> symbols, int offset,
        ReadOnlySpan<bool> expected)
    {
        if (offset < 0 || offset + expected.Length > symbols.Length) return 0;
        double real = 0, imaginary = 0, energy = 0;
        const int stride = 8;
        for (int index = 1; index < expected.Length; index += stride)
        {
            Complex32 previous = symbols[offset + index - 1], current = symbols[offset + index];
            double i = previous.I * current.I + previous.Q * current.Q;
            double q = previous.I * current.Q - previous.Q * current.I;
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += i * sign;
            imaginary += q * sign;
            energy += Math.Sqrt((previous.I * previous.I + previous.Q * previous.Q) *
                                (current.I * current.I + current.Q * current.Q));
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);
    }

    private static double EstimateCarrierStep(ReadOnlySpan<Complex32> symbols, int offset, ReadOnlySpan<bool> expected)
    {
        double real = 0, imaginary = 0;
        for (int index = 1; index < expected.Length; index++)
        {
            Complex32 previous = symbols[offset + index - 1], current = symbols[offset + index];
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
        }
        return Math.Atan2(imaginary, real);
    }

    private static double AbsoluteCorrelation(ReadOnlySpan<Complex32> symbols, int offset,
        ReadOnlySpan<bool> expected, double carrierStep, Complex32 channel)
    {
        double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
        double real = 0, imaginary = 0, energy = 0;
        for (int index = 0; index < expected.Length; index++)
        {
            Complex32 value = Equalize(Rotate(symbols[offset + index],
                -carrierStep * (offset + index)), channel, inverse);
            double sign = expected[index] ? -1 : 1;
            real += value.I * sign; imaginary += value.Q * sign;
            energy += Math.Sqrt(value.I * value.I + value.Q * value.Q);
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);
    }

    private static Complex32 EstimateChannel(ReadOnlySpan<Complex32> symbols, int offset,
        ReadOnlySpan<bool> expected, double carrierStep)
    {
        double i = 0, q = 0;
        for (int index = 0; index < expected.Length; index++)
        {
            Complex32 value = Rotate(symbols[offset + index], -carrierStep * (offset + index));
            double sign = expected[index] ? -1 : 1;
            i += value.I * sign; q += value.Q * sign;
        }
        return new((float)(i / expected.Length), (float)(q / expected.Length));
    }

    private static void CorrectCarrier(Span<Complex32> symbols, double carrierStep, Complex32 channel)
    {
        double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
        for (int index = 0; index < symbols.Length; index++)
            symbols[index] = Equalize(Rotate(symbols[index], -carrierStep * index), channel, inverse);
    }

    private Complex32[] SliceSymbols(double sampleOffset, int count)
    {
        var output = new Complex32[count];
        for (int symbol = 0; symbol < count; symbol++)
        {
            float i = 0, q = 0;
            double offset = sampleOffset + symbol * SamplesPerSymbol;
            for (int sample = 0; sample < SamplesPerSymbol; sample++)
            {
                double position = offset + sample;
                int lower = (int)Math.Floor(position);
                double fraction = position - lower;
                Complex32 left = working[Math.Clamp(lower, 0, working.Count - 1)];
                Complex32 right = working[Math.Clamp(lower + 1, 0, working.Count - 1)];
                i += (float)(left.I + (right.I - left.I) * fraction);
                q += (float)(left.Q + (right.Q - left.Q) * fraction);
            }
            output[symbol] = new(i / SamplesPerSymbol, q / SamplesPerSymbol);
        }
        return output;
    }

    private Complex32[] SliceTrackedSymbols(BurstCandidate candidate, int count)
    {
        var output = new Complex32[count];
        Complex32[] preamble = SliceSymbols(candidate.SampleOffset, HfdlPhysicalLayer.PreambleSymbols);
        preamble.CopyTo(output, 0);
        bool[] probe = HfdlPhysicalLayer.ShiftedM1(candidate.Mode.M1Shift);
        double timingAdjustment = 0;
        for (int frame = 0; frame < candidate.Mode.FrameCount; frame++)
        {
            int symbolOffset = HfdlPhysicalLayer.PreambleSymbols + frame * HfdlPhysicalLayer.SymbolsPerFrame;
            double frameSampleOffset = candidate.SampleOffset + symbolOffset * SamplesPerSymbol;
            double bestAdjustment = timingAdjustment;
            Complex32[] currentProbe = SliceSymbols(frameSampleOffset +
                HfdlPhysicalLayer.DataSymbolsPerFrame * SamplesPerSymbol + timingAdjustment,
                HfdlPhysicalLayer.ProbeSymbolsPerFrame);
            double bestScore = DifferentialCorrelation(currentProbe, 0,
                probe.AsSpan(0, HfdlPhysicalLayer.ProbeSymbolsPerFrame));
            for (int quarter = -2; quarter <= 2; quarter++)
            {
                if (quarter == 0) continue;
                double adjustment = timingAdjustment + quarter * 0.25;
                Complex32[] receivedProbe = SliceSymbols(frameSampleOffset +
                    HfdlPhysicalLayer.DataSymbolsPerFrame * SamplesPerSymbol + adjustment,
                    HfdlPhysicalLayer.ProbeSymbolsPerFrame);
                double score = DifferentialCorrelation(receivedProbe, 0,
                    probe.AsSpan(0, HfdlPhysicalLayer.ProbeSymbolsPerFrame));
                // Hysteresis prevents a flat correlation plateau from causing a
                // random timing walk while still following a real clock drift.
                if (score <= bestScore + 0.003) continue;
                bestScore = score;
                bestAdjustment = adjustment;
            }
            timingAdjustment = Math.Clamp(bestAdjustment, -4, 4);
            Complex32[] symbols = SliceSymbols(frameSampleOffset + timingAdjustment,
                HfdlPhysicalLayer.SymbolsPerFrame);
            symbols.CopyTo(output, symbolOffset);
        }
        return output;
    }

    private void RemoveWorking(int count)
    {
        if (count <= 0) return;
        working.RemoveRange(0, count);
        workingSampleStart += (long)Math.Round(count * (double)inputSampleRate / WorkingSampleRate);
    }

    private static Complex32 Rotate(Complex32 value, double phase)
    {
        double cosine = Math.Cos(phase), sine = Math.Sin(phase);
        return new((float)(value.I * cosine - value.Q * sine), (float)(value.I * sine + value.Q * cosine));
    }
    private static Complex32 Equalize(Complex32 value, Complex32 channel, double inverse) => new(
        (float)((value.I * channel.I + value.Q * channel.Q) * inverse),
        (float)((value.Q * channel.I - value.I * channel.Q) * inverse));

    private List<(byte[] Bytes, int BitOffset)> ExtractFlagDelimitedFrames()
    {
        var flags = new List<int>();
        for (int index = 0; index + 8 <= postFecBits.Count; index++) if (IsFlag(index)) { flags.Add(index); index += 7; }
        var frames = new List<(byte[], int)>();
        for (int index = 0; index + 1 < flags.Count; index++)
        {
            int start = flags[index] + 8, end = flags[index + 1];
            if (end - start < 24) continue;
            byte[] bytes = PackLsbFirst(Unstuff(postFecBits.GetRange(start, end - start)));
            if (bytes.Length >= 3) frames.Add((bytes, start));
        }
        if (flags.Count > 0) postFecBits.RemoveRange(0, flags[^1]);
        else if (postFecBits.Count > 65_536) postFecBits.RemoveRange(0, postFecBits.Count - 8);
        return frames;
    }
    private bool IsFlag(int offset)
    { for (int bit = 0; bit < 8; bit++) if (postFecBits[offset + bit] != ((0x7e & (1 << bit)) != 0)) return false; return true; }
    private static List<bool> Unstuff(List<bool> input)
    { var output = new List<bool>(input.Count); int ones = 0; foreach (bool bit in input) { if (!bit && ones == 5) { ones = 0; continue; } output.Add(bit); ones = bit ? ones + 1 : 0; } return output; }
    private static byte[] PackLsbFirst(List<bool> input)
    { var output = new byte[input.Count / 8]; for (int index = 0; index < output.Length; index++) for (int bit = 0; bit < 8; bit++) if (input[index * 8 + bit]) output[index] |= (byte)(1 << bit); return output; }


    /// <summary>
    /// Converts zero-IF HFDL channel IQ back to the conventional 1,440 Hz
    /// receiver tone and applies a slow, modulation-preserving monitor AGC.
    /// </summary>
    internal sealed class AudioMonitor
    {
        private const double MonitorCenterHz = 1_440;
        private static readonly double RotationI = Math.Cos(2 * Math.PI * MonitorCenterHz / WorkingSampleRate);
        private static readonly double RotationQ = Math.Sin(2 * Math.PI * MonitorCenterHz / WorkingSampleRate);
        private double oscillatorI = 1;
        private double oscillatorQ;
        private int normalizationCounter;
        private double signalPower;

        public float Process(float inputI, float inputQ)
        {
            signalPower += 0.001 * (inputI * inputI + inputQ * inputQ - signalPower);
            double audio = inputI * oscillatorI - inputQ * oscillatorQ;
            double nextI = oscillatorI * RotationI - oscillatorQ * RotationQ;
            oscillatorQ = oscillatorI * RotationQ + oscillatorQ * RotationI;
            oscillatorI = nextI;
            if (++normalizationCounter == 4_096)
            {
                double inverseMagnitude = 1 / Math.Sqrt(
                    oscillatorI * oscillatorI + oscillatorQ * oscillatorQ);
                oscillatorI *= inverseMagnitude;
                oscillatorQ *= inverseMagnitude;
                normalizationCounter = 0;
            }
            double gain = 0.22 / Math.Sqrt(Math.Max(signalPower, 1e-10));
            return (float)Math.Tanh(audio * Math.Min(gain, 2_000));
        }

        public void Reset()
        {
            oscillatorI = 1;
            oscillatorQ = 0;
            normalizationCounter = 0;
            signalPower = 0;
        }
    }

    private readonly record struct BurstCandidate(double SampleOffset, double CarrierStep, Complex32 Channel,
        HfdlPhysicalMode Mode, double Correlation);
}
