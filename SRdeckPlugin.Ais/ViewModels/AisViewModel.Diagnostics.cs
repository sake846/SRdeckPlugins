using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Ais.Dsp;
using SRdeckPlugin.Ais.Models;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ais.ViewModels;

public sealed partial class AisViewModel
{
    public void Apply(
        IReadOnlyCollection<AisTargetState> states,
        AisReceiver.DiagnosticsSnapshot channelA,
        AisReceiver.DiagnosticsSnapshot channelB,
        float? signalLevelDbm = null)
    {
        var active = states.Select(item => item.Mmsi).ToHashSet();
        foreach (uint stale in trails.Keys.Where(item => !active.Contains(item)).ToArray()) trails.Remove(stale);
        var rows = Targets.ToDictionary(item => item.Mmsi);
        foreach (AisTargetState state in states.OrderByDescending(item => item.LastSeen))
        {
            if (!rows.TryGetValue(state.Mmsi, out AisTargetRow? row))
            {
                row = new(state.Mmsi);
                Targets.Add(row);
            }
            row.Apply(state);
            row.ReplaceHistory(Messages.Where(message => message.Mmsi == row.Mmsi).Take(20));
            rows.Remove(state.Mmsi);
        }
        foreach (AisTargetRow stale in rows.Values)
        {
            if (ReferenceEquals(SelectedTarget, stale)) SelectedTarget = null;
            Targets.Remove(stale);
        }
        StableRecencyOrder.Reorder(Targets, item => item.LastSeen);
        RefreshFilteredTargets();
        OnPropertyChanged(nameof(RecentTargets));

        MapMarkers.Clear();
        foreach (AisTargetRow row in Targets.Where(item => item.Latitude is not null && item.Longitude is not null))
        {
            string color = row.IsAidToNavigation ? AisMapPresentation.AidToNavigationColor :
                row.NavigationStatus is 1 or 5 ? AisMapPresentation.AnchoredVesselColor : AisMapPresentation.VesselColor;
            List<GeoMapPoint> trail = GetUpdatedTrail(row, color);
            double? heading = row.HeadingDegrees is int trueHeading && trueHeading is >= 0 and < 360
                ? trueHeading
                : row.CourseDegrees;
            MapMarkers.Add(new(row.MmsiText, row.Latitude!.Value, row.Longitude!.Value,
                string.IsNullOrWhiteSpace(row.DisplayName) ? row.MmsiText : row.DisplayName,
                $"{row.MmsiText} / {row.SpeedKnots:F1} kt / {row.CourseDegrees:F1}° / {row.NavigationStatusText}",
                color, heading, trail.ToArray(), row.IsBaseStation || row.IsAidToNavigation ? "station" : "vessel"));
        }
        OnPropertyChanged(nameof(PositionedTargetCount));

        ValidFrames = channelA.ValidFrames + channelB.ValidFrames;
        RejectedFrames = channelA.RejectedFrames + channelB.RejectedFrames;
        UpdateDiagnosticWindow(channelA, channelB);
        InputSampleRateHz = Math.Max(channelA.InputSampleRateHz, channelB.InputSampleRateHz);
        CenterFrequencyHz = channelA.InputCenterFrequencyHz != 0
            ? channelA.InputCenterFrequencyHz : channelB.InputCenterFrequencyHz;
        LastSignalQuality = Math.Max(channelA.LastSignalQuality, channelB.LastSignalQuality);
        AverageSignalQuality = (channelA.AverageSignalQuality + channelB.AverageSignalQuality) / 2;
        MaximumSignalQuality = Math.Max(channelA.MaximumSignalQuality, channelB.MaximumSignalQuality);
        CoherentFrames = channelA.CoherentFrames + channelB.CoherentFrames;
        FallbackFrames = channelA.FallbackFrames + channelB.FallbackFrames;
        AisReceiver.DiagnosticsSnapshot correctionSource = channelA.CoherentFrames > 0
            ? channelA : channelB;
        FrequencyCorrectionText = CoherentFrames == 0 ? "—" :
            $"{-correctionSource.LastFrequencyCorrectionHz:+0;-0;0} Hz";
        ChannelADiagnosticText = FormatChannelDiagnostic(channelA, signalLevelDbm);
        ChannelBDiagnosticText = FormatChannelDiagnostic(channelB, signalLevelDbm);
        ChannelADetectionText = FormatChannelDetection(channelA.Channel,
            recentAFlags, recentACandidates, recentAHypothesisValid);
        ChannelBDetectionText = FormatChannelDetection(channelB.Channel,
            recentBFlags, recentBCandidates, recentBHypothesisValid);
        long recentValid = recentAValid + recentBValid;
        long recentRejected = recentARejected + recentBRejected;
        long recentTotal = recentValid + recentRejected;
        string rate = recentTotal == 0 ? "—" : $"{recentValid * 100.0 / recentTotal:F1} %";
        RecentValidationText = $"直近60秒: AIS 1 有効/fallback不一致 {recentAValid:N0}/{recentARejected:N0}, " +
            $"AIS 2 有効/fallback不一致 {recentBValid:N0}/{recentBRejected:N0}, 検証合格率 {rate}";
        UpdateInputDiagnostics(channelA, channelB);
        UpdateOverallStatus();
    }

    private void UpdateInputDiagnostics(
        AisReceiver.DiagnosticsSnapshot channelA,
        AisReceiver.DiagnosticsSnapshot channelB)
    {
        MonitoredChannelsText = "2 ch / AIS 1 161.975 / AIS 2 162.025 MHz";
        long center = channelA.InputCenterFrequencyHz != 0
            ? channelA.InputCenterFrequencyHz : channelB.InputCenterFrequencyHz;
        ChannelOffsetText = center == 0 ? "—" :
            $"{(channelA.ChannelFrequencyHz - center) / 1_000.0:+0.000;-0.000;0.000} / " +
            $"{(channelB.ChannelFrequencyHz - center) / 1_000.0:+0.000;-0.000;0.000} kHz";

        AisReceiver.DiagnosticsSnapshot configuration = channelA.InputSampleRateHz > 0 ? channelA : channelB;
        bothChannelsInPassband = configuration.InputSampleRateHz > 0 && center > 0 &&
            Math.Abs(channelA.ChannelFrequencyHz - center) <= configuration.InputSampleRateHz * 0.5 &&
            Math.Abs(channelB.ChannelFrequencyHz - center) <= configuration.InputSampleRateHz * 0.5;
        PassbandStatusText = configuration.InputSampleRateHz <= 0 ? "AIS 1/2 / 入力待機中" :
            $"AIS 1/2 {(bothChannelsInPassband ? "帯域内" : "帯域外")} / 必要 {MinimumDualChannelSampleRateHz / 1000} kS/s以上";
        if (configuration.InputSampleRateHz <= 0)
        {
            RateConversionSummaryText = "—";
            RateConversionText = "—";
            IntermediateRateText = "—";
            return;
        }

        int coarse = Math.Max(configuration.CoarseDecimationFactor, 1);
        int fine = Math.Max(configuration.FineDecimationFactor, 1);
        int interpolation = Math.Max(configuration.ResamplerInterpolationFactor, 1);
        int resamplerDecimation = Math.Max(configuration.ResamplerDecimationFactor, 1);
        RateConversionText =
            $"CIC ÷{coarse} → CIC ÷{fine} → Polyphase FIR {interpolation}/{resamplerDecimation}";

        double inputRate = configuration.InputSampleRateHz;
        double coarseRate = inputRate / coarse;
        double fineRate = coarseRate / fine;
        double outputRate = configuration.OutputSampleRateHz > 0
            ? configuration.OutputSampleRateHz : AisReceiver.DemodulationSampleRateHz;
        var stages = new List<string> { FormatSampleRate(inputRate) };
        if (coarse > 1 && Math.Abs(coarseRate - inputRate) > 0.1)
            stages.Add(FormatSampleRate(coarseRate));
        if (fine > 1 && Math.Abs(fineRate - coarseRate) > 0.1)
            stages.Add(FormatSampleRate(fineRate));
        stages.Add(FormatSampleRate(outputRate));
        IntermediateRateText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
            inputRate,
            stages.Count > 2 ? fineRate : 0,
            outputRate);
        RateConversionSummaryText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
            SRdeckPlugin.Wpf.DiagnosticRateDisplay.IsDistinct(inputRate, outputRate) ||
            coarse > 1 || fine > 1 || interpolation != resamplerDecimation,
            "標準チャネル");
    }

    private static string FormatSampleRate(double sampleRateHz) =>
        sampleRateHz >= 1_000_000
            ? $"{sampleRateHz / 1_000_000.0:F3} MS/s"
            : $"{sampleRateHz / 1_000.0:N1} kS/s";

    private List<GeoMapPoint> GetUpdatedTrail(AisTargetRow row, string color)
    {
        if (!trails.TryGetValue(row.Mmsi, out List<GeoMapPoint>? trail))
            trails[row.Mmsi] = trail = [];
        var point = new GeoMapPoint(row.Latitude!.Value, row.Longitude!.Value, color);
        if (trail.Count == 0 ||
            Math.Abs(trail[^1].Latitude - point.Latitude) > 0.00001 ||
            Math.Abs(trail[^1].Longitude - point.Longitude) > 0.00001)
            trail.Add(point);
        if (trail.Count > MaximumTrailPoints) trail.RemoveRange(0, trail.Count - MaximumTrailPoints);
        return trail;
    }

    private void TrimTrails()
    {
        foreach (List<GeoMapPoint> trail in trails.Values)
            if (trail.Count > maximumTrailPoints)
                trail.RemoveRange(0, trail.Count - maximumTrailPoints);
        for (int index = 0; index < MapMarkers.Count; index++)
        {
            GeoMapMarker marker = MapMarkers[index];
            if (uint.TryParse(marker.Id, out uint mmsi) && trails.TryGetValue(mmsi, out List<GeoMapPoint>? trail))
                MapMarkers[index] = marker with { Trail = trail.ToArray() };
        }
    }

    private void UpdateOverallStatus()
    {
        OverallLastUpdated = DateTime.Now.ToString("HH:mm:ss");
        if (InputSampleRateHz > 0 && InputSampleRateHz < MinimumDualChannelSampleRateHz)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "入力";
            OverallSummary = "AIS 1 / AIS 2の同時受信に必要な入力帯域が不足しています";
            OverallRecommendation = "確認: IQサンプルレートを240 kS/s以上に設定してください";
        }
        else if (!bothChannelsInPassband && InputSampleRateHz > 0)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "選局";
            OverallSummary = "中心周波数がAIS帯域から離れています";
            OverallRecommendation = "確認: 中心周波数を162.000 MHz付近に設定してください";
        }
        else if ((recentARejected >= 3 && recentARejected > recentAValid) ||
                 (recentBRejected >= 3 && recentBRejected > recentBValid))
        {
            string channel = recentARejected > recentAValid ? "AIS 1" : "AIS 2";
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "検証";
            OverallSummary = $"{channel}でFCS不一致が直近60秒の有効フレームを上回っています";
            OverallRecommendation = "確認: 該当チャネルの受信レベル、周波数補正、近接妨害を確認してください";
        }
        else if ((recentAValid > 0) != (recentBValid > 0))
        {
            string missing = recentAValid > 0 ? "AIS 2" : "AIS 1";
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "チャネル";
            OverallSummary = $"直近60秒は{missing}のみ有効フレームが未受信です";
            OverallRecommendation = $"確認: {missing}のレベル、周波数補正、周辺トラフィックを確認してください";
        }
        else if (recentAValid > 0 && recentBValid > 0)
        {
            OverallStatus = "正常";
            OverallStatusKind = OverallStatusKind.Success;
            OverallPhase = "受信中";
            OverallSummary = "AISフレームを正常に受信・復号しています";
            OverallRecommendation = "確認: AIS 1とAIS 2を同時監視しています";
        }
        else
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "受信処理";
            OverallSummary = "AIS 1 / AIS 2を監視中ですが、有効なフレームは未検出です";
            OverallRecommendation = "確認: VHFアンテナ、利得、周辺の船舶トラフィックを確認してください";
        }
    }

    private void UpdateDiagnosticWindow(AisReceiver.DiagnosticsSnapshot channelA, AisReceiver.DiagnosticsSnapshot channelB)
    {
        DateTimeOffset measuredAt = channelA.MeasuredAt > channelB.MeasuredAt ? channelA.MeasuredAt : channelB.MeasuredAt;
        if (measuredAt == default) measuredAt = DateTimeOffset.Now;
        if (diagnosticHistory.Count > 0)
        {
            var previous = diagnosticHistory.Last();
            if (channelA.ValidFrames < previous.AValid || channelA.RejectedFrames < previous.ARejected ||
                channelB.ValidFrames < previous.BValid || channelB.RejectedFrames < previous.BRejected ||
                channelA.HypothesisFlagCount < previous.AFlags ||
                channelA.HypothesisFrameCandidateCount < previous.ACandidates ||
                channelA.HypothesisValidFrameCount < previous.AHypothesisValid ||
                channelB.HypothesisFlagCount < previous.BFlags ||
                channelB.HypothesisFrameCandidateCount < previous.BCandidates ||
                channelB.HypothesisValidFrameCount < previous.BHypothesisValid)
                diagnosticHistory.Clear();
        }
        diagnosticHistory.Enqueue((measuredAt, channelA.ValidFrames, channelA.RejectedFrames,
            channelB.ValidFrames, channelB.RejectedFrames,
            channelA.HypothesisFlagCount, channelA.HypothesisFrameCandidateCount, channelA.HypothesisValidFrameCount,
            channelB.HypothesisFlagCount, channelB.HypothesisFrameCandidateCount, channelB.HypothesisValidFrameCount));
        while (diagnosticHistory.Count > 1 && measuredAt - diagnosticHistory.Peek().At > DiagnosticWindow)
            diagnosticHistory.Dequeue();
        var baseline = diagnosticHistory.Peek();
        recentAValid = Math.Max(0, channelA.ValidFrames - baseline.AValid);
        recentARejected = Math.Max(0, channelA.RejectedFrames - baseline.ARejected);
        recentBValid = Math.Max(0, channelB.ValidFrames - baseline.BValid);
        recentBRejected = Math.Max(0, channelB.RejectedFrames - baseline.BRejected);
        recentAFlags = Math.Max(0, channelA.HypothesisFlagCount - baseline.AFlags);
        recentACandidates = Math.Max(0, channelA.HypothesisFrameCandidateCount - baseline.ACandidates);
        recentAHypothesisValid = Math.Max(0, channelA.HypothesisValidFrameCount - baseline.AHypothesisValid);
        recentBFlags = Math.Max(0, channelB.HypothesisFlagCount - baseline.BFlags);
        recentBCandidates = Math.Max(0, channelB.HypothesisFrameCandidateCount - baseline.BCandidates);
        recentBHypothesisValid = Math.Max(0, channelB.HypothesisValidFrameCount - baseline.BHypothesisValid);
    }

    private static string FormatChannelDetection(string channel, long flags, long candidates, long valid) =>
        $"{channel}（直近60秒・復調仮説合計）: フラグ同期 {flags:N0} / フレーム候補 {candidates:N0} / FCS合格 {valid:N0}";

    private static string FormatChannelDiagnostic(AisReceiver.DiagnosticsSnapshot value, float? signalLevelDbm = null)
    {
        string last = value.LastFrameAt == default ? "最終受信 —" : $"最終受信 {value.LastFrameAt.LocalDateTime:HH:mm:ss}";
        string level = signalLevelDbm is { } dbm && float.IsFinite(dbm)
            ? $"レベル {dbm:F1} dBm"
            : (double.IsFinite(value.ChannelLevelDbfs) ? $"レベル {value.ChannelLevelDbfs:F1} dBFS" : "レベル —");
        string squelch = value.IsSquelchEnabled
            ? $"SQ {(value.IsSquelchOpen ? "OPEN" : "CLOSED")} ({value.SquelchThresholdDbfs:F0} dBm)"
            : "SQ OFF";
        return $"{value.Channel}: {level} / {squelch} / 品質 {value.LastSignalQuality:F2} / 周波数補正 {-value.LastFrequencyCorrectionHz:+0;-0;0} Hz / {last}";
    }
}
