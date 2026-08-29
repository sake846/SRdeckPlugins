using System.Numerics;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Acars.Dsp;

public sealed partial class AcarsReceiver
{
    private void EnsureCapacity(int symbolCount)
    {
        int phaseCorrelationCount = checked(symbolCount * SamplesPerBit);
        if (phaseCorrelationCount > lowCorrelations.Length)
        {
            Array.Resize(ref lowCorrelations, phaseCorrelationCount);
            Array.Resize(ref highCorrelations, phaseCorrelationCount);
            Array.Resize(ref confidenceBuffer, phaseCorrelationCount);
        }
        if (symbolCount * 2 > predecessorsBuffer.Length)
        {
            Array.Resize(ref predecessorsBuffer, symbolCount * 4);
            Array.Resize(ref tonesBuffer, symbolCount * 2);
            Array.Resize(ref bitsBuffer, symbolCount * 2);
        }
        int packedByteCount = symbolCount / 8;
        if (packedByteCount > packedBytesBuffer.Length)
            Array.Resize(ref packedBytesBuffer, packedByteCount * 2);
    }

    private bool DecodePhase(PhaseAcquisitionCandidate candidate, int phaseStride,
        IqBlockMetadata metadata, List<AcarsFrame> result)
    {
        ReadOnlySpan<ToneCorrelation> low = lowCorrelations.AsSpan(
            candidate.Phase * phaseStride, candidate.SymbolCount);
        ReadOnlySpan<ToneCorrelation> high = highCorrelations.AsSpan(
            candidate.Phase * phaseStride, candidate.SymbolCount);
        ReadOnlySpan<float> confidence = confidenceBuffer.AsSpan(
            candidate.Phase * phaseStride, candidate.SymbolCount);
        ReadOnlySpan<bool> coherentTones = DecodeCoherentToneSequence(
            low, high, predecessorsBuffer, tonesBuffer);
        bool decoded = DecodeToneSequence(
            coherentTones, confidence, candidate.Phase, metadata, result);
        lastFullDecodePhaseCount++;
        totalFullDecodePhaseCount++;
        return decoded;
    }

    internal readonly record struct PhaseAcquisitionCandidate(
        int Phase,
        int SymbolCount,
        int ExactSyncCount,
        int MinimumSyncDistance,
        float BestSyncConfidence);

    internal static PhaseAcquisitionCandidate MeasurePhaseAcquisition(
        ReadOnlySpan<ToneCorrelation> low,
        ReadOnlySpan<ToneCorrelation> high,
        ReadOnlySpan<float> confidence,
        int phase,
        Span<bool> decodedBits)
    {
        const uint syncPattern = 0x01_16_16;
        const uint syncMask = 0x00ff_ffff;
        const int syncBitCount = 24;
        if (low.Length != high.Length || low.Length != confidence.Length)
            throw new ArgumentException("ACARS acquisition input lengths differ.");
        if (decodedBits.Length < low.Length)
            throw new ArgumentException("The acquisition bit buffer is too small.", nameof(decodedBits));

        int exactSyncCount = 0;
        int minimumSyncDistance = syncBitCount;
        float bestSyncConfidence = 0;
        for (int polarity = 0; polarity < 2; polarity++)
        {
            bool state = false;
            for (int symbol = 0; symbol < low.Length; symbol++)
            {
                bool hardLowTone = low[symbol].Power > high[symbol].Power;
                if (hardLowTone ^ (polarity != 0)) state = !state;
                decodedBits[symbol] = state;
            }
            if (low.Length < syncBitCount) continue;

            uint window = 0;
            for (int bit = 0; bit < syncBitCount; bit++)
                if (decodedBits[bit]) window |= 1u << bit;
            for (int start = 0; start + syncBitCount <= low.Length; start++)
            {
                int normalDistance = BitOperations.PopCount((window ^ syncPattern) & syncMask);
                int invertedDistance = BitOperations.PopCount(((~window) ^ syncPattern) & syncMask);
                int distance = Math.Min(normalDistance, invertedDistance);
                minimumSyncDistance = Math.Min(minimumSyncDistance, distance);
                if (distance == 0)
                {
                    exactSyncCount++;
                    double confidenceSum = 0;
                    for (int bit = 0; bit < syncBitCount; bit++)
                        confidenceSum += confidence[start + bit];
                    bestSyncConfidence = MathF.Max(bestSyncConfidence,
                        (float)(confidenceSum / syncBitCount));
                }

                window >>= 1;
                int nextBit = start + syncBitCount;
                if (nextBit < low.Length && decodedBits[nextBit])
                    window |= 1u << (syncBitCount - 1);
            }
        }
        return new(phase, low.Length, exactSyncCount,
            minimumSyncDistance, bestSyncConfidence);
    }

    private static void SortPhaseAcquisitionCandidates(
        Span<PhaseAcquisitionCandidate> candidates)
    {
        for (int index = 1; index < candidates.Length; index++)
        {
            PhaseAcquisitionCandidate candidate = candidates[index];
            int insert = index;
            while (insert > 0 && Compare(candidates[insert - 1], candidate) > 0)
            {
                candidates[insert] = candidates[insert - 1];
                insert--;
            }
            candidates[insert] = candidate;
        }

        static int Compare(PhaseAcquisitionCandidate left, PhaseAcquisitionCandidate right)
        {
            int value = right.ExactSyncCount.CompareTo(left.ExactSyncCount);
            if (value != 0) return value;
            value = left.MinimumSyncDistance.CompareTo(right.MinimumSyncDistance);
            if (value != 0) return value;
            value = right.BestSyncConfidence.CompareTo(left.BestSyncConfidence);
            return value != 0 ? value : left.Phase.CompareTo(right.Phase);
        }
    }
}
