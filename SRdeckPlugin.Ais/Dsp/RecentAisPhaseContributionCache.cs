namespace SRdeckPlugin.Ais.Dsp;

internal readonly record struct AisPhaseContributionChange(
    int AddedUniquePhase = -1,
    int RemovedUniquePhase = -1);

/// <summary>
/// Attributes an FCS-valid fallback frame to a phase only while no other
/// noncoherent phase has recovered the same frame in the duplicate window.
/// </summary>
internal sealed class RecentAisPhaseContributionCache(
    long duplicateWindowSamples,
    long retentionSamples,
    long pruneIntervalSamples)
{
    private readonly Dictionary<FrameKey, List<Entry>> entries = [];
    private readonly List<FrameKey> staleKeys = [];
    private long nextPrunePosition = long.MinValue;

    public AisPhaseContributionChange Track(byte[] payload, long position, int phase)
    {
        if ((uint)phase >= AisReceiver.SamplesPerSymbol)
            throw new ArgumentOutOfRangeException(nameof(phase));
        var key = new FrameKey(payload.Length, Hash(payload));
        if (!entries.TryGetValue(key, out List<Entry>? bucket))
        {
            bucket = [];
            entries.Add(key, bucket);
        }

        ushort phaseBit = (ushort)(1 << phase);
        for (int index = 0; index < bucket.Count; index++)
        {
            Entry previous = bucket[index];
            if (!payload.AsSpan().SequenceEqual(previous.Payload)) continue;
            if (position - previous.Position < duplicateWindowSamples)
            {
                if ((previous.PhaseMask & phaseBit) != 0) return new(-1, -1);
                int removed = IsSinglePhase(previous.PhaseMask)
                    ? FirstPhase(previous.PhaseMask)
                    : -1;
                bucket[index] = previous with
                {
                    Position = position,
                    PhaseMask = (ushort)(previous.PhaseMask | phaseBit)
                };
                return new(RemovedUniquePhase: removed);
            }

            bucket[index] = new(payload, position, phaseBit);
            PruneIfDue(position);
            return new(AddedUniquePhase: phase);
        }

        bucket.Add(new(payload, position, phaseBit));
        PruneIfDue(position);
        return new(AddedUniquePhase: phase);
    }

    public void Clear()
    {
        entries.Clear();
        staleKeys.Clear();
        nextPrunePosition = long.MinValue;
    }

    private void PruneIfDue(long position)
    {
        if (nextPrunePosition != long.MinValue && position < nextPrunePosition) return;
        staleKeys.Clear();
        foreach ((FrameKey key, List<Entry> bucket) in entries)
        {
            for (int index = bucket.Count - 1; index >= 0; index--)
            {
                if (position - bucket[index].Position <= retentionSamples) continue;
                bucket.RemoveAt(index);
            }
            if (bucket.Count == 0) staleKeys.Add(key);
        }
        foreach (FrameKey key in staleKeys) entries.Remove(key);
        nextPrunePosition = checked(position + pruneIntervalSamples);
    }

    private static bool IsSinglePhase(ushort mask) =>
        mask != 0 && (mask & (mask - 1)) == 0;

    private static int FirstPhase(ushort mask)
    {
        for (int phase = 0; phase < AisReceiver.SamplesPerSymbol; phase++)
        {
            if ((mask & (1 << phase)) != 0) return phase;
        }
        return -1;
    }

    private static ulong Hash(ReadOnlySpan<byte> payload)
    {
        const ulong offset = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        ulong value = offset;
        foreach (byte item in payload)
        {
            value ^= item;
            value *= prime;
        }
        return value;
    }

    private readonly record struct FrameKey(int Length, ulong Hash);
    private readonly record struct Entry(byte[] Payload, long Position, ushort PhaseMask);
}
