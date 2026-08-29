using System.Numerics;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;

namespace SRdeckPlugin.Acars.Dsp;

public sealed partial class AcarsReceiver
{
    internal enum CorrectionStatus
    {
        None,
        SingleBit,
        DoubleBit,
        Ambiguous,
        CandidateLimitReached,
    }

    internal readonly record struct CorrectionReport(
        CorrectionStatus Status,
        int FirstBitIndex,
        int SecondBitIndex,
        float FirstBitConfidence,
        float SecondBitConfidence,
        int TrialCount);

    internal readonly record struct RankedCorrectionCandidate(
        int FirstBitIndex,
        int SecondBitIndex,
        float Cost);

    private readonly record struct BitCandidate(int BitIndex, float Confidence);

    internal const int MaximumCorrectedBits = 2;
    internal const int MaxCandidatesPerParityByte = 4;
    internal const int MaxBcsCandidates = 8;
    internal const int MaxNoParityCandidates = 8;
    internal const int MaxTwoBitCorrectionTrials = 64;
    internal const float MinimumCorrectionCostMargin = 0.02f;

    internal static bool TryCorrectSingleBit(byte[] raw, out byte[] corrected,
        out AcarsMessage? message) =>
        TryCorrectSingleBitCore(raw, out corrected, out message, out _, out _);

    internal static bool TryCorrectWithConfidence(
        byte[] raw,
        ReadOnlySpan<float> bitConfidence,
        out byte[] corrected,
        out AcarsMessage? message,
        out CorrectionReport report)
    {
        corrected = raw;
        message = null;

        if (!AcarsMessageParser.TryParse(raw, out AcarsMessage? original) || original is null ||
            original.IsBlockCheckValid)
        {
            report = NoCorrectionReport(CorrectionStatus.None, 0);
            return false;
        }

        if (TryCorrectSingleBitCore(
                raw,
                out corrected,
                out message,
                out bool oneBitAmbiguous,
                out int oneBitTrialCount))
        {
            int changedBit = FindChangedBit(raw, corrected);
            report = new CorrectionReport(
                CorrectionStatus.SingleBit,
                changedBit,
                -1,
                GetBitConfidence(bitConfidence, changedBit),
                0f,
                oneBitTrialCount);
            return true;
        }

        if (oneBitAmbiguous)
        {
            report = NoCorrectionReport(CorrectionStatus.Ambiguous, oneBitTrialCount);
            return false;
        }

        if (bitConfidence.Length < raw.Length * 8 || !TryFindTerminator(raw, out int terminator))
        {
            report = NoCorrectionReport(CorrectionStatus.None, oneBitTrialCount);
            return false;
        }

        Span<int> parityErrorBytes = stackalloc int[3];
        int parityErrorCount = FindParityErrorBytes(raw, terminator, parityErrorBytes);
        if (parityErrorCount > MaximumCorrectedBits)
        {
            report = NoCorrectionReport(CorrectionStatus.CandidateLimitReached, oneBitTrialCount);
            return false;
        }

        Span<BitCandidate> firstCandidates = stackalloc BitCandidate[MaxNoParityCandidates];
        Span<BitCandidate> secondCandidates = stackalloc BitCandidate[MaxBcsCandidates];
        int firstCount;
        int secondCount;
        bool usePairsWithinFirst;
        bool candidatePoolTruncated;

        switch (parityErrorCount)
        {
            case 2:
                firstCount = SelectLowestConfidenceBits(
                    bitConfidence,
                    parityErrorBytes[0] * 8,
                    8,
                    firstCandidates[..MaxCandidatesPerParityByte]);
                secondCount = SelectLowestConfidenceBits(
                    bitConfidence,
                    parityErrorBytes[1] * 8,
                    8,
                    secondCandidates[..MaxCandidatesPerParityByte]);
                usePairsWithinFirst = false;
                candidatePoolTruncated = true;
                break;

            case 1:
                firstCount = SelectLowestConfidenceBits(
                    bitConfidence,
                    parityErrorBytes[0] * 8,
                    8,
                    firstCandidates[..MaxCandidatesPerParityByte]);
                secondCount = SelectLowestConfidenceBits(
                    bitConfidence,
                    (terminator + 1) * 8,
                    16,
                    secondCandidates);
                usePairsWithinFirst = false;
                candidatePoolTruncated = true;
                break;

            default:
                int firstCorrectionByte = Math.Min(3, raw.Length);
                firstCount = SelectLowestConfidenceBits(
                    bitConfidence,
                    firstCorrectionByte * 8,
                    (raw.Length - firstCorrectionByte) * 8,
                    firstCandidates);
                secondCount = 0;
                usePairsWithinFirst = true;
                candidatePoolTruncated = raw.Length - firstCorrectionByte > 1;
                break;
        }

        Span<RankedCorrectionCandidate> validCandidates =
            stackalloc RankedCorrectionCandidate[MaxTwoBitCorrectionTrials];
        int validCandidateCount = 0;
        int twoBitTrialCount = 0;
        bool trialLimitReached = false;

        if (usePairsWithinFirst)
        {
            for (int first = 0; first < firstCount - 1 && !trialLimitReached; first++)
            {
                for (int second = first + 1; second < firstCount; second++)
                {
                    if (!TryTwoBitCandidate(
                            raw,
                            firstCandidates[first],
                            firstCandidates[second],
                            validCandidates,
                            ref validCandidateCount,
                            ref twoBitTrialCount))
                    {
                        trialLimitReached = true;
                        break;
                    }
                }
            }
        }
        else
        {
            for (int first = 0; first < firstCount && !trialLimitReached; first++)
            {
                for (int second = 0; second < secondCount; second++)
                {
                    if (!TryTwoBitCandidate(
                            raw,
                            firstCandidates[first],
                            secondCandidates[second],
                            validCandidates,
                            ref validCandidateCount,
                            ref twoBitTrialCount))
                    {
                        trialLimitReached = true;
                        break;
                    }
                }
            }
        }

        int trialCount = oneBitTrialCount + twoBitTrialCount;
        int selected = SelectUniqueMinimumCost(validCandidates[..validCandidateCount], out bool ambiguous);
        if (ambiguous)
        {
            report = NoCorrectionReport(CorrectionStatus.Ambiguous, trialCount);
            return false;
        }

        if (selected < 0)
        {
            CorrectionStatus status = candidatePoolTruncated || trialLimitReached
                ? CorrectionStatus.CandidateLimitReached
                : CorrectionStatus.None;
            report = NoCorrectionReport(status, trialCount);
            return false;
        }

        RankedCorrectionCandidate candidate = validCandidates[selected];
        byte[] selectedTrial = (byte[])raw.Clone();
        FlipBit(selectedTrial, candidate.FirstBitIndex);
        FlipBit(selectedTrial, candidate.SecondBitIndex);
        if (!AcarsMessageParser.TryParse(selectedTrial, out message) || message is null ||
            !message.IsBlockCheckValid || !message.HasValidOddParity)
        {
            report = NoCorrectionReport(CorrectionStatus.None, trialCount);
            message = null;
            return false;
        }

        corrected = selectedTrial;
        report = new CorrectionReport(
            CorrectionStatus.DoubleBit,
            candidate.FirstBitIndex,
            candidate.SecondBitIndex,
            GetBitConfidence(bitConfidence, candidate.FirstBitIndex),
            GetBitConfidence(bitConfidence, candidate.SecondBitIndex),
            trialCount);
        return true;
    }

    internal static int SelectUniqueMinimumCost(
        ReadOnlySpan<RankedCorrectionCandidate> candidates,
        out bool ambiguous)
    {
        int bestIndex = -1;
        float bestCost = float.PositiveInfinity;
        for (int i = 0; i < candidates.Length; i++)
        {
            float cost = candidates[i].Cost;
            if (float.IsFinite(cost) && cost < bestCost)
            {
                bestIndex = i;
                bestCost = cost;
            }
        }

        float secondBestCost = float.PositiveInfinity;
        for (int i = 0; i < candidates.Length; i++)
            if (i != bestIndex && float.IsFinite(candidates[i].Cost))
                secondBestCost = MathF.Min(secondBestCost, candidates[i].Cost);

        ambiguous = bestIndex >= 0 && secondBestCost - bestCost < MinimumCorrectionCostMargin;
        return ambiguous ? -1 : bestIndex;
    }

    private static bool TryCorrectSingleBitCore(
        byte[] raw,
        out byte[] corrected,
        out AcarsMessage? message,
        out bool ambiguous,
        out int trialCount)
    {
        corrected = raw;
        message = null;
        ambiguous = false;
        trialCount = 0;
        if (!AcarsMessageParser.TryParse(raw, out AcarsMessage? original) || original is null ||
            original.IsBlockCheckValid) return false;

        int terminator = -1;
        for (int index = 15; index < raw.Length; index++)
        {
            if ((raw[index] & 0x7f) is 0x03 or 0x17)
            {
                terminator = index;
                break;
            }
        }
        if (terminator < 0 || terminator + 2 >= raw.Length) return false;

        int parityError = -1;
        for (int index = 0; index <= terminator; index++)
        {
            if ((System.Numerics.BitOperations.PopCount(raw[index]) & 1) == 1) continue;
            if (parityError >= 0) return false;
            parityError = index;
        }

        Span<int> candidateBytes = stackalloc int[2];
        int candidateCount;
        if (parityError >= 0)
        {
            candidateBytes[0] = parityError;
            candidateCount = 1;
        }
        else
        {
            candidateBytes[0] = terminator + 1;
            candidateBytes[1] = terminator + 2;
            candidateCount = 2;
        }

        byte[]? uniqueCorrection = null;
        AcarsMessage? uniqueMessage = null;
        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            int byteIndex = candidateBytes[candidate];
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] trial = (byte[])raw.Clone();
                trial[byteIndex] ^= (byte)(1 << bit);
                trialCount++;
                if (!AcarsMessageParser.TryParse(trial, out AcarsMessage? parsed) || parsed is null ||
                    !parsed.IsBlockCheckValid || !parsed.HasValidOddParity) continue;
                // More than one BCS-valid result is ambiguous and must not be
                // repaired silently.
                if (uniqueCorrection is not null)
                {
                    ambiguous = true;
                    return false;
                }
                uniqueCorrection = trial;
                uniqueMessage = parsed;
            }
        }
        if (uniqueCorrection is null) return false;
        corrected = uniqueCorrection;
        message = uniqueMessage;
        return true;
    }

    private static bool TryTwoBitCandidate(
        byte[] raw,
        BitCandidate first,
        BitCandidate second,
        Span<RankedCorrectionCandidate> validCandidates,
        ref int validCandidateCount,
        ref int twoBitTrialCount)
    {
        if (twoBitTrialCount >= MaxTwoBitCorrectionTrials || first.BitIndex == second.BitIndex)
        {
            return false;
        }

        byte[] trial = (byte[])raw.Clone();
        FlipBit(trial, first.BitIndex);
        FlipBit(trial, second.BitIndex);
        twoBitTrialCount++;
        if (AcarsMessageParser.TryParse(trial, out AcarsMessage? candidate) &&
            candidate is not null &&
            candidate.IsBlockCheckValid &&
            candidate.HasValidOddParity)
        {
            validCandidates[validCandidateCount++] = new RankedCorrectionCandidate(
                first.BitIndex,
                second.BitIndex,
                first.Confidence + second.Confidence);
        }

        return true;
    }

    private static bool TryFindTerminator(byte[] raw, out int terminator)
    {
        for (int index = 15; index < raw.Length; index++)
        {
            if ((raw[index] & 0x7f) is 0x03 or 0x17)
            {
                terminator = index;
                return terminator + 2 < raw.Length;
            }
        }

        terminator = -1;
        return false;
    }

    private static int FindParityErrorBytes(byte[] raw, int terminator, Span<int> destination)
    {
        int count = 0;
        for (int index = 0; index <= terminator; index++)
        {
            if ((BitOperations.PopCount(raw[index]) & 1) == 1) continue;
            if (count < destination.Length) destination[count] = index;
            count++;
        }

        return count;
    }

    private static int SelectLowestConfidenceBits(
        ReadOnlySpan<float> bitConfidence,
        int startBit,
        int bitCount,
        Span<BitCandidate> destination)
    {
        int selectedCount = 0;
        int endBit = Math.Min(startBit + bitCount, bitConfidence.Length);
        for (int bitIndex = Math.Max(0, startBit); bitIndex < endBit; bitIndex++)
        {
            BitCandidate candidate = new(bitIndex, GetBitConfidence(bitConfidence, bitIndex));
            int insertAt = selectedCount;
            while (insertAt > 0 && CompareCandidates(candidate, destination[insertAt - 1]) < 0)
                insertAt--;

            if (insertAt >= destination.Length) continue;

            int moveCount = Math.Min(selectedCount, destination.Length - 1) - insertAt;
            if (moveCount > 0)
                destination.Slice(insertAt, moveCount).CopyTo(destination.Slice(insertAt + 1));
            destination[insertAt] = candidate;
            selectedCount = Math.Min(selectedCount + 1, destination.Length);
        }

        return selectedCount;
    }

    private static int CompareCandidates(BitCandidate left, BitCandidate right)
    {
        int confidenceComparison = left.Confidence.CompareTo(right.Confidence);
        return confidenceComparison != 0
            ? confidenceComparison
            : left.BitIndex.CompareTo(right.BitIndex);
    }

    private static int FindChangedBit(byte[] original, byte[] corrected)
    {
        for (int byteIndex = 0; byteIndex < Math.Min(original.Length, corrected.Length); byteIndex++)
        {
            int difference = original[byteIndex] ^ corrected[byteIndex];
            if (difference == 0) continue;
            for (int bit = 0; bit < 8; bit++)
                if ((difference & (1 << bit)) != 0)
                    return byteIndex * 8 + bit;
        }

        return -1;
    }

    private static void FlipBit(byte[] bytes, int bitIndex) =>
        bytes[bitIndex / 8] ^= (byte)(1 << (bitIndex % 8));

    private static float GetBitConfidence(ReadOnlySpan<float> bitConfidence, int bitIndex)
    {
        if ((uint)bitIndex >= (uint)bitConfidence.Length || !float.IsFinite(bitConfidence[bitIndex]))
            return 1f;
        return Math.Clamp(bitConfidence[bitIndex], 0f, 1f);
    }

    private static CorrectionReport NoCorrectionReport(CorrectionStatus status, int trialCount) =>
        new(status, -1, -1, 0f, 0f, trialCount);
}
