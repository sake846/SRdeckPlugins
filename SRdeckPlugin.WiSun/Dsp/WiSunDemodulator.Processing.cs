using SRdeckPlugin.Contracts;
using SRdeckPlugin.WiSun.Models;

namespace SRdeckPlugin.WiSun.Dsp;

/// <summary>
/// Streaming IEEE 802.15.4 SUN-FSK receiver for JP FAN mode #1b and HAN/B Route.
/// The input must be a host-channelized complex baseband stream with 8 samples/bit.
/// </summary>
public sealed partial class WiSunDemodulator
{
    public IReadOnlyList<WiSunPacketFrame> ProcessChannel(
        ReadOnlySpan<Complex32> inputSamples,
        ChannelIqBlockMetadata metadata,
        float squelchThresholdDbfs)
    {
        if (!IsValidSampleRate(metadata.Configuration.OutputSampleRateHz))
            throw new InvalidOperationException(
                $"Wi-SUN requires a supported channel stream rate (got {metadata.Configuration.OutputSampleRateHz:N0} samples/s).");
        if (metadata.OutputSampleStart == 0 || metadata.Source.Discontinuity != IqDiscontinuity.None)
            Reset();
        IReadOnlyList<WiSunPacketFrame> frames = ProcessWorkingBlock(inputSamples, metadata.Configuration.OutputSampleRateHz,
            metadata.Configuration.ChannelCenterFrequencyHz, squelchThresholdDbfs,
            metadata.Source.UtcTimestamp);
        LastSourceInputSampleRateHz = metadata.Configuration.InputSampleRateHz;
        LastIntermediateSampleRateHz = metadata.Configuration.InputSampleRateHz /
            (double)Math.Max(1, metadata.Configuration.CoarseDecimationFactor) /
            Math.Max(1, metadata.Configuration.FineDecimationFactor);
        UsesHostChannelRateConversion = true;
        return frames;
    }

    public IReadOnlyList<WiSunPacketFrame> ProcessWorkingBlock(
        ReadOnlySpan<Complex32> inputSamples,
        int sampleRateHz,
        long frequencyHz,
        float squelchThresholdDbfs,
        DateTimeOffset timestamp)
    {
        if (!IsValidSampleRate(sampleRateHz))
            throw new InvalidOperationException(
                $"Wi-SUN requires a supported channel stream rate (got {sampleRateHz:N0} samples/s).");
        if (inputSamples.IsEmpty) return Array.Empty<WiSunPacketFrame>();

        float rfBurstThresholdPower = MathF.Pow(10, squelchThresholdDbfs / 10);
        double inputPower = 0;
        foreach (Complex32 sample in inputSamples)
        {
            float magnitudeSquared = sample.I * sample.I + sample.Q * sample.Q;
            inputPower += magnitudeSquared;
            if (magnitudeSquared < rfBurstThresholdPower)
                UpdateNoiseFloor(magnitudeSquared);
            TrackRfBurst(magnitudeSquared, sampleRateHz, rfBurstThresholdPower);
            float phaseDifference = 0;
            if (hasPreviousSample)
            {
                float dot = sample.I * previousSample.I + sample.Q * previousSample.Q;
                float cross = sample.Q * previousSample.I - sample.I * previousSample.Q;
                phaseDifference = FastAtan2(cross, dot);
            }
            previousSample = sample;
            hasPreviousSample = true;
            discriminator.Add(phaseDifference);
            power.Add(magnitudeSquared);
            discriminatorPrefix.Add(discriminatorPrefix[^1] + phaseDifference);
            powerPrefix.Add(powerPrefix[^1] + magnitudeSquared);
        }
        LastInputLevelDbfs = 10 * Math.Log10(Math.Max(inputPower / inputSamples.Length, 1e-12));
        LastNoiseFloorDbfs = noiseFloorSamples > 0
            ? 10 * Math.Log10(Math.Max(noiseFloorPower, 1e-12))
            : double.NaN;
        LastInputSampleRateHz = sampleRateHz;
        LastSourceInputSampleRateHz = sampleRateHz;
        LastIntermediateSampleRateHz = 0;
        UsesHostChannelRateConversion = false;
        LastFrequencyHz = frequencyHz;
        LastMeasuredAt = DateTimeOffset.Now;

        var frames = new List<WiSunPacketFrame>();
        DecodeAvailable(sampleRateHz, frequencyHz, squelchThresholdDbfs, timestamp, frames);
        TrimConsumedSamples();
        OnDiagnosticCountersChanged?.Invoke();
        foreach (WiSunPacketFrame frame in frames) OnPacketDecoded?.Invoke(frame);
        return frames;
    }

    private void TrackRfBurst(
        float magnitudeSquared,
        int sampleRateHz,
        float thresholdPower)
    {
        bool aboveSquelch = magnitudeSquared >= thresholdPower;
        if (aboveSquelch)
        {
            if (!inRfBurst)
            {
                inRfBurst = true;
                rfBurstSamples = 0;
                rfBurstStartSample = discriminator.Count;
            }
            rfBurstSamples++;
            rfBurstGapSamples = 0;
            return;
        }

        if (!inRfBurst) return;
        rfBurstSamples++;
        rfBurstGapSamples++;
        int endGapSamples = Math.Max(1, sampleRateHz / 10_000); // 0.1 ms debounce
        if (rfBurstGapSamples < endGapSamples) return;

        int activeSamples = rfBurstSamples - rfBurstGapSamples;
        if (activeSamples >= sampleRateHz * 2 / 1_000)
        {
            TotalRfBursts++;
        }
        inRfBurst = false;
        rfBurstSamples = 0;
        rfBurstGapSamples = 0;
    }

    private void CheckAndLogRawBurst(DateTimeOffset timestamp)
    {
        if (!EnableRawBurstLog || rfBurstStartSample < 0) return;
        int requiredSamples = rfBurstStartSample + 128 * SamplesPerBit;
        if (discriminator.Count < requiredSamples) return;

        int burstStart = rfBurstStartSample;
        rfBurstStartSample = -1;

        int byteCount = 16;
        Span<byte> rawBytes = stackalloc byte[byteCount];
        var sbBits = new System.Text.StringBuilder(byteCount * 8);

        for (int byteIndex = 0; byteIndex < byteCount; byteIndex++)
        {
            byte b = 0;
            for (int bitIndex = 0; bitIndex < 8; bitIndex++)
            {
                int bitPos = byteIndex * 8 + bitIndex;
                int samplePos = burstStart + bitPos * SamplesPerBit;
                float metric = SymbolMetric(samplePos);
                bool bit = metric >= 0;
                if (bit) b |= (byte)(1 << (7 - bitIndex));
                sbBits.Append(bit ? '1' : '0');
            }
            rawBytes[byteIndex] = b;
        }

        string hex = Convert.ToHexString(rawBytes);
        OnDiagnosticLog?.Invoke($"[{timestamp:HH:mm:ss.fff}] [BURST RAW] Sample:{burstStart} | Hex:{hex} | Bits:{sbBits}");
    }

    private void DecodeAvailable(
        int sampleRateHz,
        long frequencyHz,
        float squelchThresholdDbfs,
        DateTimeOffset timestamp,
        List<WiSunPacketFrame> frames)
    {
        CheckAndLogRawBurst(timestamp);
        int fixedHeaderSamples = (SfdBitCount + PhrBitCount) * SamplesPerBit;
        float squelchPower = MathF.Pow(10, squelchThresholdDbfs / 10);
        while (scanSample + fixedHeaderSamples <= discriminator.Count)
        {
            if (!TrySynchronize(scanSample, squelchPower, timestamp,
                    out int polarity, out float discriminatorBias,
                    out int sfdErrors, out ushort matchedSfd, out bool newSfd))
            {
                scanSample++;
                continue;
            }
            if (newSfd) TotalSfdMatches++;

            // SUN-FSK transmits the 16-bit PHR MSB first. The PSDU that follows
            // is transmitted LSB first. Do not infer alternate layouts from a
            // failed CRC: a false layout can turn noise into a very long frame.
            SymbolTimingState timing = CreateTimingState(
                scanSample, matchedSfd, polarity, discriminatorBias);
            if (!TryReadTrackedWordMsbFirst(ref timing, out ushort phr)) return;
            int psduLength = phr & 0x07FF;
            bool whiteningEnabled = (phr & 0x0800) != 0;
            bool usesTwoByteFcs = (phr & 0x1000) != 0;
            int fcsLength = usesTwoByteFcs ? 2 : 4;
            if (psduLength < fcsLength || psduLength > MaximumPsduLength)
            {
                scanSample += SamplesPerBit;
                continue;
            }
            if (newSfd) TotalPhrValid++;

            double estimatedFrameEnd = timing.NextSymbolStart +
                psduLength * 8 * timing.SamplesPerBit + 2 * SamplesPerBit;
            if (estimatedFrameEnd > discriminator.Count) return;

            var psdu = new byte[psduLength];
            if (!TryReadTrackedBytesLsbFirst(ref timing, psduLength, psdu))
                return;
            TotalPayloadRead++;
            int frameEnd = Math.Min(discriminator.Count,
                (int)Math.Ceiling(timing.NextSymbolStart));
            LastRecoveredSamplesPerBit = timing.SamplesPerBit;
            if (whiteningEnabled) ApplyPn9(psdu);
            ReadFcsValues(
                psdu, fcsLength, out ulong receivedFcs, out ulong calculatedFcs);
            bool crcValid = receivedFcs == calculatedFcs;

            if (crcValid)
            {
                TotalCrcOk++;
                OnDiagnosticLog?.Invoke($"[{timestamp:HH:mm:ss.fff}] [CRC OK] PSDU {psduLength}B");
            }
            else
            {
                TotalCrcNg++;
                OnDiagnosticLog?.Invoke(
                    $"[{timestamp:HH:mm:ss.fff}] [CRC NG] " +
                    $"PHR:0x{phr:X4} LEN:{psduLength} PN9:{(whiteningEnabled ? "on" : "off")} " +
                    $"FCS:{fcsLength * 8} | " +
                    $"RX:{FormatFcs(receivedFcs, fcsLength)} " +
                    $"CALC:{FormatFcs(calculatedFcs, fcsLength)} " +
                    $"SYN:{FormatFcs(receivedFcs ^ calculatedFcs, fcsLength)} | " +
                    $"Raw:{Convert.ToHexString(psdu)}");
                if (RejectInvalidFcs)
                {
                    // Continue searching after the candidate SFD. Skipping
                    // the declared (possibly corrupt) length can hide the next
                    // valid frame.
                    scanSample += SfdBitCount * SamplesPerBit;
                    continue;
                }
            }

            byte[] macFrame = psdu[..^fcsLength];
            MeasurePower(scanSample, frameEnd, out float peakDbfs, out float averageDbfs);
            double durationMs = (frameEnd - scanSample) * 1_000.0 / sampleRateHz;
            string phyName = sampleRateHz switch
            {
                2_400_000 => "FAN Mode #5 300 kbps",
                1_600_000 => "FAN Mode #4 200 kbps",
                1_200_000 => "FAN Mode #3 150 kbps",
                800_000 => "HAN/B Route 100 kbps",
                _ => "50 kbps"
            };
            WiSunPacketFrame frame = DecodeMacFrame(macFrame, timestamp, frequencyHz,
                durationMs, peakDbfs, averageDbfs - (float)LastNoiseFloorDbfs,
                whiteningEnabled, fcsLength, crcValid, phyName);
            frames.Add(frame);
            TotalFramesPublished++;
            scanSample = frameEnd;

        }
    }

    private void UpdateNoiseFloor(float samplePower)
    {
        if (!float.IsFinite(samplePower) || samplePower < 0) return;
        double boundedPower = Math.Max(samplePower, 1e-12f);
        if (noiseFloorPower <= 0 || !double.IsFinite(noiseFloorPower))
        {
            noiseFloorPower = boundedPower;
            noiseFloorSamples++;
            return;
        }

        // Only below-squelch samples reach this method, so packet energy cannot
        // be mistaken for noise. The threshold selects idle samples; the value
        // reported as the floor is still measured from IQ power.
        noiseFloorPower += 0.002 * (boundedPower - noiseFloorPower);
        noiseFloorSamples++;
    }
}
