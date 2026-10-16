using SRdeckPlugin.Contracts;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Hfdl.Protocols;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Hfdl.Dsp;

public sealed partial class HfdlReceiver
{
    private const int SparseDifferentialStride = 8;
    // Squared form of the established conservative 0.40 coarse gate. This
    // preserves its decision boundary without a square root per scan point.
    private const double SparsePreambleCorrelationThresholdSquared = 0.16;

    private IReadOnlyList<HfdlFrame> DecodeAvailable(IqBlockMetadata metadata)
    {
        List<HfdlFrame>? output = null;
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
            ReadOnlySpan<(double Timing, double Carrier)> retries =
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
                ReadOnlySpan<bool> training = HfdlPhysicalLayer.T[..HfdlPhysicalLayer.ProbeSymbolsPerFrame];
                bool[] decoded = HfdlPhysicalLayer.DecodeBurstSymbols(symbols, trial.Mode,
                    training, out double trialQuality);
                dataQuality = Math.Max(dataQuality, trialQuality);
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
            if (mpdu.Length > 0)
            {
                HfdlModulation modulation = acceptedCandidate.Mode.DataRate switch
                { 1800 => HfdlModulation.EightPsk, 1200 => HfdlModulation.Qpsk, _ => HfdlModulation.Bpsk };
                (output ??= []).Add(new(mpdu, metadata.UtcTimestamp, metadata.StreamId, sourcePosition,
                    metadata.CenterFrequencyHz, modulation,
                    Math.Clamp((acceptedCandidate.Correlation + dataQuality) * 0.5, 0, 1)));
                ValidFrameCount++;
            }
            else RejectedFrameCount++;
            pendingCandidate = null;
            RemoveWorking(requiredSamples);
        }
        return output is null ? Array.Empty<HfdlFrame>() : output;
    }

    private BurstCandidate? FindPreamble()
    {
        long searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        preambleSearchPassCount++;
        BurstCandidate? best = null;
        double scanBestCorrelation = 0;
        int sparsePointCount = SparseDifferentialPointCount(HfdlPhysicalLayer.A.Length);
        int sampleBase = preambleSearchSampleOffset / SamplesPerSymbol * SamplesPerSymbol;
        PrepareIntegerSymbols(sampleBase);
        for (int timing = 0; timing < SamplesPerSymbol; timing++)
        {
            Span<Complex32> symbols = integerSymbolScratch.AsSpan(
                timing * integerSymbolStride, (working.Count - sampleBase - timing) / SamplesPerSymbol);
            Span<double> magnitudes = PrepareSymbolMetrics(symbols);
            ReadOnlySpan<double> differentialEnergyPrefix =
                differentialEnergyPrefixScratch.AsSpan(0, symbols.Length);
            preambleMagnitudeCalculationCount += symbols.Length;
            int maximumStart = symbols.Length - HfdlPhysicalLayer.PreambleSymbols;
            int minimumStart = Math.Max(0, (preambleSearchSampleOffset - sampleBase - timing + SamplesPerSymbol - 1) /
                SamplesPerSymbol);
            if (minimumStart > maximumStart) continue;

            preambleCorrelationEvaluationCount++;
            double currentSparseSquared = SparseDifferentialCorrelationSquared(
                symbols, magnitudes, minimumStart, HfdlPhysicalLayer.A,
                out double currentReal, out double currentImaginary);
            preambleSparseCorrelationPointCount += sparsePointCount;
            if (currentSparseSquared >= SparsePreambleCorrelationThresholdSquared)
                preambleSparseGateCandidateCount++;
            double leftSparseSquared = double.NegativeInfinity;
            int currentStart = minimumStart;
            // A sentinel right neighbor lets the final start use the same hot
            // loop as every interior three-point local-maximum decision.
            for (int nextStart = minimumStart + 1; nextStart <= maximumStart + 1; nextStart++)
            {
                double nextSparseSquared = double.NegativeInfinity;
                double nextReal = 0;
                double nextImaginary = 0;
                if (nextStart <= maximumStart)
                {
                    preambleCorrelationEvaluationCount++;
                    nextSparseSquared = SparseDifferentialCorrelationSquared(
                        symbols, magnitudes, nextStart, HfdlPhysicalLayer.A,
                        out nextReal, out nextImaginary);
                    preambleSparseCorrelationPointCount += sparsePointCount;
                    if (nextSparseSquared >= SparsePreambleCorrelationThresholdSquared)
                        preambleSparseGateCandidateCount++;
                }

                int candidateStart = currentStart;
                double candidateReal = currentReal;
                double candidateImaginary = currentImaginary;
                bool isCoarsePeak = currentSparseSquared >= SparsePreambleCorrelationThresholdSquared &&
                    currentSparseSquared >= leftSparseSquared &&
                    currentSparseSquared >= nextSparseSquared;
                leftSparseSquared = currentSparseSquared;
                currentSparseSquared = nextSparseSquared;
                currentReal = nextReal;
                currentImaginary = nextImaginary;
                currentStart = nextStart;
                if (!isCoarsePeak) continue;

                preambleCoarsePeakCandidateCount++;
                double first = CompleteDifferentialCorrelation(
                    symbols, differentialEnergyPrefix, candidateStart, HfdlPhysicalLayer.A,
                    candidateReal, candidateImaginary, out double carrierStep);
                preambleFullCorrelationEvaluationCount++;
                preambleFullCorrelationPointCount += HfdlPhysicalLayer.A.Length - 1 - sparsePointCount;
                preambleReusedCorrelationPointCount += sparsePointCount;
                scanBestCorrelation = Math.Max(scanBestCorrelation, first);
                if (first < MinimumPreambleCorrelation) continue;
                preambleCandidateCount++;
                double second = DifferentialCorrelation(
                    symbols, differentialEnergyPrefix, candidateStart + 127, HfdlPhysicalLayer.A, out _);
                preambleFullCorrelationEvaluationCount++;
                preambleFullCorrelationPointCount += HfdlPhysicalLayer.A.Length - 1;
                double correlation = (first + second) * 0.5;
                if (second < MinimumPreambleCorrelation ||
                    (best is not null && correlation <= best.Value.Correlation)) continue;

                Complex32 channel = EstimateChannel(
                    symbols, candidateStart, HfdlPhysicalLayer.A, carrierStep);
                HfdlPhysicalMode? mode = IdentifyMode(symbols, magnitudes, candidateStart + 254,
                    carrierStep, channel, out double modeCorrelation);
                if (mode is null || modeCorrelation < MinimumPreambleCorrelation) continue;
                ReadOnlySpan<bool> shiftedM1 = HfdlPhysicalLayer.ShiftedM1(mode.Value.M1Shift);
                double m2Correlation = AbsoluteCorrelation(symbols, magnitudes, candidateStart + 381,
                    shiftedM1[..HfdlPhysicalLayer.ProbeSymbolsPerFrame], carrierStep, channel);
                double tailCorrelation = AbsoluteCorrelation(symbols, magnitudes, candidateStart + 396,
                    HfdlPhysicalLayer.T, carrierStep, channel);
                if (m2Correlation < MinimumPreambleCorrelation ||
                    tailCorrelation < MinimumPreambleCorrelation) continue;
                Complex32 burstRelativeChannel = Rotate(channel, carrierStep * candidateStart);
                best = new(sampleBase + timing + candidateStart * SamplesPerSymbol, carrierStep,
                    burstRelativeChannel, mode.Value,
                    (correlation + modeCorrelation + m2Correlation + tailCorrelation) * 0.25);
            }
        }
        lastSearchBestCorrelation = scanBestCorrelation;
        if (best is null)
        {
            // Starts up to working.Count - preamble length were fully tested.
            // Keep the first incomplete sample position so the next block only
            // evaluates newly completed preambles.
            preambleSearchSampleOffset = Math.Max(0,
                working.Count - HfdlPhysicalLayer.PreambleSymbols * SamplesPerSymbol + 1);
        }
        if (best is not null)
        {
            best = RefineCandidate(best.Value);
            synchronizationCount++;
        }
        preambleSearchCpuTicks += System.Diagnostics.Stopwatch.GetTimestamp() - searchStarted;
        return best;
    }

    internal static PreambleSearchProbe EvaluatePreambleSearch(
        ReadOnlySpan<Complex32> workingSamples)
    {
        var receiver = new HfdlReceiver();
        receiver.working.EnsureCapacity(workingSamples.Length);
        foreach (Complex32 sample in workingSamples) receiver.working.Add(sample);
        BurstCandidate? candidate = receiver.FindPreamble();
        DiagnosticsSnapshot diagnostics = receiver.GetDiagnostics();
        return new(candidate is not null,
            candidate?.SampleOffset ?? double.NaN,
            candidate?.Correlation ?? 0,
            diagnostics.PreambleSparseGateCandidateCount,
            diagnostics.PreambleCoarsePeakCandidateCount,
            diagnostics.PreambleFullCorrelationEvaluationCount,
            diagnostics.PreambleRefinementCount);
    }

    private BurstCandidate RefineCandidate(BurstCandidate initial)
    {
        preambleRefinementCount++;
        BurstCandidate best = initial;
        Span<Complex32> symbols = stackalloc Complex32[HfdlPhysicalLayer.PreambleSymbols];
        for (int quarter = -3; quarter <= 3; quarter++)
        {
            double sampleOffset = initial.SampleOffset + quarter * 0.25;
            if (sampleOffset < 0 || sampleOffset + HfdlPhysicalLayer.PreambleSymbols * SamplesPerSymbol + 1 >= working.Count)
                continue;
            SliceFractionalSymbols(sampleOffset, symbols);
            Span<double> magnitudes = PrepareSymbolMetrics(symbols);
            ReadOnlySpan<double> differentialEnergyPrefix =
                differentialEnergyPrefixScratch.AsSpan(0, symbols.Length);
            preambleMagnitudeCalculationCount += symbols.Length;
            double first = DifferentialCorrelation(
                symbols, differentialEnergyPrefix, 0, HfdlPhysicalLayer.A, out double carrierStep);
            double second = DifferentialCorrelation(
                symbols, differentialEnergyPrefix, 127, HfdlPhysicalLayer.A, out _);
            preambleFullCorrelationEvaluationCount += 2;
            preambleFullCorrelationPointCount += 2L * (HfdlPhysicalLayer.A.Length - 1);
            if (first < MinimumPreambleCorrelation || second < MinimumPreambleCorrelation) continue;
            Complex32 channel = EstimateChannel(symbols, 0, HfdlPhysicalLayer.A, carrierStep);
            HfdlPhysicalMode? mode = IdentifyMode(symbols, magnitudes, 254,
                carrierStep, channel, out double modeCorrelation);
            if (mode is null) continue;
            ReadOnlySpan<bool> shiftedM1 = HfdlPhysicalLayer.ShiftedM1(mode.Value.M1Shift);
            double m2 = AbsoluteCorrelation(symbols, magnitudes, 381,
                shiftedM1[..HfdlPhysicalLayer.ProbeSymbolsPerFrame], carrierStep, channel);
            double tail = AbsoluteCorrelation(symbols, magnitudes, 396,
                HfdlPhysicalLayer.T, carrierStep, channel);
            double score = ((first + second) * 0.5 + modeCorrelation + m2 + tail) * 0.25;
            if (score <= best.Correlation) continue;
            best = new(sampleOffset, carrierStep, channel, mode.Value, score);
        }
        return best;
    }

    private static HfdlPhysicalMode? IdentifyMode(ReadOnlySpan<Complex32> symbols,
        ReadOnlySpan<double> magnitudes, int offset, double carrierStep,
        Complex32 channel, out double bestCorrelation)
    {
        HfdlPhysicalMode? best = null;
        bestCorrelation = 0;
        double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
        double inverseMagnitude = Math.Sqrt(inverse);
        Span<Complex32> corrected = stackalloc Complex32[127];
        var rotator = new HfdlPhysicalLayer.LinearPhaseRotator(
            -carrierStep * offset, -carrierStep);
        double energy = 0;
        for (int index = 0; index < corrected.Length; index++)
        {
            corrected[index] = Equalize(rotator.RotateNext(symbols[offset + index]), channel, inverse);
            energy += magnitudes[offset + index] * inverseMagnitude;
        }
        foreach (HfdlPhysicalMode mode in HfdlPhysicalMode.All)
        {
            ReadOnlySpan<bool> sequence = HfdlPhysicalLayer.ShiftedM1(mode.M1Shift);
            double real = 0, imaginary = 0;
            for (int index = 0; index < sequence.Length; index++)
            {
                Complex32 value = corrected[index];
                float sign = sequence[index] ? -1 : 1;
                real += value.I * sign; imaginary += value.Q * sign;
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

    private static double ReferenceSparseDifferentialCorrelation(
        ReadOnlySpan<Complex32> symbols, int offset, ReadOnlySpan<bool> expected)
    {
        if (offset < 0 || offset + expected.Length > symbols.Length) return 0;
        double real = 0, imaginary = 0, energy = 0;
        for (int index = 1; index < expected.Length; index += SparseDifferentialStride)
        {
            Complex32 previous = symbols[offset + index - 1];
            Complex32 current = symbols[offset + index];
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
            energy += Math.Sqrt((previous.I * previous.I + previous.Q * previous.Q) *
                                (current.I * current.I + current.Q * current.Q));
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);
    }

    private static double DifferentialCorrelation(ReadOnlySpan<Complex32> symbols,
        ReadOnlySpan<double> differentialEnergyPrefix, int offset, ReadOnlySpan<bool> expected,
        out double phase)
    {
        phase = 0;
        if (offset < 0 || offset + expected.Length > symbols.Length) return 0;
        double real = 0, imaginary = 0;
        for (int index = 1; index < expected.Length; index++)
        {
            Complex32 previous = symbols[offset + index - 1];
            Complex32 current = symbols[offset + index];
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
        }
        double energy = differentialEnergyPrefix[offset + expected.Length - 1] -
                        differentialEnergyPrefix[offset];
        phase = Math.Atan2(imaginary, real);
        return CorrelationValue(real, imaginary, energy);
    }

    private static double SparseDifferentialCorrelationSquared(ReadOnlySpan<Complex32> symbols,
        ReadOnlySpan<double> magnitudes, int offset, ReadOnlySpan<bool> expected,
        out double real, out double imaginary)
    {
        real = 0;
        imaginary = 0;
        if (offset < 0 || offset + expected.Length > symbols.Length) return 0;
        double energy = 0;
        for (int index = 1; index < expected.Length; index += SparseDifferentialStride)
        {
            Complex32 previous = symbols[offset + index - 1];
            Complex32 current = symbols[offset + index];
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
            energy += magnitudes[offset + index - 1] * magnitudes[offset + index];
        }
        double denominator = Math.Max(energy, 1e-12);
        return (real * real + imaginary * imaginary) / (denominator * denominator);
    }

    private static double CompleteDifferentialCorrelation(ReadOnlySpan<Complex32> symbols,
        ReadOnlySpan<double> differentialEnergyPrefix, int offset, ReadOnlySpan<bool> expected,
        double sparseReal, double sparseImaginary, out double phase)
    {
        phase = 0;
        if (offset < 0 || offset + expected.Length > symbols.Length) return 0;
        double real = sparseReal;
        double imaginary = sparseImaginary;
        int nextSparsePoint = 1;
        for (int index = 1; index < expected.Length; index++)
        {
            if (index == nextSparsePoint)
            {
                nextSparsePoint += SparseDifferentialStride;
                continue;
            }
            Complex32 previous = symbols[offset + index - 1];
            Complex32 current = symbols[offset + index];
            double sign = expected[index - 1] == expected[index] ? 1 : -1;
            real += (previous.I * current.I + previous.Q * current.Q) * sign;
            imaginary += (previous.I * current.Q - previous.Q * current.I) * sign;
        }
        double energy = differentialEnergyPrefix[offset + expected.Length - 1] -
                        differentialEnergyPrefix[offset];
        phase = Math.Atan2(imaginary, real);
        return CorrelationValue(real, imaginary, energy);
    }

    private static double CorrelationValue(double real, double imaginary, double energy) =>
        Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);

    private static int SparseDifferentialPointCount(int expectedLength) =>
        expectedLength <= 1 ? 0 : (expectedLength - 2) / SparseDifferentialStride + 1;

    internal static DifferentialCorrelationProbe EvaluateDifferentialCorrelation(
        ReadOnlySpan<Complex32> symbols, int offset, ReadOnlySpan<bool> expected)
    {
        if (expected.Length < 2) throw new ArgumentException("At least two expected symbols are required.", nameof(expected));
        var magnitudes = new double[symbols.Length];
        var differentialEnergyPrefix = new double[symbols.Length];
        CalculateSymbolMetrics(symbols, magnitudes, differentialEnergyPrefix);
        int sparsePointCount = SparseDifferentialPointCount(expected.Length);
        double sparseSquared = SparseDifferentialCorrelationSquared(
            symbols, magnitudes, offset, expected, out double sparseReal, out double sparseImaginary);
        double complete = CompleteDifferentialCorrelation(
            symbols, differentialEnergyPrefix, offset, expected,
            sparseReal, sparseImaginary, out _);
        double sparseReference = ReferenceSparseDifferentialCorrelation(symbols, offset, expected);
        double reference = DifferentialCorrelation(symbols, offset, expected);
        return new(Math.Sqrt(sparseSquared), sparseReference, complete, reference,
            sparsePointCount, expected.Length - 1 - sparsePointCount,
            sparseSquared >= SparsePreambleCorrelationThresholdSquared);
    }

    private static double AbsoluteCorrelation(ReadOnlySpan<Complex32> symbols,
        ReadOnlySpan<double> magnitudes, int offset, ReadOnlySpan<bool> expected,
        double carrierStep, Complex32 channel)
    {
        double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
        double inverseMagnitude = Math.Sqrt(inverse);
        double real = 0, imaginary = 0, energy = 0;
        var rotator = new HfdlPhysicalLayer.LinearPhaseRotator(
            -carrierStep * offset, -carrierStep);
        for (int index = 0; index < expected.Length; index++)
        {
            Complex32 value = Equalize(rotator.RotateNext(symbols[offset + index]), channel, inverse);
            double sign = expected[index] ? -1 : 1;
            real += value.I * sign; imaginary += value.Q * sign;
            energy += magnitudes[offset + index] * inverseMagnitude;
        }
        return Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(energy, 1e-12);
    }

    private static Complex32 EstimateChannel(ReadOnlySpan<Complex32> symbols, int offset,
        ReadOnlySpan<bool> expected, double carrierStep)
    {
        double i = 0, q = 0;
        var rotator = new HfdlPhysicalLayer.LinearPhaseRotator(
            -carrierStep * offset, -carrierStep);
        for (int index = 0; index < expected.Length; index++)
        {
            Complex32 value = rotator.RotateNext(symbols[offset + index]);
            double sign = expected[index] ? -1 : 1;
            i += value.I * sign; q += value.Q * sign;
        }
        return new((float)(i / expected.Length), (float)(q / expected.Length));
    }

    private static void CorrectCarrier(Span<Complex32> symbols, double carrierStep, Complex32 channel)
    {
        double inverse = 1.0 / Math.Max(channel.I * channel.I + channel.Q * channel.Q, 1e-12f);
        var rotator = new HfdlPhysicalLayer.LinearPhaseRotator(0, -carrierStep);
        for (int index = 0; index < symbols.Length; index++)
            symbols[index] = Equalize(rotator.RotateNext(symbols[index]), channel, inverse);
    }

    private Span<double> PrepareSymbolMetrics(ReadOnlySpan<Complex32> symbols)
    {
        if (symbolMagnitudeScratch.Length < symbols.Length)
            Array.Resize(ref symbolMagnitudeScratch,
                Math.Max(symbols.Length, symbolMagnitudeScratch.Length * 2));
        if (differentialEnergyPrefixScratch.Length < symbols.Length)
            Array.Resize(ref differentialEnergyPrefixScratch,
                Math.Max(symbols.Length, differentialEnergyPrefixScratch.Length * 2));
        Span<double> magnitudes = symbolMagnitudeScratch.AsSpan(0, symbols.Length);
        CalculateSymbolMetrics(symbols, magnitudes,
            differentialEnergyPrefixScratch.AsSpan(0, symbols.Length));
        return magnitudes;
    }

    private static void CalculateSymbolMetrics(ReadOnlySpan<Complex32> symbols, Span<double> magnitudes,
        Span<double> differentialEnergyPrefix)
    {
        if (magnitudes.Length < symbols.Length)
            throw new ArgumentException("The magnitude buffer is too small.", nameof(magnitudes));
        if (differentialEnergyPrefix.Length < symbols.Length)
            throw new ArgumentException("The differential energy buffer is too small.", nameof(differentialEnergyPrefix));
        for (int index = 0; index < symbols.Length; index++)
        {
            Complex32 symbol = symbols[index];
            magnitudes[index] = Math.Sqrt(symbol.I * symbol.I + symbol.Q * symbol.Q);
        }
        if (symbols.Length == 0) return;
        differentialEnergyPrefix[0] = 0;
        for (int index = 1; index < symbols.Length; index++)
            differentialEnergyPrefix[index] = differentialEnergyPrefix[index - 1] +
                magnitudes[index - 1] * magnitudes[index];
    }

    private void PrepareIntegerSymbols(int sampleOffset)
    {
        integerSymbolStride = (working.Count - sampleOffset) / SamplesPerSymbol;
        int requiredLength = integerSymbolStride * SamplesPerSymbol;
        if (workingSampleScratch.Length < working.Count)
            Array.Resize(ref workingSampleScratch,
                Math.Max(working.Count, workingSampleScratch.Length * 2));
        if (integerSymbolScratch.Length < requiredLength)
            Array.Resize(ref integerSymbolScratch,
                Math.Max(requiredLength, integerSymbolScratch.Length * 2));
        if (integerSymbolStride == 0) return;
        // Linearize the circular history once, then only prepare the unsearched
        // suffix. The sample-aligned base preserves all eight timing phases.
        Span<Complex32> source = workingSampleScratch.AsSpan(0, working.Count);
        working.CopyTo(source);
        FillIntegerSymbolPhases(source[sampleOffset..],
            integerSymbolScratch.AsSpan(0, requiredLength), integerSymbolStride);
    }

    internal static Complex32[] EvaluateIntegerSymbolSlicing(
        ReadOnlySpan<Complex32> samples, int timing)
    {
        if ((uint)timing >= SamplesPerSymbol)
            throw new ArgumentOutOfRangeException(nameof(timing));
        int count = (samples.Length - timing) / SamplesPerSymbol;
        if (count <= 0) return [];
        int stride = samples.Length / SamplesPerSymbol;
        var phases = new Complex32[stride * SamplesPerSymbol];
        FillIntegerSymbolPhases(samples, phases, stride);
        var output = new Complex32[count];
        phases.AsSpan(timing * stride, count).CopyTo(output);
        return output;
    }

    private static void FillIntegerSymbolPhases(ReadOnlySpan<Complex32> samples,
        Span<Complex32> phases, int stride)
    {
        double sumI = 0;
        double sumQ = 0;
        for (int sample = 0; sample < SamplesPerSymbol; sample++)
        {
            sumI += samples[sample].I;
            sumQ += samples[sample].Q;
        }
        const double scale = 1.0 / SamplesPerSymbol;
        int lastStart = samples.Length - SamplesPerSymbol;
        for (int start = 0; start <= lastStart; start++)
        {
            int phase = start % SamplesPerSymbol;
            int symbol = start / SamplesPerSymbol;
            phases[phase * stride + symbol] =
                new((float)(sumI * scale), (float)(sumQ * scale));
            if (start == lastStart) break;
            sumI += samples[start + SamplesPerSymbol].I - samples[start].I;
            sumQ += samples[start + SamplesPerSymbol].Q - samples[start].Q;
        }
    }

    private void SliceFractionalSymbols(double sampleOffset, Span<Complex32> output)
    {
        for (int symbol = 0; symbol < output.Length; symbol++)
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
    }

    private Complex32[] SliceTrackedSymbols(BurstCandidate candidate, int count)
    {
        var output = new Complex32[count];
        SliceFractionalSymbols(candidate.SampleOffset, output.AsSpan(0, HfdlPhysicalLayer.PreambleSymbols));
        ReadOnlySpan<bool> probe = HfdlPhysicalLayer.T[..HfdlPhysicalLayer.ProbeSymbolsPerFrame];
        Span<Complex32> receivedProbe = stackalloc Complex32[HfdlPhysicalLayer.ProbeSymbolsPerFrame];
        double timingAdjustment = 0;
        for (int frame = 0; frame < candidate.Mode.FrameCount; frame++)
        {
            int symbolOffset = HfdlPhysicalLayer.PreambleSymbols + frame * HfdlPhysicalLayer.SymbolsPerFrame;
            double frameSampleOffset = candidate.SampleOffset + symbolOffset * SamplesPerSymbol;
            double bestAdjustment = timingAdjustment;
            SliceFractionalSymbols(frameSampleOffset +
                HfdlPhysicalLayer.DataSymbolsPerFrame * SamplesPerSymbol + timingAdjustment,
                receivedProbe);
            double bestScore = DifferentialCorrelation(receivedProbe, 0, probe);
            for (int quarter = -2; quarter <= 2; quarter++)
            {
                if (quarter == 0) continue;
                double adjustment = timingAdjustment + quarter * 0.25;
                SliceFractionalSymbols(frameSampleOffset +
                    HfdlPhysicalLayer.DataSymbolsPerFrame * SamplesPerSymbol + adjustment,
                    receivedProbe);
                double score = DifferentialCorrelation(receivedProbe, 0, probe);
                // Hysteresis prevents a flat correlation plateau from causing a
                // random timing walk while still following a real clock drift.
                if (score <= bestScore + 0.003) continue;
                bestScore = score;
                bestAdjustment = adjustment;
            }
            timingAdjustment = Math.Clamp(bestAdjustment, -4, 4);
            SliceFractionalSymbols(frameSampleOffset + timingAdjustment,
                output.AsSpan(symbolOffset, HfdlPhysicalLayer.SymbolsPerFrame));
        }
        return output;
    }

    private void RemoveWorking(int count)
    {
        if (count <= 0) return;
        if (pendingCandidate is { } candidate)
        {
            double adjustedSampleOffset = candidate.SampleOffset - count;
            pendingCandidate = adjustedSampleOffset >= 0
                ? candidate with { SampleOffset = adjustedSampleOffset }
                : null;
        }
        working.RemoveFirst(count);
        preambleSearchSampleOffset = Math.Max(0, preambleSearchSampleOffset - count);
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
    /// Converts zero-IF HFDL channel IQ back to the conventional USB 1,440 Hz
    /// receiver tone and applies a slow, modulation-preserving monitor AGC.
    /// </summary>
    internal sealed class AudioMonitor
    {
        private const double MonitorCenterHz = 1_440;
        private static readonly double RotationI = Math.Cos(2 * Math.PI * MonitorCenterHz / WorkingSampleRate);
        private static readonly double RotationQ = Math.Sin(2 * Math.PI * MonitorCenterHz / WorkingSampleRate);
        private readonly ComplexIqLowPassFilter usbLowPass = new();
        private double oscillatorI = 1;
        private double oscillatorQ;
        private int normalizationCounter;
        private double signalPower;

        public AudioMonitor() => usbLowPass.Configure(WorkingSampleRate, MonitorCenterHz);

        public float Process(float inputI, float inputQ)
        {
            usbLowPass.Process(inputI, inputQ, out float filteredI, out float filteredQ);
            signalPower += 0.001 * (filteredI * filteredI + filteredQ * filteredQ - signalPower);
            // Re{(I+jQ) * exp(+j*2*pi*1440*t)} is the USB orientation:
            // positive IQ modulation remains above the dial frequency.
            double audio = filteredI * oscillatorI - filteredQ * oscillatorQ;
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
            usbLowPass.Reset();
            oscillatorI = 1;
            oscillatorQ = 0;
            normalizationCounter = 0;
            signalPower = 0;
        }
    }

    internal readonly record struct DifferentialCorrelationProbe(
        double Sparse,
        double SparseReference,
        double Complete,
        double Reference,
        int SparsePointCount,
        int CompleteEvaluatedPointCount,
        bool SparseGatePassed);

    internal readonly record struct PreambleSearchProbe(
        bool Found,
        double SampleOffset,
        double Correlation,
        long SparseGateCandidateCount,
        long CoarsePeakCandidateCount,
        long FullCorrelationEvaluationCount,
        long RefinementCount);

    private readonly record struct BurstCandidate(double SampleOffset, double CarrierStep, Complex32 Channel,
        HfdlPhysicalMode Mode, double Correlation);
}
