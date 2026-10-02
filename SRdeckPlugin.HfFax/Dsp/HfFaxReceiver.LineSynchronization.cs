using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Models;
using System.Runtime.CompilerServices;

namespace SRdeckPlugin.HfFax.Dsp;

internal sealed partial class HfFaxReceiver
{
    private void ResetSignalTracking()
    {
        hasPreviousSample = false;
        ResetVideoBandwidthFilter();
        ResetAutomaticDetector();
        detectorNoisePower = InitialDetectorNoisePower;
        detectorToneNoisePower = InitialDetectorToneNoisePower;
    }

    private void ResetAutomaticDetector()
    {
        detectorSamples = 0;
        detectorUpdateCount = 0;
        detectorPower = 0;
        startWindows = releaseWindows = stopWindows = 0;
        startMissedWindows = 0;
        lastStartPower = 0;
        lastStopPower = 0;
        start300.Reset();
        start675.Reset();
        stop450.Reset();
        if (State is HfFaxReceiverState.StartDetected or HfFaxReceiverState.Phasing)
            SetState(HfFaxReceiverState.Idle);
    }

    private void UpdateLineSynchronizer(
        double normalized,
        HfFaxDecodeOptions options,
        DateTimeOffset receivedAt)
    {
        if (lineSyncDetectionSuspended)
        {
            ResetLineSyncEdgeDetector();
            lineSyncDetectionSuspended = false;
        }
        long sampleIndex = lineSyncSampleIndex++;
        if (!hasLineSyncFilteredValue)
        {
            lineSyncFilteredValue = normalized;
            hasLineSyncFilteredValue = true;
        }
        else
        {
            lineSyncFilteredValue += LineSyncFilterAlpha *
                (normalized - lineSyncFilteredValue);
        }

        int runKind = lineSyncFilteredValue >= LineSyncHighThreshold ? 1 :
            lineSyncFilteredValue <= LineSyncLowThreshold ? 0 : -1;
        if (runKind == lineSyncRunKind)
        {
            if (runKind >= 0) lineSyncRunLength++;
            return;
        }

        FinishLineSyncRun(options, receivedAt);
        lineSyncRunKind = runKind;
        lineSyncRunStart = sampleIndex;
        lineSyncRunLength = runKind >= 0 ? 1 : 0;
    }

    private void AdvanceLineSynchronizerWithoutDetection()
    {
        lineSyncSampleIndex++;
        if (lineSyncDetectionSuspended) return;
        ResetLineSyncEdgeDetector();
        lineSyncDetectionSuspended = true;
    }

    private void ResetLineSyncEdgeDetector()
    {
        hasLineSyncFilteredValue = false;
        lineSyncFilteredValue = 0;
        lineSyncRunKind = -1;
        lineSyncRunLength = 0;
        lineSyncRunStart = 0;
        previousLineSyncRunKind = -1;
        previousLineSyncRunLength = 0;
    }

    private void FinishLineSyncRun(HfFaxDecodeOptions options, DateTimeOffset receivedAt)
    {
        if (lineSyncRunKind < 0 || lineSyncRunLength <= 0) return;

        double nominalPeriod = linePeriodSamples > 0
            ? linePeriodSamples / SlantScale(options.SlantCorrectionPpm)
            : BaseLinePeriodSamples(options.LinesPerMinute);
        int minimumBaselineLength = Math.Max(32,
            (int)Math.Round(nominalPeriod * LineSyncMinimumBaselineRatio));
        int minimumPulseLength = Math.Max(8, (int)Math.Round(nominalPeriod * 0.002));
        int maximumPulseLength = Math.Max(minimumPulseLength + 1,
            (int)Math.Round(nominalPeriod * LineSyncMaximumPulseRatio));

        bool isPulse = lineSyncRunLength >= minimumPulseLength &&
            lineSyncRunLength <= maximumPulseLength &&
            previousLineSyncRunKind >= 0 &&
            previousLineSyncRunKind != lineSyncRunKind &&
            previousLineSyncRunLength >= minimumBaselineLength;
        if (isPulse)
        {
            long pulseLeadingWhite = lineSyncRunKind == 1
                ? lineSyncRunStart
                : lineSyncRunStart + lineSyncRunLength;
            RegisterLineSyncPulse(pulseLeadingWhite, options, receivedAt);
        }
        else if (State != HfFaxReceiverState.Receiving &&
                 previousLineSyncRunKind >= 0 &&
                 previousLineSyncRunKind != lineSyncRunKind &&
                 IsSymmetricPhasingRun(previousLineSyncRunLength, nominalPeriod) &&
                 IsSymmetricPhasingRun(lineSyncRunLength, nominalPeriod) &&
                 lineSyncRunKind == 1)
        {
            RegisterLineSyncPulse(lineSyncRunStart, options, receivedAt);
        }

        previousLineSyncRunKind = lineSyncRunKind;
        previousLineSyncRunLength = lineSyncRunLength;
    }

    private void RegisterLineSyncPulse(
        long pulseSample,
        HfFaxDecodeOptions options,
        DateTimeOffset receivedAt)
    {
        // If automatic reception reached the image without a phasing lock,
        // ordinary vertical image content is not a valid new phase reference.
        // A later image-boundary analysis handles the horizontal origin instead.
        if (!lineSyncLocked && State == HfFaxReceiverState.Receiving &&
            activeImage is { WasManuallyStarted: false, WasAutomaticallyAligned: false })
            return;

        if (lastLineSyncPulse < 0)
        {
            lastLineSyncPulse = pulseSample;
            lineSyncReferencePulseCount = 1;
            return;
        }

        double interval = pulseSample - lastLineSyncPulse;
        if (lineSyncLocked)
        {
            double slantScale = SlantScale(options.SlantCorrectionPpm);
            double expectedPeriod = linePeriodSamples / slantScale;
            int elapsedPeriods = expectedPeriod > 0
                ? (int)Math.Round(interval / expectedPeriod)
                : 0;
            if (elapsedPeriods >= 1 && elapsedPeriods <= options.MaximumLines)
            {
                double observedPeriod = interval / elapsedPeriods;
                double phaseError = interval - elapsedPeriods * expectedPeriod;
                if (Math.Abs(phaseError) <=
                    expectedPeriod * LineSyncTrackingToleranceRatio)
                {
                    lastLineSyncPulse = pulseSample;
                    lineSyncReferencePulseCount++;
                    recoveredLineSyncPeriods += elapsedPeriods - 1;
                    double trackingWeight = elapsedPeriods == 1 ? 0.08 : 0.16;
                    linePeriodSamples = Blend(expectedPeriod, observedPeriod,
                        trackingWeight) * slantScale;
                    if (State == HfFaxReceiverState.Phasing)
                    {
                        if (phasingDurationReached)
                            BeginPhasingImage(receivedAt, alignToLinePulse: true, pulseSample);
                    }
                    else
                        AlignLineToPulse(receivedAt, pulseSample);
                    return;
                }
            }
            return;
        }

        double matchedPeriod = FindMatchingLinePeriod(interval, options.LinesPerMinute);
        if (matchedPeriod <= 0)
        {
            lastLineSyncPulse = pulseSample;
            lineSyncReferencePulseCount = 1;
            lineSyncCandidateCount = 0;
            lineSyncCandidatePeriod = 0;
            return;
        }
        lastLineSyncPulse = pulseSample;
        lineSyncReferencePulseCount++;

        if (!lineSyncLocked)
        {
            if (lineSyncCandidateCount == 0 ||
                Math.Abs(interval - lineSyncCandidatePeriod) <= lineSyncCandidatePeriod * 0.12)
            {
                lineSyncCandidatePeriod = lineSyncCandidateCount == 0
                    ? interval
                    : Blend(lineSyncCandidatePeriod, interval, 0.25);
                lineSyncCandidateCount++;
            }
            else
            {
                lineSyncCandidatePeriod = interval;
                lineSyncCandidateCount = 1;
                lineSyncReferencePulseCount = 2;
            }

            if (lineSyncCandidateCount >= 2)
            {
                lineSyncLocked = true;
                linePeriodSamples = lineSyncCandidatePeriod *
                    SlantScale(options.SlantCorrectionPpm);
            }
        }
        if (lineSyncLocked)
        {
            if (State == HfFaxReceiverState.Phasing)
            {
                if (phasingDurationReached)
                    BeginPhasingImage(receivedAt, alignToLinePulse: true, pulseSample);
            }
            else
                AlignLineToPulse(receivedAt, pulseSample);
        }
    }

    private void AlignLineToPulse(DateTimeOffset receivedAt, long pulseSample)
    {
        if (State != HfFaxReceiverState.Receiving || activeImage is null) return;
        double detectedPhase = LinePhaseAtCurrentSample(pulseSample);

        if (!lineSyncApplied)
        {
            lineSyncApplied = true;
            lineSamplePosition = detectedPhase;
            Array.Clear(pixelSums);
            Array.Clear(pixelCounts);
            return;
        }

        if (lineSamplePosition > linePeriodSamples * 0.35 && lineIndex > 0)
            CompleteLine(receivedAt);
        else
        {
            Array.Clear(pixelSums);
            Array.Clear(pixelCounts);
        }
        lineSamplePosition = detectedPhase;
    }

    private double LinePhaseAtCurrentSample(long pulseSample)
    {
        if (pulseSample < 0 || linePeriodSamples <= 0) return 0;
        long currentSample = Math.Max(0, lineSyncSampleIndex - 1);
        return PositiveModulo(currentSample - pulseSample, linePeriodSamples);
    }

    private void ResetLineSynchronizer()
    {
        lineSyncSampleIndex = 0;
        ResetLineSyncEdgeDetector();
        lastLineSyncPulse = -1;
        lineSyncCandidatePeriod = 0;
        lineSyncCandidateCount = 0;
        lineSyncReferencePulseCount = 0;
        recoveredLineSyncPeriods = 0;
        lineSyncLocked = false;
        lineSyncApplied = false;
        freezeRememberedLinePhase = false;
        lineSyncDetectionSuspended = false;
        phasingDurationReached = false;
        phasingFallbackSample = 0;
        linePeriodSamples = 0;
    }

    internal static double FindMatchingLinePeriod(double observedPeriod, int configuredLinesPerMinute)
    {
        if (observedPeriod <= 0) return 0;
        double best = BaseLinePeriodSamples(configuredLinesPerMinute);
        double bestDistance = Math.Abs(best - observedPeriod);
        foreach (int linesPerMinute in SupportedLinesPerMinute)
        {
            double candidate = BaseLinePeriodSamples(linesPerMinute);
            if (candidate == best) continue;
            double distance = Math.Abs(candidate - observedPeriod);
            if (distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return Math.Abs(best - observedPeriod) <= best * 0.20 ? best : 0;
    }

    private static int EffectiveIoc(int ioc) => ioc == 288 ? 288 : 576;

    private int EffectiveLinesPerMinute(int configuredLinesPerMinute, int slantCorrectionPpm)
    {
        if (configuredLinesPerMinute is 60 or 90 or 120 or 240)
            return configuredLinesPerMinute;
        if (!lineSyncLocked || linePeriodSamples <= 0)
            return 120;

        double scale = SlantScale(slantCorrectionPpm);
        return ClosestSupportedLinesPerMinute(linePeriodSamples, scale);
    }

    private static double BaseLinePeriodSamples(int linesPerMinute) =>
        OutputSampleRateHz * 60.0 / (linesPerMinute is 60 or 90 or 120 or 240
            ? linesPerMinute : 120);

    private static bool IsSymmetricPhasingRun(int runLength, double nominalPeriod) =>
        runLength >= nominalPeriod * 0.35 && runLength <= nominalPeriod * 0.65;

    private static double SlantScale(int slantCorrectionPpm) =>
        1 + slantCorrectionPpm / 1_000_000.0;

    private double EstimatedLinePeriodPpm()
    {
        if (!lineSyncLocked || linePeriodSamples <= 0) return double.NaN;
        HfFaxDecodeOptions options = activeImage is null ? latestOptions : activeOptions;
        double measuredPeriod = linePeriodSamples / SlantScale(options.SlantCorrectionPpm);
        int linesPerMinute = options.LinesPerMinute;
        if (linesPerMinute is not (60 or 90 or 120 or 240))
        {
            linesPerMinute = ClosestSupportedLinesPerMinute(measuredPeriod);
        }
        return (measuredPeriod / BaseLinePeriodSamples(linesPerMinute) - 1) * 1_000_000;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    internal static int ClosestSupportedLinesPerMinute(double observedPeriod,
        double periodScale = 1)
    {
        int best = SupportedLinesPerMinute[0];
        double bestDistance = Math.Abs(BaseLinePeriodSamples(best) * periodScale - observedPeriod);
        for (int index = 1; index < SupportedLinesPerMinute.Length; index++)
        {
            int candidate = SupportedLinesPerMinute[index];
            double distance = Math.Abs(
                BaseLinePeriodSamples(candidate) * periodScale - observedPeriod);
            if (distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    private static double Blend(double current, double sample, double weight) =>
        current + weight * (sample - current);

    private void SetState(HfFaxReceiverState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public readonly record struct DiagnosticsSnapshot(
        HfFaxReceiverState State,
        int CompletedLines,
        int ImageWidth,
        double ChannelRms,
        double LastToneHz,
        double StartDetectorPower,
        double StopDetectorPower,
        double InRangeRatio,
        long ProcessedSamples,
        bool LineSyncLocked,
        double LinePeriodSamples,
        int LineSyncPulseCount,
        int RecoveredLineSyncPeriods,
        double EstimatedLinePeriodPpm,
        bool PerLineSynchronizationEnabled,
        bool AutomaticHorizontalAlignmentEnabled,
        bool AutomaticHorizontalAlignmentApplied,
        int AutomaticHorizontalShiftPixels);

    private sealed class DetectorToneBank
    {
        private static readonly double[] FrequencyOffsets = [-0.01, -0.005, 0, 0.005, 0.01];
        private readonly DetectorTone[] tones;

        public DetectorToneBank(double frequencyHz, int sampleRateHz)
        {
            tones = new DetectorTone[FrequencyOffsets.Length];
            for (int index = 0; index < tones.Length; index++)
                tones[index] = new DetectorTone(
                    frequencyHz * (1 + FrequencyOffsets[index]), sampleRateHz);
        }

        public void Add(double value)
        {
            foreach (DetectorTone tone in tones) tone.Add(value);
        }

        public double Energy(int sampleCount)
        {
            double energy = 0;
            foreach (DetectorTone tone in tones)
                energy = Math.Max(energy, tone.Energy(sampleCount));
            return energy;
        }

        public void Reset()
        {
            foreach (DetectorTone tone in tones) tone.Reset();
        }
    }

    internal sealed class DetectorTone
    {
        private readonly double coefficient;
        private double state1;
        private double state2;

        public DetectorTone(double frequencyHz, int sampleRateHz)
        {
            coefficient = 2 * Math.Cos(TwoPi * frequencyHz / sampleRateHz);
        }

        public void Add(double value)
        {
            double state = value + coefficient * state1 - state2;
            state2 = state1;
            state1 = state;
        }

        public double Energy(int sampleCount)
        {
            if (sampleCount <= 0) return 0;
            double power = state1 * state1 + state2 * state2 -
                coefficient * state1 * state2;
            return 4 * Math.Max(0, power) / (sampleCount * (double)sampleCount);
        }

        public void Reset()
        {
            state1 = 0;
            state2 = 0;
        }
    }
}
