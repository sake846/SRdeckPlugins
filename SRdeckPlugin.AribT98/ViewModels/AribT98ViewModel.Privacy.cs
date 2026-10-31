using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.AribT98.Codec;
using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.ViewModels;

public sealed partial class AribT98ViewModel
{
    private readonly Queue<AribT98Frame> privacyFrames = new();
    private AribT98Frame? lastPrivacyFrame;
    private int privacyGeneration;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(EstimatePrivacyCommand))]
    private int privacyFrameCount;
    [ObservableProperty] private string privacyAnalysisStatus = "通常音声の標準秘話フレームを受信待ち（解析は手動）";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UsePrivacyCandidateCommand))]
    private AribT98PrivacyCandidate? selectedPrivacyCandidate;
    public ObservableCollection<AribT98PrivacyCandidate> PrivacyCandidates { get; } = [];

    private bool CanEstimatePrivacy() => PrivacyFrameCount >= AribT98PrivacyContinuityEstimator.MinimumFrames && !HasPendingSettings;
    private bool CanUsePrivacyCandidate() => SelectedPrivacyCandidate is { HasStrongEvidence: true } && !HasPendingSettings;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HasPendingSettings))
        {
            EstimatePrivacyCommand.NotifyCanExecuteChanged();
            UsePrivacyCandidateCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEstimatePrivacy), IncludeCancelCommand = true)]
    private async Task EstimatePrivacyAsync(CancellationToken cancellationToken)
    {
        if (!CanEstimatePrivacy()) return;
        int generation = privacyGeneration;
        var snapshot = privacyFrames.ToArray();
        bool allowParallel = appliedSettings.ParallelPrivacySearchEnabled;
        SelectedPrivacyCandidate = null;
        PrivacyCandidates.Clear();
        PrivacyAnalysisStatus = $"{snapshot.Length}フレームを{(allowParallel ? "並列" : "直列")}解析中…";
        try
        {
            var result = await Task.Run(() => AribT98PrivacyEstimator.Estimate(snapshot, cancellationToken, allowParallel), cancellationToken);
            // Tuning, stop, UC/call changes and clear invalidate an in-flight result.
            if (generation != privacyGeneration) return;
            foreach (var candidate in result.Candidates) PrivacyCandidates.Add(candidate);
            SelectedPrivacyCandidate = PrivacyCandidates.FirstOrDefault();
            string counts = $"解析 {result.ExaminedFrames}フレーム / {result.ExaminedBlocks}音声区間 / 誤り・信頼度不足で除外 {result.SkippedBlocks}";
            if (result.Candidates.Count == 0)
                PrivacyAnalysisStatus = $"{counts}\n{result.AnalysisStatus ?? "候補なし。受信誤り、非標準方式、音声区間不足などが考えられます。"}";
            else
                PrivacyAnalysisStatus = $"{counts}\n全32767コードを通常音声のAMBE連続性で比較しました。" +
                    (result.Candidates[0].HasStrongEvidence
                        ? $" 最有力は {result.Candidates[0].Key:D5}（次候補比 {result.Candidates[0].Separation:F1}）。受信音で確認してから転記してください。"
                        : $" {result.AnalysisStatus}") +
                    $"\n隣接 {result.InputTransitions} / 連続区間 {result.ContiguousRuns} / 最長 {result.LongestRunBlocks}音声区間";
        }
        catch (OperationCanceledException)
        {
            if (generation == privacyGeneration) PrivacyAnalysisStatus = "解析を中止しました";
        }
        catch (Exception e)
        {
            if (generation == privacyGeneration) PrivacyAnalysisStatus = $"解析できません: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePrivacyCandidate))]
    private void UsePrivacyCandidate()
    {
        if (!CanUsePrivacyCandidate()) return;
        PrivacyKey = SelectedPrivacyCandidate!.Key;
        SelectedSettingsTabIndex = 0;
        PrivacyAnalysisStatus = "候補を現在鍵へ設定し、自動保存します。";
        SettingsStatus = "秘話コード候補を自動保存しています";
        ApplyCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearPrivacyAnalysis() => ResetPrivacyAnalysis();

    private void ResetPrivacyAnalysis()
    {
        privacyGeneration++;
        EstimatePrivacyCommand.Cancel();
        privacyFrames.Clear();
        lastPrivacyFrame = null;
        PrivacyFrameCount = 0;
        SelectedPrivacyCandidate = null;
        PrivacyCandidates.Clear();
        PrivacyAnalysisStatus = "通常音声の標準秘話フレームを受信待ち（解析は手動）";
    }

    private void CapturePrivacyFrame(AribT98Frame frame)
    {
        if (!AribT98PrivacyEstimator.IsEligible(frame) ||
            (appliedSettings.UserCode != 0 && frame.UserCode != appliedSettings.UserCode))
        {
            if (privacyFrames.Count > 0) ResetPrivacyAnalysis();
            return;
        }
        if (!AribT98AmbeAnalysis.IsAvailable)
        {
            PrivacyAnalysisStatus = "秘話解析は利用できません（既知の鍵を指定してください）";
            return;
        }
        if (lastPrivacyFrame is { } last)
        {
            if (frame.UserCode != last.UserCode || frame.Manufacturer != last.Manufacturer)
                ResetPrivacyAnalysis();
            else if (frame.SamplePosition == last.SamplePosition) return;
            else if (frame.SamplePosition - last.SamplePosition is < 3700 or > 24000)
                ResetPrivacyAnalysis();
        }
        // The ring is memory-only and contains at most eight seconds of the current call.
        var snapshot = frame with
        {
            TrafficBits = (byte[])frame.TrafficBits.Clone(),
            TrafficReliability = frame.TrafficReliability is { } confidence ? (byte[])confidence.Clone() : null
        };
        privacyFrames.Enqueue(snapshot);
        while (privacyFrames.Count > AribT98PrivacyEstimator.MaximumFrames ||
            frame.SamplePosition - privacyFrames.Peek().SamplePosition >= 8 * 48000) privacyFrames.Dequeue();
        lastPrivacyFrame = snapshot;
        PrivacyFrameCount = privacyFrames.Count;
    }
}
