using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Hfdl.Dsp;
using SRdeckPlugin.Hfdl.Models;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Hfdl.ViewModels;

public sealed partial class HfdlViewModel
{
    public void Add(HfdlReception value, long valid, long rejected)
    {
        Messages.Insert(0, value);
        while (Messages.Count > MaximumHistory) Messages.RemoveAt(Messages.Count - 1);
        OnPropertyChanged(nameof(RecentMessages));
        OnPropertyChanged(nameof(LastReceptionText));
        ValidFrames = valid;
        RejectedFrames = rejected;
        RebuildCategories();
        RefreshFilteredHistory();
    }

    internal void UpdateDiagnostics(HfdlReceiver.DiagnosticsSnapshot value,
        HfdlPluginModule.Channel channel, long tunedCenterFrequencyHz, float? signalLevelDbm = null)
    {
        DateTimeOffset measuredAt = DateTimeOffset.Now;
        UpdateDiagnosticWindow(measuredAt, value,
            out long recentSearch, out long recentCandidates, out long recentSync,
            out long recentValid, out long recentRejected);
        ValidFrames = value.ValidFrameCount;
        RejectedFrames = value.RejectedFrameCount;
        InputRateText = value.InputSampleRateHz == 0 ? "IQ入力待機中" :
            value.InputSampleRateHz >= 1_000_000
                ? $"{value.InputSampleRateHz / 1_000_000.0:F3} MS/s"
                : $"{value.InputSampleRateHz / 1_000.0:N1} kS/s";
        long signalCenterHz = channel.FrequencyHz + HfdlPluginModule.SignalOffsetHz;
        SelectedFrequencyText = $"{channel.StationName} / {channel.FrequencyDisplay} / 信号中心 {signalCenterHz / 1_000_000.0:F6} MHz";
        bool isInPassband = value.InputSampleRateHz > 0 &&
            Math.Abs(signalCenterHz - tunedCenterFrequencyHz) <= value.InputSampleRateHz * 0.5;
        PassbandStatusText = value.InputSampleRateHz <= 0
            ? "入力待機中"
            : $"{(isInPassband ? "帯域内" : "帯域外")} / 必要 {HfdlReceiver.MinimumSampleRateHz / 1_000.0:F1} kS/s以上";

        bool hasHfdlIntermediateRate = value.InputSampleRateHz > 0 &&
            (value.CoarseDecimationFactor * value.FineDecimationFactor) > 1 &&
            Math.Abs(value.IntermediateSampleRateHz - HfdlReceiver.MonitorAudioSampleRateHz) > 0.1 &&
            Math.Abs(value.IntermediateSampleRateHz - value.InputSampleRateHz) > 0.1;

        if (value.InputSampleRateHz <= 0)
        {
            RateConversionSummaryText = "—";
            IntermediateRateText = "—";
            RateConversionText = "—";
        }
        else
        {
            string hfdlInputRateStr = value.InputSampleRateHz >= 1_000_000
                ? $"{value.InputSampleRateHz / 1_000_000.0:F3} MS/s"
                : $"{value.InputSampleRateHz / 1_000.0:N1} kS/s";
            string hfdlWorkingRateStr = $"{HfdlReceiver.MonitorAudioSampleRateHz / 1_000.0:N1} kS/s";

            double coarseRateHz = value.CoarseDecimationFactor > 1
                ? value.InputSampleRateHz / (double)value.CoarseDecimationFactor
                : value.InputSampleRateHz;
            double fineRateHz = value.IntermediateSampleRateHz;

            bool hasCoarseStage = value.CoarseDecimationFactor > 1 &&
                Math.Abs(coarseRateHz - value.InputSampleRateHz) > 0.1 &&
                Math.Abs(coarseRateHz - fineRateHz) > 0.1 &&
                Math.Abs(coarseRateHz - HfdlReceiver.MonitorAudioSampleRateHz) > 0.1;

            bool hasFineStage = value.FineDecimationFactor > 1 &&
                Math.Abs(fineRateHz - coarseRateHz) > 0.1 &&
                Math.Abs(fineRateHz - value.InputSampleRateHz) > 0.1 &&
                Math.Abs(fineRateHz - HfdlReceiver.MonitorAudioSampleRateHz) > 0.1;

            var stages = new List<string> { hfdlInputRateStr };
            if (hasCoarseStage) stages.Add($"{coarseRateHz / 1_000.0:N1} kS/s");
            if (hasFineStage) stages.Add($"{fineRateHz / 1_000.0:N1} kS/s");
            stages.Add(hfdlWorkingRateStr);

            IntermediateRateText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
                value.InputSampleRateHz,
                hasHfdlIntermediateRate ? value.IntermediateSampleRateHz : 0,
                HfdlReceiver.MonitorAudioSampleRateHz);

            RateConversionText = $"CIC ÷{value.CoarseDecimationFactor} → CIC ÷{value.FineDecimationFactor} → Polyphase FIR {value.ResamplerInterpolationFactor}/{value.ResamplerDecimationFactor}";
            RateConversionSummaryText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                SRdeckPlugin.Wpf.DiagnosticRateDisplay.IsDistinct(
                    value.InputSampleRateHz, HfdlReceiver.MonitorAudioSampleRateHz) ||
                value.CoarseDecimationFactor > 1 || value.FineDecimationFactor > 1 ||
                value.ResamplerInterpolationFactor != value.ResamplerDecimationFactor,
                "標準チャネル／内部DSP");
        }
        ChannelOffsetText = $"{(signalCenterHz - tunedCenterFrequencyHz) / 1_000.0:+0.000;-0.000;0.000} kHz（USB +1.440 kHz）";
        InputLevelText = signalLevelDbm is { } inputDbm && float.IsFinite(inputDbm)
            ? $"{inputDbm:F1} dBm"
            : LevelText(value.InputRms);
        ChannelLevelText = signalLevelDbm is { } dbm && float.IsFinite(dbm)
            ? $"{dbm:F1} dBm"
            : LevelText(value.ChannelRms);
        float? calOffset = signalLevelDbm is { } sDbm && value.ChannelRms > 0
            ? (float)(sDbm - 20 * Math.Log10(value.ChannelRms)) : null;
        ChannelPeakText = calOffset.HasValue && value.ChannelPeak > 0
            ? $"{20 * Math.Log10(value.ChannelPeak) + calOffset.Value:F1} dBm"
            : LevelText(value.ChannelPeak);
        SearchCorrelationText = $"最高 {value.LastSearchBestCorrelation * 100:F1} % / 判定しきい値 72.0 %";
        SynchronizationQualityText = $"プリアンブル {value.LastPreambleCorrelation * 100:F1} % / データ {value.LastDataQuality * 100:F1} %";
        CarrierOffsetText = $"{value.LastCarrierOffsetHz:+0.0;-0.0;0.0} Hz";
        string modulation = value.LastDataRate switch { 1800 => "8PSK", 1200 => "QPSK", > 0 => "BPSK", _ => "-" };
        ModeText = value.LastDataRate > 0
            ? $"{value.LastDataRate} bit/s / {modulation} / {(value.HasPendingBurst ? "バースト継続待ち" : "完了")}"
            : value.HasPendingBurst ? "バースト継続待ち" : "同期待機中";
        SearchCountsText = $"探索 {value.PreambleSearchPassCount:N0} / 候補 {value.PreambleCandidateCount:N0} / 同期 {value.SynchronizationCount:N0}";
        RecentSearchCountsText = $"直近120秒: 探索 {recentSearch:N0} / 候補 {recentCandidates:N0} / 同期 {recentSync:N0}";
        long recentTotal = recentValid + recentRejected;
        string recentRate = recentTotal == 0 ? "—" : $"{recentValid * 100.0 / recentTotal:F1} %";
        RecentValidationText = $"直近120秒: CRC/FEC合格 {recentValid:N0} / 不一致 {recentRejected:N0} / 検証合格率 {recentRate}";
        BufferText = $"{value.BufferedWorkingSamples:N0} sample / {value.BufferedWorkingSamples * 1_000.0 / HfdlReceiver.MonitorAudioSampleRateHz:N1} ms";
        double inputSeconds = value.InputSampleRateHz > 0 ? value.ProcessedInputSamples / (double)value.InputSampleRateHz : 0;
        ProcessedText = $"IQ {value.ProcessedInputSamples:N0} / 復調 {value.ProcessedWorkingSamples:N0} sample / {inputSeconds:N2} s";
        DiagnosisText = Diagnose(value, signalCenterHz - tunedCenterFrequencyHz);

        OverallLastUpdated = measuredAt.LocalDateTime.ToString("HH:mm:ss");
        if (value.ProcessedInputSamples == 0)
        {
            OverallStatus = "待機中";
            OverallStatusKind = OverallStatusKind.Idle;
            OverallPhase = "選局";
            OverallSummary = "IQ入力を待機しています";
            OverallRecommendation = "確認: SDRの接続および地上局・周波数選択を確認してください";
        }
        else if (Math.Abs(signalCenterHz - tunedCenterFrequencyHz) > 100)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "選局";
            OverallSummary = "信号中心オフセット偏差が100Hzを超えています";
            OverallRecommendation = "確認: 選択周波数、SDR中心周波数、USB +1,440 Hzオフセットを確認してください";
        }
        else if (value.InputRms < 0.00001f)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "入力";
            OverallSummary = "IQ入力レベルが極めて低い状態です";
            OverallRecommendation = "確認: アンテナ接続、受信ゲイン、SDRソースレベルを確認してください";
        }
        else if (value.ChannelRms < 0.0001f)
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "信号";
            OverallSummary = "HFDL帯域内の信号が微弱です";
            OverallRecommendation = "確認: 時刻別の推奨周波数、伝搬状況（太陽時）、アンテナ感度を確認してください";
        }
        else if (value.LastSearchBestCorrelation < 0.30f)
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "同期";
            OverallSummary = "HFDLプリアンブル候補が未検出です";
            OverallRecommendation = "確認: 選択周波数の伝搬開口、近接チャネル妨害を確認してください";
        }
        else if (value.LastSearchBestCorrelation < 0.72f)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "同期";
            OverallSummary = "プリアンブル候補を検出していますが同期しきい値(72%)未満です";
            OverallRecommendation = "確認: 周波数偏調、S/N比、短波マルチパス状態を確認してください";
        }
        else if (recentRejected > recentValid && recentRejected >= 3)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "検証";
            OverallSummary = "CRC/FEC不一致が優勢です";
            OverallRecommendation = "確認: 同期品質およびデータエラー率、短波伝搬歪みを確認してください";
        }
        else if (recentValid > 0 && lastValidFrameAt is not null && measuredAt - lastValidFrameAt <= DiagnosticWindow && value.LastDataQuality >= 0.55f)
        {
            OverallStatus = "正常";
            OverallStatusKind = OverallStatusKind.Success;
            OverallPhase = "受信処理";
            OverallSummary = "HFDLフレームを正常に同期・復号しています";
            OverallRecommendation = "確認: 処理は正常に動作しています";
        }
        else
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "受信処理";
            OverallSummary = "HFDLバーストの同期・完了を待機しています";
            OverallRecommendation = "確認: 継続して受信を監視してください";
        }
    }

    private void UpdateDiagnosticWindow(DateTimeOffset measuredAt, HfdlReceiver.DiagnosticsSnapshot value,
        out long recentSearch, out long recentCandidates, out long recentSync, out long recentValid, out long recentRejected)
    {
        if (diagnosticHistory.Count > 0)
        {
            var previous = diagnosticHistory.Last();
            if (value.PreambleSearchPassCount < previous.Search || value.PreambleCandidateCount < previous.Candidates ||
                value.SynchronizationCount < previous.Sync || value.ValidFrameCount < previous.Valid || value.RejectedFrameCount < previous.Rejected)
            {
                diagnosticHistory.Clear();
                lastSynchronizationAt = null;
                lastValidFrameAt = null;
            }
            else
            {
                if (value.SynchronizationCount > previous.Sync) lastSynchronizationAt = measuredAt;
                if (value.ValidFrameCount > previous.Valid) lastValidFrameAt = measuredAt;
            }
        }
        diagnosticHistory.Enqueue((measuredAt, value.PreambleSearchPassCount, value.PreambleCandidateCount,
            value.SynchronizationCount, value.ValidFrameCount, value.RejectedFrameCount));
        while (diagnosticHistory.Count > 1 && measuredAt - diagnosticHistory.Peek().At > DiagnosticWindow)
            diagnosticHistory.Dequeue();
        var baseline = diagnosticHistory.Peek();
        recentSearch = Math.Max(0, value.PreambleSearchPassCount - baseline.Search);
        recentCandidates = Math.Max(0, value.PreambleCandidateCount - baseline.Candidates);
        recentSync = Math.Max(0, value.SynchronizationCount - baseline.Sync);
        recentValid = Math.Max(0, value.ValidFrameCount - baseline.Valid);
        recentRejected = Math.Max(0, value.RejectedFrameCount - baseline.Rejected);
    }

    internal void RefreshPropagationGuidance() => OnPropertyChanged(nameof(PropagationGuidance));

    private static string LevelText(float rms) =>
        rms <= 0 ? "-∞ dBFS" : $"{20 * Math.Log10(rms):F1} dBFS / {rms:F6}";

    private static string Diagnose(HfdlReceiver.DiagnosticsSnapshot value, long centerOffsetHz)
    {
        if (value.ProcessedInputSamples == 0) return "IQ入力を待っています。";
        if (Math.Abs(centerOffsetHz) > 100) return "受信中心がHFDL信号中心から外れています。選択周波数とUSB +1,440 Hzを確認してください。";
        if (value.InputRms < 0.00001f) return "IQ入力レベルが非常に低い状態です。SDR入力、ゲイン、アンテナを確認してください。";
        if (value.ChannelRms < 0.0001f) return "HFDL帯域内の信号が弱い状態です。時刻別の推奨周波数、受信レベル、伝搬状況を確認してください。";
        if (value.RejectedFrameCount > value.ValidFrameCount && value.RejectedFrameCount >= 3) return "CRC/FEC不一致が多い状態です。受信レベル、周波数ずれ、フェージングを確認してください。";
        if (value.ValidFrameCount > 0 && value.LastDataQuality >= 0.55) return "HFDLを正常に同期・復号しています。";
        if (value.LastSearchBestCorrelation < 0.30) return "HFDLプリアンブルを確認できません。使用中の周波数、伝搬状況、近接妨害を確認してください。";
        if (value.LastSearchBestCorrelation < 0.72) return "プリアンブル候補はありますが同期しきい値未満です。周波数ずれ、雑音、フェージングを確認してください。";
        if (value.SynchronizationCount == 0) return "プリアンブル断片を検出しています。完全なバーストが入るまで待機しています。";
        if (value.LastDataQuality < 0.55) return "同期後のシンボル品質が低めです。マルチパス、雑音、搬送波ずれを確認してください。";
        return "HFDL同期は成立しています。フレーム完了を待っています。";
    }

    [RelayCommand]
    private void ClearMessages()
    {
        SelectedTimelineReception = null;
        Messages.Clear();
        FlightIdGroups.Clear();
        KindGroups.Clear();
        OnPropertyChanged(nameof(IdentifiedFlightCount));
        OnPropertyChanged(nameof(LastReceptionText));
        OnPropertyChanged(nameof(RecentFlightGroups));
        RefreshFilteredHistory();
        ClearRequested?.Invoke();
    }

    private void RebuildCategories()
    {
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-RetentionMinutes);
        var activeFlightGroups = Messages
            .GroupBy(item => DisplayKey(item.FlightId, "Flight ID不明"))
            .Where(group => group.Any(item => item.ReceivedAt >= cutoff))
            .OrderByDescending(group => group.Max(item => item.ReceivedAt))
            .Take(MaximumAircraft);
        ReplaceCategories(FlightIdGroups, activeFlightGroups);
        ReplaceCategories(KindGroups, Messages.GroupBy(item => DisplayKey(item.Kind, "種別不明")));
        OnPropertyChanged(nameof(IdentifiedFlightCount));
        OnPropertyChanged(nameof(RecentFlightGroups));
    }

    private static void ReplaceCategories(ObservableCollection<HfdlCategorySummary> target,
        IEnumerable<IGrouping<string, HfdlReception>> groups)
    {
        HfdlCategorySummary[] summaries = groups.Select(group =>
        {
            HfdlReception latest = group.OrderByDescending(item => item.ReceivedAt).First();
            return new HfdlCategorySummary(group.Key, group.Count(), latest.ReceivedAt.ToLocalTime(),
                latest.FlightId, latest.Kind, latest.PayloadHex,
                group.OrderByDescending(item => item.ReceivedAt).Take(20).ToArray());
        }).ToArray();
        StableRecencyOrder.Replace(target, summaries, item => item.Key, item => item.LastReceivedAt);
    }

    private void RefreshFilteredHistory()
    {
        string? selectedKey = SelectedListGroup?.Key;
        FilteredMessages.Clear();
        string filter = SearchText?.Trim() ?? string.Empty;
        foreach (HfdlReception msg in Messages)
        {
            if (string.IsNullOrEmpty(filter) ||
                (msg.FlightId?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.Kind?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                (msg.PayloadHex?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
            {
                FilteredMessages.Add(msg);
            }
        }

        FilteredFlightIdGroups.Clear();
        foreach (HfdlCategorySummary grp in FlightIdGroups)
        {
            if (string.IsNullOrEmpty(filter) ||
                grp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                grp.LatestPayload.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredFlightIdGroups.Add(grp);
            }
        }

        FilteredKindGroups.Clear();
        foreach (HfdlCategorySummary grp in KindGroups)
        {
            if (string.IsNullOrEmpty(filter) ||
                grp.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                grp.LatestPayload.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredKindGroups.Add(grp);
            }
        }

        ObservableCollection<HfdlCategorySummary> activeGroups =
            IsFlightIdMode ? FilteredFlightIdGroups : FilteredKindGroups;
        SelectedListGroup = selectedKey is null
            ? activeGroups.FirstOrDefault()
            : (activeGroups.FirstOrDefault(group =>
                string.Equals(group.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) ?? activeGroups.FirstOrDefault());
        if (SelectedTimelineReception is null || !FilteredMessages.Contains(SelectedTimelineReception))
            SelectedTimelineReception = FilteredMessages.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredCount));
    }

    private static string DisplayKey(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(TotalFrames));
        OnPropertyChanged(nameof(AcceptanceRate));
    }

    private void NotifyChannelSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedChannelId));
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(SelectedGroundStationId));
        OnPropertyChanged(nameof(SelectedGroundStation));
        OnPropertyChanged(nameof(StationChannels));
        OnPropertyChanged(nameof(SettingStatus));
        OnPropertyChanged(nameof(PropagationGuidance));
        UpdateMapMarker();
    }

    private void UpdateMapMarker()
    {
        MapMarkers.Clear();
        HfdlPluginModule.GroundStation station = SelectedGroundStation;
        MapMarkers.Add(new($"hfdl-gs{station.Id:D2}", station.Latitude, station.Longitude,
            $"{station.Name} HFDL",
            $"{station.Region} / {station.Country} / 受信対象 {SelectedChannel.FrequencyDisplay}",
            "#b582ff", Symbol: "station"));
    }

    public Func<ValueTask>? ResetSettingsRequested { get; set; }

    [RelayCommand]
    private async Task ResetPluginSettingsAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetSettingsAsync(
            "HFDL",
            async () =>
            {
                if (ResetSettingsRequested is not null)
                {
                    await ResetSettingsRequested();
                }
            },
            () => { });
    }

    [RelayCommand]
    private void ResetPluginData()
    {
        SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndClearData(
            "HFDL",
            () =>
            {
                ClearMessages();
            });
    }

    [RelayCommand]
    private async Task ResetAllPluginAsync()
    {
        await SRdeckPlugin.Wpf.PluginResetHelper.ConfirmAndResetAllAsync(
            "HFDL",
            async () =>
            {
                if (ResetSettingsRequested is not null)
                {
                    await ResetSettingsRequested();
                }
            },
            () => { },
            () =>
            {
                ClearMessages();
            });
    }
}
