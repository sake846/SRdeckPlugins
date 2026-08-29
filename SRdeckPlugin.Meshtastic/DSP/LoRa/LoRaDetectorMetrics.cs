using System;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

internal readonly record struct LoRaDetectorMetricsSnapshot(
    bool Enabled,
    long InputSamples,
    long SymbolAnalyses,
    long SearchAnalyses,
    long AligningAnalyses,
    long PreambleAnalyses,
    long SyncLowAnalyses,
    long SfdAnalyses,
    long HeaderAnalyses,
    long PayloadAnalyses,
    long SearchCandidateRejects,
    long PreambleDetections,
    long FrameSynchronizations,
    long HeadersDecoded,
    long HeaderChecksumsValid,
    long PayloadsDecoded,
    long PayloadCrcValid,
    long PayloadCrcInvalid,
    long FftExecutions,
    long FftSkips,
    long TotalAnalysisTicks,
    long SearchTicks,
    long AligningTicks,
    long PreambleTicks,
    long SyncLowTicks,
    long SfdTicks,
    long HeaderTicks,
    long PayloadTicks,
    long CrcListEligibleFrames,
    long CrcListRetries,
    long CrcListRecoveredFrames,
    long CrcListAmbiguousFrames,
    long CrcListTicks)
{
    public static LoRaDetectorMetricsSnapshot Disabled { get; } = new(
        false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

internal sealed class LoRaDetectorMetricsAccumulator
{
    private readonly long[] stateAnalyses = new long[7];
    private readonly long[] stateTicks = new long[7];

    public long InputSamples;
    public long SymbolAnalyses;
    public long SearchCandidateRejects;
    public long PreambleDetections;
    public long FrameSynchronizations;
    public long HeadersDecoded;
    public long HeaderChecksumsValid;
    public long PayloadsDecoded;
    public long PayloadCrcValid;
    public long PayloadCrcInvalid;
    public long FftExecutions;
    public long FftSkips;
    public long TotalAnalysisTicks;
    public long CrcListEligibleFrames;
    public long CrcListRetries;
    public long CrcListRecoveredFrames;
    public long CrcListAmbiguousFrames;
    public long CrcListTicks;

    public void RecordAnalysis(int state, long elapsedTicks)
    {
        SymbolAnalyses++;
        stateAnalyses[state]++;
        stateTicks[state] += elapsedTicks;
        TotalAnalysisTicks += elapsedTicks;
    }

    public LoRaDetectorMetricsSnapshot Snapshot() => new(
        true,
        InputSamples,
        SymbolAnalyses,
        stateAnalyses[0],
        stateAnalyses[1],
        stateAnalyses[2],
        stateAnalyses[3],
        stateAnalyses[4],
        stateAnalyses[5],
        stateAnalyses[6],
        SearchCandidateRejects,
        PreambleDetections,
        FrameSynchronizations,
        HeadersDecoded,
        HeaderChecksumsValid,
        PayloadsDecoded,
        PayloadCrcValid,
        PayloadCrcInvalid,
        FftExecutions,
        FftSkips,
        TotalAnalysisTicks,
        stateTicks[0],
        stateTicks[1],
        stateTicks[2],
        stateTicks[3],
        stateTicks[4],
        stateTicks[5],
        stateTicks[6],
        CrcListEligibleFrames,
        CrcListRetries,
        CrcListRecoveredFrames,
        CrcListAmbiguousFrames,
        CrcListTicks);

    public void Reset()
    {
        Array.Clear(stateAnalyses);
        Array.Clear(stateTicks);
        InputSamples = 0;
        SymbolAnalyses = 0;
        SearchCandidateRejects = 0;
        PreambleDetections = 0;
        FrameSynchronizations = 0;
        HeadersDecoded = 0;
        HeaderChecksumsValid = 0;
        PayloadsDecoded = 0;
        PayloadCrcValid = 0;
        PayloadCrcInvalid = 0;
        FftExecutions = 0;
        FftSkips = 0;
        TotalAnalysisTicks = 0;
        CrcListEligibleFrames = 0;
        CrcListRetries = 0;
        CrcListRecoveredFrames = 0;
        CrcListAmbiguousFrames = 0;
        CrcListTicks = 0;
    }
}
