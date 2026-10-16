
// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Services;

public static class MeshtasticDeferredIqPolicy
{
    public static bool IsAvailable(long absoluteStart, int count, long latestEnd, int capacity, int safetySamples)
    {
        if (absoluteStart < 0 || count <= 0 || capacity <= 0 || latestEnd < absoluteStart + count) return false;
        long ageFromStart = latestEnd - absoluteStart;
        return ageFromStart + Math.Max(0, safetySamples) <= capacity;
    }
}

public readonly record struct MeshtasticPerformanceInput(
    long SubmittedBlocks,
    long ProcessedBlocks,
    long DroppedBlocks,
    int QueueDepth,
    double CurrentQueueDelayMs,
    double CurrentInputBlockTimeMs,
    double AverageProcessingLoadPercent,
    double OldestDeferredIqMs = 0,
    double DeferredRetentionRemainingMs = 0);

public enum MeshtasticPerformanceState
{
    Idle,
    WarmingUp,
    Healthy,
    Warning,
    Overloaded
}

public static class MeshtasticPerformanceEvaluator
{
    public static string Evaluate(MeshtasticPerformanceInput input) => Format(EvaluateState(input));

    public static string Format(MeshtasticPerformanceState state) => state switch
    {
        MeshtasticPerformanceState.Idle => "待機中",
        MeshtasticPerformanceState.WarmingUp => "測定準備中",
        MeshtasticPerformanceState.Healthy => "正常",
        MeshtasticPerformanceState.Warning => "注意",
        MeshtasticPerformanceState.Overloaded => "過負荷",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static MeshtasticPerformanceState EvaluateState(MeshtasticPerformanceInput input)
    {
        if (input.SubmittedBlocks == 0 && input.ProcessedBlocks == 0 && input.DroppedBlocks == 0)
            return MeshtasticPerformanceState.Idle;
        if (input.DroppedBlocks > 0)
            return MeshtasticPerformanceState.Overloaded;
        if (input.ProcessedBlocks < 5)
            return MeshtasticPerformanceState.WarmingUp;
        if ((input.DeferredRetentionRemainingMs > 0 && input.DeferredRetentionRemainingMs < 1_000) ||
            input.OldestDeferredIqMs >= 5_000 || input.AverageProcessingLoadPercent >= 100)
            return MeshtasticPerformanceState.Overloaded;
        if (input.OldestDeferredIqMs >= 500 || input.AverageProcessingLoadPercent >= 75 ||
            (input.CurrentInputBlockTimeMs > 0 && input.CurrentQueueDelayMs > input.CurrentInputBlockTimeMs))
            return MeshtasticPerformanceState.Warning;
        return MeshtasticPerformanceState.Healthy;
    }
}
