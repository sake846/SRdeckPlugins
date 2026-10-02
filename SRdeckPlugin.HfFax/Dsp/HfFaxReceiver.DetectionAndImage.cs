using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Models;
using System.Runtime.CompilerServices;

namespace SRdeckPlugin.HfFax.Dsp;

internal sealed partial class HfFaxReceiver
{
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
        // Only completed rows are read; grow the buffer as the image arrives.
        imagePixels = new byte[checked(width * Math.Min(128, options.MaximumLines))];
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
            phasingAligned: wasLineSyncLocked);
        if (wasLineSyncLocked)
        {
            lineSyncApplied = true;
            lineSamplePosition = LinePhaseAtCurrentSample(
                alignToLinePulse ? pulseSample : lastLineSyncPulse);
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

        int requiredLines = lineIndex + 1;
        if (imagePixels.Length < checked(requiredLines * image.Width))
        {
            int capacity = Math.Min(image.MaximumLines,
                Math.Max(requiredLines, imagePixels.Length / image.Width * 2));
            Array.Resize(ref imagePixels, checked(capacity * image.Width));
        }
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

}
