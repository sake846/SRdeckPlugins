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
    private IReadOnlyList<AcarsFrame> Decode(IqBlockMetadata metadata)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        List<AcarsFrame>? result = null;
        float bestRecentMeanConfidence = 0;
        float bestRecentSquelchMetric = 0;
        ReadOnlySpan<float> audioSpan = audioBuffer.AsSpan(0, audioCount);
        int newCorrelationWindows = PopulateCorrelationCache(audioSpan);
        int correlationWindows = 0;
        EnsureCapacity(audioCount / SamplesPerBit);
        Span<PhaseAcquisitionCandidate> phaseCandidates =
            stackalloc PhaseAcquisitionCandidate[SamplesPerBit];
        int phaseCandidateCount = 0;
        for (int phase = 0; phase < SamplesPerBit; phase++)
        {
            int symbolCount = (audioCount - phase) / SamplesPerBit;
            if (symbolCount < 8 * 18) continue;
            GatherPhaseCorrelations(phase, symbolCount);
            ReadOnlySpan<ToneCorrelation> phaseLow = lowCorrelations.AsSpan(0, symbolCount);
            ReadOnlySpan<ToneCorrelation> phaseHigh = highCorrelations.AsSpan(0, symbolCount);
            ReadOnlySpan<float> phaseConfidence = confidenceBuffer.AsSpan(0, symbolCount);
            correlationWindows += symbolCount;
            int recentSymbolCount = Math.Min(symbolCount,
                DecodeIntervalSamples / SamplesPerBit);
            double recentConfidenceSum = 0;
            for (int index = symbolCount - recentSymbolCount;
                 index < symbolCount; index++)
            {
                recentConfidenceSum += phaseConfidence[index];
            }
            float recentMeanConfidence = recentSymbolCount == 0 ? 0 :
                (float)(recentConfidenceSum / recentSymbolCount);
            bestRecentMeanConfidence = MathF.Max(
                bestRecentMeanConfidence, recentMeanConfidence);
            double recentSquelchSum = 0;
            double recentLowPower = 0;
            double recentHighPower = 0;
            for (int index = symbolCount - recentSymbolCount;
                 index < symbolCount; index++)
            {
                recentSquelchSum += MeasureSquelchMetric(
                    audioSpan,
                    phase + index * SamplesPerBit,
                    phaseLow[index].Power,
                    phaseHigh[index].Power,
                    phaseConfidence[index]);
                recentLowPower += phaseLow[index].Power;
                recentHighPower += phaseHigh[index].Power;
            }
            float recentMeanSquelchMetric = recentSymbolCount == 0 ? 0 :
                (float)(recentSquelchSum / recentSymbolCount);
            double toneBalance = 2 * Math.Min(recentLowPower, recentHighPower) /
                Math.Max(recentLowPower + recentHighPower, 1e-20);
            double toneDiversity = Math.Clamp(toneBalance / 0.25, 0, 1);
            recentMeanSquelchMetric *= (float)toneDiversity;
            bestRecentSquelchMetric = MathF.Max(
                bestRecentSquelchMetric, recentMeanSquelchMetric);

            phaseCandidates[phaseCandidateCount++] = MeasurePhaseAcquisition(
                phaseLow, phaseHigh, phaseConfidence, phase, bitsBuffer);
        }

        Span<PhaseAcquisitionCandidate> rankedCandidates =
            stackalloc PhaseAcquisitionCandidate[SamplesPerBit];
        phaseCandidates[..phaseCandidateCount].CopyTo(rankedCandidates);
        SortPhaseAcquisitionCandidates(rankedCandidates[..phaseCandidateCount]);
        int selectedCount = 0;
        while (selectedCount < phaseCandidateCount &&
               selectedCount < MaximumAcquisitionCandidatePhases &&
               rankedCandidates[selectedCount].ExactSyncCount > 0)
            selectedCount++;
        lastAcquisitionCandidatePhaseCount = selectedCount;
        lastFullDecodePhaseCount = 0;

        Span<bool> selectedPhases = stackalloc bool[SamplesPerBit];
        bool selectedPhaseDecoded = false;
        for (int index = 0; index < selectedCount; index++)
        {
            PhaseAcquisitionCandidate candidate = rankedCandidates[index];
            selectedPhases[candidate.Phase] = true;
            selectedPhaseDecoded |= DecodePhase(candidate, metadata, ref result);
        }

        // An exact hard-decision synchronization word is a protocol-defined
        // acquisition gate, not a tuned signal threshold. If acquisition does
        // not produce a BCS-valid frame, retain the exhaustive decoder as the
        // low-confidence fallback and evaluate every remaining phase.
        if (selectedCount == 0 || !selectedPhaseDecoded)
        {
            acquisitionFallbackCount++;
            for (int index = 0; index < phaseCandidateCount; index++)
            {
                PhaseAcquisitionCandidate candidate = phaseCandidates[index];
                if (!selectedPhases[candidate.Phase])
                    DecodePhase(candidate, metadata, ref result);
            }
        }
        lastToneConfidence = bestRecentMeanConfidence;
        lastMskSquelchMetric = bestRecentSquelchMetric;
        UpdateMskSquelch(bestRecentSquelchMetric);
        decodePassCount++;
        lastDecodeScanStartPosition = audioSampleStart;
        lastDecodeScanEndPosition = audioSampleStart + (long)Math.Round(
            audioCount * (double)inputSampleRate / DemodulationSampleRateHz);
        lastDecodeScannedAudioSampleCount = audioCount;
        lastDecodeToneCorrelationCount = newCorrelationWindows * 2L;
        lastDecodeReusedToneCorrelationCount =
            Math.Max(0, correlationWindows - newCorrelationWindows) * 2L;
        totalToneCorrelationCount += lastDecodeToneCorrelationCount;
        lastDecodeCpuMilliseconds =
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        return result is null ? Array.Empty<AcarsFrame>() : result;
    }

    private int PopulateCorrelationCache(ReadOnlySpan<float> audioSpan)
    {
        int requiredCount = Math.Max(0, audioSpan.Length - SamplesPerBit + 1);
        EnsureCorrelationCacheCapacity(requiredCount);
        int previousCount = Math.Min(correlationCacheCount, requiredCount);
        for (int offset = previousCount; offset < requiredCount; offset++)
        {
            MeasureTones(audioSpan, offset,
                out lowCorrelationCache[offset],
                out highCorrelationCache[offset],
                out confidenceCache[offset]);
        }

        correlationCacheCount = requiredCount;
        return requiredCount - previousCount;
    }

    private void EnsureCorrelationCacheCapacity(int requiredCount)
    {
        if (requiredCount <= lowCorrelationCache.Length) return;
        int capacity = Math.Max(requiredCount, lowCorrelationCache.Length * 2);
        Array.Resize(ref lowCorrelationCache, capacity);
        Array.Resize(ref highCorrelationCache, capacity);
        Array.Resize(ref confidenceCache, capacity);
    }

    private void UpdateMskSquelch(float separation)
    {
        if (separation < MskSquelchOpenThreshold) return;
        isMskSquelchOpen = true;
        mskSquelchHoldSamples = MskSquelchHoldSamples;
    }

    private void ProcessFastMskSquelch(float sample)
    {
        if (mskSquelchHoldSamples > 0 && --mskSquelchHoldSamples == 0)
            isMskSquelchOpen = false;

        // Measure half-cycle lengths before the correlation hop. At 48 ksps
        // they are 20 samples for 1,200 Hz and 10 samples for 2,400 Hz.
        // Requiring both lengths rejects a continuous tone without delaying
        // a real alternating MSK preamble until the 100 ms decoder pass.
        fastMskLowToneAge = Math.Min(10_000, fastMskLowToneAge + 1);
        fastMskHighToneAge = Math.Min(10_000, fastMskHighToneAge + 1);
        fastMskCrossingInterval = Math.Min(10_000, fastMskCrossingInterval + 1);
        const float crossingThreshold = 0.002f;
        int crossingSign = sample > crossingThreshold ? 1 :
            sample < -crossingThreshold ? -1 : 0;
        if (crossingSign != 0)
        {
            if (fastMskCrossingSign == 0)
            {
                fastMskCrossingSign = crossingSign;
                fastMskCrossingInterval = 0;
            }
            else if (crossingSign != fastMskCrossingSign)
            {
                int interval = fastMskCrossingInterval;
                fastMskCrossingSign = crossingSign;
                fastMskCrossingInterval = 0;
                if (interval is >= 16 and <= 24) fastMskLowToneAge = 0;
                else if (interval is >= 7 and <= 13) fastMskHighToneAge = 0;
            }
        }

        float previousSample = fastMskWindow[fastMskWindowPosition];
        fastMskWindowPower += sample * sample - previousSample * previousSample;
        fastMskWindow[fastMskWindowPosition] = sample;
        if (++fastMskWindowPosition == SamplesPerBit) fastMskWindowPosition = 0;
        if (fastMskWindowCount < SamplesPerBit)
        {
            fastMskWindowCount++;
            if (fastMskWindowCount < SamplesPerBit) return;
        }
        if (++fastMskHopCount < 10) return;
        fastMskHopCount = 0;

        MeasureMskTonesRing(fastMskWindow, fastMskWindowPosition,
            out ToneCorrelation lowTone, out ToneCorrelation highTone);
        double lowPower = lowTone.Power;
        double highPower = highTone.Power;
        float separation = CalculateMskToneSeparation(lowPower, highPower);
        float metric = CalculateSquelchMetric(
            lowPower, highPower, separation, Math.Max(fastMskWindowPower, 0));
        lastToneConfidence = MathF.Max(lastToneConfidence, separation);
        lastMskSquelchMetric = MathF.Max(lastMskSquelchMetric, metric);
        fastMskMetricSum -= fastMskMetrics[fastMskMetricPosition];
        fastMskMetrics[fastMskMetricPosition] = metric;
        fastMskMetricSum += metric;
        if (++fastMskMetricPosition == FastMskMetricWindowHops)
            fastMskMetricPosition = 0;
        if (fastMskMetricCount < FastMskMetricWindowHops)
            fastMskMetricCount++;
        float averageMetric = fastMskMetricSum / fastMskMetricCount;
        // Both MSK tones must occur within about 20 ms. A continuous birdie at
        // either 1,200 or 2,400 Hz must not hold the squelch open. Requiring an
        // 8 ms average also prevents isolated noise peaks from refreshing the
        // hang timer indefinitely.
        if (fastMskMetricCount < FastMskMetricWindowHops ||
            averageMetric < FastMskAverageOpenThreshold ||
            fastMskLowToneAge > 960 || fastMskHighToneAge > 960)
            return;
        isMskSquelchOpen = true;
        mskSquelchHoldSamples = MskSquelchHoldSamples;
    }

    private bool DecodeToneSequence(ReadOnlySpan<bool> tones, ReadOnlySpan<float> confidence, int phase,
        IqBlockMetadata metadata, ref List<AcarsFrame>? result)
    {
        bool anyDecoded = false;
        if (bitsBuffer.Length < tones.Length * 2)
            Array.Resize(ref bitsBuffer, tones.Length * 2);
        if (bitConfidenceBuffer.Length < tones.Length)
            Array.Resize(ref bitConfidenceBuffer, tones.Length);

        Span<float> bitConfidence = bitConfidenceBuffer.AsSpan(0, tones.Length);
        MapToneConfidenceToNrziBits(confidence, bitConfidence);

        Span<bool> decodedBits = bitsBuffer.AsSpan(0, tones.Length * 2);
        Span<ToneHypothesis> hypotheses = stackalloc ToneHypothesis[32];
        int hypothesisCount = BuildToneHypotheses(tones, decodedBits, hypotheses);
        hypothesisEvaluationCount += hypothesisCount;

        int exactSyncCount = 0;
        for (int index = 0; index < hypothesisCount; index++)
            exactSyncCount += hypotheses[index].ExactSyncCount;
        syncCandidateCount += exactSyncCount;

        Span<int> decodedSingleSyncPositions = stackalloc int[32];
        int decodedSingleSyncPositionCount = 0;
        for (int index = 0; index < hypothesisCount; index++)
        {
            ToneHypothesis hypothesis = hypotheses[index];
            // Scan requires the same three seven-bit sync bytes measured by
            // BuildToneHypotheses. Packing a hypothesis without them cannot
            // produce a frame, including one eligible for BCS correction.
            if (hypothesis.ExactSyncCount == 0)
            {
                hypothesisPrunedCount++;
                continue;
            }
            if (hypothesis.ExactSyncCount == 1 &&
                decodedSingleSyncPositions[..decodedSingleSyncPositionCount]
                    .Contains(hypothesis.BestSyncBitIndex))
            {
                hypothesisEarlyExitCount++;
                continue;
            }

            ReadOnlySpan<bool> bits = decodedBits.Slice(
                hypothesis.TransitionTonePolarity * tones.Length, tones.Length);
            int byteCount = Pack(bits, hypothesis.BitOffset, hypothesis.Invert,
                packedBytesBuffer);
            hypothesisScanCount++;
            bool decoded = Scan(packedBytesBuffer.AsSpan(0, byteCount),
                bitConfidence, phase, hypothesis.BitOffset, metadata, ref result);
            if (decoded)
            {
                anyDecoded = true;
                if (hypothesis.ExactSyncCount == 1)
                    decodedSingleSyncPositions[decodedSingleSyncPositionCount++] = hypothesis.BestSyncBitIndex;
            }
        }
        return anyDecoded;
    }

    internal readonly record struct ToneHypothesis(
        int TransitionTonePolarity,
        int BitOffset,
        bool Invert,
        int MinimumSyncDistance,
        int ExactSyncCount,
        int BestSyncBitIndex,
        int OriginalOrder);

    internal static ToneHypothesis[] RankToneHypotheses(ReadOnlySpan<bool> tones)
    {
        var decodedBits = new bool[tones.Length * 2];
        var hypotheses = new ToneHypothesis[32];
        BuildToneHypotheses(tones, decodedBits, hypotheses);
        return hypotheses;
    }

    private static int BuildToneHypotheses(ReadOnlySpan<bool> tones, Span<bool> decodedBits,
        Span<ToneHypothesis> hypotheses)
    {
        const uint syncPattern = 0x01_16_16;
        const uint sevenBitMask = 0x7f_7f_7f;
        const int comparedSyncBits = 21;
        const int hypothesisCount = 32;
        if (decodedBits.Length < tones.Length * 2)
            throw new ArgumentException("The decoded bit buffer is too small.", nameof(decodedBits));
        if (hypotheses.Length < hypothesisCount)
            throw new ArgumentException("The hypothesis buffer is too small.", nameof(hypotheses));

        int hypothesisIndex = 0;
        for (int polarity = 0; polarity < 2; polarity++)
            for (int bitOffset = 0; bitOffset < 8; bitOffset++)
                for (int invert = 0; invert < 2; invert++)
                {
                    hypotheses[hypothesisIndex] = new(polarity, bitOffset, invert != 0,
                        comparedSyncBits, 0, -1, hypothesisIndex);
                    hypothesisIndex++;
                }

        // ACARS is NRZI. Different SDR I/Q conventions and AM detector
        // polarity can interchange the apparent 1200/2400 Hz states. Keep both
        // interpretations because changing transition polarity is not the same
        // operation as inverting the packed bit stream.
        bool normalState = false;
        bool oppositeState = false;
        Span<bool> normalBits = decodedBits[..tones.Length];
        Span<bool> oppositeBits = decodedBits.Slice(tones.Length, tones.Length);
        for (int symbol = 0; symbol < tones.Length; symbol++)
        {
            if (tones[symbol]) normalState = !normalState;
            else oppositeState = !oppositeState;
            normalBits[symbol] = normalState;
            oppositeBits[symbol] = oppositeState;
        }

        if (tones.Length >= 24)
        {
            for (int polarity = 0; polarity < 2; polarity++)
            {
                ReadOnlySpan<bool> bits = decodedBits.Slice(polarity * tones.Length, tones.Length);
                uint window = 0;
                for (int bit = 0; bit < 24; bit++)
                    if (bits[bit]) window |= 1u << bit;

                for (int start = 0; start + 24 <= bits.Length; start++)
                {
                    int bitOffset = start & 7;
                    int baseIndex = polarity * 16 + bitOffset * 2;
                    int normalDistance = BitOperations.PopCount((window ^ syncPattern) & sevenBitMask);
                    int invertedDistance = BitOperations.PopCount(((~window) ^ syncPattern) & sevenBitMask);
                    hypotheses[baseIndex] = UpdateSyncScore(
                        hypotheses[baseIndex], normalDistance, start);
                    hypotheses[baseIndex + 1] = UpdateSyncScore(
                        hypotheses[baseIndex + 1], invertedDistance, start);

                    window >>= 1;
                    int nextBit = start + 24;
                    if (nextBit < bits.Length && bits[nextBit]) window |= 1u << 23;
                }
            }
        }

        SortHypotheses(hypotheses[..hypothesisCount]);
        return hypothesisCount;

        static ToneHypothesis UpdateSyncScore(ToneHypothesis hypothesis, int distance, int start)
        {
            int exactSyncCount = hypothesis.ExactSyncCount + (distance == 0 ? 1 : 0);
            if (distance >= hypothesis.MinimumSyncDistance)
                return hypothesis with { ExactSyncCount = exactSyncCount };
            return hypothesis with
            {
                MinimumSyncDistance = distance,
                ExactSyncCount = exactSyncCount,
                BestSyncBitIndex = start
            };
        }

        static void SortHypotheses(Span<ToneHypothesis> values)
        {
            // Stable insertion sort keeps the previous exhaustive-search order
            // for hypotheses with the same synchronization score.
            for (int index = 1; index < values.Length; index++)
            {
                ToneHypothesis value = values[index];
                int insert = index;
                while (insert > 0 && Compare(values[insert - 1], value) > 0)
                {
                    values[insert] = values[insert - 1];
                    insert--;
                }
                values[insert] = value;
            }
        }

        static int Compare(ToneHypothesis left, ToneHypothesis right)
        {
            int score = left.MinimumSyncDistance.CompareTo(right.MinimumSyncDistance);
            return score != 0 ? score : left.OriginalOrder.CompareTo(right.OriginalOrder);
        }
    }

    internal static void MapToneConfidenceToNrziBits(
        ReadOnlySpan<float> toneConfidence,
        Span<float> bitConfidence)
    {
        if (bitConfidence.Length < toneConfidence.Length)
            throw new ArgumentException("The NRZI confidence buffer is too small.", nameof(bitConfidence));
        if (toneConfidence.IsEmpty) return;

        float previous = NormalizeConfidence(toneConfidence[0]);
        bitConfidence[0] = previous;
        for (int symbol = 1; symbol < toneConfidence.Length; symbol++)
        {
            float current = NormalizeConfidence(toneConfidence[symbol]);
            // An NRZI bit is determined from two adjacent tone states. Treat
            // the weaker decision as the reliability of that decoded bit.
            bitConfidence[symbol] = MathF.Min(previous, current);
            previous = current;
        }

        static float NormalizeConfidence(float value) =>
            float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    internal readonly record struct ToneCorrelation(double I, double Q, double Power);

    internal static bool[] DecodeCoherentToneSequence(
        ReadOnlySpan<ToneCorrelation> low, ReadOnlySpan<ToneCorrelation> high)
    {
        if (low.Length != high.Length) throw new ArgumentException("Tone correlation lengths differ.");
        if (low.IsEmpty) return [];
        var predecessors = new byte[low.Length * 2];
        var tones = new bool[low.Length];
        DecodeCoherentToneSequence(low, high, predecessors, tones);
        return tones;
    }

    internal static Span<bool> DecodeCoherentToneSequence(
        ReadOnlySpan<ToneCorrelation> low, ReadOnlySpan<ToneCorrelation> high,
        Span<byte> predecessors, Span<bool> tones)
    {
        if (low.Length != high.Length) throw new ArgumentException("Tone correlation lengths differ.");
        if (low.IsEmpty) return Span<bool>.Empty;
        double highScore = Emission(high[0], low[0].Power + high[0].Power);
        double lowScore = Emission(low[0], low[0].Power + high[0].Power);
        for (int symbol = 1; symbol < low.Length; symbol++)
        {
            double totalPower = low[symbol].Power + high[symbol].Power;
            double highEmission = Emission(high[symbol], totalPower);
            double lowEmission = Emission(low[symbol], totalPower);
            double highFromHigh = highScore + PhaseScore(high[symbol - 1], high[symbol], false);
            double highFromLow = lowScore + PhaseScore(low[symbol - 1], high[symbol], true);
            double lowFromHigh = highScore + PhaseScore(high[symbol - 1], low[symbol], false);
            double lowFromLow = lowScore + PhaseScore(low[symbol - 1], low[symbol], true);
            bool highPreviousLow = highFromLow > highFromHigh;
            bool lowPreviousLow = lowFromLow > lowFromHigh;
            predecessors[symbol * 2] = highPreviousLow ? (byte)1 : (byte)0;
            predecessors[symbol * 2 + 1] = lowPreviousLow ? (byte)1 : (byte)0;
            highScore = Math.Max(highFromHigh, highFromLow) + highEmission;
            lowScore = Math.Max(lowFromHigh, lowFromLow) + lowEmission;
        }

        int state = lowScore > highScore ? 1 : 0;
        for (int symbol = low.Length - 1; symbol >= 0; symbol--)
        {
            tones[symbol] = state != 0;
            if (symbol > 0) state = predecessors[symbol * 2 + state];
        }
        return tones.Slice(0, low.Length);

        static double Emission(ToneCorrelation correlation, double totalPower) =>
            Math.Log((correlation.Power + 1e-20) / Math.Max(totalPower, 1e-20));

        static double PhaseScore(ToneCorrelation previous, ToneCorrelation current, bool previousWasLow)
        {
            double magnitude = Math.Sqrt(previous.Power * current.Power);
            if (magnitude < 1e-20) return 0;
            double phaseCoherence = (previous.I * current.I + previous.Q * current.Q) / magnitude;
            // Over one 2400-baud symbol, 1200 Hz advances by pi while
            // 2400 Hz advances by 2*pi. Correlation phase therefore flips only
            // after the low tone.
            if (previousWasLow) phaseCoherence = -phaseCoherence;
            return 0.75 * Math.Clamp(phaseCoherence, -1, 1);
        }
    }

    private bool Scan(ReadOnlySpan<byte> bytes, ReadOnlySpan<float> confidence,
        int phase, int bitOffset,
        IqBlockMetadata metadata, ref List<AcarsFrame>? result)
    {
        bool decoded = false;
        for (int index = 0; index + 18 <= bytes.Length; index++)
        {
            if ((bytes[index] & 0x7f) != 0x16 || (bytes[index + 1] & 0x7f) != 0x16 ||
                (bytes[index + 2] & 0x7f) != 0x01) continue;
            int end = index + 15;
            while (end < bytes.Length && (bytes[end] & 0x7f) is not (0x03 or 0x17)) end++;
            if (end + 2 >= bytes.Length) continue;
            byte[] raw = bytes[index..(end + 3)].ToArray();
            if (!AcarsMessageParser.TryParse(raw, out AcarsMessage? parsed) || parsed is null) continue;
            long demodOffset = phase + ((long)bitOffset + index * 8) * SamplesPerBit;
            long position = audioSampleStart + (long)Math.Round(demodOffset * (double)inputSampleRate / DemodulationSampleRateHz);
            ReadOnlySpan<float> confidenceSlice = confidence.Slice(bitOffset + index * 8, raw.Length * 8);
            double quality = confidenceSlice.IsEmpty ? 0 : Average(confidenceSlice);
            CorrectionReport correctionReport = NoCorrectionReport(CorrectionStatus.None, 0);
            if (!parsed.IsBlockCheckValid &&
                TryCorrectWithConfidence(
                    raw,
                    confidenceSlice,
                    out byte[] corrected,
                    out AcarsMessage? correctedMessage,
                    out correctionReport))
            {
                raw = corrected;
                parsed = correctedMessage!;
            }
            if (parsed.IsBlockCheckValid)
            {
                decoded = true;
                if (position <= lastFramePosition + inputSampleRate / 20) continue;
            }
            if (!parsed.IsBlockCheckValid)
            {
                if (position > lastCandidatePosition + inputSampleRate / 20)
                {
                    lastCandidatePosition = position;
                    RejectedFrameCount++;
                    RecordCorrectionRejection(correctionReport.Status);
                }
                continue;
            }
            // All twenty sampling phases inspect the same transmission. A
            // marginal phase can produce a parseable header with a bad BCS
            // before the optimum phase is visited. Do not count that one
            // over-the-air frame as both rejected and valid.
            long duplicateWindow = inputSampleRate / 20;
            if (RejectedFrameCount > 0 &&
                lastCandidatePosition > lastFramePosition + duplicateWindow &&
                Math.Abs(position - lastCandidatePosition) <= duplicateWindow)
            {
                RejectedFrameCount--;
                ClearLastCorrectionRejection();
            }
            // A decoded BCS is required on air. Parity is retained as a
            // diagnostic because some SDR recordings have already stripped it.
            (result ??= []).Add(new(raw, metadata.UtcTimestamp, metadata.StreamId, position,
                targetFrequencyHz, Math.Clamp(quality, 0, 1)));
            decoded = true;
            lastFramePosition = position;
            lastCandidatePosition = Math.Max(lastCandidatePosition, position);
            ValidFrameCount++;
            RecordAcceptedCorrection(correctionReport);
        }
        return decoded;
    }

    private void RecordCorrectionRejection(CorrectionStatus status)
    {
        lastRejectedCorrectionStatus = status;
        switch (status)
        {
            case CorrectionStatus.Ambiguous:
                ambiguousCorrectionRejectionCount++;
                break;
            case CorrectionStatus.CandidateLimitReached:
                candidateLimitRejectionCount++;
                break;
        }
    }

    private void ClearLastCorrectionRejection()
    {
        switch (lastRejectedCorrectionStatus)
        {
            case CorrectionStatus.Ambiguous when ambiguousCorrectionRejectionCount > 0:
                ambiguousCorrectionRejectionCount--;
                break;
            case CorrectionStatus.CandidateLimitReached when candidateLimitRejectionCount > 0:
                candidateLimitRejectionCount--;
                break;
        }

        lastRejectedCorrectionStatus = CorrectionStatus.None;
    }

    private void RecordAcceptedCorrection(CorrectionReport report)
    {
        lastRejectedCorrectionStatus = CorrectionStatus.None;
        switch (report.Status)
        {
            case CorrectionStatus.SingleBit:
                singleBitCorrectionCount++;
                break;
            case CorrectionStatus.DoubleBit:
                doubleBitCorrectionCount++;
                break;
            default:
                uncorrectedValidFrameCount++;
                return;
        }

        lastCorrectedBitIndex1 = report.FirstBitIndex;
        lastCorrectedBitIndex2 = report.SecondBitIndex;
        lastCorrectedBitConfidence1 = report.FirstBitConfidence;
        lastCorrectedBitConfidence2 = report.SecondBitConfidence;
        lastCorrectionTrialCount = report.TrialCount;
    }

    private void MeasureTones(ReadOnlySpan<float> audioSpan, int offset, out ToneCorrelation lowTone,
        out ToneCorrelation highTone, out float confidence)
    {
        MeasureMskTones(audioSpan.Slice(offset, SamplesPerBit),
            out lowTone, out highTone);
        confidence = CalculateMskToneSeparation(lowTone.Power, highTone.Power);
    }

    private float MeasureSquelchMetric(
        ReadOnlySpan<float> audioSpan, int offset, double lowPower, double highPower, float confidence)
    {
        ReadOnlySpan<float> window = audioSpan.Slice(offset, SamplesPerBit);
        float windowPower = SumOfSquares(window);
        return CalculateSquelchMetric(lowPower, highPower, confidence, windowPower);
    }

    private static float CalculateSquelchMetric(
        double lowPower, double highPower, float confidence, double windowPower)
    {
        double tonePresence = (lowPower + highPower) /
            Math.Max(windowPower * SamplesPerBit * 0.5, 1e-20);
        const double minimumFullScaleToneRms = 0.015;
        double windowRms = Math.Sqrt(windowPower / SamplesPerBit);
        double amplitudePresence = Math.Clamp(windowRms / minimumFullScaleToneRms, 0, 1);
        return confidence * (float)(
            Math.Clamp(tonePresence, 0, 1) * amplitudePresence);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SumOfSquares(ReadOnlySpan<float> span)
    {
        float sum = 0f;
        int index = 0;
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            Vector<float> vsum = Vector<float>.Zero;
            int vectorEnd = span.Length - (span.Length % Vector<float>.Count);
            for (; index < vectorEnd; index += Vector<float>.Count)
            {
                Vector<float> v = new(span.Slice(index, Vector<float>.Count));
                vsum += v * v;
            }
            sum = Vector.Dot(vsum, Vector<float>.One);
        }
        for (; index < span.Length; index++)
        {
            float val = span[index];
            sum += val * val;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Average(ReadOnlySpan<float> span)
    {
        if (span.IsEmpty) return 0f;
        float sum = 0f;
        int index = 0;
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            Vector<float> vsum = Vector<float>.Zero;
            int vectorEnd = span.Length - (span.Length % Vector<float>.Count);
            for (; index < vectorEnd; index += Vector<float>.Count)
            {
                Vector<float> v = new(span.Slice(index, Vector<float>.Count));
                vsum += v;
            }
            sum = Vector.Dot(vsum, Vector<float>.One);
        }
        for (; index < span.Length; index++)
        {
            sum += span[index];
        }
        return sum / span.Length;
    }

    /// <summary>
    /// Corrects one damaged over-the-air bit when odd character parity identifies
    /// the affected byte and the BCS uniquely identifies the bit. If all character
    /// parity is intact, only the two unprotected BCS bytes are considered.
    /// </summary>
}
