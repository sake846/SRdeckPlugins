using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Vdl.Dsp;
using SRdeckPlugin.Vdl;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Vdl.ViewModels;

public sealed partial class VdlViewModel
{
    public void AddFrame(VdlDecodedFrame decoded)
    {
        frames.Insert(0, decoded);
        while (frames.Count > MaximumHistory) frames.RemoveAt(frames.Count - 1);
        OnPropertyChanged(nameof(RecentFrames));
        OnPropertyChanged(nameof(LastReceptionText));
        RebuildCategories();
        RefreshFilteredHistory();
    }

    public void UpdateDiagnostics(VdlMode2Receiver.DiagnosticsSnapshot diag, long validFrames, long rejectedFrames,
        long syncCount, long offsetHz, VdlPipelineDiagnosticsSnapshot pipeline,
        float? signalLevelDbm = null, float? noiseFloorDbm = null)
    {
        DateTimeOffset measuredAt = DateTimeOffset.Now;
        if (diagnosticWindow.TryPeek(out DiagnosticWindowSample first) &&
            (validFrames < first.ValidFrames || rejectedFrames < first.RejectedFrames ||
             diag.PreambleCandidateCount < first.Candidates || diag.SynchronizationCount < first.Synchronizations ||
             diag.HeaderAcceptedCount < first.Headers))
        {
            diagnosticWindow.Clear();
            lastValidFrameObservedAt = null;
        }
        if (ValidFrameCount < validFrames) lastValidFrameObservedAt = measuredAt;
        diagnosticWindow.Enqueue(new(measuredAt, diag.PreambleCandidateCount,
            diag.SynchronizationCount, diag.HeaderAcceptedCount, validFrames, rejectedFrames));
        while (diagnosticWindow.TryPeek(out first) && measuredAt - first.MeasuredAt > TimeSpan.FromSeconds(60))
            diagnosticWindow.Dequeue();
        ValidFrameCount = validFrames;
        RejectedFrameCount = rejectedFrames;
        SynchronizationCount = syncCount;
        FrequencyOffsetHz = offsetHz;
        OnPropertyChanged(nameof(TotalFrameCount));
        OnPropertyChanged(nameof(AcceptanceRate));

        InputRateText = diag.InputSampleRateHz == 0 ? "—" :
            diag.InputSampleRateHz >= 1_000_000
                ? $"{diag.InputSampleRateHz / 1_000_000.0:F3} MS/s"
                : $"{diag.InputSampleRateHz / 1_000.0:F1} kS/s";
        ChannelText = $"中心 {diag.CenterFrequencyHz / 1_000_000.0:F3} MHz → 対象 {diag.TargetFrequencyHz / 1_000_000.0:F3} MHz";
        ChannelOffsetText = $"{offsetHz / 1_000.0:+0.000;-0.000;0.000} kHz";
        bool isInPassband = diag.InputSampleRateHz > 0 &&
            Math.Abs(offsetHz) <= diag.InputSampleRateHz * 0.5;
        PassbandStatusText = diag.InputSampleRateHz <= 0
            ? "入力待機中"
            : $"{(isInPassband ? "帯域内" : "帯域外")} / 必要 {diag.WorkingSampleRateHz / 1_000.0:F1} kS/s以上";

        bool hasIntermediateRate = diag.CoarseDecimationFactor > 1 &&
            Math.Abs(diag.IntermediateSampleRateHz - diag.WorkingSampleRateHz) > 0.1 &&
            Math.Abs(diag.IntermediateSampleRateHz - diag.InputSampleRateHz) > 0.1;

        if (diag.InputSampleRateHz == 0)
        {
            RateConversionSummaryText = "—";
            RateConversionText = "—";
            IntermediateRateText = "—";
        }
        else
        {
            string inputRateStr = diag.InputSampleRateHz >= 1_000_000
                ? $"{diag.InputSampleRateHz / 1_000_000.0:F3} MS/s"
                : $"{diag.InputSampleRateHz / 1_000.0:F1} kS/s";
            string workingRateStr = $"{diag.WorkingSampleRateHz / 1_000.0:F1} kS/s";

            IntermediateRateText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
                diag.InputSampleRateHz,
                hasIntermediateRate ? diag.IntermediateSampleRateHz : 0,
                diag.WorkingSampleRateHz);

            RateConversionText = diag.CoarseDecimationFactor > 1
                ? $"CIC ÷{diag.CoarseDecimationFactor} → Polyphase FIR {diag.ResamplerInterpolationFactor}/{diag.ResamplerDecimationFactor} → RRC α=0.6"
                : (diag.ResamplerInterpolationFactor != 1 || diag.ResamplerDecimationFactor != 1
                    ? $"Polyphase FIR {diag.ResamplerInterpolationFactor}/{diag.ResamplerDecimationFactor} → RRC α=0.6"
                    : $"直復調 RRC α=0.6");
            RateConversionSummaryText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                SRdeckPlugin.Wpf.DiagnosticRateDisplay.IsDistinct(
                    diag.InputSampleRateHz, diag.WorkingSampleRateHz) ||
                diag.CoarseDecimationFactor > 1 ||
                diag.ResamplerInterpolationFactor != diag.ResamplerDecimationFactor,
                "標準チャネル／内部DSP");
        }
        InputLevelText = signalLevelDbm is { } inputDbm && float.IsFinite(inputDbm)
            ? $"{inputDbm:F1} dBm"
            : LevelText(diag.InputRms);
        ChannelLevelText = signalLevelDbm is { } dbm && float.IsFinite(dbm)
            ? $"{dbm:F1} dBm"
            : LevelText(diag.ChannelRms);
        float? calOffset = signalLevelDbm is { } sDbm && diag.ChannelRms > 0
            ? (float)(sDbm - 20 * Math.Log10(diag.ChannelRms)) : null;
        ChannelPeakText = calOffset.HasValue && diag.ChannelPeak > 0
            ? $"{20 * Math.Log10(diag.ChannelPeak) + calOffset.Value:F1} dBm"
            : LevelText(diag.ChannelPeak);
        NoiseFloorText = noiseFloorDbm is { } nDbm && float.IsFinite(nDbm)
            ? $"{nDbm:F1} dBm"
            : LevelText(diag.NoiseFloorRms);
        PreambleLevelText = calOffset.HasValue && diag.PreambleRms > 0
            ? $"{20 * Math.Log10(diag.PreambleRms) + calOffset.Value:F1} dBm"
            : LevelText(diag.PreambleRms);

        if (!double.IsFinite(diag.BestSynchronizationError)) SynchronizationMetricText = "測定待ち";
        else
        {
            double phaseMatch = Math.Clamp(1 - diag.BestSynchronizationError / diag.CandidateThreshold, 0, 1);
            double score = Math.Clamp((diag.PreambleCoherence > 0 ? diag.PreambleCoherence : 1.0) * phaseMatch, 0, 1) * 100;
            double thresholdScore = diag.PreambleCoherenceThreshold * 100;
            SynchronizationMetricText = $"パターン一致度 {score:F1} % / 条件 ≥ {thresholdScore:F1} %";
        }

        DetectorText = $"候補 {diag.PreambleCandidateCount:N0} → 同期 {diag.SynchronizationCount:N0} → " +
                       $"ヘッダー正常 {diag.HeaderAcceptedCount:N0}";

        string snr = double.IsPositiveInfinity(diag.PreambleSnrDb) ? "∞" :
            double.IsFinite(diag.PreambleSnrDb) ? diag.PreambleSnrDb.ToString("F1") : "--";
        PreambleQualityText = $"複素相関 {diag.PreambleCoherence * 100:F1} % / 条件 ≥ {diag.PreambleCoherenceThreshold * 100:F1} % / " +
                              $"推定S/N {snr} dB / 条件 ≥ {diag.PreambleSnrThresholdDb:F1} dB";

        double ppm = diag.TimingRateCorrection / VdlMode2Receiver.SamplesPerSymbol * 1_000_000;
        TimingRecoveryText = $"Gardner誤差 {diag.TimingError:+0.0000;-0.0000;0.0000} / 小数位置 {diag.TimingOffsetSamples:+0.000;-0.000;0.000} sample / " +
                             $"クロック補正 {ppm:+0;-0;0} ppm / {diag.TimingUpdateCount:N0} 回";

        double errorDegrees = diag.CarrierErrorRadians * 180 / Math.PI;
        double rmsDegrees = diag.CarrierErrorRmsRadians * 180 / Math.PI;
        CarrierRecoveryText = $"残留位相 {errorDegrees:+0.00;-0.00;0.00}° / RMS {rmsDegrees:F2}° / " +
                              $"推定オフセット {diag.CarrierOffsetHz:+0.0;-0.0;0.0} Hz / {diag.CarrierUpdateCount:N0} 回";

        double seconds = diag.InputSampleRateHz == 0 ? 0 : diag.ProcessedInputSamples / (double)diag.InputSampleRateHz;
        ProcessingText = $"IQ {diag.ProcessedInputSamples:N0} sample / 内部 {diag.ProcessedWorkingSamples:N0} sample / {seconds:F1} 秒";
        PipelineTimingText = $"合計 {pipeline.LastTotalMs:F1} ms (最大 {pipeline.MaximumTotalMs:F1}) / " +
                             $"待機 {pipeline.LastLockWaitMs:F1} (最大 {pipeline.MaximumLockWaitMs:F1}) / " +
                             $"復調 {pipeline.LastReceiverMs:F1} (最大 {pipeline.MaximumReceiverMs:F1}) ms";
        PipelineInputText = $"到着間隔 {pipeline.LastInputIntervalMs:F1} ms (最大 {pipeline.MaximumInputIntervalMs:F1}) / " +
                            $"超過 {pipeline.LastInputDelayMs:F1} ms (最大 {pipeline.MaximumInputDelayMs:F1}) / " +
                            $"不連続 {pipeline.DiscontinuousBlocks:N0}・入力ドロップ {pipeline.SourceDroppedBlocks:N0}・seq欠落 {pipeline.MissingBlocks:N0}";
        PipelineAudioText = $"PCM投入 {pipeline.LastAudioMs:F1} ms (最大 {pipeline.MaximumAudioMs:F1}) / " +
                            $"履歴 {pipeline.LastPretriggerMs:F1} (最大 {pipeline.MaximumPretriggerMs:F1}) / " +
                            $"解析 {pipeline.LastProtocolMs:F1} (最大 {pipeline.MaximumProtocolMs:F1}) ms / " +
                            $"音声投入失敗 {pipeline.FailedAudioSubmissions:N0} / GC {pipeline.Gen0Collections:N0}/{pipeline.Gen1Collections:N0}/{pipeline.Gen2Collections:N0}";

        HeaderStatusText = $"無訂正 {diag.HeaderCleanCount:N0} / 1-bit訂正 {diag.HeaderCorrectedCount:N0} / " +
                           $"FEC不能 {diag.HeaderFecRejectedCount:N0} / 長さ不正 {diag.HeaderLengthRejectedCount:N0} / " +
                           $"タイムアウト {diag.BurstTimeoutCount:N0}";

        FecStatusText = $"RS無訂正 {diag.FecCleanBlockCount:N0} / FECなし末尾 {diag.FecUnprotectedBlockCount:N0} / " +
                        $"ハード訂正 {diag.FecCorrectedBlockCount:N0} block・{diag.FecCorrectedOctetCount:N0} byte / " +
                        $"ソフト訂正 {diag.FecSoftCorrectedBlockCount:N0} block・{diag.FecSoftCorrectedOctetCount:N0} byte " +
                        $"(試行 {diag.FecSoftAttemptBlockCount:N0} / FCS棄却 {diag.FecSoftRejectedBlockCount:N0}) / " +
                        $"訂正不能 {diag.FecUncorrectableBlockCount:N0} / AVLCフラグ対 {diag.AvlcFlagPairCount:N0}・" +
                        $"アンスタッフ {diag.AvlcUnstuffedFrameCount:N0}・FCS不一致 {diag.AvlcFcsRejectedFrameCount:N0} / " +
                        $"Chase-RS {diag.ChaseSuccessCount:N0}/{diag.ChaseAttemptCount:N0}・救済 {diag.ChaseRecoveredFrameCount:N0} frame / " +
                        $"多仮説 {diag.PhaseHypothesisSuccessCount:N0}/{diag.PhaseHypothesisAttemptCount:N0}・" +
                        $"救済 {diag.PhaseHypothesisRecoveredFrameCount:N0} frame " +
                        $"(時刻 {diag.LastPhaseHypothesisTimingOffset:+0.00;-0.00;0.00} sample / " +
                        $"周波数 {diag.LastPhaseHypothesisFrequencyOffsetHz:+0;-0;0} Hz)";

        long failedBlocks = diag.FecSoftRejectedBlockCount + diag.FecUncorrectableBlockCount;
        string failedDetail = failedBlocks > 0 ?
            $" (ソフトFCS不合格 {diag.FecSoftRejectedBlockCount:N0}・RS不能 {diag.FecUncorrectableBlockCount:N0})" : "";

        RsFecStatusText = $"無訂正 {diag.FecCleanBlockCount:N0} blk / " +
                          $"ハード {diag.FecCorrectedBlockCount:N0} blk ({diag.FecCorrectedOctetCount:N0} B) / " +
                          $"ソフト {diag.FecSoftCorrectedBlockCount:N0} blk / " +
                          $"失敗 {failedBlocks:N0} blk{failedDetail} / " +
                          $"保護外 {diag.FecUnprotectedBlockCount:N0} blk";

        AvlcStatusText = $"フラグ検出 {diag.AvlcFlagPairCount:N0} ➔ " +
                         $"構造復元 {diag.AvlcUnstuffedFrameCount:N0} ➔ " +
                         $"FCS合格 {diag.ValidFrameCount:N0} (不一致 {diag.AvlcFcsRejectedFrameCount:N0})";

        RescueStatusText = $"Chase-RS 成功 {diag.ChaseSuccessCount:N0}/{diag.ChaseAttemptCount:N0} (救済 {diag.ChaseRecoveredFrameCount:N0} frame) / " +
                           $"多仮説 成功 {diag.PhaseHypothesisSuccessCount:N0}/{diag.PhaseHypothesisAttemptCount:N0} (救済 {diag.PhaseHypothesisRecoveredFrameCount:N0} frame) " +
                           $"/ 時間予算超過 {diag.RecoveryBudgetExceededCount:N0} " +
                           $"(時刻 {diag.LastPhaseHypothesisTimingOffset:+0.00;-0.00;0.00} sample / 周波数 {diag.LastPhaseHypothesisFrequencyOffsetHz:+0;-0;0} Hz)";

        DiagnosisText = BuildDiagnosisSummary(diag);

        OverallLastUpdated = measuredAt.ToLocalTime().ToString("HH:mm:ss");
        DiagnosticWindowSample oldest = diagnosticWindow.TryPeek(out DiagnosticWindowSample value)
            ? value : default;
        long recentCandidates = Math.Max(0, diag.PreambleCandidateCount - oldest.Candidates);
        long recentSynchronizations = Math.Max(0, diag.SynchronizationCount - oldest.Synchronizations);
        long recentHeaders = Math.Max(0, diag.HeaderAcceptedCount - oldest.Headers);
        long recentValid = Math.Max(0, validFrames - oldest.ValidFrames);
        long recentRejected = Math.Max(0, rejectedFrames - oldest.RejectedFrames);
        if (pipeline.LastInputDelayMs > 150 || pipeline.LastTotalMs > 150)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "リアルタイム処理";
            OverallSummary = "IQブロック処理または到着間隔が音声バッファ余裕を超えました";
            OverallRecommendation = "確認: リアルタイム処理の最大値、到着間隔、不連続・欠落数、およびGC回数を確認してください";
        }
        else if (pipeline.FailedAudioSubmissions > 0)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "音声キュー";
            OverallSummary = "モニタ音声フレームの投入失敗を検出しました";
            OverallRecommendation = "確認: 音声キューの詰まり、他プラグインの音声出力、およびOSのオーディオ負荷を確認してください";
        }
        else if (diag.InputRms < 1e-5)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "入力";
            OverallSummary = "IQ入力レベルが不足しています";
            OverallRecommendation = "確認: SDRソースの接続および受信ゲイン設定を確認してください";
        }
        else if (diag.InputSampleRateHz > 0 &&
                 Math.Abs(diag.TargetFrequencyHz - diag.CenterFrequencyHz) + 20_000 > diag.InputSampleRateHz / 2)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "選局";
            OverallSummary = "VDL2対象チャネルが入力帯域の端または帯域外です";
            OverallRecommendation = "確認: 中心周波数、入力サンプルレート、対象チャネルを確認してください";
        }
        else if (diag.ChannelRms < 1e-5)
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "信号";
            OverallSummary = "入力はありますがVDL2チャネル内の信号は微弱です";
            OverallRecommendation = "確認: VHFアンテナ、受信ゲイン、周辺トラフィックを確認してください";
        }
        else if (recentRejected > recentValid && recentRejected >= 3)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = recentHeaders > 0 ? "検証・復号" : "ヘッダー検証";
            OverallSummary = recentHeaders > 0
                ? "直近60秒はRS/FCS棄却が有効フレームを上回っています"
                : "同期後のヘッダー検証に失敗しています";
            OverallRecommendation = "確認: S/N、搬送波・シンボル回復、FEC内訳を確認してください";
        }
        else if (recentValid > 0 ||
                 (lastValidFrameObservedAt is DateTimeOffset last && measuredAt - last <= TimeSpan.FromSeconds(60)))
        {
            OverallStatus = "正常";
            OverallStatusKind = OverallStatusKind.Success;
            OverallPhase = "検証・復号";
            OverallSummary = "直近60秒にVDL2フレームを正常に同期・復号しています";
            OverallRecommendation = "確認: 受信処理は正常に動作しています";
        }
        else if (recentCandidates > 0 && recentSynchronizations == 0)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "同期";
            OverallSummary = "VDL2候補を検出しましたが同期が成立していません";
            OverallRecommendation = "確認: preamble相関、S/N、周波数偏差を確認してください";
        }
        else if (recentSynchronizations > 0 && recentHeaders == 0)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "ヘッダー検証";
            OverallSummary = "同期は成立しましたがヘッダーを受理できていません";
            OverallRecommendation = "確認: header FEC、長さ不正、burst timeoutの内訳を確認してください";
        }
        else
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "受信処理";
            OverallSummary = "入力信号を監視中ですが、有効なフレームは未検出です";
            OverallRecommendation = "確認: トラフィックの発生または伝搬状態を確認してください";
        }
    }

    private readonly record struct DiagnosticWindowSample(DateTimeOffset MeasuredAt,
        long Candidates, long Synchronizations, long Headers, long ValidFrames, long RejectedFrames);

    private string BuildDiagnosisSummary(VdlMode2Receiver.DiagnosticsSnapshot diag)
    {
        var sb = new System.Text.StringBuilder();
        if (diag.InputRms < 1e-5) sb.Append("無信号または入力レベル不足。");
        else sb.Append("信号入力検知中。");

        if (diag.PreambleCandidateCount > 0 && diag.SynchronizationCount == 0)
            sb.Append(" バースト候補は検出されていますが同期に失敗しています。");
        else if (diag.SynchronizationCount > 0 && diag.HeaderAcceptedCount == 0)
            sb.Append(" 同期成功もヘッダー検証に失敗。");
        else if (diag.HeaderAcceptedCount > 0 && ValidFrameCount == 0)
            sb.Append(" ヘッダー正常もデータRS/FCS検証で棄却中。");
        else if (ValidFrameCount > 0)
            sb.Append($" 正常フレーム受信中 (成功率 {AcceptanceRate:F1}%)。");

        return sb.ToString();
    }

    private void RebuildCategories()
    {
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-RetentionMinutes);
        var activeCallsignGroups = frames
            .GroupBy(item => DisplayKey(item.Callsign, "Callsign不明"))
            .Where(group => group.Any(item => item.ReceivedAt >= cutoff))
            .OrderByDescending(group => group.Max(item => item.ReceivedAt))
            .Take(MaximumAircraft);
        ReplaceCategories(callsignGroups, activeCallsignGroups);
        ReplaceCategories(protocolGroups, frames.GroupBy(item => DisplayKey(item.Protocol, "プロトコル不明")));
        OnPropertyChanged(nameof(IdentifiedCallsignCount));
        OnPropertyChanged(nameof(RecentCallsignGroups));
    }

    private static void ReplaceCategories(ObservableCollection<VdlCategorySummary> target,
        IEnumerable<IGrouping<string, VdlDecodedFrame>> groups)
    {
        VdlCategorySummary[] summaries = groups.Select(group =>
        {
            VdlDecodedFrame latest = group.OrderByDescending(item => item.ReceivedAt).First();
            return new VdlCategorySummary(group.Key, group.Count(), latest.ReceivedAt.ToLocalTime(),
                latest.Callsign, latest.Protocol, latest.FrameType, latest.Summary,
                group.OrderByDescending(item => item.ReceivedAt).Take(20).ToArray());
        }).ToArray();
        StableRecencyOrder.Replace(target, summaries, item => item.Key, item => item.LastReceivedAt);
    }

    private void RefreshFilteredHistory()
    {
        string? selectedKey = SelectedListGroup?.Key;
        filteredFrames.Clear();
        string filter = SearchText?.Trim() ?? string.Empty;
        foreach (VdlDecodedFrame frame in frames)
        {
            if (string.IsNullOrEmpty(filter) ||
                (frame.Callsign?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (frame.Protocol?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (frame.Summary?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (frame.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
            {
                filteredFrames.Add(frame);
            }
        }

        filteredCallsignGroups.Clear();
        foreach (VdlCategorySummary grp in callsignGroups)
        {
            if (string.IsNullOrEmpty(filter) ||
                grp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                grp.LatestText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                filteredCallsignGroups.Add(grp);
            }
        }

        filteredProtocolGroups.Clear();
        foreach (VdlCategorySummary grp in protocolGroups)
        {
            if (string.IsNullOrEmpty(filter) ||
                grp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                grp.LatestText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                filteredProtocolGroups.Add(grp);
            }
        }

        ObservableCollection<VdlCategorySummary> activeGroups =
            IsCallsignMode ? filteredCallsignGroups : filteredProtocolGroups;
        SelectedListGroup = selectedKey is null
            ? activeGroups.FirstOrDefault()
            : (activeGroups.FirstOrDefault(group =>
                string.Equals(group.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) ?? activeGroups.FirstOrDefault());
        if (SelectedTimelineFrame is null || !filteredFrames.Contains(SelectedTimelineFrame))
            SelectedTimelineFrame = filteredFrames.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredCount));
    }

    private void TrimTrails()
    {
        foreach (GeoMapMarker marker in mapMarkersByAircraft.Values.ToArray())
        {
            if (marker.Trail is null || marker.Trail.Count <= maximumTrailPoints) continue;
            mapMarkersByAircraft[marker.Id] = marker with
            {
                Trail = marker.Trail.Skip(marker.Trail.Count - maximumTrailPoints).ToArray()
            };
        }
        mapMarkers.Clear();
        foreach (GeoMapMarker marker in mapMarkersByAircraft.Values) mapMarkers.Add(marker);
    }

    private static string DisplayKey(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();


    private static string LevelText(double value) => value > 0 && double.IsFinite(value)
        ? $"{20 * Math.Log10(value):F1} dBFS" : "-∞ dBFS";


}
