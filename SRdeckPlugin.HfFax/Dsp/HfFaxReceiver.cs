using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Models;
using System.Runtime.CompilerServices;

namespace SRdeckPlugin.HfFax.Dsp;

internal readonly record struct HfFaxDecodeOptions(
    int Ioc,
    int LinesPerMinute,
    int MaximumLines,
    int HorizontalShiftPixels,
    int SlantCorrectionPpm,
    bool AutomaticStartStop,
    bool InvertImage,
    int VideoBandwidthHz = HfFaxReceiver.DefaultVideoBandwidthHz,
    bool PerLineSynchronizationEnabled = true,
    bool AutomaticHorizontalAlignmentEnabled = true);

internal sealed partial class HfFaxReceiver
{
    public const int OutputSampleRateHz = 12_000;
    public const int ReceiverBandwidthHz = 3_000;
    public const int AudioCenterFrequencyHz = 1_900;
    public const int BlackToneFrequencyHz = 1_500;
    public const int WhiteToneFrequencyHz = 2_300;
    public const int DefaultVideoBandwidthHz = 1_200;

    private const int DetectorWindowSamples = OutputSampleRateHz / 2;
    private const int PhasingDurationSamples = OutputSampleRateHz * 30;
    private const int MinimumStartWindows = 10;
    private const int MaximumStartMissedWindows = 3;
    private const int MinimumReleaseWindows = 2;
    // A 5-second stop tone can straddle detector windows. Nine positive
    // half-second windows still require essentially the complete WMO tone,
    // while avoiding a one-window miss at the block boundary.
    private const int MinimumStopWindows = 9;
    private const double InitialDetectorNoisePower = 0.0005;
    private const double InitialDetectorToneNoisePower = 0.00025;
    private const double MinimumDetectorSignalPower = 0.0015;
    private const double MinimumDetectorTonePower = 0.001;
    private const double MaximumDetectorSignalPower = 0.015;
    private const double MaximumDetectorTonePower = 0.008;
    private const double DetectorSignalNoiseRatio = 2.2;
    private const double DetectorToneNoiseRatio = 3.0;
    private const double LineSyncFilterAlpha = 0.15;
    private const double LineSyncLowThreshold = 0.28;
    private const double LineSyncHighThreshold = 0.72;
    private const double LineSyncMinimumBaselineRatio = 0.25;
    private const double LineSyncMaximumPulseRatio = 0.35;
    private const double LineSyncTrackingToleranceRatio = 0.025;
    private const int MinimumManualAlignmentPulses = 6;
    private const double PhasingSyncFallbackPeriods = 1.0;
    private const double TwoPi = 2 * Math.PI;
    private static readonly int[] SupportedLinesPerMinute = [60, 90, 120, 240];

    private readonly DetectorToneBank start300 = new(300, OutputSampleRateHz);
    private readonly DetectorToneBank start675 = new(675, OutputSampleRateHz);
    private readonly DetectorToneBank stop450 = new(450, OutputSampleRateHz);
    private Complex32 previousSample;
    private bool hasPreviousSample;
    private int detectorSamples;
    private long detectorUpdateCount;
    private double detectorPower;
    private int startWindows;
    private int startMissedWindows;
    private int releaseWindows;
    private int stopWindows;
    private double[] pixelSums = [];
    private int[] pixelCounts = [];
    private byte[] imagePixels = [];
    private byte[] lineBuffer = [];
    private double lineSamplePosition;
    private long manualAlignmentSamplesRemaining;
    private int lineIndex;
    private int automaticHorizontalShiftPixels;
    private bool automaticHorizontalAlignmentApplied;
    private int nextAutomaticHorizontalAlignmentLine = HfFaxImagePhaseAligner.MinimumLines;
    private long totalSamples;
    private long inRangeSamples;
    private double channelPower;
    private double lastToneHz = AudioCenterFrequencyHz;
    private double lastStartPower;
    private double lastStopPower;
    private int videoBandwidthHz = -1;
    private bool videoBandwidthInitialized;
    private double videoBandwidthB0;
    private double videoBandwidthB1;
    private double videoBandwidthB2;
    private double videoBandwidthA1;
    private double videoBandwidthA2;
    private double videoBandwidthInput1;
    private double videoBandwidthInput2;
    private double videoBandwidthOutput1;
    private double videoBandwidthOutput2;
    private HfFaxImageStarted? activeImage;
    private HfFaxDecodeOptions activeOptions;
    private double detectorNoisePower = InitialDetectorNoisePower;
    private double detectorToneNoisePower = InitialDetectorToneNoisePower;
    private long lineSyncSampleIndex;
    private bool hasLineSyncFilteredValue;
    private double lineSyncFilteredValue;
    private int lineSyncRunKind = -1;
    private int lineSyncRunLength;
    private long lineSyncRunStart;
    private int previousLineSyncRunKind = -1;
    private int previousLineSyncRunLength;
    private long lastLineSyncPulse = -1;
    private double lineSyncCandidatePeriod;
    private int lineSyncCandidateCount;
    private int lineSyncReferencePulseCount;
    private int recoveredLineSyncPeriods;
    private bool lineSyncLocked;
    private bool lineSyncApplied;
    private bool freezeRememberedLinePhase;
    private bool lineSyncDetectionSuspended;
    private bool phasingDurationReached;
    private long phasingFallbackSample;
    private double linePeriodSamples;
    private HfFaxDecodeOptions pendingOptions;
    private Guid pendingStreamId;
    private long pendingSamplePosition;
    private long pendingFrequencyHz;
    private long phasingStartTotalSamples;
    private HfFaxDecodeOptions latestOptions;

    public HfFaxReceiverState State { get; private set; }
    internal long DetectorUpdateCount => detectorUpdateCount;
    public event Action<HfFaxImageStarted>? ImageStarted;
    public event Action<HfFaxLine>? LineCompleted;
    public event Action<HfFaxImageSnapshot>? ImageRealigned;
    public event Action<HfFaxImageSnapshot>? ImageCompleted;
    public event Action<HfFaxReceiverState>? StateChanged;

    public void Process(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata,
        long frequencyHz,
        HfFaxDecodeOptions options)
    {
        if (metadata.Configuration.OutputSampleRateHz != OutputSampleRateHz)
            throw new InvalidOperationException($"HF-FAX requires a {OutputSampleRateHz} Hz channel stream.");

        bool automaticStartStopChanged =
            latestOptions.AutomaticStartStop != options.AutomaticStartStop;
        latestOptions = options;
        if (automaticStartStopChanged)
            ResetAutomaticDetector();

        if (metadata.Source.Discontinuity != IqDiscontinuity.None)
        {
            if (State == HfFaxReceiverState.Receiving)
                CompleteImage(metadata.Source.UtcTimestamp, "入力不連続");
            else if (State is HfFaxReceiverState.StartDetected or HfFaxReceiverState.Phasing)
                SetState(HfFaxReceiverState.Idle);
            ResetSignalTracking();
            ResetLineSynchronizer();
        }

        int cutoffHz = options.VideoBandwidthHz <= 0 ? 0 :
            Math.Clamp(options.VideoBandwidthHz, 1, OutputSampleRateHz / 2 - 1);
        if (videoBandwidthHz != cutoffHz)
        {
            if (cutoffHz == 0)
            {
                ResetVideoBandwidthFilter();
                videoBandwidthHz = 0;
            }
            else
                ConfigureVideoBandwidthFilter(cutoffHz);
        }

        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 sample = samples[index];
            if (!float.IsFinite(sample.I) || !float.IsFinite(sample.Q))
                continue;

            double samplePower = sample.I * sample.I + sample.Q * sample.Q;
            channelPower += 0.001 * (samplePower - channelPower);
            if (!hasPreviousSample)
            {
                previousSample = sample;
                hasPreviousSample = true;
                continue;
            }

            double real = sample.I * previousSample.I + sample.Q * previousSample.Q;
            double imaginary = sample.Q * previousSample.I - sample.I * previousSample.Q;
            previousSample = sample;
            double deviationHz = DiscriminatorAngle(imaginary, real) * OutputSampleRateHz / TwoPi;
            double toneHz = AudioCenterFrequencyHz + deviationHz;
            lastToneHz = toneHz;
            double rawNormalized = Math.Clamp((toneHz - BlackToneFrequencyHz) /
                (WhiteToneFrequencyHz - BlackToneFrequencyHz), 0, 1);
            if (State == HfFaxReceiverState.Receiving &&
                (!options.PerLineSynchronizationEnabled || freezeRememberedLinePhase))
                AdvanceLineSynchronizerWithoutDetection();
            else
                UpdateLineSynchronizer(rawNormalized, options, metadata.Source.UtcTimestamp);
            // A spectrum-analyzer-style VBW filter belongs after FM detection.
            // Keep APT and line synchronization on the unfiltered discriminator
            // output so a narrow display VBW cannot weaken protocol timing tones.
            double normalized = ApplyVideoBandwidthFilter(rawNormalized);

            totalSamples++;
            if (deviationHz is >= -600 and <= 600) inRangeSamples++;
            if (options.AutomaticStartStop)
                UpdateDetector(rawNormalized, metadata.Source, frequencyHz, options);
            if (State == HfFaxReceiverState.Phasing &&
                !phasingDurationReached &&
                totalSamples - phasingStartTotalSamples >= PhasingDurationSamples - 1)
            {
                phasingDurationReached = true;
                if (!lineSyncLocked)
                    BeginPhasingImage(metadata.Source.UtcTimestamp, alignToLinePulse: false);
                else
                    phasingFallbackSample = totalSamples + Math.Max(1,
                        (long)Math.Ceiling(linePeriodSamples * PhasingSyncFallbackPeriods));
            }
            if (State == HfFaxReceiverState.Phasing && phasingDurationReached &&
                lineSyncLocked && totalSamples >= phasingFallbackSample)
            {
                // A locked detector normally starts on the next sync pulse. If
                // the pulse is lost at the end of phasing, do not remain stuck
                // forever; start a provisional line and re-align on the next
                // pulse in Receiving state.
                BeginPhasingImage(metadata.Source.UtcTimestamp, alignToLinePulse: false);
            }
            if (State == HfFaxReceiverState.Receiving)
            {
                if (options.InvertImage) normalized = 1 - normalized;
                byte gray = (byte)Math.Round(normalized * byte.MaxValue);
                AppendImageSample(gray, metadata.Source.UtcTimestamp);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal static double DiscriminatorAngle(double imaginary, double real)
    {
        if (imaginary == 0)
        {
            if (real < 0 || BitConverter.DoubleToInt64Bits(real) < 0)
                return BitConverter.DoubleToInt64Bits(imaginary) < 0 ? -Math.PI : Math.PI;
            return imaginary;
        }
        if (real == 0) return imaginary > 0 ? Math.PI / 2 : -Math.PI / 2;

        // Reduce atan to [-tan(pi/8), tan(pi/8)] before the degree-15 series.
        // Its next-term bound is below 1.6e-8 rad across every finite IQ phase.
        double absoluteReal = Math.Abs(real);
        double absoluteImaginary = Math.Abs(imaginary);
        bool imaginaryDominant = absoluteImaginary > absoluteReal;
        double ratio = imaginaryDominant
            ? absoluteReal / absoluteImaginary
            : absoluteImaginary / absoluteReal;
        double angle;
        if (ratio > 0.41421356237309503)
        {
            double reduced = (ratio - 1) / (ratio + 1);
            angle = Math.PI / 4 + AtanTaylor15(reduced);
        }
        else
        {
            angle = AtanTaylor15(ratio);
        }

        if (imaginaryDominant) angle = Math.PI / 2 - angle;
        if (real < 0) angle = Math.PI - angle;
        return imaginary < 0 ? -angle : angle;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double AtanTaylor15(double value)
    {
        double squared = value * value;
        return value * (1 + squared * (-1.0 / 3 + squared * (1.0 / 5 + squared *
            (-1.0 / 7 + squared * (1.0 / 9 + squared * (-1.0 / 11 + squared *
                (1.0 / 13 + squared * (-1.0 / 15))))))));
    }

    public void StartManually(
        DateTimeOffset startedAt,
        Guid streamId,
        long samplePosition,
        long frequencyHz,
        HfFaxDecodeOptions options) =>
        BeginImage(startedAt, streamId, samplePosition, frequencyHz, options, true);

    public void StopManually(DateTimeOffset completedAt) =>
        CompleteImage(completedAt, "手動停止");

    public void Stop(DateTimeOffset completedAt, string reason) =>
        CompleteImage(completedAt, reason);

    public void Reset()
    {
        activeImage = null;
        imagePixels = [];
        pixelSums = [];
        pixelCounts = [];
        lineSamplePosition = 0;
        manualAlignmentSamplesRemaining = 0;
        lineIndex = 0;
        automaticHorizontalShiftPixels = 0;
        automaticHorizontalAlignmentApplied = false;
        nextAutomaticHorizontalAlignmentLine = HfFaxImagePhaseAligner.MinimumLines;
        totalSamples = 0;
        inRangeSamples = 0;
        channelPower = 0;
        ResetLineSynchronizer();
        SetState(HfFaxReceiverState.Idle);
        ResetSignalTracking();
    }

    public HfFaxImageSnapshot? GetCurrentSnapshot(DateTimeOffset now, string reason)
    {
        if (activeImage is null || lineIndex == 0) return null;
        int length = checked(activeImage.Width * lineIndex);
        return CreateSnapshot(activeImage, imagePixels.AsSpan(0, length).ToArray(), lineIndex,
            now, reason);
    }

    public HfFaxImageSnapshot? ApplyHorizontalShift(
        int horizontalShiftPixels,
        DateTimeOffset now)
    {
        pendingOptions = pendingOptions with { HorizontalShiftPixels = horizontalShiftPixels };
        if (activeImage is not { } image || State != HfFaxReceiverState.Receiving)
        {
            activeOptions = activeOptions with { HorizontalShiftPixels = horizontalShiftPixels };
            return null;
        }

        int previousShift = NormalizeHorizontalShift(
            activeOptions.HorizontalShiftPixels, image.Width);
        int newShift = NormalizeHorizontalShift(horizontalShiftPixels, image.Width);
        int delta = newShift - previousShift;
        if (delta != 0)
        {
            for (int row = 0; row < lineIndex; row++)
                RotateLine(imagePixels.AsSpan(row * image.Width, image.Width), delta);
        }

        activeOptions = activeOptions with { HorizontalShiftPixels = horizontalShiftPixels };
        return GetCurrentSnapshot(now, "横位置補正");
    }

    public DiagnosticsSnapshot GetDiagnostics() => new(
        State,
        lineIndex,
        activeImage?.Width ?? WidthFromIoc(activeOptions.Ioc == 0 ? 576 : activeOptions.Ioc),
        Math.Sqrt(Math.Max(channelPower, 0)),
        lastToneHz,
        lastStartPower,
        lastStopPower,
        totalSamples == 0 ? 0 : inRangeSamples / (double)totalSamples,
        totalSamples,
        lineSyncLocked,
        linePeriodSamples,
        lineSyncReferencePulseCount,
        recoveredLineSyncPeriods,
        EstimatedLinePeriodPpm(),
        latestOptions.PerLineSynchronizationEnabled,
        latestOptions.AutomaticHorizontalAlignmentEnabled,
        automaticHorizontalAlignmentApplied,
        HfFaxImagePhaseAligner.SignedShift(automaticHorizontalShiftPixels,
            activeImage?.Width ?? WidthFromIoc(activeOptions.Ioc == 0 ? 576 : activeOptions.Ioc)));

    public static int WidthFromIoc(int ioc) => (int)Math.Round(Math.PI * (ioc == 288 ? 288 : 576));

    private double ApplyVideoBandwidthFilter(double value)
    {
        if (videoBandwidthHz == 0) return value;
        if (!videoBandwidthInitialized)
        {
            // Seed the filter at the current level rather than fading in from
            // black at stream start or after an input discontinuity.
            videoBandwidthInput1 = videoBandwidthInput2 = value;
            videoBandwidthOutput1 = videoBandwidthOutput2 = value;
            videoBandwidthInitialized = true;
            return value;
        }

        double output = videoBandwidthB0 * value +
            videoBandwidthB1 * videoBandwidthInput1 +
            videoBandwidthB2 * videoBandwidthInput2 -
            videoBandwidthA1 * videoBandwidthOutput1 -
            videoBandwidthA2 * videoBandwidthOutput2;
        videoBandwidthInput2 = videoBandwidthInput1;
        videoBandwidthInput1 = value;
        videoBandwidthOutput2 = videoBandwidthOutput1;
        videoBandwidthOutput1 = output;
        return Math.Clamp(output, 0, 1);
    }

    private void ConfigureVideoBandwidthFilter(int cutoffHz)
    {
        // Second-order Butterworth low-pass. The selected VBW is its -3 dB
        // frequency, matching the usual meaning of a spectrum-analyzer VBW.
        double k = Math.Tan(Math.PI * cutoffHz / OutputSampleRateHz);
        double kSquared = k * k;
        double normalization = 1 / (1 + Math.Sqrt(2) * k + kSquared);
        videoBandwidthB0 = kSquared * normalization;
        videoBandwidthB1 = 2 * videoBandwidthB0;
        videoBandwidthB2 = videoBandwidthB0;
        videoBandwidthA1 = 2 * (kSquared - 1) * normalization;
        videoBandwidthA2 = (1 - Math.Sqrt(2) * k + kSquared) * normalization;
        videoBandwidthHz = cutoffHz;
        videoBandwidthInitialized = false;
    }

    private void ResetVideoBandwidthFilter()
    {
        videoBandwidthHz = -1;
        videoBandwidthInitialized = false;
        videoBandwidthInput1 = videoBandwidthInput2 = 0;
        videoBandwidthOutput1 = videoBandwidthOutput2 = 0;
    }

}
