using System.Numerics;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Acars.Dsp;

/// <summary>Streaming VHF ACARS AM-MSK receiver.</summary>
public sealed partial class AcarsReceiver
{
    public IReadOnlyList<AcarsFrame> Process(ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata)
    {
        return ProcessCore(samples, metadata, metadata.CenterFrequencyHz,
            Span<float>.Empty, captureAudio: false, out _);
    }

    public IReadOnlyList<AcarsFrame> Process(
        ReadOnlySpan<Complex32> samples, IqBlockMetadata metadata, long targetFrequencyHz)
    {
        return ProcessCore(samples, metadata, targetFrequencyHz,
            Span<float>.Empty, captureAudio: false, out _);
    }

    public IReadOnlyList<AcarsFrame> Process(
        ReadOnlySpan<Complex32> samples,
        IqBlockMetadata metadata,
        Span<float> demodulatedAudio,
        out int audioSampleCount)
    {
        return ProcessCore(samples, metadata, metadata.CenterFrequencyHz,
            demodulatedAudio, captureAudio: true, out audioSampleCount);
    }

    /// <summary>
    /// Demodulates a channel which may be offset from the IQ centre frequency.
    /// The channel is mixed to zero IF, low-pass filtered, then resampled.
    /// </summary>
    public IReadOnlyList<AcarsFrame> Process(
        ReadOnlySpan<Complex32> samples,
        IqBlockMetadata metadata,
        long targetFrequencyHz,
        Span<float> demodulatedAudio,
        out int audioSampleCount)
    {
        return ProcessCore(samples, metadata, targetFrequencyHz,
            demodulatedAudio, captureAudio: true, out audioSampleCount);
    }

    private IReadOnlyList<AcarsFrame> ProcessCore(
        ReadOnlySpan<Complex32> samples,
        IqBlockMetadata metadata,
        long targetFrequencyHz,
        Span<float> demodulatedAudio,
        bool captureAudio,
        out int audioSampleCount)
    {
        audioSampleCount = 0;
        if (metadata.SampleRateHz < DemodulationSampleRateHz)
            throw new InvalidOperationException("ACARS reception requires an IQ sample rate of at least 48 kHz.");

        long offsetHz = targetFrequencyHz - metadata.CenterFrequencyHz;
        if (Math.Abs(offsetHz) > metadata.SampleRateHz * 0.5)
            return Array.Empty<AcarsFrame>();

        if (inputSampleRate != metadata.SampleRateHz || metadata.Discontinuity != IqDiscontinuity.None)
            Reset(metadata.AbsoluteSampleStart, metadata.SampleRateHz);
        downconverter.Configure(offsetHz, metadata.SampleRateHz);
        this.targetFrequencyHz = targetFrequencyHz;
        if (audioCount == 0) audioSampleStart = metadata.AbsoluteSampleStart;

        audioSampleCount = 0;
        foreach (Complex32 sample in samples)
        {
            downconverter.Mix(sample.I, sample.Q, out float mixedI, out float mixedQ);

            if (!coarseDecimator.TryProcess(mixedI, mixedQ, out float coarseI, out float coarseQ) ||
                !fineDecimator.TryProcess(coarseI, coarseQ, out float fineI, out float fineQ)) continue;

            if (!finalResampler.TryProcess(fineI, fineQ, out float resampledI, out float resampledQ))
                continue;
            ProcessBasebandSample(resampledI, resampledQ, demodulatedAudio,
                captureAudio, ref audioSampleCount);
        }
        IReadOnlyList<AcarsFrame> frames = Array.Empty<AcarsFrame>();
        if (samplesSinceDecode >= DecodeIntervalSamples)
        {
            samplesSinceDecode %= DecodeIntervalSamples;
            frames = Decode(metadata);
        }
        int retain = DemodulationSampleRateHz;
        if (audioCount > retain)
        {
            int remove = audioCount - retain;
            Array.Copy(audioBuffer, remove, audioBuffer, 0, retain);
            audioCount = retain;
            audioSampleStart += (long)Math.Round(remove * (double)inputSampleRate / DemodulationSampleRateHz);
        }
        return frames;
    }

    public IReadOnlyList<AcarsFrame> ProcessChannel(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata channelMetadata,
        Span<float> demodulatedAudio,
        out int audioSampleCount)
    {
        AppliedChannelConfiguration applied = channelMetadata.Configuration;
        if (applied.OutputSampleRateHz != DemodulationSampleRateHz)
            throw new InvalidOperationException($"ACARS requires a {DemodulationSampleRateHz} Hz channel stream.");
        IqBlockMetadata source = channelMetadata.Source;
        if (channelMetadata.OutputSampleStart == 0 || source.Discontinuity != IqDiscontinuity.None)
        {
            Reset(channelMetadata.MapOutputToSource(0), source.SampleRateHz);
            audioSampleStart = channelMetadata.MapOutputToSource(0);
        }
        targetFrequencyHz = applied.ChannelCenterFrequencyHz;
        audioSampleCount = 0;
        foreach (Complex32 sample in samples)
            ProcessBasebandSample(sample.I, sample.Q, demodulatedAudio, true, ref audioSampleCount);
        IReadOnlyList<AcarsFrame> frames = Array.Empty<AcarsFrame>();
        if (samplesSinceDecode >= DecodeIntervalSamples)
        {
            samplesSinceDecode %= DecodeIntervalSamples;
            frames = Decode(source);
        }
        int retain = DemodulationSampleRateHz;
        if (audioCount > retain)
        {
            int remove = audioCount - retain;
            Array.Copy(audioBuffer, remove, audioBuffer, 0, retain);
            audioCount = retain;
            audioSampleStart += (long)Math.Round(remove * (double)inputSampleRate /
                DemodulationSampleRateHz);
        }
        return frames;
    }

    private void ProcessBasebandSample(
        float inputI,
        float inputQ,
        Span<float> demodulatedAudio,
        bool captureAudio,
        ref int audioSampleCount)
    {
        (float agcI, float agcQ) = channelAgc.Process(inputI, inputQ);
        float envelope = MathF.Sqrt(agcI * agcI + agcQ * agcQ);
        dc += 0.0025f * (envelope - dc);
        float demodulated = demodulatedAudioLowPass.Process(envelope - dc);
        ProcessFastMskSquelch(demodulated);
        float monitorAudio = monitorAudioSquelchGate.Process(
            demodulated, !IsSquelchEnabled || isMskSquelchOpen);
        demodulatedPower += AudioPowerCoefficient *
            (demodulated * demodulated - demodulatedPower);
        demodulatedPeak = MathF.Max(MathF.Abs(demodulated), demodulatedPeak * AudioPeakDecay);
        processedAudioSampleCount++;
        if (audioCount >= audioBuffer.Length)
            Array.Resize(ref audioBuffer, audioBuffer.Length * 2);
        audioBuffer[audioCount++] = demodulated;
        samplesSinceDecode++;
        if (!captureAudio) return;
        if (audioSampleCount >= demodulatedAudio.Length)
            throw new ArgumentException("The demodulated audio buffer is too small.", nameof(demodulatedAudio));
        demodulatedAudio[audioSampleCount++] = monitorAudio;
    }

    public void Reset(long absoluteSampleStart = 0, int sampleRateHz = DemodulationSampleRateHz)
    {
        audioCount = 0;
        inputSampleRate = sampleRateHz;
        downconverter.ResetPhase();
        currentRatePlan = SelectRateConversionPlan(sampleRateHz);
        intermediateDecimationFactor = currentRatePlan.CoarseFactor * currentRatePlan.FineFactor;
        coarseDecimator.Configure(currentRatePlan.CoarseFactor, 3);
        fineDecimator.Configure(currentRatePlan.FineFactor, 3);
        finalResampler.Configure(sampleRateHz, intermediateDecimationFactor,
            DemodulationSampleRateHz, 5_000);
        channelAgc.Reset();
        demodulatedAudioLowPass.Reset();
        dc = 0;
        demodulatedPower = 0;
        demodulatedPeak = 0;
        lastToneConfidence = 0;
        lastMskSquelchMetric = 0;
        isMskSquelchOpen = false;
        mskSquelchHoldSamples = 0;
        monitorAudioSquelchGate.Reset();
        fastMskWindowPosition = 0;
        fastMskWindowCount = 0;
        fastMskHopCount = 0;
        fastMskLowToneAge = 10_000;
        fastMskHighToneAge = 10_000;
        fastMskCrossingInterval = 0;
        fastMskCrossingSign = 0;
        fastMskWindowPower = 0;
        Array.Clear(fastMskMetrics);
        fastMskMetricPosition = 0;
        fastMskMetricCount = 0;
        fastMskMetricSum = 0;
        decodePassCount = 0;
        processedAudioSampleCount = 0;
        samplesSinceDecode = 0;
        audioSampleStart = absoluteSampleStart;
        lastFramePosition = long.MinValue;
        lastCandidatePosition = long.MinValue;
    }

    internal readonly record struct RateConversionPlan(
        int CoarseFactor,
        int FineFactor,
        double IntermediateSampleRateHz,
        int InterpolationFactor,
        int ResamplerDecimationFactor);

    internal static RateConversionPlan SelectRateConversionPlan(int sampleRateHz)
    {
        if (sampleRateHz < DemodulationSampleRateHz)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        RateConversionPlan best = default;
        bool found = false;
        double bestIntermediateDistance = double.MaxValue;
        double bestCoarseDistance = double.MaxValue;
        int maximumCoarseFactor = Math.Max(1, sampleRateHz / 240_000 + 1);
        for (int coarseFactor = 1; coarseFactor <= maximumCoarseFactor; coarseFactor++)
        {
            double coarseRate = sampleRateHz / (double)coarseFactor;
            if (coarseRate is < 240_000 or > 400_000) continue;
            for (int fineFactor = 1; fineFactor <= 8; fineFactor++)
            {
                int totalFactor = coarseFactor * fineFactor;
                double intermediateRate = sampleRateHz / (double)totalFactor;
                if (intermediateRate is < 56_000 or > 72_000) continue;
                long numerator = (long)DemodulationSampleRateHz * totalFactor;
                long divisor = GreatestCommonDivisor(numerator, sampleRateHz);
                int interpolation = checked((int)(numerator / divisor));
                int decimation = checked((int)(sampleRateHz / divisor));
                double intermediateDistance = Math.Abs(intermediateRate - 64_000);
                double coarseDistance = Math.Abs(coarseRate - 320_000);
                if (found && (interpolation > best.InterpolationFactor ||
                    interpolation == best.InterpolationFactor && intermediateDistance > bestIntermediateDistance ||
                    interpolation == best.InterpolationFactor && intermediateDistance == bestIntermediateDistance &&
                    coarseDistance >= bestCoarseDistance)) continue;
                best = new(coarseFactor, fineFactor, intermediateRate, interpolation, decimation);
                bestIntermediateDistance = intermediateDistance;
                bestCoarseDistance = coarseDistance;
                found = true;
            }
        }
        if (found) return best;

        int fallbackCoarse = Math.Max(1, sampleRateHz / 300_000);
        int fallbackCoarseRate = sampleRateHz / fallbackCoarse;
        int fallbackFine = Math.Max(1, fallbackCoarseRate / 60_000);
        int fallbackTotal = fallbackCoarse * fallbackFine;
        long fallbackNumerator = (long)DemodulationSampleRateHz * fallbackTotal;
        long fallbackDivisor = GreatestCommonDivisor(fallbackNumerator, sampleRateHz);
        return new(fallbackCoarse, fallbackFine, sampleRateHz / (double)fallbackTotal,
            checked((int)(fallbackNumerator / fallbackDivisor)),
            checked((int)(sampleRateHz / fallbackDivisor)));
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }
}
