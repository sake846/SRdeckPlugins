using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Codec;

/// <summary>Finds and applies a standard-privacy code for the current call.</summary>
internal sealed class AribT98PresetPrivacy : IDisposable
{
    private readonly Queue<AribT98Frame> recent = new();
    private int[] presets = [];
    private AribT98Frame? last;
    private int currentKey;
    private int sinceEstimate;
    private bool parallelSearch = true;
    private bool checkedCurrentKey;
    private CancellationTokenSource? analysisCancellation;
    private Task<AribT98PrivacyEstimate>? fullAnalysis;

    public int Key { get; private set; }
    public int ConfirmedKey { get; private set; }
    public string Status { get; private set; } = "秘話探索：総当たり・秘話待ち";

    public void Configure(AribT98Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        currentKey = settings.PrivacyKey;
        parallelSearch = settings.ParallelPrivacySearchEnabled;
        presets = settings.PrivacyKeyPresets.Where(key => key != 0 && key != currentKey).ToArray();
        ResetCall();
    }

    public void ResetCall()
    {
        analysisCancellation?.Cancel();
        analysisCancellation?.Dispose();
        analysisCancellation = null; fullAnalysis = null;
        recent.Clear(); last = null; sinceEstimate = 0; Key = ConfirmedKey = 0; checkedCurrentKey = false;
        Status = $"秘話探索：現在鍵 → プリセット{presets.Length}件 → 総当たり・秘話待ち";
    }

    public void Observe(AribT98Frame frame, int userCode)
    {
        if (!AribT98PrivacyEstimator.IsEligible(frame) || (userCode != 0 && frame.UserCode != userCode))
        {
            ResetCall();
            return;
        }
        if (!AribT98AmbeAnalysis.IsAvailable)
        {
            Key = currentKey != 0 ? currentKey : presets.Length == 1 ? presets[0] : 0;
            Status = "秘話設定：現在鍵／単一プリセットのみ使用（自動探索は利用できません）";
            return;
        }
        if (last is { } previous)
        {
            if (frame.UserCode != previous.UserCode || frame.Manufacturer != previous.Manufacturer) ResetCall();
            else if (frame.SamplePosition == previous.SamplePosition) return;
            else if (frame.SamplePosition - previous.SamplePosition is < 3700 or > 3980) ResetCall();
        }
        last = frame;
        if (Key == 0 && !checkedCurrentKey) Key = currentKey != 0 ? currentKey : presets.Length == 1 ? presets[0] : 0;
        if (ConfirmedKey != 0) return;

        recent.Enqueue(frame);
        // Mixed data/voice frames provide only one adjacent AMBE pair,
        // requiring more frames to collect enough speech continuity.
        while (recent.Count > AribT98PrivacyEstimator.MaximumFrames) recent.Dequeue();
        if (fullAnalysis is not null)
        {
            if (!fullAnalysis.IsCompleted)
            {
                Status = FullAnalysisStatus;
                return;
            }
            FinishFullAnalysis();
            return;
        }

        sinceEstimate++;
        if (recent.Count < AribT98PrivacyContinuityEstimator.MinimumFrames ||
            (recent.Count != AribT98PrivacyContinuityEstimator.MinimumFrames && sinceEstimate < 4))
        {
            string trial = Key == 0 ? "" : $"{Key:D5}を試行中・";
            Status = $"秘話探索：{trial}通常音声を収集中 {recent.Count}/{AribT98PrivacyContinuityEstimator.MinimumFrames}フレーム";
            return;
        }
        sinceEstimate = 0;

        var snapshot = recent.ToArray();
        if (Key != 0 && !checkedCurrentKey)
        {
            var check = AribT98PrivacyContinuityEstimator.CheckCurrentKey(snapshot, Key);
            var winner = check.Candidates.FirstOrDefault();
            if (winner is { HasStrongEvidence: true } candidate && candidate.Key == Key)
            {
                Confirm(Key, Key == currentKey ? "前回の鍵を確認・継続使用" : "プリセットから発見");
                return;
            }
            if (check.Candidates.Count == 0 || (winner is { Key: var candidateKey } && candidateKey == Key &&
                snapshot.Length < AribT98PrivacyContinuityEstimator.FullEvidenceFrames))
            {
                Status = $"秘話探索：{Key:D5}を確認中・通常音声を収集中 {snapshot.Length}/{AribT98PrivacyContinuityEstimator.FullEvidenceFrames}フレーム";
                return;
            }
            // Weak speech continuity does not prove the current code is wrong.
            // Keep rendering with it until another code has strong evidence.
            checkedCurrentKey = true;
        }

        if (presets.Length >= 2)
        {
            var candidate = AribT98PrivacyContinuityEstimator.EstimatePresets(snapshot, presets)
                .Candidates.FirstOrDefault();
            if (candidate is { HasStrongEvidence: true })
            {
                Confirm(candidate.Key, "プリセットから発見");
                return;
            }
        }

        var cancellation = new CancellationTokenSource();
        analysisCancellation = cancellation;
        CancellationToken analysisToken = cancellation.Token;
        bool allowParallel = parallelSearch;
        fullAnalysis = Task.Run(() => AribT98PrivacyEstimator.Estimate(snapshot, analysisToken, allowParallel), analysisToken);
        Status = FullAnalysisStatus;
    }

    private string FullAnalysisStatus => $"秘話探索：{(Key == 0 ? "" : $"{Key:D5}を継続使用・")}{(parallelSearch ? "並列" : "直列")}総当たり解析中（32767コード）";

    private void FinishFullAnalysis()
    {
        Task<AribT98PrivacyEstimate> completed = fullAnalysis!;
        fullAnalysis = null;
        analysisCancellation?.Dispose();
        analysisCancellation = null;
        try
        {
            var candidate = completed.GetAwaiter().GetResult().Candidates.FirstOrDefault();
            if (candidate is { HasStrongEvidence: true }) Confirm(candidate.Key, "総当たりで発見");
            else Status = "秘話探索：候補を確定できません・追加音声待ち";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = $"秘話探索に失敗：{e.GetBaseException().Message}"; }
    }

    private void Confirm(int key, string source)
    {
        presets = presets.Where(preset => preset != key).ToArray();
        currentKey = Key = ConfirmedKey = key;
        Status = $"現在の秘話鍵：{key:D5}（{source}）";
    }

    public void Dispose()
    {
        analysisCancellation?.Cancel();
        analysisCancellation?.Dispose();
    }
}
