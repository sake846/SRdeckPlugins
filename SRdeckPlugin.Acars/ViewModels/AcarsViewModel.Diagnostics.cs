using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Acars.Dsp;
using SRdeckPlugin.Acars.Models;
using SRdeckPlugin.Acars.Protocols;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Acars.ViewModels;

public sealed partial class AcarsViewModel
{
    public void Add(AcarsReception reception, long valid, long rejected)
    {
        Messages.Insert(0, reception);
        while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);
        OnPropertyChanged(nameof(RecentMessages));
        OnPropertyChanged(nameof(LastReceptionText));
        ValidFrames = valid;
        RejectedFrames = rejected;
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-RetentionMinutes);
        bool markerUpdated = false;
        if (reception.ReceivedAt >= cutoff &&
            AcarsPositionParser.TryParseWithAltitude(reception.Text, out double latitude, out double longitude, out int? altitudeFeet))
        {
            UpdateMapMarker(reception, latitude, longitude, altitudeFeet);
            markerUpdated = true;
        }
        PruneMapMarkers(cutoff, forceRefresh: markerUpdated);
        RebuildCategories();
        RefreshFilteredHistory();
    }

    private void UpdateMapMarker(AcarsReception reception, double latitude, double longitude, int? altitudeFeet)
    {
        string aircraft = AcarsReception.NormalizeRegistration(reception.Aircraft);
        mapMarkersByAircraft.TryGetValue(aircraft, out var previousEntry);
        GeoMapMarker? previous = previousEntry.Marker;
        List<GeoMapPoint> trail = previous?.Trail?.ToList() ?? [];
        double? heading = previous?.HeadingDegrees;
        string markerColor = GetAltitudeBandColor(altitudeFeet);

        if (previous is null)
            trail.Add(new(latitude, longitude, markerColor));
        else if (HasMoved(previous.Latitude, previous.Longitude, latitude, longitude))
        {
            heading = InitialBearing(previous.Latitude, previous.Longitude, latitude, longitude);
            trail.Add(new(latitude, longitude, markerColor));
            if (trail.Count > MaximumTrailPoints)
                trail.RemoveRange(0, trail.Count - MaximumTrailPoints);
        }

        string altText = altitudeFeet.HasValue ? $"{altitudeFeet.Value:N0} ft" : "高度不明";
        var marker = new GeoMapMarker(aircraft, latitude, longitude, aircraft,
            $"Label {reception.Label} / {reception.ReceivedAt:HH:mm:ss} / 高度: {altText} / {reception.Text}",
            markerColor, heading, trail.ToArray(), "aircraft", true);
        mapMarkersByAircraft[aircraft] = (marker, reception.ReceivedAt);
    }

    private void PruneMapMarkers(DateTimeOffset cutoff, bool forceRefresh = false)
    {
        bool changed = forceRefresh;
        foreach (var (id, entry) in mapMarkersByAircraft.ToArray())
        {
            if (entry.LastReceivedAt < cutoff)
            {
                mapMarkersByAircraft.Remove(id);
                changed = true;
            }
        }
        if (changed || MapMarkers.Count != mapMarkersByAircraft.Count)
        {
            MapMarkers.Clear();
            foreach (var entry in mapMarkersByAircraft.Values) MapMarkers.Add(entry.Marker);
            OnPropertyChanged(nameof(PositionedAircraftCount));
        }
    }

    private void RebuildMapMarkers()
    {
        DateTimeOffset cutoff = DateTimeOffset.Now.AddMinutes(-RetentionMinutes);
        mapMarkersByAircraft.Clear();
        foreach (AcarsReception msg in Messages.Where(m => m.ReceivedAt >= cutoff).Reverse())
        {
            if (AcarsPositionParser.TryParseWithAltitude(msg.Text, out double lat, out double lon, out int? alt))
            {
                UpdateMapMarker(msg, lat, lon, alt);
            }
        }
        PruneMapMarkers(cutoff, forceRefresh: true);
    }

    private void TrimTrails()
    {
        foreach (var (id, entry) in mapMarkersByAircraft.ToArray())
        {
            if (entry.Marker.Trail is null || entry.Marker.Trail.Count <= maximumTrailPoints) continue;
            mapMarkersByAircraft[id] = (entry.Marker with
            {
                Trail = entry.Marker.Trail.Skip(entry.Marker.Trail.Count - maximumTrailPoints).ToArray()
            }, entry.LastReceivedAt);
        }
        MapMarkers.Clear();
        foreach (var entry in mapMarkersByAircraft.Values) MapMarkers.Add(entry.Marker);
        OnPropertyChanged(nameof(PositionedAircraftCount));
    }

    internal void UpdateDiagnostics(AcarsReceiver.DiagnosticsSnapshot snapshot,
        long centerFrequencyHz, long targetFrequencyHz, int monitoredChannelCount,
        int openAudioChannelCount, long validFrameCount, long rejectedFrameCount,
        float? signalLevelDbm = null)
    {
        DateTimeOffset measuredAt = DateTimeOffset.Now;
        UpdateDiagnosticWindow(measuredAt, validFrameCount, rejectedFrameCount, snapshot.DecodePassCount,
            out long recentValid, out long recentRejected, out long recentCandidates);
        ValidFrames = validFrameCount;
        RejectedFrames = rejectedFrameCount;
        MonitoredChannelsText = $"{monitoredChannelCount:N0} ch";
        InputRateText = snapshot.InputSampleRateHz == 0 ? "—" :
            snapshot.InputSampleRateHz >= 1_000_000
                ? $"{snapshot.InputSampleRateHz / 1_000_000.0:F3} MS/s"
                : $"{snapshot.InputSampleRateHz / 1_000.0:N1} kS/s";

        bool hasAcarsIntermediateRate = snapshot.InputSampleRateHz > 0 &&
            (snapshot.CoarseDecimationFactor * snapshot.FineDecimationFactor) > 1 &&
            Math.Abs(snapshot.IntermediateSampleRateHz - 48_000) > 0.1 &&
            Math.Abs(snapshot.IntermediateSampleRateHz - snapshot.InputSampleRateHz) > 0.1;

        if (snapshot.InputSampleRateHz <= 0)
        {
            RateConversionSummaryText = "—";
            IntermediateRateText = "—";
            RateConversionText = "—";
        }
        else
        {
            IntermediateRateText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
                snapshot.InputSampleRateHz,
                hasAcarsIntermediateRate ? snapshot.IntermediateSampleRateHz : 0,
                AcarsReceiver.DemodulationSampleRateHz);

            RateConversionText = $"CIC ÷{snapshot.CoarseDecimationFactor} → CIC ÷{snapshot.FineDecimationFactor} → Polyphase FIR {snapshot.ResamplerInterpolationFactor}/{snapshot.ResamplerDecimationFactor}";
            RateConversionSummaryText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                SRdeckPlugin.Wpf.DiagnosticRateDisplay.IsDistinct(
                    snapshot.InputSampleRateHz, AcarsReceiver.DemodulationSampleRateHz) ||
                snapshot.CoarseDecimationFactor > 1 || snapshot.FineDecimationFactor > 1 ||
                snapshot.ResamplerInterpolationFactor != snapshot.ResamplerDecimationFactor,
                "標準チャネル／内部DSP");
        }
        ChannelOffsetText = $"{(targetFrequencyHz - centerFrequencyHz) / 1_000.0:+0.000;-0.000;0.000} kHz";
        bool isInPassband = snapshot.InputSampleRateHz > 0 &&
            Math.Abs(targetFrequencyHz - centerFrequencyHz) <= snapshot.InputSampleRateHz * 0.5;
        PassbandStatusText = snapshot.InputSampleRateHz <= 0
            ? "入力待機中"
            : $"{(isInPassband ? "帯域内" : "帯域外")} / 必要 {AcarsReceiver.DemodulationSampleRateHz / 1_000.0:F1} kS/s以上";
        ChannelLevelText = signalLevelDbm is { } dbm && float.IsFinite(dbm)
            ? $"{dbm:F1} dBm"
            : LevelText(snapshot.ChannelInputRms);
        AgcGainText = GainText(snapshot.ChannelAgcGain);
        AudioLevelText = LevelText(snapshot.DemodulatedAudioRms);
        AudioPeakText = LevelText(snapshot.DemodulatedAudioPeak);
        ToneConfidenceText = $"分離 {snapshot.ToneConfidence * 100:F1} % / SQ {snapshot.MskSquelchMetric * 100:F1} %";
        DetectorText = snapshot.DecodePassCount == 0
            ? "同期データ待ち / SQ CLOSED"
            : $"コヒーレントMSK / SQ {(snapshot.IsMskSquelchOpen ? "OPEN" : "CLOSED")} ({openAudioChannelCount}/{monitoredChannelCount} ch)";
        DecodePassText = $"{snapshot.DecodePassCount:N0} 回 / " +
            $"{snapshot.ProcessedAudioSampleCount / (double)AcarsReceiver.DemodulationSampleRateHz:F1} 秒 / " +
            $"位相候補 {snapshot.LastAcquisitionCandidatePhaseCount:N0} / " +
            $"完全復号 {snapshot.LastFullDecodePhaseCount:N0} / " +
            $"フォールバック {snapshot.AcquisitionFallbackCount:N0}";
        long recentTotal = recentValid + recentRejected;
        string recentRate = recentTotal == 0 ? "—" : $"{recentValid * 100.0 / recentTotal:F1} %";
        RecentValidationText = $"直近60秒: 候補 {recentCandidates:N0} / BCS合格 {recentValid:N0} / 不一致 {recentRejected:N0} / 検証合格率 {recentRate}";

        OverallLastUpdated = measuredAt.LocalDateTime.ToString("HH:mm:ss");
        if (snapshot.ChannelInputRms <= MinimumInputRms)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "入力";
            OverallSummary = "チャネル入力信号が検出されていません";
            OverallRecommendation = "確認: アンテナ接続、SDRゲイン、対象チャネルの設定を確認してください";
        }
        else if (snapshot.ChannelAgcGain > MaximumAgcGainWarning)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "選局";
            OverallSummary = "AGCが最大利得付近で、チャネル入力が弱い状態です";
            OverallRecommendation = "確認: アンテナ接続、RFゲイン、対象周波数と伝搬状態を確認してください";
        }
        else if (snapshot.DemodulatedAudioRms > 0.001f && snapshot.ToneConfidence < MinimumToneConfidence)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "復調";
            OverallSummary = "AM音声は入っていますが1200/2400Hzシンボル確信度が低めです";
            OverallRecommendation = "確認: S/N、帯域内歪み、近接チャネルの混信度を確認してください";
        }
        else if (recentRejected > recentValid && recentRejected >= 3)
        {
            OverallStatus = "要確認";
            OverallStatusKind = OverallStatusKind.Warning;
            OverallPhase = "検証";
            OverallSummary = "BCSチェック不一致のフレームが多めです";
            OverallRecommendation = "確認: シンボル一致度や受信レベルを確認してください";
        }
        else if (recentValid > 0 && lastValidFrameAt is not null && measuredAt - lastValidFrameAt <= DiagnosticWindow)
        {
            OverallStatus = "正常";
            OverallStatusKind = OverallStatusKind.Success;
            OverallPhase = "受信処理";
            OverallSummary = "ACARSフレームを正常に受信・復号しています";
            OverallRecommendation = "確認: 処理は正常に動作しています";
        }
        else
        {
            OverallStatus = "監視中";
            OverallStatusKind = OverallStatusKind.Running;
            OverallPhase = "受信処理";
            OverallSummary = "入力信号を監視中ですが、有効なACARSフレームはまだ未検出です";
            OverallRecommendation = "確認: トラフィックの発生または伝搬状態を確認してください";
        }
    }

    private void UpdateDiagnosticWindow(DateTimeOffset measuredAt, long valid, long rejected, long candidates,
        out long recentValid, out long recentRejected, out long recentCandidates)
    {
        if (diagnosticHistory.Count > 0)
        {
            var previous = diagnosticHistory.Last();
            if (valid < previous.Valid || rejected < previous.Rejected || candidates < previous.Candidates)
                diagnosticHistory.Clear();
            else if (valid > previous.Valid)
                lastValidFrameAt = measuredAt;
        }
        diagnosticHistory.Enqueue((measuredAt, valid, rejected, candidates));
        while (diagnosticHistory.Count > 1 && measuredAt - diagnosticHistory.Peek().At > DiagnosticWindow)
            diagnosticHistory.Dequeue();
        var baseline = diagnosticHistory.Peek();
        recentValid = Math.Max(0, valid - baseline.Valid);
        recentRejected = Math.Max(0, rejected - baseline.Rejected);
        recentCandidates = Math.Max(0, candidates - baseline.Candidates);
    }

    private void RebuildChannelSelections()
    {
        RegionalChannelSelections.Clear();
        foreach (AcarsPluginModule.Channel channel in RegionalChannels)
        {
            var selection = new AcarsChannelSelection(channel,
                monitoredChannelIds.Contains(channel.Id), channel.Id == selectedChannelId);
            selection.Changed = OnChannelMonitoringChanged;
            RegionalChannelSelections.Add(selection);
        }
    }

    private bool OnChannelMonitoringChanged(AcarsChannelSelection selection, bool enabled)
    {
        if (synchronizingSettings) return true;
        var proposed = new HashSet<string>(monitoredChannelIds, StringComparer.Ordinal);
        if (enabled) proposed.Add(selection.Channel.Id);
        else proposed.Remove(selection.Channel.Id);
        if (proposed.Count == 0) return false;
        string[] ordered = Channels.Where(channel => proposed.Contains(channel.Id))
            .Select(channel => channel.Id).ToArray();
        if (MonitoredChannelsChanged is not null && !MonitoredChannelsChanged(ordered)) return false;
        monitoredChannelIds.Clear();
        foreach (string id in ordered) monitoredChannelIds.Add(id);
        if (!monitoredChannelIds.Contains(selectedChannelId))
            selectedChannelId = ordered[0];
        OnPropertyChanged(nameof(SettingStatus));
        return true;
    }

    private static string LevelText(float value) => value > 0 && float.IsFinite(value)
        ? $"{20 * MathF.Log10(value):F1} dBFS"
        : "-∞ dBFS";

    private static string GainText(float value) => value > 0 && float.IsFinite(value)
        ? $"{20 * MathF.Log10(value):+0.0;-0.0;0.0} dB ({value:F2}×)"
        : "--";

    private static bool HasMoved(double fromLatitude, double fromLongitude,
        double toLatitude, double toLongitude) =>
        Math.Abs(fromLatitude - toLatitude) > 0.00001 ||
        Math.Abs(fromLongitude - toLongitude) > 0.00001;

    private static double InitialBearing(double fromLatitude, double fromLongitude,
        double toLatitude, double toLongitude)
    {
        double fromLat = fromLatitude * Math.PI / 180;
        double toLat = toLatitude * Math.PI / 180;
        double deltaLon = (toLongitude - fromLongitude) * Math.PI / 180;
        double y = Math.Sin(deltaLon) * Math.Cos(toLat);
        double x = Math.Cos(fromLat) * Math.Sin(toLat) -
            Math.Sin(fromLat) * Math.Cos(toLat) * Math.Cos(deltaLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    [RelayCommand]
    private void ClearMessages()
    {
        SelectedReception = null;
        SelectedTimelineReception = null;
        SelectedAircraftGroup = null;
        Messages.Clear();
        AircraftGroups.Clear();
        MapMarkers.Clear();
        mapMarkersByAircraft.Clear();
        OnPropertyChanged(nameof(PositionedAircraftCount));
        OnPropertyChanged(nameof(ReceivedAircraftCount));
        OnPropertyChanged(nameof(RecentAircraftGroups));
        OnPropertyChanged(nameof(LastReceptionText));
        RefreshFilteredHistory();
        ClearRequested?.Invoke();
    }

    [RelayCommand]
    private void StartCapture() => CaptureRequested?.Invoke();
}
