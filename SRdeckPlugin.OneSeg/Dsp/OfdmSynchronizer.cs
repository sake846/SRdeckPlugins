using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Dsp;

internal readonly record struct OfdmSymbol(
    IsdbTMode Mode,
    IsdbTGuardInterval GuardInterval,
    ReadOnlyMemory<ComplexF> Spectrum,
    double FrequencyOffsetHz,
    double CyclicPrefixCorrelation,
    bool TimingDiscontinuity);

internal sealed class OfdmSynchronizer
{
    private const int TimingLookbehind = 8;
    private const int TimingTrackingRadius = 2;
    private const int MaximumConsecutiveWideTimingRecoveries = 2;
    private static readonly IsdbTGuardInterval[] Guards =
    [
        IsdbTGuardInterval.OneQuarter,
        IsdbTGuardInterval.OneEighth,
        IsdbTGuardInterval.OneSixteenth,
        IsdbTGuardInterval.OneThirtySecond
    ];

    private readonly ComplexSampleBuffer buffer = new();
    private IsdbTMode mode;
    private IsdbTGuardInterval guard;
    private Radix2Fft? fft;
    private ComplexF[] time = [];
    private ComplexF[] spectrum = [];
    private double oscillatorPhase;
    private double frequencyOffsetHz;
    private int consecutiveFailures;
    private int consecutiveWideTimingRecoveries;
    private bool hasTimingLookbehind;

    internal long Acquisitions { get; private set; }
    internal long CorrelationFailures { get; private set; }
    internal long LockLosses { get; private set; }
    internal long TimingSamplesSkipped { get; private set; }
    internal int MaximumTimingSkip { get; private set; }
    internal long WideTimingRecoveries { get; private set; }
    internal long PredictedTimingSymbols { get; private set; }

    public bool IsLocked => mode != IsdbTMode.Unknown;
    public IsdbTMode Mode => mode;
    public IsdbTGuardInterval GuardInterval => guard;
    public double FrequencyOffsetHz => frequencyOffsetHz;

    public void Reset()
    {
        buffer.Clear();
        mode = IsdbTMode.Unknown;
        guard = IsdbTGuardInterval.Unknown;
        fft = null;
        time = [];
        spectrum = [];
        oscillatorPhase = 0;
        frequencyOffsetHz = 0;
        consecutiveFailures = 0;
        consecutiveWideTimingRecoveries = 0;
        hasTimingLookbehind = false;
        Acquisitions = 0;
        CorrelationFailures = 0;
        LockLosses = 0;
        TimingSamplesSkipped = 0;
        MaximumTimingSkip = 0;
        WideTimingRecoveries = 0;
        PredictedTimingSymbols = 0;
    }

    public void Append(ReadOnlySpan<Complex32> samples) => buffer.Append(samples);

    public bool TryRead(out OfdmSymbol symbol)
    {
        symbol = default;
        while (true)
        {
            if (!IsLocked && !TryAcquire()) return false;
            int fftSize = IsdbTParameters.FftSize(mode);
            int guardSize = IsdbTParameters.GuardSamples(mode, guard);
            int symbolSize = fftSize + guardSize;
            int expectedStart = hasTimingLookbehind ? TimingLookbehind : 0;
            int localFrom = hasTimingLookbehind ? expectedStart - TimingTrackingRadius : 0;
            int localTo = hasTimingLookbehind ? expectedStart + TimingTrackingRadius : 0;
            if (buffer.Count < localTo + symbolSize) return false;

            ReadOnlySpan<Complex32> samples = buffer.Span;
            // Keep a short overlap from the preceding symbol.  This permits a
            // symmetric early/late search around the predicted boundary, tracking
            // tuner sample-clock error without the one-way random walk caused by a
            // forward-only search.
            (int start, ComplexF correlation, double score) = FindBestCorrelation(
                samples, fftSize, guardSize, localFrom, localTo, 1);
            bool usedPredictedTiming = false;
            bool usedWideTimingRecovery = false;
            bool timingDiscontinuity = !hasTimingLookbehind;
            if (score < 0.16 && consecutiveFailures == 0)
            {
                // A single damaged cyclic prefix must not delete a complete OFDM
                // symbol: that permanently destroys the time-interleaver cadence.
                // Hold the established timing and CFO for one symbol.  A repeated
                // miss still falls through to the wide reacquisition below.
                start = expectedStart;
                double predictedPhaseAdvance = frequencyOffsetHz * 2.0 * Math.PI *
                    fftSize / IsdbTParameters.OutputSampleRateHz;
                correlation = ComplexF.Unit(predictedPhaseAdvance);
                usedPredictedTiming = true;
                consecutiveFailures = 1;
                PredictedTimingSymbols++;
            }
            if (!usedPredictedTiming && score < 0.16)
            {
                int wideMaximum = Math.Min(fftSize + guardSize,
                    samples.Length - fftSize - guardSize);
                if (wideMaximum > localTo)
                {
                    (int coarse, _, _) = FindBestCorrelation(samples, fftSize, guardSize,
                        localTo + 1, wideMaximum, 4);
                    int refineFrom = Math.Max(localTo + 1, coarse - 5);
                    int refineTo = Math.Min(wideMaximum, coarse + 5);
                    (int wideStart, ComplexF wideCorrelation, double wideScore) =
                        FindBestCorrelation(samples, fftSize, guardSize, refineFrom, refineTo, 1);
                    if (wideScore >= 0.16)
                    {
                        start = wideStart;
                        correlation = wideCorrelation;
                        score = wideScore;
                        WideTimingRecoveries++;
                        usedWideTimingRecovery = true;
                        timingDiscontinuity = true;

                        // A wrong short guard interval can still correlate strongly
                        // with a suffix of the real cyclic prefix. It then advances
                        // by the wrong symbol length and repeatedly finds the next
                        // true boundary only through the wide search. Treat two such
                        // recoveries without stable local tracking as evidence that
                        // mode/GI acquisition itself was wrong. Discard the current
                        // input block. The receiver observes the loss
                        // on the next block, resets all downstream interleavers, and
                        // then reconsiders every mode and guard interval.
                        if (++consecutiveWideTimingRecoveries >=
                            MaximumConsecutiveWideTimingRecoveries)
                        {
                            LoseLock();
                            continue;
                        }
                    }
                }
            }
            if (!usedPredictedTiming && score < 0.16)
            {
                CorrelationFailures++;
                consecutiveFailures++;
                buffer.Consume(Math.Min(buffer.Count, Math.Max(1, (fftSize + guardSize) / 2)));
                hasTimingLookbehind = false;
                if (consecutiveFailures >= 3) LoseLock();
                // A failed correlation consumed input and may have exposed the next
                // valid symbol.  Keep searching in the same callback instead of
                // leaving a growing backlog that advances only once per IQ block.
                continue;
            }

            if (!usedPredictedTiming)
            {
                consecutiveFailures = 0;
                if (!usedWideTimingRecovery) consecutiveWideTimingRecoveries = 0;
            }
            double phaseAdvance = Math.Atan2(correlation.Imaginary, correlation.Real);
            double estimateHz = phaseAdvance * IsdbTParameters.OutputSampleRateHz /
                (2.0 * Math.PI * fftSize);
            estimateHz = Math.Clamp(estimateHz, -1000.0, 1000.0);
            frequencyOffsetHz = frequencyOffsetHz == 0
                ? estimateHz : frequencyOffsetHz * 0.95 + estimateHz * 0.05;

            int usefulStart = start + guardSize;
            double correctionStep = -phaseAdvance / fftSize;
            ComplexF rotation = ComplexF.Unit(
                oscillatorPhase + correctionStep * usefulStart);
            ComplexF rotationStep = ComplexF.Unit(correctionStep);
            for (int index = 0; index < fftSize; index++)
            {
                Complex32 source = samples[usefulStart + index];
                ComplexF sample = new(source.I, source.Q);
                time[index] = sample * rotation;
                rotation = rotation * rotationStep;
                if ((index & 63) == 63)
                {
                    float magnitudeSquared = rotation.MagnitudeSquared;
                    if (magnitudeSquared > 0 && float.IsFinite(magnitudeSquared))
                        rotation *= 1f / MathF.Sqrt(magnitudeSquared);
                }
            }
            fft!.Forward(time, spectrum);
            int timingAdjustment = start - expectedStart;
            if (timingAdjustment > 0) TimingSamplesSkipped += timingAdjustment;
            MaximumTimingSkip = Math.Max(MaximumTimingSkip, Math.Abs(timingAdjustment));
            int consume = start + symbolSize - TimingLookbehind;
            oscillatorPhase = Math.IEEERemainder(
                oscillatorPhase + correctionStep * consume, 2.0 * Math.PI);
            buffer.Consume(consume);
            hasTimingLookbehind = true;
            symbol = new(mode, guard, spectrum, frequencyOffsetHz, score, timingDiscontinuity);
            return true;
        }
    }

    private bool TryAcquire()
    {
        if (buffer.Count < 8_192) return false;
        ReadOnlySpan<Complex32> samples = buffer.Span;
        IsdbTMode bestMode = IsdbTMode.Unknown;
        IsdbTGuardInterval bestGuard = IsdbTGuardInterval.Unknown;
        int bestStart = 0;
        double bestScore = 0;
        ComplexF bestCorrelation = default;
        double bestReliableWeightedScore = -1;

        foreach (IsdbTMode candidateMode in new[] { IsdbTMode.Mode3, IsdbTMode.Mode2, IsdbTMode.Mode1 })
        {
            int n = IsdbTParameters.FftSize(candidateMode);
            foreach (IsdbTGuardInterval candidateGuard in Guards)
            {
                int g = IsdbTParameters.GuardSamples(candidateMode, candidateGuard);
                int stop = Math.Min(samples.Length - n - g, (n + g) * 4);
                if (stop <= 0) continue;
                int acquisitionStride = Math.Clamp(g / 32, 2, 8);
                (int coarse, _, _) = FindBestCorrelation(samples, n, g, 0, stop,
                    acquisitionStride);
                int from = Math.Max(0, coarse - 6);
                int to = Math.Min(stop, coarse + 6);
                (int start, ComplexF corr, double score) = FindBestCorrelation(samples, n, g, from, to, 1);
                // A cyclic prefix also correlates over every shorter suffix. Selecting only
                // the normalized score therefore biases acquisition toward GI 1/32, while
                // selecting only the longest candidate can accept a partial accidental
                // correlation as GI 1/4. Weight reliable scores by their correlated sample
                // count so the complete prefix wins both cases.
                bool reliable = score >= 0.35;
                double weightedScore = score * g;
                bool betterReliable = reliable &&
                    (weightedScore > bestReliableWeightedScore ||
                     (weightedScore == bestReliableWeightedScore && score > bestScore));
                bool betterFallback = bestReliableWeightedScore < 0 && score > bestScore;
                if (!betterReliable && !betterFallback) continue;
                bestScore = score;
                bestMode = candidateMode;
                bestGuard = candidateGuard;
                bestStart = start;
                bestCorrelation = corr;
                if (reliable) bestReliableWeightedScore = weightedScore;
            }
        }

        if (bestMode == IsdbTMode.Unknown || bestReliableWeightedScore < 0)
        {
            // No reliable OFDM candidate was found in the current search
            // window.  Advance past one maximum-mode symbol so the next
            // attempt examines fresh samples.  Without this, stale or
            // transitional data at the front of the buffer can block
            // acquisition indefinitely after a channel change because
            // TryAcquire always searches from position zero.
            int step = IsdbTParameters.FftSize(IsdbTMode.Mode3) +
                IsdbTParameters.GuardSamples(IsdbTMode.Mode3, IsdbTGuardInterval.OneQuarter);
            int advance = Math.Max(step, buffer.Count / 2);
            buffer.Consume(Math.Min(buffer.Count, advance));
            return false;
        }
        mode = bestMode;
        guard = bestGuard;
        Acquisitions++;
        int size = IsdbTParameters.FftSize(mode);
        fft = new Radix2Fft(size);
        time = new ComplexF[size];
        spectrum = new ComplexF[size];
        double phaseAdvance = Math.Atan2(bestCorrelation.Imaginary, bestCorrelation.Real);
        frequencyOffsetHz = phaseAdvance * IsdbTParameters.OutputSampleRateHz /
            (2.0 * Math.PI * size);
        oscillatorPhase = 0;
        consecutiveWideTimingRecoveries = 0;
        hasTimingLookbehind = false;
        if (bestStart > 0) buffer.Consume(bestStart);
        return true;
    }

    private static (int Start, ComplexF Correlation, double Score) FindBestCorrelation(
        ReadOnlySpan<Complex32> samples, int n, int g, int from, int to, int stride)
    {
        if (from < 0 || to < from || stride <= 0 || to + n + g > samples.Length)
            throw new ArgumentOutOfRangeException(nameof(from));

        int bestStart = from;
        ComplexF bestCorrelation = default;
        double best = double.NegativeInfinity;
        double real = 0, imaginary = 0, energyA = 0, energyB = 0;
        for (int index = 0; index < g; index++)
            UpdateCorrelation(samples[from + index], samples[from + n + index], 1,
                ref real, ref imaginary, ref energyA, ref energyB);

        int start = from;
        while (true)
        {
            double score = (real * real + imaginary * imaginary) /
                Math.Max(1e-20, energyA * energyB);
            if (score > best)
            {
                best = score;
                bestStart = start;
                bestCorrelation = new ComplexF((float)real, (float)imaginary);
            }

            if (start == to) break;
            int next = Math.Min(to, start + stride);
            for (int shift = 0; shift < next - start; shift++)
            {
                int leaving = start + shift;
                int entering = leaving + g;
                UpdateCorrelation(samples[leaving], samples[leaving + n], -1,
                    ref real, ref imaginary, ref energyA, ref energyB);
                UpdateCorrelation(samples[entering], samples[entering + n], 1,
                    ref real, ref imaginary, ref energyA, ref energyB);
            }
            start = next;
        }
        return (bestStart, bestCorrelation, best);
    }

    private static void UpdateCorrelation(Complex32 a, Complex32 b, int direction,
        ref double real, ref double imaginary, ref double energyA, ref double energyB)
    {
        real += direction * (a.I * b.I + a.Q * b.Q);
        imaginary += direction * (a.I * b.Q - a.Q * b.I);
        energyA += direction * (a.I * a.I + a.Q * a.Q);
        energyB += direction * (b.I * b.I + b.Q * b.Q);
    }

    private void LoseLock()
    {
        LockLosses++;
        mode = IsdbTMode.Unknown;
        guard = IsdbTGuardInterval.Unknown;
        fft = null;
        consecutiveFailures = 0;
        consecutiveWideTimingRecoveries = 0;
        hasTimingLookbehind = false;
        oscillatorPhase = 0;
        frequencyOffsetHz = 0;
        buffer.Clear();
        time = [];
        spectrum = [];
    }
}
