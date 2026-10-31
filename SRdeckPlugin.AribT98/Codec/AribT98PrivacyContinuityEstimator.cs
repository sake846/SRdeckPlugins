using System.Numerics;
using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Codec;

/// <summary>
/// Ranks standard PN seeds by temporal continuity of decoded AMBE parameters.
/// This is statistical evidence for ordinary speech, not a CRC or authentication.
/// </summary>
internal static class AribT98PrivacyContinuityEstimator
{
    internal const int MinimumFrames = 12;
    internal const int MinimumBlocks = 48;
    internal const int MinimumTransitions = 32;
    internal const int MinimumWindowTransitions = 12;
    private const int ReturnedCandidates = 5;
    private const double MinimumSeparation = 3.0;
    internal const int FullEvidenceFrames = 20;
    private const int FullEvidenceBlocks = 64;
    private const double MinimumEarlySeparation = 8.0;
    private const int MaximumCurrentKeyComparisons = 832;
    private static readonly int[] nearbyKeyDifferences = Enumerable.Range(1, 32767)
        .Where(value => BitOperations.PopCount((uint)value) <= 3).ToArray();

    internal static AribT98PrivacyEstimate Estimate(IReadOnlyList<AribT98Frame> frames,
        CancellationToken cancellationToken = default, bool allowParallel = true) => Estimate(frames, null, cancellationToken, allowParallel);

    internal static AribT98PrivacyEstimate EstimatePresets(IReadOnlyList<AribT98Frame> frames, int[] keys,
        CancellationToken cancellationToken = default) => Estimate(frames, keys, cancellationToken);

    internal static AribT98PrivacyEstimate CheckCurrentKey(IReadOnlyList<AribT98Frame> frames, int key) =>
        Estimate(frames, CurrentKeyComparisons(key), default, false, true);

    private static int[] CurrentKeyComparisons(int key)
    {
        if (key is < 1 or > 32767) throw new ArgumentOutOfRangeException(nameof(key));
        // Include nearby seeds so a small code change cannot win by being
        // compared only with distant seeds, plus a spread across the PN15 space.
        return [key, .. nearbyKeyDifferences.Select(difference => key ^ difference)
            .Concat(Enumerable.Range(0, 256).Select(i => (i * 4051 + 17) & 32767))
            .Where(candidate => candidate != 0 && candidate != key).Distinct()];
    }

    private static AribT98PrivacyEstimate Estimate(IReadOnlyList<AribT98Frame> frames, int[]? keys,
        CancellationToken cancellationToken, bool allowParallel = true, bool checkingCurrentKey = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var blocks = new List<byte>();
        var markers = new List<byte>();
        var seen = new HashSet<long>();
        long lastBlockFrame = long.MinValue;
        int lastBlockSlot = -1;
        int examinedFrames = 0, skippedBlocks = 0, lowConfidenceBlocks = 0;
        Span<byte> parameters = stackalloc byte[49];

        foreach (var frame in frames.TakeLast(AribT98PrivacyEstimator.MaximumFrames))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AribT98PrivacyEstimator.IsEligible(frame) || !seen.Add(frame.SamplePosition)) continue;
            examinedFrames++;
            for (int slot = 0; slot < 4; slot++)
            {
                if (!frame.IsVoiceSlot(slot)) continue;
                int errors = AmbeDecoder.UnpackVoice(frame, slot, parameters, out int c0Errors, out bool lowConfidence);
                if (lowConfidence) { skippedBlocks++; lowConfidenceBlocks++; continue; }
                if (errors > 3 || c0Errors > 3) { skippedBlocks++; continue; }

                bool followsPrevious = lastBlockSlot >= 0 &&
                    ((lastBlockFrame == frame.SamplePosition && slot == lastBlockSlot + 1) ||
                     (frame.SamplePosition - lastBlockFrame is >= 3700 and <= 3980 && lastBlockSlot == 3 && slot == 0));
                markers.Add((byte)(slot | (followsPrevious ? 0 : 0x80)));
                blocks.AddRange(parameters);
                lastBlockFrame = frame.SamplePosition;
                lastBlockSlot = slot;
            }
        }

        return EstimateParametersCore(blocks.ToArray(), markers.ToArray(), examinedFrames, skippedBlocks, keys, cancellationToken,
            allowParallel, checkingCurrentKey)
            with { SkippedLowConfidenceBlocks = lowConfidenceBlocks };
    }

    internal static AribT98PrivacyEstimate EstimateParameters(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, int examinedFrames, int skippedBlocks,
        CancellationToken cancellationToken = default, bool allowParallel = true) =>
        EstimateParametersCore(blocks, slotMarkers, examinedFrames, skippedBlocks, null, cancellationToken, allowParallel);

    internal static AribT98PrivacyEstimate CheckCurrentKeyParameters(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, int examinedFrames, int key) =>
        EstimateParametersCore(blocks, slotMarkers, examinedFrames, 0, CurrentKeyComparisons(key), default, false, true);

    internal static AribT98PrivacyEstimate EstimatePresetParameters(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, int examinedFrames, int skippedBlocks, int[] keys,
        CancellationToken cancellationToken = default) =>
        EstimateParametersCore(blocks, slotMarkers, examinedFrames, skippedBlocks, keys, cancellationToken);

    private static AribT98PrivacyEstimate EstimateParametersCore(ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> slotMarkers, int examinedFrames, int skippedBlocks, int[]? keys,
        CancellationToken cancellationToken, bool allowParallel = true, bool checkingCurrentKey = false)
    {
        if (examinedFrames < 0 || skippedBlocks < 0 || blocks.Length != slotMarkers.Length * 49)
            throw new ArgumentException("Invalid AMBE privacy-analysis input.");
        if (keys is { Length: < 1 } || keys?.Length > (checkingCurrentKey ? MaximumCurrentKeyComparisons : 6) ||
            keys?.Any(key => key is < 1 or > 32767) == true ||
            keys?.Distinct().Count() != keys?.Length)
            throw new ArgumentException("Expected one current key and up to five distinct privacy presets.", nameof(keys));
        cancellationToken.ThrowIfCancellationRequested();
        int transitions = 0, runs = 0, longestRun = 0, runLength = 0;
        for (int i = 0; i < slotMarkers.Length; i++)
        {
            if ((slotMarkers[i] & 0x7c) != 0) throw new ArgumentException("Invalid AMBE slot marker.");
            if (i == 0 || (slotMarkers[i] & 0x80) != 0) { runs++; runLength = 1; }
            else { transitions++; runLength++; }
            longestRun = Math.Max(longestRun, runLength);
        }
        foreach (byte bit in blocks) if (bit > 1) throw new ArgumentException("Expected binary AMBE parameters.");
        var empty = new AribT98PrivacyEstimate(examinedFrames, slotMarkers.Length, skippedBlocks, [])
        { InputTransitions = transitions, ContiguousRuns = runs, LongestRunBlocks = longestRun };
        if (examinedFrames < MinimumFrames || slotMarkers.Length < MinimumBlocks)
            return empty with { AnalysisStatus = "通常音声を解析するためのフレーム・音声区間が不足しています。" };
        if (transitions < MinimumTransitions)
            return empty with { AnalysisStatus = $"連続する音声区間が不足しています（有効な隣接 {transitions}/{MinimumTransitions}）。" };

        bool earlyEvidence = examinedFrames < FullEvidenceFrames || slotMarkers.Length < FullEvidenceBlocks;
        var scored = Score(blocks, slotMarkers, MinimumTransitions, keys, cancellationToken, allowParallel, checkingCurrentKey, earlyEvidence);
        if (scored.Order.Length == 0)
            return empty with { AnalysisStatus = "通常音声として比較できる遷移が不足しています。" };
        int winner = scored.Order[0];
        int winnerKey = keys is null ? winner + 1 : keys[winner];
        bool strong = scored.Strong;
        int supportingWindows = 0;
        // Validate on disjoint temporal halves, resetting predictor state at the
        // boundary. The halves are not claimed to be statistically independent.
        if (strong)
        {
            int split = slotMarkers.Length / 2;
            foreach ((int start, int length) in new[] { (0, split), (split, slotMarkers.Length - split) })
            {
                var markers = slotMarkers.Slice(start, length).ToArray();
                markers[0] |= 0x80;
                var window = blocks.Slice(start * 49, length * 49);
                var validation = Score(window, markers, MinimumWindowTransitions, keys, cancellationToken, allowParallel, checkingCurrentKey, earlyEvidence);
                if (validation.Strong && validation.Order[0] == winner &&
                    CountDistinctSpeech(window, markers, winnerKey) >= 3) supportingWindows++;
            }
            strong = supportingWindows == 2;
        }
        var candidates = new AribT98PrivacyCandidate[Math.Min(ReturnedCandidates, scored.Order.Length)];
        for (int rank = 0; rank < candidates.Length; rank++)
        {
            int index = scored.Order[rank];
            candidates[rank] = new(keys is null ? index + 1 : keys[index], rank == 0 ? scored.Separation : 0,
                rank == 0 && strong, rank + 1)
            {
                SpeechTransitions = scored.Metrics.SpeechTransitions[index],
                SupportingWindows = rank == 0 ? supportingWindows : 0
            };
        }
        return empty with
        {
            Candidates = candidates,
            AnalysisStatus = strong ? "重複しない前半・後半の両方で同じ候補を支持しました。"
                : "候補の分離または前半・後半の音声根拠が不足しています。追加受信して再解析してください。"
        };
    }

    private sealed record Scores(AribT98PrivacyMetricSet Metrics, int[] Order, double Separation, bool Strong);

    private static Scores Score(ReadOnlySpan<byte> blocks, ReadOnlySpan<byte> markers, int minimumTransitions,
        int[]? keys, CancellationToken cancellationToken, bool allowParallel, bool checkingCurrentKey, bool earlyEvidence)
    {
        var metrics = keys is null ? AribT98AmbeAnalysis.ScorePrivacy(blocks, markers, cancellationToken, allowParallel) :
            AribT98AmbeAnalysis.ScorePrivacy(blocks, markers, keys, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        int[] invalidRanks = Rank(metrics.InvalidB0);
        int[] pitchRanks = Rank(metrics.Pitch);
        int[] gainRanks = Rank(metrics.Gain);
        int[] spectrumRanks = Rank(metrics.Spectrum);
        int[] voicingRanks = Rank(metrics.Voicing);
        int[] rawRanks = Rank(metrics.RawHamming);
        var aggregate = new int[metrics.InvalidB0.Length];
        var order = Enumerable.Range(0, aggregate.Length)
            .Where(i => metrics.SpeechTransitions[i] >= minimumTransitions).ToArray();
        for (int i = 0; i < aggregate.Length; i++)
            aggregate[i] = invalidRanks[i] * 3 + pitchRanks[i] * 3 + gainRanks[i] * 2 +
                spectrumRanks[i] * 3 + voicingRanks[i] + rawRanks[i];
        Array.Sort(order, (left, right) =>
        {
            int comparison = aggregate[left].CompareTo(aggregate[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });

        if (order.Length < 2) return new(metrics, order, 0, false);
        int winner = order[0], runnerUp = order[1];
        double separation = (double)aggregate[runnerUp] / Math.Max(1, aggregate[winner]);
        bool strong = separation >= (keys is null || checkingCurrentKey ? MinimumSeparation : 1.5) &&
            metrics.InvalidB0[winner] <= Math.Max(2, markers.Length / 20) &&
            pitchRanks[winner] <= (checkingCurrentKey ? 1 : Math.Min(128, aggregate.Length)) &&
            spectrumRanks[winner] <= (checkingCurrentKey ? 1 : Math.Min(128, aggregate.Length)) &&
            rawRanks[winner] <= (checkingCurrentKey ? 1 : Math.Min(64, aggregate.Length));
        // Short captures must win both spectral and raw-bit continuity outright,
        // with no invalid pitch and a wider margin in both temporal halves.
        // Ambiguous captures retain the original checks once enough speech arrives.
        if (earlyEvidence)
            strong &= separation >= MinimumEarlySeparation && metrics.InvalidB0[winner] == 0 &&
                pitchRanks[winner] <= Math.Min(32, aggregate.Length) &&
                spectrumRanks[winner] == 1 && rawRanks[winner] == 1;
        return new(metrics, order, separation, strong);
    }

    private static int CountDistinctSpeech(ReadOnlySpan<byte> blocks, ReadOnlySpan<byte> markers, int key)
    {
        Span<byte> plain = stackalloc byte[49];
        var distinct = new HashSet<ulong>();
        for (int i = 0; i < markers.Length; i++)
        {
            blocks.Slice(i * 49, 49).CopyTo(plain);
            AmbeDecoder.Descramble(plain, key, markers[i] & 3);
            if (AribT98AmbeAnalysis.ClassifyParameters(plain) != AribT98AmbeKind.Speech) continue;
            ulong packed = 0;
            foreach (byte bit in plain) packed = (packed << 1) | bit;
            distinct.Add(packed);
            if (distinct.Count >= 3) break;
        }
        return distinct.Count;
    }

    private static int[] Rank(int[] values)
    {
        int[] order = Enumerable.Range(0, values.Length).ToArray();
        Array.Sort(order, (left, right) =>
        {
            int comparison = values[left].CompareTo(values[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        var ranks = new int[values.Length];
        for (int position = 0; position < order.Length;)
        {
            int end = position + 1;
            while (end < order.Length && values[order[end]] == values[order[position]]) end++;
            for (int i = position; i < end; i++) ranks[order[i]] = position + 1;
            position = end;
        }
        return ranks;
    }

    private static int[] Rank(double[] values)
    {
        int[] order = Enumerable.Range(0, values.Length).ToArray();
        Array.Sort(order, (left, right) =>
        {
            int comparison = values[left].CompareTo(values[right]);
            return comparison != 0 ? comparison : left.CompareTo(right);
        });
        var ranks = new int[values.Length];
        for (int position = 0; position < order.Length;)
        {
            int end = position + 1;
            double first = values[order[position]];
            double tolerance = Math.Max(1e-12, Math.Abs(first) * 1e-12);
            while (end < order.Length && Math.Abs(values[order[end]] - first) <= tolerance) end++;
            for (int i = position; i < end; i++) ranks[order[i]] = position + 1;
            position = end;
        }
        return ranks;
    }
}
