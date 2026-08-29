namespace SRdeckPlugin.Ais.Dsp;

internal enum RecentAisFrameResult
{
    Added,
    Duplicate,
    Promoted
}

internal sealed class RecentAisFrameCache(
    long duplicateWindowSamples,
    long retentionSamples,
    long pruneIntervalSamples)
{
    private readonly Dictionary<FrameKey, List<Entry>> entries = [];
    private readonly List<FrameKey> staleKeys = [];
    private long nextPrunePosition = long.MinValue;

    public int Count { get; private set; }

    public RecentAisFrameResult TryAdd(byte[] payload, long position, bool coherent,
        ulong? hashOverride = null)
    {
        var key = new FrameKey(payload.Length, hashOverride ?? Hash(payload));
        if (!entries.TryGetValue(key, out List<Entry>? bucket))
        {
            bucket = [];
            entries.Add(key, bucket);
        }

        for (int index = 0; index < bucket.Count; index++)
        {
            Entry previous = bucket[index];
            if (!payload.AsSpan().SequenceEqual(previous.Payload)) continue;
            if (position - previous.Position < duplicateWindowSamples)
            {
                if (coherent && !previous.Coherent)
                {
                    bucket[index] = previous with { Coherent = true };
                    return RecentAisFrameResult.Promoted;
                }
                return RecentAisFrameResult.Duplicate;
            }

            bucket[index] = new Entry(payload, position, coherent);
            PruneIfDue(position);
            return RecentAisFrameResult.Added;
        }

        bucket.Add(new Entry(payload, position, coherent));
        Count++;
        PruneIfDue(position);
        return RecentAisFrameResult.Added;
    }

    public void Clear()
    {
        entries.Clear();
        staleKeys.Clear();
        Count = 0;
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
                Count--;
            }
            if (bucket.Count == 0) staleKeys.Add(key);
        }
        foreach (FrameKey key in staleKeys) entries.Remove(key);
        nextPrunePosition = checked(position + pruneIntervalSamples);
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
    private readonly record struct Entry(byte[] Payload, long Position, bool Coherent);
}
