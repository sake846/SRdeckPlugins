using System.Diagnostics;
using System.Numerics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Streaming VDL Mode 2 receiver. The physical layer is D8PSK at 10.5 ksym/s
/// with a 16-symbol preamble, a scrambled 25-bit length header and an
/// interleaved Reed-Solomon protected data field containing HDLC/AVLC frames.
/// </summary>
public sealed partial class VdlMode2Receiver
{
    public IReadOnlyList<VdlFrame> Process(ReadOnlySpan<Complex32> input, IqBlockMetadata metadata) =>
        Process(input, metadata, metadata.CenterFrequencyHz);

    /// <summary>Mixes the selected VDL channel to zero IF before filtering and resampling.</summary>
    public IReadOnlyList<VdlFrame> Process(ReadOnlySpan<Complex32> input, IqBlockMetadata metadata,
        long targetFrequencyHz) =>
        Process(input, metadata, targetFrequencyHz, null, out _);

    public IReadOnlyList<VdlFrame> Process(ReadOnlySpan<Complex32> input, IqBlockMetadata metadata,
        long targetFrequencyHz, float[]? monitorAudio, out int monitorAudioSampleCount)
        => Process(input, metadata, targetFrequencyHz, monitorAudio, out monitorAudioSampleCount,
            null, out _);

    public IReadOnlyList<VdlFrame> Process(ReadOnlySpan<Complex32> input, IqBlockMetadata metadata,
        long targetFrequencyHz, float[]? monitorAudio, out int monitorAudioSampleCount,
        Complex32[]? channelIq, out int channelIqSampleCount)
    {
        monitorAudioSampleCount = 0;
        channelIqSampleCount = 0;

        if (metadata.SampleRateHz < SymbolRate * 4)
            return Array.Empty<VdlFrame>();

        long offsetHz = targetFrequencyHz - metadata.CenterFrequencyHz;
        if (Math.Abs(offsetHz) + SymbolRate > metadata.SampleRateHz / 2.0)
            return Array.Empty<VdlFrame>();

        if (inputSampleRate != metadata.SampleRateHz || metadata.Discontinuity != IqDiscontinuity.None)
            Reset(metadata.SampleRateHz);
        FrequencyOffsetHz = offsetHz;
        centerFrequencyHz = metadata.CenterFrequencyHz;
        this.targetFrequencyHz = targetFrequencyHz;
        downconverter.Configure(offsetHz, metadata.SampleRateHz);
        double inputPower = 0;
        double workingPower = 0;
        double workingPeak = 0;
        long workingCount = 0;
        int monitorCount = 0;
        int channelCount = 0;
        monitorAudioSampleCount = 0;
        channelIqSampleCount = 0;
        currentBlockMinSyncError = double.PositiveInfinity;
        currentBlockMaxCoherence = 0;

        var frames = new List<VdlFrame>();
        void Emit(float i, float q)
        {
            if (channelIq is not null)
            {
                if (channelCount >= channelIq.Length)
                    throw new ArgumentException("The VDL channel IQ buffer is too small.", nameof(channelIq));
                channelIq[channelCount++] = new Complex32(i, q);
            }
            Complex32 filtered = matchedFilter.Process(new Complex32(i, q));
            double power = filtered.I * filtered.I + filtered.Q * filtered.Q;
            workingPower += power;
            workingPeak = Math.Max(workingPeak, Math.Sqrt(power));
            workingCount++;
            if (audioMonitor.TryProcess(filtered, noiseFloorPower, state == ReceiverState.Receiving, IsSquelchEnabled, out float audioSample))
            {
                if (monitorAudio is not null && monitorCount >= monitorAudio.Length)
                    throw new ArgumentException("The VDL monitor audio buffer is too small.", nameof(monitorAudio));
                if (monitorAudio is not null) monitorAudio[monitorCount++] = audioSample;
            }
            ProcessWorkingSample(filtered, metadata, frames);
        }
        foreach (Complex32 sample in input)
        {
            inputPower += sample.I * sample.I + sample.Q * sample.Q;
            downconverter.Mix(sample.I, sample.Q, out float mixedI, out float mixedQ);
            if (coarseDecimator.TryProcess(mixedI, mixedQ, out float coarseI, out float coarseQ))
                resampler.Process(coarseI, coarseQ, Emit);
        }
        inputRms = input.Length == 0 ? 0 : Math.Sqrt(inputPower / input.Length);
        channelRms = workingCount == 0 ? 0 : Math.Sqrt(workingPower / workingCount);
        channelPeak = workingPeak;
        processedInputSamples += input.Length;
        processedWorkingSamples += workingCount;
        monitorAudioSampleCount = monitorCount;
        channelIqSampleCount = channelCount;
        UpdateDisplayMetrics(currentBlockMinSyncError, currentBlockMaxCoherence);
        return frames;
    }

    public IReadOnlyList<VdlFrame> ProcessChannel(
        ReadOnlySpan<Complex32> input,
        ChannelIqBlockMetadata channelMetadata,
        float[]? monitorAudio,
        out int monitorAudioSampleCount)
    {
        AppliedChannelConfiguration applied = channelMetadata.Configuration;
        if (applied.OutputSampleRateHz != WorkingSampleRate)
            throw new InvalidOperationException($"VDL2 requires a {WorkingSampleRate} Hz channel stream.");
        IqBlockMetadata source = channelMetadata.Source;
        if (inputSampleRate != source.SampleRateHz || channelMetadata.OutputSampleStart == 0 ||
            source.Discontinuity != IqDiscontinuity.None)
            Reset(source.SampleRateHz);
        var workingMetadata = source with
        {
            AbsoluteSampleStart = channelMetadata.OutputSampleStart,
            SampleRateHz = WorkingSampleRate,
            CenterFrequencyHz = applied.ChannelCenterFrequencyHz,
            SampleCount = input.Length
        };
        FrequencyOffsetHz = applied.ChannelCenterFrequencyHz - source.CenterFrequencyHz;
        centerFrequencyHz = source.CenterFrequencyHz;
        targetFrequencyHz = applied.ChannelCenterFrequencyHz;
        double inputPower = 0;
        double workingPower = 0;
        double workingPeak = 0;
        int monitorCount = 0;
        currentBlockMinSyncError = double.PositiveInfinity;
        currentBlockMaxCoherence = 0;
        var frames = new List<VdlFrame>();
        foreach (Complex32 sample in input)
        {
            inputPower += sample.I * sample.I + sample.Q * sample.Q;
            Complex32 filtered = matchedFilter.Process(sample);
            double power = filtered.I * filtered.I + filtered.Q * filtered.Q;
            workingPower += power;
            workingPeak = Math.Max(workingPeak, Math.Sqrt(power));
            if (audioMonitor.TryProcess(filtered, noiseFloorPower, state == ReceiverState.Receiving, IsSquelchEnabled, out float audioSample))
            {
                if (monitorAudio is not null && monitorCount >= monitorAudio.Length)
                    throw new ArgumentException("The VDL monitor audio buffer is too small.", nameof(monitorAudio));
                if (monitorAudio is not null) monitorAudio[monitorCount++] = audioSample;
            }
            ProcessWorkingSample(filtered, workingMetadata, frames);
        }

        inputRms = input.Length == 0 ? 0 : Math.Sqrt(inputPower / input.Length);
        channelRms = input.Length == 0 ? 0 : Math.Sqrt(workingPower / input.Length);
        channelPeak = workingPeak;
        processedInputSamples += input.Length;
        processedWorkingSamples += input.Length;
        monitorAudioSampleCount = monitorCount;
        for (int index = 0; index < frames.Count; index++)
        {
            VdlFrame frame = frames[index];
            long sourcePosition = channelMetadata.MapOutputToSource(frame.SamplePosition);
            frames[index] = frame with
            {
                SamplePosition = sourcePosition,
                ReceivedAt = source.UtcTimestamp.AddSeconds(
                    (sourcePosition - source.AbsoluteSampleStart) / (double)source.SampleRateHz),
                StreamId = source.StreamId,
                FrequencyHz = applied.ChannelCenterFrequencyHz
            };
        }
        UpdateDisplayMetrics(currentBlockMinSyncError, currentBlockMaxCoherence);
        return frames;
    }

    public void Reset(int sampleRateHz = WorkingSampleRate)
    {
        inputSampleRate = sampleRateHz;
        coarseDecimationFactor = SelectCoarseDecimationFactor(sampleRateHz);
        intermediateSampleRate = sampleRateHz / (double)coarseDecimationFactor;
        coarseDecimator.Configure(coarseDecimationFactor, 3);
        resampler.Configure(sampleRateHz, coarseDecimationFactor, WorkingSampleRate, 8_400);
        matchedFilter.Reset();
        audioMonitor.Reset();
        syncWriteIndex = 0;
        syncSampleCount = 0;
        Array.Clear(syncBuffer);
        Array.Clear(timingBuffer);
        workingSampleIndex = -1;
        noiseFloorPower = 0;
        noiseEstimateCount = 0;
        lastPreambleCoherence = 0;
        lastPreamblePower = 0;
        lastPreambleSnrDb = double.NaN;
        lastPreamblePhaseResidualRms = 0;
        lastPreambleAmplitudeCoefficientOfVariation = 0;
        currentBlockMinSyncError = double.PositiveInfinity;
        currentBlockMaxCoherence = 0;
        displaySyncError = double.PositiveInfinity;
        displayCoherence = 0;
        displayHoldUntil = DateTime.MinValue;
        downconverter.ResetPhase();
        ResetBurst();
    }

    private void UpdateDisplayMetrics(double blockSyncError, double blockCoherence)
    {
        DateTime now = DateTime.UtcNow;
        if (!double.IsFinite(blockSyncError)) return;

        if (blockCoherence >= displayCoherence || now > displayHoldUntil)
        {
            if (blockCoherence > displayCoherence)
                displayHoldUntil = now.AddMilliseconds(800);

            displayCoherence = blockCoherence;
            displaySyncError = blockSyncError;
        }
        else
        {
            displayCoherence += 0.2 * (blockCoherence - displayCoherence);
            displaySyncError += 0.2 * (blockSyncError - displaySyncError);
        }
    }

    private void ProcessWorkingSample(Complex32 sample, IqBlockMetadata metadata, List<VdlFrame> output)
    {
        workingSampleIndex++;
        timingBuffer[(int)(workingSampleIndex % TimingBufferLength)] = sample;
        if (state == ReceiverState.Searching)
        {
            UpdateNoiseEstimate(sample.I * sample.I + sample.Q * sample.Q);
            syncBuffer[syncWriteIndex] = sample;
            syncWriteIndex = (syncWriteIndex + 1) % syncBuffer.Length;
            if (syncSampleCount < syncBuffer.Length) syncSampleCount++;
            if (syncSampleCount == syncBuffer.Length)
            {
                double syncError = CalculateSynchronizationError(out double phase, out double drift,
                    out double coherence, out double preamblePower);
                if (syncError < currentBlockMinSyncError) currentBlockMinSyncError = syncError;
                if (coherence > currentBlockMaxCoherence) currentBlockMaxCoherence = coherence;
                if (previousSyncError < SyncThreshold && previousSyncCoherence >= PreambleCoherenceThreshold && syncError > previousSyncError)
                {
                    PreambleCandidateCount++;
                    RecordPreambleQuality(previousSyncCoherence, previousSyncPower);
                    if (PreambleQualityAccepted())
                    {
                        CalculatePreambleChannelFit(previousSyncDrift, previousSyncPhase,
                            out double flatChannelNmse, out double threeTapChannelNmse,
                            out Complex[] channelTaps);
                        PreambleQualityObserver?.Invoke(new(PreambleVerificationSymbols,
                            previousSyncCoherence, lastPreambleSnrDb, previousSyncPhaseResidualRms,
                            previousSyncAmplitudeCoefficientOfVariation,
                            previousSyncDrift * SymbolRate / (2 * Math.PI), flatChannelNmse,
                            threeTapChannelNmse));
                        previousPhase = previousSyncPhase;
                        phaseDriftPerSymbol = previousSyncDrift;
                        ConfigureAdaptiveEqualizer(channelTaps, flatChannelNmse,
                            threeTapChannelNmse, previousSyncPhase + phaseDriftPerSymbol);
                        burstInitialPreviousPhase = previousPhase;
                        burstInitialPhaseDrift = phaseDriftPerSymbol;
                        burstBits.Clear();
                        burstReliabilities.Clear();
                        burstSymbols.Clear();
                        burstSymbolsEarly.Clear();
                        burstSymbolsLate.Clear();
                        ResetBurstQuality();
                        requestedBurstBits = HeaderLength;
                        transmissionLength = 0;
                        state = ReceiverState.Receiving;
                        SynchronizationCount++;
                        // The previous correlation window ended at the preceding
                        // sample. Its next symbol centre is the current sample.
                        AcceptSymbol(sample, workingSampleIndex, metadata, output);
                        if (state == ReceiverState.Receiving)
                            StartTimingRecovery(workingSampleIndex, sample);
                    }
                    else
                    {
                        QualityRejectedCount++;
                        previousSyncError = double.PositiveInfinity;
                    }
                }
                else
                {
                    previousSyncError = syncError;
                    previousSyncPhase = phase;
                    previousSyncDrift = drift;
                    previousSyncCoherence = coherence;
                    previousSyncPower = preamblePower;
                    previousSyncPhaseResidualRms = lastPreamblePhaseResidualRms;
                    previousSyncAmplitudeCoefficientOfVariation =
                        lastPreambleAmplitudeCoefficientOfVariation;
                }
            }
            return;
        }

        UpdateTimingRecovery();
        if (state != ReceiverState.Receiving || !CanInterpolate(nextSymbolTime)) return;
        double symbolTime = nextSymbolTime;
        Complex32 symbol = Interpolate(symbolTime);
        AcceptSymbol(symbol, symbolTime, metadata, output);
        if (state != ReceiverState.Receiving) return;
        timingCenterTime = symbolTime;
        timingCenterSample = symbol;
        timingErrorPending = true;
        lastTimingOffsetSamples = symbolTime - Math.Round(symbolTime);
        nextSymbolTime = symbolTime + SamplesPerSymbol + timingRateCorrection;
    }
}
