using System.Diagnostics;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Vdl;

public readonly record struct VdlPipelineDiagnosticsSnapshot(
    long ProcessedBlocks,
    long DiscontinuousBlocks,
    long SourceDroppedBlocks,
    long SequenceGapCount,
    long MissingBlocks,
    long FailedAudioSubmissions,
    double LastInputIntervalMs,
    double MaximumInputIntervalMs,
    double LastInputDelayMs,
    double MaximumInputDelayMs,
    double LastTotalMs,
    double MaximumTotalMs,
    double LastLockWaitMs,
    double MaximumLockWaitMs,
    double LastReceiverMs,
    double MaximumReceiverMs,
    double LastAudioMs,
    double MaximumAudioMs,
    double LastPretriggerMs,
    double MaximumPretriggerMs,
    double LastProtocolMs,
    double MaximumProtocolMs,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections);

/// <summary>Per-block VDL pipeline timings used to diagnose monitor-audio interruptions.</summary>
internal sealed class VdlPipelineDiagnostics
{
    private readonly object gate = new();
    private long previousStartTimestamp;
    private long previousGeneration = long.MinValue;
    private long previousSequence = -1;
    private int previousGen0 = GC.CollectionCount(0);
    private int previousGen1 = GC.CollectionCount(1);
    private int previousGen2 = GC.CollectionCount(2);
    private VdlPipelineDiagnosticsSnapshot snapshot;

    public VdlPipelineDiagnosticsSnapshot Snapshot
    {
        get { lock (gate) return snapshot; }
    }

    public VdlPipelineDiagnosticsSnapshot Record(
        IqBlockMetadata metadata,
        long blockStartTimestamp,
        TimeSpan totalElapsed,
        TimeSpan processingElapsed,
        TimeSpan receiverElapsed,
        TimeSpan audioElapsed,
        TimeSpan pretriggerElapsed,
        TimeSpan protocolElapsed,
        bool audioSubmitted,
        bool discontinuous)
    {
        lock (gate)
        {
            double inputIntervalMs = previousStartTimestamp == 0
                ? 0
                : Stopwatch.GetElapsedTime(previousStartTimestamp, blockStartTimestamp).TotalMilliseconds;
            double expectedBlockMs = metadata.SampleRateHz <= 0
                ? 0
                : metadata.SampleCount * 1_000.0 / metadata.SampleRateHz;
            double inputDelayMs = Math.Max(0, inputIntervalMs - expectedBlockMs);

            long sequenceGaps = snapshot.SequenceGapCount;
            long missingBlocks = snapshot.MissingBlocks;
            if (previousGeneration == metadata.Generation && previousSequence >= 0 &&
                metadata.Sequence > previousSequence + 1)
            {
                sequenceGaps++;
                missingBlocks += metadata.Sequence - previousSequence - 1;
            }

            int currentGen0 = GC.CollectionCount(0);
            int currentGen1 = GC.CollectionCount(1);
            int currentGen2 = GC.CollectionCount(2);
            long gen0 = snapshot.Gen0Collections + Math.Max(0, currentGen0 - previousGen0);
            long gen1 = snapshot.Gen1Collections + Math.Max(0, currentGen1 - previousGen1);
            long gen2 = snapshot.Gen2Collections + Math.Max(0, currentGen2 - previousGen2);
            previousGen0 = currentGen0;
            previousGen1 = currentGen1;
            previousGen2 = currentGen2;
            previousStartTimestamp = blockStartTimestamp;
            previousGeneration = metadata.Generation;
            previousSequence = metadata.Sequence;

            double totalMs = totalElapsed.TotalMilliseconds;
            double lockWaitMs = Math.Max(0, totalElapsed.TotalMilliseconds - processingElapsed.TotalMilliseconds);
            snapshot = new(
                snapshot.ProcessedBlocks + 1,
                snapshot.DiscontinuousBlocks + (discontinuous ? 1 : 0),
                snapshot.SourceDroppedBlocks +
                    ((metadata.Discontinuity & IqDiscontinuity.SamplesDropped) != 0 ? 1 : 0),
                sequenceGaps,
                missingBlocks,
                snapshot.FailedAudioSubmissions + (audioSubmitted ? 0 : 1),
                inputIntervalMs,
                Math.Max(snapshot.MaximumInputIntervalMs, inputIntervalMs),
                inputDelayMs,
                Math.Max(snapshot.MaximumInputDelayMs, inputDelayMs),
                totalMs,
                Math.Max(snapshot.MaximumTotalMs, totalMs),
                lockWaitMs,
                Math.Max(snapshot.MaximumLockWaitMs, lockWaitMs),
                receiverElapsed.TotalMilliseconds,
                Math.Max(snapshot.MaximumReceiverMs, receiverElapsed.TotalMilliseconds),
                audioElapsed.TotalMilliseconds,
                Math.Max(snapshot.MaximumAudioMs, audioElapsed.TotalMilliseconds),
                pretriggerElapsed.TotalMilliseconds,
                Math.Max(snapshot.MaximumPretriggerMs, pretriggerElapsed.TotalMilliseconds),
                protocolElapsed.TotalMilliseconds,
                Math.Max(snapshot.MaximumProtocolMs, protocolElapsed.TotalMilliseconds),
                gen0, gen1, gen2);
            return snapshot;
        }
    }
}
