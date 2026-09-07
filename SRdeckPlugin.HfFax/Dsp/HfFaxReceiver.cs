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

internal sealed class HfFaxReceiver
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
            double normalized = ApplyVideoBandwidthFilter(rawNormalized,
                options.VideoBandwidthHz);
            if (options.InvertImage) normalized = 1 - normalized;
            byte gray = (byte)Math.Round(normalized * byte.MaxValue);

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
                AppendImageSample(gray, metadata.Source.UtcTimestamp);
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

    private double ApplyVideoBandwidthFilter(double value, int cutoffHz)
    {
        if (cutoffHz <= 0)
        {
            if (videoBandwidthHz != 0) ResetVideoBandwidthFilter();
            videoBandwidthHz = 0;
            return value;
        }

        cutoffHz = Math.Clamp(cutoffHz, 1, OutputSampleRateHz / 2 - 1);
        if (videoBandwidthHz != cutoffHz)
            ConfigureVideoBandwidthFilter(cutoffHz);
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

    private void UpdateDetector(
        double normalized,
        IqBlockMetadata metadata,
        long frequencyHz,
        HfFaxDecodeOptions options)
    {
        detectorUpdateCount++;
        double centered = normalized - 0.5;
        start300.Add(centered);
        start675.Add(centered);
        stop450.Add(centered);
        detectorPower += centered * centered;
        detectorSamples++;
        if (detectorSamples < DetectorWindowSamples) return;

        double signalPower = detectorPower / detectorSamples;
        double startPower = Math.Max(start300.Energy(detectorSamples), start675.Energy(detectorSamples));
        double stopPower = stop450.Energy(detectorSamples);
        int detectedIoc = options.Ioc is 288 or 576
            ? options.Ioc
            : start300.Energy(detectorSamples) >= start675.Energy(detectorSamples)
                ? 576 : 288;
        lastStartPower = startPower;
        lastStopPower = stopPower;
        double signalThreshold = Math.Clamp(
            detectorNoisePower * DetectorSignalNoiseRatio,
            MinimumDetectorSignalPower, MaximumDetectorSignalPower);
        double toneThreshold = Math.Clamp(
            detectorToneNoisePower * DetectorToneNoiseRatio,
            MinimumDetectorTonePower, MaximumDetectorTonePower);
        bool startDetected = signalPower > signalThreshold && startPower > toneThreshold &&
            startPower > stopPower * 1.8;
        bool stopDetected = signalPower > signalThreshold && stopPower > toneThreshold &&
            stopPower > startPower * 1.8;

        if (State == HfFaxReceiverState.Idle && !startDetected && !stopDetected)
        {
            detectorNoisePower = Blend(detectorNoisePower, signalPower, 0.08);
            detectorToneNoisePower = Blend(detectorToneNoisePower,
                Math.Max(startPower, stopPower), 0.08);
        }

        start300.Reset();
        start675.Reset();
        stop450.Reset();
        detectorSamples = 0;
        detectorPower = 0;

        if (State == HfFaxReceiverState.Idle)
        {
            if (startDetected)
            {
                startWindows++;
                startMissedWindows = 0;
            }
            else if (startWindows > 0)
            {
                startMissedWindows++;
                if (startMissedWindows > MaximumStartMissedWindows)
                {
                    startWindows = 0;
                    startMissedWindows = 0;
                }
            }
            if (startWindows >= MinimumStartWindows)
            {
                releaseWindows = 0;
                pendingOptions = options with { Ioc = detectedIoc };
                SetState(HfFaxReceiverState.StartDetected);
            }
            return;
        }

        if (State == HfFaxReceiverState.StartDetected)
        {
            if (startDetected)
            {
                releaseWindows = 0;
                return;
            }
            releaseWindows++;
            if (releaseWindows >= MinimumReleaseWindows)
                BeginPhasing(metadata.StreamId, metadata.AbsoluteSampleStart,
                    frequencyHz, pendingOptions);
            return;
        }

        if (State == HfFaxReceiverState.Phasing)
        {
            if (stopDetected)
                SetState(HfFaxReceiverState.Idle);
            return;
        }

        stopWindows = stopDetected ? stopWindows + 1 : 0;
        if (stopWindows >= MinimumStopWindows)
            CompleteImage(metadata.UtcTimestamp, "APT停止信号");
    }

    private void BeginImage(
        DateTimeOffset startedAt,
        Guid streamId,
        long samplePosition,
        long frequencyHz,
        HfFaxDecodeOptions options,
        bool manual,
        bool phasingAligned = false)
    {
        if (State == HfFaxReceiverState.Receiving && lineIndex > 0)
            CompleteImage(startedAt, "新しい受信を開始");

        bool automaticallyAlignManualStart = manual &&
            lineSyncLocked &&
            linePeriodSamples > 0 &&
            lastLineSyncPulse >= 0 &&
            lineSyncReferencePulseCount >= MinimumManualAlignmentPulses;
        double rememberedLinePhase = automaticallyAlignManualStart
            ? PositiveModulo(lineSyncSampleIndex - lastLineSyncPulse, linePeriodSamples)
            : 0;

        options = options with
        {
            Ioc = EffectiveIoc(options.Ioc),
            LinesPerMinute = EffectiveLinesPerMinute(options.LinesPerMinute,
                options.SlantCorrectionPpm)
        };
        activeOptions = options;
        if (manual && !automaticallyAlignManualStart) ResetLineSynchronizer();
        if (!lineSyncLocked || linePeriodSamples <= 0)
            linePeriodSamples = BaseLinePeriodSamples(options.LinesPerMinute) *
                SlantScale(options.SlantCorrectionPpm);
        lineSyncApplied = automaticallyAlignManualStart;
        phasingDurationReached = false;
        int width = WidthFromIoc(options.Ioc);
        bool wasPhasingAligned = automaticallyAlignManualStart || phasingAligned;
        activeImage = new(startedAt, streamId, samplePosition, frequencyHz, width,
            options.MaximumLines, options.Ioc, options.LinesPerMinute, manual,
            wasPhasingAligned,
            wasPhasingAligned ? linePeriodSamples : 0,
            wasPhasingAligned ? lineSyncReferencePulseCount : 0);
        imagePixels = new byte[checked(width * options.MaximumLines)];
        Array.Fill(imagePixels, byte.MaxValue);
        pixelSums = new double[width];
        pixelCounts = new int[width];
        if (lineBuffer.Length != width) lineBuffer = new byte[width];
        lineSamplePosition = 0;
        manualAlignmentSamplesRemaining = automaticallyAlignManualStart && rememberedLinePhase >= 1
            ? (long)Math.Ceiling(linePeriodSamples - rememberedLinePhase)
            : 0;
        freezeRememberedLinePhase = manualAlignmentSamplesRemaining > 0;
        lineIndex = 0;
        automaticHorizontalShiftPixels = 0;
        automaticHorizontalAlignmentApplied = false;
        nextAutomaticHorizontalAlignmentLine = HfFaxImagePhaseAligner.MinimumLines;
        totalSamples = 0;
        inRangeSamples = 0;
        startWindows = releaseWindows = stopWindows = 0;
        startMissedWindows = 0;
        SetState(HfFaxReceiverState.Receiving);
        ImageStarted?.Invoke(activeImage);
    }

    private void BeginPhasing(
        Guid streamId,
        long samplePosition,
        long frequencyHz,
        HfFaxDecodeOptions options)
    {
        pendingOptions = options;
        pendingStreamId = streamId;
        pendingSamplePosition = samplePosition;
        pendingFrequencyHz = frequencyHz;
        // The state transition is made after the first non-start detector
        // window. Count that window as the beginning of the 30-second phase
        // sequence so a block boundary cannot cost half a second.
        phasingStartTotalSamples = Math.Max(0,
            totalSamples - DetectorWindowSamples * Math.Max(1, releaseWindows));
        ResetLineSynchronizer();
        SetState(HfFaxReceiverState.Phasing);
    }

    private void BeginPhasingImage(
        DateTimeOffset startedAt,
        bool alignToLinePulse,
        long pulseSample = -1)
    {
        if (State != HfFaxReceiverState.Phasing) return;
        HfFaxDecodeOptions options = pendingOptions;
        bool wasLineSyncLocked = lineSyncLocked;
        BeginImage(startedAt, pendingStreamId, pendingSamplePosition,
            pendingFrequencyHz, options, false,
            phasingAligned: alignToLinePulse && wasLineSyncLocked);
        if (alignToLinePulse && wasLineSyncLocked)
        {
            lineSyncApplied = true;
            lineSamplePosition = LinePhaseAtCurrentSample(pulseSample);
            Array.Clear(pixelSums);
            Array.Clear(pixelCounts);
        }
    }

    private void AppendImageSample(byte gray, DateTimeOffset receivedAt)
    {
        HfFaxImageStarted? image = activeImage;
        if (image is null) return;
        if (manualAlignmentSamplesRemaining > 0)
        {
            manualAlignmentSamplesRemaining--;
            if (manualAlignmentSamplesRemaining == 0)
                freezeRememberedLinePhase = false;
            return;
        }
        double samplesPerLine = linePeriodSamples > 0
            ? linePeriodSamples
            : BaseLinePeriodSamples(image.LinesPerMinute) *
              SlantScale(activeOptions.SlantCorrectionPpm);
        int pixel = Math.Min(image.Width - 1,
            (int)(lineSamplePosition * image.Width / samplesPerLine));
        pixelSums[pixel] += gray;
        pixelCounts[pixel]++;
        lineSamplePosition++;
        if (lineSamplePosition < samplesPerLine) return;
        lineSamplePosition -= samplesPerLine;
        CompleteLine(receivedAt);
    }

    private void CompleteLine(DateTimeOffset receivedAt)
    {
        HfFaxImageStarted? image = activeImage;
        if (image is null) return;
        Span<byte> line = lineBuffer.AsSpan(0, image.Width);
        byte fallback = lineIndex == 0 ? byte.MaxValue :
            imagePixels[(lineIndex - 1) * image.Width + image.Width - 1];
        for (int pixel = 0; pixel < line.Length; pixel++)
        {
            if (pixelCounts[pixel] > 0)
                fallback = (byte)Math.Clamp((int)Math.Round(pixelSums[pixel] / pixelCounts[pixel]), 0, 255);
            line[pixel] = fallback;
        }

        int combinedShift = NormalizeHorizontalShift(
            NormalizeHorizontalShift(automaticHorizontalShiftPixels, image.Width) +
            NormalizeHorizontalShift(activeOptions.HorizontalShiftPixels, image.Width),
            image.Width);
        RotateLine(line, combinedShift);

        line.CopyTo(imagePixels.AsSpan(lineIndex * image.Width, image.Width));
        double quality = totalSamples == 0 ? 0 : inRangeSamples / (double)totalSamples;
        int completedLineIndex = lineIndex;
        lineIndex++;
        Array.Clear(pixelSums);
        Array.Clear(pixelCounts);
        TryApplyAutomaticHorizontalAlignment(receivedAt);
        byte[] completedPixels = imagePixels.AsSpan(
            completedLineIndex * image.Width, image.Width).ToArray();
        LineCompleted?.Invoke(new HfFaxLine(completedLineIndex, completedPixels, quality));
        if (lineIndex >= image.MaximumLines)
            CompleteImage(receivedAt, "最大ライン数");
    }

    private void TryApplyAutomaticHorizontalAlignment(DateTimeOffset receivedAt)
    {
        HfFaxImageStarted? image = activeImage;
        if (image is null || automaticHorizontalAlignmentApplied ||
            !latestOptions.AutomaticHorizontalAlignmentEnabled ||
            lineIndex < nextAutomaticHorizontalAlignmentLine)
            return;

        ReadOnlySpan<byte> completedPixels = imagePixels.AsSpan(
            0, checked(image.Width * lineIndex));
        if (!HfFaxImagePhaseAligner.TryGetCorrection(completedPixels,
                image.Width, lineIndex, activeOptions.HorizontalShiftPixels,
                out int correction))
        {
            nextAutomaticHorizontalAlignmentLine = lineIndex + 32;
            return;
        }

        automaticHorizontalAlignmentApplied = true;
        automaticHorizontalShiftPixels = correction;
        if (correction != 0)
            HfFaxImagePhaseAligner.RotateRows(
                imagePixels, image.Width, lineIndex, correction);
        HfFaxImageSnapshot? snapshot = GetCurrentSnapshot(
            receivedAt, "横位置自動補正");
        if (snapshot is not null) ImageRealigned?.Invoke(snapshot);
    }

    private void CompleteImage(DateTimeOffset completedAt, string reason)
    {
        HfFaxImageStarted? image = activeImage;
        if (image is null || State != HfFaxReceiverState.Receiving)
        {
            SetState(HfFaxReceiverState.Idle);
            return;
        }

        if (lineIndex > 0)
        {
            int length = checked(image.Width * lineIndex);
            HfFaxImageSnapshot snapshot = CreateSnapshot(image,
                imagePixels.AsSpan(0, length).ToArray(), lineIndex, completedAt, reason);
            ImageCompleted?.Invoke(snapshot);
        }

        activeImage = null;
        imagePixels = [];
        pixelSums = [];
        pixelCounts = [];
        lineIndex = 0;
        lineSamplePosition = 0;
        manualAlignmentSamplesRemaining = 0;
        automaticHorizontalShiftPixels = 0;
        automaticHorizontalAlignmentApplied = false;
        nextAutomaticHorizontalAlignmentLine = HfFaxImagePhaseAligner.MinimumLines;
        startWindows = releaseWindows = stopWindows = 0;
        startMissedWindows = 0;
        ResetLineSynchronizer();
        SetState(HfFaxReceiverState.Idle);
    }

    private HfFaxImageSnapshot CreateSnapshot(
        HfFaxImageStarted image,
        byte[] pixels,
        int height,
        DateTimeOffset completedAt,
        string reason) => new(
            $"{image.StreamId:N}-{image.SamplePosition}",
            image.StartedAt,
            completedAt,
            image.StreamId,
            image.SamplePosition,
            image.FrequencyHz,
            image.Width,
            height,
            image.Ioc,
            image.LinesPerMinute,
            pixels,
            totalSamples == 0 ? 0 : inRangeSamples / (double)totalSamples,
            reason,
            image.WasAutomaticallyAligned,
            image.MeasuredLinePeriodSamples,
            image.PhasingPulseCount,
            HfFaxImagePhaseAligner.SignedShift(
                automaticHorizontalShiftPixels, image.Width));

    internal static int NormalizeHorizontalShift(int shift, int width)
    {
        int normalized = shift % width;
        return normalized < 0 ? normalized + width : normalized;
    }

    internal static void RotateLine(Span<byte> line, int shift)
    {
        if (line.IsEmpty) return;
        int normalized = NormalizeHorizontalShift(shift, line.Length);
        if (normalized == 0) return;

        line.Reverse();
        line[..normalized].Reverse();
        line[normalized..].Reverse();
    }

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
