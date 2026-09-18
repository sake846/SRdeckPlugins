using System.Diagnostics;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

public sealed record LoRaCrcListDecodingOptions
{
    public LoRaCrcListDecodingOptions(
        int maxCandidateSymbols = 12,
        int maxCandidateFrames = 128,
        int maxRetries = 64,
        int maxCpuMilliseconds = 10)
    {
        if (maxCandidateSymbols is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(maxCandidateSymbols));
        if (maxCandidateFrames is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxCandidateFrames));
        if (maxRetries is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));
        if (maxCpuMilliseconds is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxCpuMilliseconds));

        MaxCandidateSymbols = maxCandidateSymbols;
        MaxCandidateFrames = maxCandidateFrames;
        MaxRetries = maxRetries;
        MaxCpuMilliseconds = maxCpuMilliseconds;
    }

    public int MaxCandidateSymbols { get; }
    public int MaxCandidateFrames { get; }
    public int MaxRetries { get; }
    public int MaxCpuMilliseconds { get; }
}

public sealed record LoRaCrcListDecodingDiagnostic(
    DateTimeOffset Timestamp,
    int Retries,
    int MatchingCandidates,
    int UniqueMatchingFrames,
    bool Recovered,
    bool Ambiguous,
    bool TimeLimitReached,
    float? SelectedMetric,
    long ElapsedTicks);

internal sealed record LoRaCrcListDecodingResult(
    LoRaPayloadFrame? RecoveredFrame,
    int Retries,
    int MatchingCandidates,
    int UniqueMatchingFrames,
    bool TimeLimitReached,
    float? SelectedMetric,
    long ElapsedTicks)
{
    public bool Recovered => RecoveredFrame is not null;
    public bool Ambiguous => UniqueMatchingFrames > 1;
}

internal static class LoRaCrcListDecoder
{
    private readonly record struct SymbolReference(bool IsHeader, int SymbolIndex, LoRaSoftSymbol Symbol);
    private sealed record SearchState(byte[] Ranks, float Metric, ulong Key);

    public static LoRaCrcListDecodingResult TryRecover(
        LoRaExplicitHeader expectedHeader,
        LoRaSoftFrameInformation softInformation,
        LoRaCrcListDecodingOptions options)
    {
        ArgumentNullException.ThrowIfNull(expectedHeader);
        ArgumentNullException.ThrowIfNull(softInformation);
        ArgumentNullException.ThrowIfNull(options);
        long started = Stopwatch.GetTimestamp();
        if (!expectedHeader.HasPayloadCrc)
            return Empty(started);

        SymbolReference[] weakSymbols = SelectWeakSymbols(softInformation, options.MaxCandidateSymbols);
        if (weakSymbols.Length == 0)
            return Empty(started);

        var queue = new PriorityQueue<SearchState, float>();
        var visited = new HashSet<ulong>();
        var initial = new SearchState(new byte[weakSymbols.Length], 0, 0);
        queue.Enqueue(initial, 0);
        visited.Add(0);
        int enqueued = 1;
        int retries = 0;
        int matchingCandidates = 0;
        bool timeLimitReached = false;
        var matchingFrames = new Dictionary<string, (LoRaPayloadFrame Frame, float Metric)>(StringComparer.Ordinal);

        while (queue.Count > 0 && retries < options.MaxRetries)
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= options.MaxCpuMilliseconds)
            {
                timeLimitReached = true;
                break;
            }

            SearchState state = queue.Dequeue();
            if (state.Key != 0)
            {
                retries++;
                LoRaPayloadFrame? candidate = DecodeCandidate(
                    expectedHeader, softInformation, weakSymbols, state.Ranks);
                if (candidate?.IsPayloadCrcValid is true)
                {
                    matchingCandidates++;
                    string key = Convert.ToHexString(candidate.Payload) + ":" +
                                 candidate.ReceivedCrc.GetValueOrDefault().ToString("X4");
                    if (!matchingFrames.TryGetValue(key, out var prior) || state.Metric < prior.Metric)
                        matchingFrames[key] = (candidate, state.Metric);
                }
            }

            for (int symbolIndex = 0;
                 symbolIndex < weakSymbols.Length && enqueued < options.MaxCandidateFrames;
                 symbolIndex++)
            {
                byte oldRank = state.Ranks[symbolIndex];
                IReadOnlyList<LoRaSymbolCandidate> candidates = weakSymbols[symbolIndex].Symbol.Candidates;
                if (oldRank + 1 >= candidates.Count) continue;
                var ranks = (byte[])state.Ranks.Clone();
                ranks[symbolIndex]++;
                ulong key = EncodeKey(ranks);
                if (!visited.Add(key)) continue;
                float metric = state.Metric - candidates[oldRank].RelativeCost +
                               candidates[ranks[symbolIndex]].RelativeCost;
                queue.Enqueue(new SearchState(ranks, metric, key), metric);
                enqueued++;
            }
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        if (matchingFrames.Count != 1 || timeLimitReached)
        {
            return new LoRaCrcListDecodingResult(
                null, retries, matchingCandidates, matchingFrames.Count,
                timeLimitReached, null, elapsedTicks);
        }

        (_, (LoRaPayloadFrame frame, float selectedMetric)) = matchingFrames.First();
        LoRaPayloadFrame recovered = frame with
        {
            WasRecoveredByListDecoding = true,
            CrcListMetric = selectedMetric,
            CrcListRetries = retries
        };
        return new LoRaCrcListDecodingResult(
            recovered, retries, matchingCandidates, 1,
            false, selectedMetric, elapsedTicks);
    }

    private static SymbolReference[] SelectWeakSymbols(
        LoRaSoftFrameInformation information,
        int maximum)
    {
        var symbols = new List<SymbolReference>();
        for (int index = 0; index < information.HeaderSymbols.Count; index++)
        {
            LoRaSoftSymbol symbol = information.HeaderSymbols[index];
            if (symbol.Candidates.Count > 1)
                symbols.Add(new SymbolReference(true, index, symbol));
        }
        for (int index = 0; index < information.PayloadSymbols.Count; index++)
        {
            LoRaSoftSymbol symbol = information.PayloadSymbols[index];
            if (symbol.Candidates.Count > 1)
                symbols.Add(new SymbolReference(false, index, symbol));
        }
        return symbols
            .OrderBy(reference => reference.Symbol.Candidates[1].RelativeCost -
                                  reference.Symbol.Candidates[0].RelativeCost)
            .ThenBy(reference => reference.IsHeader ? 0 : 1)
            .ThenBy(reference => reference.SymbolIndex)
            .Take(maximum)
            .ToArray();
    }

    private static LoRaPayloadFrame? DecodeCandidate(
        LoRaExplicitHeader expectedHeader,
        LoRaSoftFrameInformation information,
        IReadOnlyList<SymbolReference> weakSymbols,
        IReadOnlyList<byte> ranks)
    {
        ushort[] headerSymbols = information.HeaderSymbols
            .Select(symbol => symbol.Candidates[0].Symbol)
            .ToArray();
        ushort[] payloadSymbols = information.PayloadSymbols
            .Select(symbol => symbol.Candidates[0].Symbol)
            .ToArray();
        for (int index = 0; index < weakSymbols.Count; index++)
        {
            SymbolReference reference = weakSymbols[index];
            ushort selected = reference.Symbol.Candidates[ranks[index]].Symbol;
            if (reference.IsHeader) headerSymbols[reference.SymbolIndex] = selected;
            else payloadSymbols[reference.SymbolIndex] = selected;
        }

        if (headerSymbols.Length != 8) return null;
        int spreadingFactor = expectedHeader.InitialPayloadNibbles.Count + 7;
        LoRaExplicitHeader header;
        try
        {
            header = LoRaExplicitHeaderDecoder.Decode(headerSymbols, spreadingFactor);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (!header.IsChecksumValid ||
            header.PayloadLength != expectedHeader.PayloadLength ||
            header.CodingRateDenominator != expectedHeader.CodingRateDenominator ||
            header.HasPayloadCrc != expectedHeader.HasPayloadCrc)
            return null;

        int requiredNibbles = (header.PayloadLength * 2) + 4;
        var nibbles = new List<byte>(requiredNibbles);
        foreach (byte nibble in header.InitialPayloadNibbles)
        {
            if (nibbles.Count == requiredNibbles) break;
            nibbles.Add(nibble);
        }
        int correctedCodewords = header.CorrectedCodewords;
        for (int offset = 0;
             offset + header.CodingRateDenominator <= payloadSymbols.Length && nibbles.Count < requiredNibbles;
             offset += header.CodingRateDenominator)
        {
            byte[] block = LoRaPayloadDecoder.DecodeBlock(
                payloadSymbols.AsSpan(offset, header.CodingRateDenominator).ToArray(),
                header.CodingRateDenominator,
                spreadingFactor,
                out int corrected);
            correctedCodewords += corrected;
            foreach (byte nibble in block)
            {
                if (nibbles.Count == requiredNibbles) break;
                nibbles.Add(nibble);
            }
        }
        if (nibbles.Count < requiredNibbles) return null;
        return LoRaPayloadDecoder.BuildFrame(header, nibbles, correctedCodewords, information);
    }

    private static ulong EncodeKey(IReadOnlyList<byte> ranks)
    {
        ulong key = 0;
        for (int index = 0; index < ranks.Count; index++)
            key |= (ulong)ranks[index] << (index * 3);
        return key;
    }

    private static LoRaCrcListDecodingResult Empty(long started) => new(
        null, 0, 0, 0, false, null, Stopwatch.GetTimestamp() - started);
}
