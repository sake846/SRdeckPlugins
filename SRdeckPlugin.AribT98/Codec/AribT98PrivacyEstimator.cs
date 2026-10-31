using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Codec;

public sealed record AribT98PrivacyCandidate(int Key, double Separation, bool HasStrongEvidence, int ContinuityRank)
{
    public int SpeechTransitions { get; init; }
    public int SupportingWindows { get; init; }
    public string Label => $"{Key:D5} / 通常音声解析 {ContinuityRank}位{(ContinuityRank == 1 ? $"・次候補比 {Separation:F1}" : "")}";
    public string EvidenceDescription => HasStrongEvidence
        ? $"AMBE連続性：最有力（次候補比 {Separation:F1}・音声遷移 {SpeechTransitions}・区間一致 {SupportingWindows}/2）。統計判定のため受信音で確認してください。"
        : $"AMBE連続性：{ContinuityRank}位（音声遷移 {SpeechTransitions}・区間一致 {SupportingWindows}/2）。根拠が不足しているため転記できません。";
}

public sealed record AribT98PrivacyEstimate(int ExaminedFrames, int ExaminedBlocks, int SkippedBlocks,
    IReadOnlyList<AribT98PrivacyCandidate> Candidates)
{
    public int InputTransitions { get; init; }
    public int ContiguousRuns { get; init; }
    public int LongestRunBlocks { get; init; }
    public int SkippedLowConfidenceBlocks { get; init; }
    public string? AnalysisStatus { get; init; }
}

/// <summary>
/// Statistical standard PN(15,14) privacy-code analysis for ordinary AMBE speech.
/// Does not authenticate keys, synthesize audio, change settings, or handle proprietary privacy.
/// </summary>
public static class AribT98PrivacyEstimator
{
    public const int MaximumFrames = 100;

    public static bool IsEligible(AribT98Frame frame) => frame.Modulation == AribT98Modulation.FourFsk &&
        frame.IsCommunication && frame.CallType == 1 && frame.FirstUnit && frame.RemainingUnits == 0 &&
        frame.MessageType == 1 && frame.Mode is 1 or 2 or 3 && frame.TrafficBits.Length == 288;

    public static AribT98PrivacyEstimate Estimate(IReadOnlyList<AribT98Frame> frames,
        CancellationToken cancellationToken = default, bool allowParallel = true) =>
        AribT98PrivacyContinuityEstimator.Estimate(frames, cancellationToken, allowParallel);
}
