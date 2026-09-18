
// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

public enum LoRaFecDecisionMode
{
    Hard,
    MaxLog
}

public sealed record LoRaSoftDecodingOptions
{
    public LoRaSoftDecodingOptions(
        LoRaFecDecisionMode decisionMode = LoRaFecDecisionMode.MaxLog,
        int candidateCount = 4,
        float missingCandidatePenalty = 24.0f)
    {
        if (candidateCount is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(candidateCount), "The candidate count must be from 2 to 8.");
        if (!float.IsFinite(missingCandidatePenalty) || missingCandidatePenalty <= 0)
            throw new ArgumentOutOfRangeException(nameof(missingCandidatePenalty));

        DecisionMode = decisionMode;
        CandidateCount = candidateCount;
        MissingCandidatePenalty = missingCandidatePenalty;
    }

    public LoRaFecDecisionMode DecisionMode { get; }
    public int CandidateCount { get; }
    public float MissingCandidatePenalty { get; }
}

/// <summary>
/// One Gray-mapped symbol hypothesis. RelativeCost is a max-log cost in dB;
/// zero is the strongest candidate and lower values are more likely.
/// </summary>
public readonly record struct LoRaSymbolCandidate(
    ushort Symbol,
    float Power,
    float LogLikelihood,
    float RelativeCost);

/// <summary>
/// Candidate information retained for one demodulated LoRa symbol.
/// </summary>
public sealed record LoRaSoftSymbol(
    IReadOnlyList<LoRaSymbolCandidate> Candidates,
    float InterpolatedBin,
    float CarrierFrequencyCorrectionHz,
    int TimingCorrectionSamples,
    bool WasClipped);

/// <summary>
/// Candidate history retained through frame completion for CRC-aided decoding.
/// Header symbols also carry the initial payload nibbles in explicit-header mode.
/// </summary>
public sealed record LoRaSoftFrameInformation(
    LoRaFecDecisionMode DecisionMode,
    int CandidateCount,
    IReadOnlyList<LoRaSoftSymbol> HeaderSymbols,
    IReadOnlyList<LoRaSoftSymbol> PayloadSymbols);

internal sealed class LoRaCodewordSoftMetrics
{
    public LoRaCodewordSoftMetrics(int bitCount)
    {
        ZeroCosts = new float[bitCount];
        OneCosts = new float[bitCount];
    }

    public float[] ZeroCosts { get; }
    public float[] OneCosts { get; }
    public byte HardValue { get; set; }

    public float Score(byte codeword)
    {
        float score = 0;
        for (int bitIndex = 0; bitIndex < ZeroCosts.Length; bitIndex++)
        {
            int bit = (codeword >> (ZeroCosts.Length - 1 - bitIndex)) & 1;
            score += bit == 0 ? ZeroCosts[bitIndex] : OneCosts[bitIndex];
        }
        return score;
    }
}

internal static class LoRaSoftMetricPropagator
{
    /// <summary>
    /// Uses max-log marginalization for every Gray-symbol bit, then applies the
    /// same diagonal deinterleaver as the hard path. A missing bit hypothesis is
    /// assigned the best opposite-bit cost plus a bounded penalty.
    /// </summary>
    public static LoRaCodewordSoftMetrics[] Deinterleave(
        IReadOnlyList<LoRaSoftSymbol> symbols,
        int codewordCount,
        int codewordLength,
        float missingCandidatePenalty)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count != codewordLength)
            throw new ArgumentException($"Expected exactly {codewordLength} soft symbols.", nameof(symbols));
        if (codewordCount is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(codewordCount));
        if (codewordLength is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(codewordLength));

        var codewords = new LoRaCodewordSoftMetrics[codewordCount];
        for (int index = 0; index < codewords.Length; index++)
            codewords[index] = new LoRaCodewordSoftMetrics(codewordLength);

        for (int symbolIndex = 0; symbolIndex < codewordLength; symbolIndex++)
        {
            IReadOnlyList<LoRaSymbolCandidate> candidates = symbols[symbolIndex].Candidates;
            if (candidates.Count == 0)
                throw new ArgumentException("Every soft symbol requires at least one candidate.", nameof(symbols));

            int hardSymbol = candidates[0].Symbol;
            for (int bitIndex = 0; bitIndex < codewordCount; bitIndex++)
            {
                float zeroCost = float.PositiveInfinity;
                float oneCost = float.PositiveInfinity;
                int symbolBit = codewordCount - 1 - bitIndex;
                foreach (LoRaSymbolCandidate candidate in candidates)
                {
                    if (((candidate.Symbol >> symbolBit) & 1) == 0)
                        zeroCost = Math.Min(zeroCost, candidate.RelativeCost);
                    else
                        oneCost = Math.Min(oneCost, candidate.RelativeCost);
                }

                if (float.IsPositiveInfinity(zeroCost)) zeroCost = oneCost + missingCandidatePenalty;
                if (float.IsPositiveInfinity(oneCost)) oneCost = zeroCost + missingCandidatePenalty;

                int codewordIndex = Mod(symbolIndex - bitIndex - 1, codewordCount);
                int codewordBit = codewordLength - 1 - symbolIndex;
                LoRaCodewordSoftMetrics metrics = codewords[codewordIndex];
                metrics.ZeroCosts[symbolIndex] = zeroCost;
                metrics.OneCosts[symbolIndex] = oneCost;
                if (((hardSymbol >> symbolBit) & 1) != 0)
                    metrics.HardValue |= (byte)(1 << codewordBit);
            }
        }

        return codewords;
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
