using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Dsp;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.ViewModels;

internal sealed partial class OneSegViewModel
{
    public void Configure(OneSegSettings settings, OneSegChannel channel,
        bool preserveRegionPresets = false)
    {
        synchronizingSelection = true;
        try
        {
            selectedPrefecture = OneSegPrefecture.Find(settings.PrefectureId);
            selectedRegion = OneSegRegionPreset.Find(selectedPrefecture, settings.RegionId);
            UpdateCandidates(selectedRegion, channel.Number, true);
            if (settings.ScannedStations is { Count: > 0 } scannedStations)
            {
                ReplaceStations(scannedStations);
                if (preserveRegionPresets)
                {
                    HashSet<int> receivedChannels = Stations.Where(item => !item.IsPreset)
                        .Select(item => item.ChannelNumber).ToHashSet();
                    foreach (OneSegStation preset in selectedRegion.PresetStations.Where(item =>
                        !receivedChannels.Contains(item.ChannelNumber)))
                    {
                        Stations.Add(preset);
                    }
                    EnsureSelectedChannelPreset(selectedRegion, channel.Number);
                    SortStations();
                }
                ScanStatus = "";
                ScanResultsText = FormatScanResults(Stations);
            }
            else
            {
                ReplaceStations(selectedRegion.PresetStations);
                EnsureSelectedChannelPreset(selectedRegion, channel.Number);
                SortStations();
                ScanStatus = $"地域プリセット: {selectedPrefecture.DisplayName} / {selectedRegion.DisplayName}";
                ScanResultsText = $"検出局: なし / プリセット候補: {Stations.Count}ch";
            }
            recording = false;
            autoRecordOnStart = settings.AutoRecordOnStart;
            recordingDirectory = settings.RecordingDirectory ?? "";
            recordingSegmentMinutes = settings.RecordingSegmentMinutes;
            recordingMaxBytes = settings.RecordingMaxBytes;
            recordingStorageLimitBytes = settings.RecordingStorageLimitBytes;
            recordingSidecarEnabled = settings.RecordingSidecarEnabled;
            iqCaptureDirectory = settings.IqCaptureDirectory ?? "";
            iqCaptureSeconds = settings.IqCaptureSeconds;
            volume = 100;
            desiredAudioTrackId = settings.AudioTrackId;
            captionsEnabled = settings.CaptionsEnabled;
            videoSuperResolutionEnabled = settings.VideoSuperResolutionEnabled;
            captionBackgroundEnabled = settings.CaptionBackgroundEnabled;
            selectedCaptionPosition = CaptionPositions.First(item => item.Id == settings.CaptionPosition);
            emergencyNotificationsEnabled = settings.EmergencyNotificationsEnabled;
            selectedStation = Stations.FirstOrDefault(item => item.ServiceId == settings.LastServiceId &&
                item.ChannelNumber == channel.Number) ??
                Stations.FirstOrDefault(item => item.ChannelNumber == channel.Number);
            OnPropertyChanged(string.Empty);
            RefreshProgramSummary(DateTimeOffset.Now);
            NotifyStationCommands();
        }
        finally { synchronizingSelection = false; }
    }

    private void EnsureSelectedChannelPreset(OneSegRegionPreset region, int channelNumber)
    {
        if (Stations.Any(item => item.ChannelNumber == channelNumber)) return;
        OneSegChannel? channel = OneSegChannel.JapaneseUhfChannels.FirstOrDefault(item =>
            item.Number == channelNumber);
        if (channel is null) return;
        Stations.Add(new OneSegStation(0, $"{region.DisplayName} {channel.DisplayName}",
            "地域プリセット", channel.Number) { IsPreset = true });
    }

    public void EnsureSelectedStationForCurrentChannel()
    {
        if (selectedStation is not null && Stations.Contains(selectedStation)) return;
        OneSegStation? candidate = Stations.FirstOrDefault(item =>
            item.ChannelNumber == SelectedChannel.Number);
        if (candidate is null) return;
        bool previousSynchronizingSelection = synchronizingSelection;
        synchronizingSelection = true;
        try
        {
            selectedStation = candidate;
            OnPropertyChanged(nameof(SelectedStation));
            NotifyStationCommands();
        }
        finally
        {
            synchronizingSelection = previousSynchronizingSelection;
        }
    }

    private void UpdateCandidates(OneSegRegionPreset region, int desiredChannel, bool includeDesired = false)
    {
        Channels.Clear();
        foreach (OneSegChannel channel in region.CandidateChannels) Channels.Add(channel);
        if (includeDesired && Channels.All(channel => channel.Number != desiredChannel))
        {
            OneSegChannel? extra = OneSegChannel.JapaneseUhfChannels.FirstOrDefault(channel =>
                channel.Number == desiredChannel);
            if (extra is not null) Channels.Add(extra);
        }
        selectedChannel = Channels.FirstOrDefault(channel => channel.Number == desiredChannel) ?? Channels[0];
        OnPropertyChanged(nameof(Channels));
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(ChannelFrequency));
    }

    public void BeginScan(int channelCount, bool isAppend = false)
    {
        if (!isAppend)
        {
            synchronizingSelection = true;
            try
            {
                Stations.Clear();
                selectedStation = null;
                OnPropertyChanged(nameof(SelectedStation));
            }
            finally { synchronizingSelection = false; }
        }
        isAdditionalScanning = isAppend;
        IsScanning = true;
        OnPropertyChanged(nameof(IsFullScanning));
        OnPropertyChanged(nameof(IsAdditionalScanning));
        ScanStatus = $"{(isAppend ? "追加サーチ" : "サーチ")}開始: {channelCount}ch";
        ScanResultsText = FormatScanResults(Stations);
    }

    public void ReportScanProgress(int index, int total, OneSegChannel channel) => ScanStatus =
        $"サーチ中 {index}/{total}: {channel.DisplayName}, {channel.FrequencyText}";

    public void AddScannedStation(OneSegStation station)
    {
        // Removing a preset temporarily clears WPF Selector.SelectedItem. Keep
        // that transient binding change from triggering a retune while the
        // received station is inserted and selected below.
        bool previousSynchronizingSelection = synchronizingSelection;
        synchronizingSelection = true;
        try
        {
            bool selectedPresetRemoved = selectedStation?.IsPreset == true &&
                selectedStation.ChannelNumber == station.ChannelNumber;
            bool selectReceivedStation = selectedStation is null &&
                station.ChannelNumber == SelectedChannel.Number;
            for (int i = Stations.Count - 1; i >= 0; i--)
            {
                if (Stations[i].IsPreset && Stations[i].ChannelNumber == station.ChannelNumber)
                    Stations.RemoveAt(i);
            }
            int existingIndex = -1;
            for (int i = 0; i < Stations.Count; i++)
            {
                if (Stations[i].ServiceId == station.ServiceId && Stations[i].ChannelNumber == station.ChannelNumber)
                {
                    existingIndex = i;
                    break;
                }
            }
            if (existingIndex >= 0)
            {
                OneSegStation existing = Stations[existingIndex];
                OneSegStation updated = existing.UpdateInfo(
                    serviceName: station.ServiceName,
                    providerName: station.ProviderName,
                    remoteControlKeyId: station.RemoteControlKeyId,
                    merDb: station.MerDb,
                    transportPackets: station.TransportPackets,
                    reedSolomonPackets: station.ReedSolomonPackets,
                    reedSolomonRejected: station.ReedSolomonRejected,
                    scannedAt: station.ScannedAt ?? DateTimeOffset.Now);
                Stations[existingIndex] = updated;
                if (selectedStation?.ServiceId == updated.ServiceId && selectedStation?.ChannelNumber == updated.ChannelNumber)
                {
                    selectedStation = updated;
                    OnPropertyChanged(nameof(SelectedStation));
                }
            }
            else
            {
                Stations.Add(station);
            }
            SortStations();
            if (selectedPresetRemoved || selectReceivedStation)
            {
                selectedStation = Stations.FirstOrDefault(item =>
                    !item.IsPreset && item.ChannelNumber == station.ChannelNumber);
                OnPropertyChanged(nameof(SelectedStation));
            }
            ScanResultsText = FormatScanResults(Stations);
            NotifyStationCommands();
        }
        finally
        {
            synchronizingSelection = previousSynchronizingSelection;
        }
    }

    public void EndScan(IReadOnlyList<OneSegStation> stations, int selectedChannel, bool succeeded,
        ushort? selectedServiceId = null)
    {
        synchronizingSelection = true;
        try
        {
            ReplaceStations(stations.Count > 0 ? stations : selectedRegion.PresetStations);
            selectedStation = Stations.FirstOrDefault(item => item.ChannelNumber == selectedChannel &&
                item.ServiceId == selectedServiceId) ??
                Stations.FirstOrDefault(item => item.ChannelNumber == selectedChannel);
            OnPropertyChanged(nameof(SelectedStation));
        }
        finally { synchronizingSelection = false; }
        isAdditionalScanning = false;
        IsScanning = false;
        OnPropertyChanged(nameof(IsFullScanning));
        OnPropertyChanged(nameof(IsAdditionalScanning));
        ScanStatus = succeeded ? $"サーチ完了: {stations.Count}局" : "サーチ中断";
        ScanResultsText = FormatScanResults(stations);
        NotifyStationCommands();
    }

    private static string FormatScanResults(IEnumerable<OneSegStation> stations)
    {
        OneSegStation[] detected = stations.Where(item => !item.IsPreset).ToArray();
        return detected.Length == 0 ? "検出局: なし" :
            $"検出局: {string.Join("、", detected.Select(item => item.DisplayName))}";
    }

    public void ApplyDiagnostics(OneSegDiagnostics dsp, long packets, long continuityErrors,
        bool isStreaming = true, int inputSampleRateHz = IsdbTParameters.OutputSampleRateHz,
        double intermediateSampleRateHz = 0,
        int demodulationSampleRateHz = IsdbTParameters.OutputSampleRateHz,
        bool usesHostChannelRateConversion = false,
        float? calibratedSignalLevelDbm = null)
    {
        latestDiagnostics = dsp;
        latestTransportPackets = packets;
        latestContinuityErrors = continuityErrors;
        NormalizeDiagnosticBaselines(dsp, packets, continuityErrors);
        OfdmLockStatus = dsp.OfdmLocked ? "LOCK" : "未同期";
        TmccLockStatus = dsp.TmccLocked ? "LOCK" : "未同期";
        MerStatus = dsp.OfdmLocked ? $"{dsp.MerDb:F1} dB" : "—";
        long rsTotal = dsp.ReedSolomonPackets + dsp.ReedSolomonRejected;
        RsQualityStatus = rsTotal == 0
            ? "—"
            : $"{dsp.ReedSolomonPackets * 100.0 / rsTotal:F1} %";
        string mode = dsp.Mode == IsdbTMode.Unknown ? "—" : ((int)dsp.Mode).ToString();
        string guard = dsp.GuardInterval == IsdbTGuardInterval.Unknown ? "—" : $"1/{(int)dsp.GuardInterval}";
        LockStatus = $"OFDM {(dsp.OfdmLocked ? "LOCK" : "--")} / TMCC {(dsp.TmccLocked ? "LOCK" : "--")} / Mode {mode} / GI {guard}";
        SignalStatus = dsp.OfdmLocked
            ? $"MER {dsp.MerDb:F1} dB / CFO {dsp.FrequencyOffsetHz:+0.0;-0.0;0} Hz / OFDM {dsp.OfdmSymbols:N0}"
            : $"MER — / CFO — / OFDM {dsp.OfdmSymbols:N0}";
        PacketStatus = $"TS {packets:N0} / RS {dsp.ReedSolomonPackets:N0} / RS棄却 {dsp.ReedSolomonRejected:N0} / CC誤り {continuityErrors:N0}";

        DateTimeOffset measuredAt = dsp.MeasuredAt == DateTimeOffset.MinValue
            ? DateTimeOffset.Now : dsp.MeasuredAt;
        UpdateDiagnosticWindow(measuredAt, dsp.ReedSolomonPackets,
            dsp.ReedSolomonRejected, packets, continuityErrors);
        DiagnosticLastUpdated = measuredAt.ToLocalTime().ToString("HH:mm:ss");
        DiagnosticChannelText = $"UHF {SelectedChannel.Number} ch / {SelectedChannel.FrequencyText}";
        DiagnosticInputText = inputSampleRateHz > 0
            ? $"{inputSampleRateHz / 1_000_000.0:F3} MS/s" : "—";
        DiagnosticRateConversionText = inputSampleRateHz <= 0
            ? "—"
            : SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                inputSampleRateHz != demodulationSampleRateHz,
                usesHostChannelRateConversion ? "標準チャネル" : "プラグイン内部");
        DiagnosticRatePathText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
            inputSampleRateHz, intermediateSampleRateHz, demodulationSampleRateHz);
        DiagnosticPassbandText = inputSampleRateHz >= IsdbTParameters.OccupiedBandwidthHz
            ? $"帯域内 / 必要 {IsdbTParameters.OccupiedBandwidthHz / 1_000.0:F0} kHz"
            : $"帯域不足 / 必要 {IsdbTParameters.OccupiedBandwidthHz / 1_000.0:F0} kHz";
        DiagnosticChannelLevelText = calibratedSignalLevelDbm is { } dbm && float.IsFinite(dbm)
            ? $"{dbm:F1} dBm"
            : (double.IsFinite(dsp.ChannelLevelDbfs) ? $"{dsp.ChannelLevelDbfs:F1} dBFS" : "—");
        DiagnosticMerText = dsp.OfdmLocked ? $"{dsp.MerDb:F1} dB" : "—";
        DiagnosticFrequencyOffsetText = dsp.OfdmLocked
            ? $"{dsp.FrequencyOffsetHz:+0.0;-0.0;0.0} Hz" : "—";
        UpdateDiagnosticCounterTexts(dsp, packets, continuityErrors, mode, guard);
        DiagnosticTmccParametersText = FormatTmcc(dsp.Tmcc);
        UpdateOverallDiagnostic(dsp, packets, continuityErrors, isStreaming, inputSampleRateHz);
        if (!IsScanning)
        {
            UpdateActiveStationQuality(dsp.MerDb, packets, dsp.ReedSolomonPackets, dsp.ReedSolomonRejected);
        }
    }

    private void ResetDiagnosticStatistics()
    {
        diagnosticBaselineOfdmSymbols = latestDiagnostics.OfdmSymbols;
        diagnosticBaselineTmccFrames = latestDiagnostics.TmccFrames;
        diagnosticBaselineViterbiBytes = latestDiagnostics.ViterbiBytes;
        diagnosticBaselineRsPackets = latestDiagnostics.ReedSolomonPackets;
        diagnosticBaselineRsRejected = latestDiagnostics.ReedSolomonRejected;
        diagnosticBaselineTransportPackets = latestTransportPackets;
        diagnosticBaselineContinuityErrors = latestContinuityErrors;
        diagnosticWindow.Clear();
        string mode = latestDiagnostics.Mode == IsdbTMode.Unknown
            ? "—" : ((int)latestDiagnostics.Mode).ToString();
        string guard = latestDiagnostics.GuardInterval == IsdbTGuardInterval.Unknown
            ? "—" : $"1/{(int)latestDiagnostics.GuardInterval}";
        UpdateDiagnosticCounterTexts(latestDiagnostics, latestTransportPackets,
            latestContinuityErrors, mode, guard);
    }

    private void NormalizeDiagnosticBaselines(OneSegDiagnostics dsp, long packets,
        long continuityErrors)
    {
        if (dsp.OfdmSymbols >= diagnosticBaselineOfdmSymbols &&
            dsp.TmccFrames >= diagnosticBaselineTmccFrames &&
            dsp.ViterbiBytes >= diagnosticBaselineViterbiBytes &&
            dsp.ReedSolomonPackets >= diagnosticBaselineRsPackets &&
            dsp.ReedSolomonRejected >= diagnosticBaselineRsRejected &&
            packets >= diagnosticBaselineTransportPackets &&
            continuityErrors >= diagnosticBaselineContinuityErrors)
            return;

        diagnosticBaselineOfdmSymbols = 0;
        diagnosticBaselineTmccFrames = 0;
        diagnosticBaselineViterbiBytes = 0;
        diagnosticBaselineRsPackets = 0;
        diagnosticBaselineRsRejected = 0;
        diagnosticBaselineTransportPackets = 0;
        diagnosticBaselineContinuityErrors = 0;
        diagnosticWindow.Clear();
    }

    private void UpdateDiagnosticCounterTexts(OneSegDiagnostics dsp, long packets,
        long continuityErrors, string mode, string guard)
    {
        long ofdmSymbols = Math.Max(0, dsp.OfdmSymbols - diagnosticBaselineOfdmSymbols);
        long tmccFrames = Math.Max(0, dsp.TmccFrames - diagnosticBaselineTmccFrames);
        long viterbiBytes = Math.Max(0, dsp.ViterbiBytes - diagnosticBaselineViterbiBytes);
        long rsPackets = Math.Max(0, dsp.ReedSolomonPackets - diagnosticBaselineRsPackets);
        long rsRejected = Math.Max(0, dsp.ReedSolomonRejected - diagnosticBaselineRsRejected);
        long transportPackets = Math.Max(0, packets - diagnosticBaselineTransportPackets);
        long continuity = Math.Max(0, continuityErrors - diagnosticBaselineContinuityErrors);
        DiagnosticOfdmText = $"{(dsp.OfdmLocked ? "LOCK" : "未同期")} / Mode {mode} / GI {guard} / {ofdmSymbols:N0} symbol";
        DiagnosticTmccText = $"{(dsp.TmccLocked ? "LOCK" : "未同期")} / {tmccFrames:N0} frame";
        DiagnosticFecText = $"Viterbi {viterbiBytes:N0} B / RS {rsPackets:N0} / 棄却 {rsRejected:N0}";
        DiagnosticTransportText = $"TS {transportPackets:N0} / CC誤り {continuity:N0} / {PcrStatus}";
    }

    private void UpdateDiagnosticWindow(DateTimeOffset measuredAt, long rsPackets,
        long rsRejected, long packets, long continuityErrors)
    {
        if (diagnosticWindow.TryPeek(out DiagnosticWindowSample first) &&
            (rsPackets < first.RsPackets || rsRejected < first.RsRejected ||
             packets < first.TransportPackets || continuityErrors < first.ContinuityErrors))
            diagnosticWindow.Clear();
        diagnosticWindow.Enqueue(new(measuredAt, rsPackets, rsRejected, packets, continuityErrors));
        while (diagnosticWindow.TryPeek(out first) && measuredAt - first.MeasuredAt > TimeSpan.FromSeconds(5))
            diagnosticWindow.Dequeue();
    }

    private void UpdateOverallDiagnostic(OneSegDiagnostics dsp, long packets, long continuityErrors,
        bool isStreaming, int inputSampleRateHz)
    {
        PluginRuntimeDiagnosticsSnapshot runtime;
        try { runtime = RuntimeDiagnostics.GetSnapshot(); }
        catch { runtime = default; }
        bool hasInput = runtime.ProcessedBlocks > 0 || dsp.OfdmSymbols > 0;
        DiagnosticWindowSample oldest = diagnosticWindow.TryPeek(out DiagnosticWindowSample value)
            ? value : default;
        long recentRs = Math.Max(0, dsp.ReedSolomonPackets - oldest.RsPackets);
        long recentRejected = Math.Max(0, dsp.ReedSolomonRejected - oldest.RsRejected);
        long recentTransport = Math.Max(0, packets - oldest.TransportPackets);
        long recentContinuity = Math.Max(0, continuityErrors - oldest.ContinuityErrors);

        if (!isStreaming)
            SetDiagnostic(OverallStatusKind.Idle, "停止中", "入力", "ワンセグ受信は停止しています", "確認: 受信を開始してください");
        else if (inputSampleRateHz < IsdbTParameters.OccupiedBandwidthHz)
            SetDiagnostic(OverallStatusKind.Error, "エラー", "入力・選局", "入力帯域がワンセグ占有帯域を満たしていません", "確認: チャネル出力レートと帯域幅を確認してください");
        else if (!hasInput)
            SetDiagnostic(OverallStatusKind.Idle, "入力待ち", "入力", "IQ入力を待機しています", "確認: SDR接続と受信開始状態を確認してください");
        else if (!dsp.OfdmLocked)
            SetDiagnostic(OverallStatusKind.Running, "監視中", "OFDM同期", "IQ入力はありますがOFDM同期は未成立です", "確認: UHFチャネル、アンテナ、受信レベルを確認してください");
        else if (!dsp.TmccLocked)
            SetDiagnostic(OverallStatusKind.Warning, "要確認", "TMCC同期", "OFDM同期後にTMCCを取得できていません", "確認: MER、周波数偏差、マルチパスを確認してください");
        else if (recentRs + recentRejected > 0 && recentRejected * 10 > recentRs + recentRejected)
            SetDiagnostic(OverallStatusKind.Warning, "要確認", "RS検証", "直近5秒のRS棄却率が10%を超えています", "確認: MER、周波数偏差、アンテナ方向を確認してください");
        else if (recentContinuity > 0)
            SetDiagnostic(OverallStatusKind.Warning, "要確認", "TS連続性", "直近5秒にTS continuity errorが増加しました", "確認: RS棄却、受信レベル、処理ドロップを確認してください");
        else if (recentTransport > 0)
            SetDiagnostic(OverallStatusKind.Success, "正常", "TS受信", "ワンセグTSを継続して復調・検証しています", "確認: 受信処理は正常に動作しています");
        else
            SetDiagnostic(OverallStatusKind.Running, "監視中", "FEC・TS", "同期は成立し、TSパケットを待機しています", "確認: TMCC設定と受信品質を確認しながら継続監視してください");
    }

    private void SetDiagnostic(OverallStatusKind kind, string status, string phase,
        string summary, string recommendation)
    {
        DiagnosticStatusKind = kind;
        DiagnosticStatusText = status;
        DiagnosticPhase = phase;
        DiagnosticSummary = summary;
        DiagnosticRecommendation = recommendation;
    }

    private static string FormatTmcc(TmccParameters? tmcc) => tmcc is null
        ? "—"
        : $"Layer A {tmcc.LayerA.Modulation} / {tmcc.LayerA.CodeRate} / TI {tmcc.LayerA.TimeInterleavingLength} / {tmcc.LayerA.SegmentCount} segment";

    private readonly record struct DiagnosticWindowSample(DateTimeOffset MeasuredAt,
        long RsPackets, long RsRejected, long TransportPackets, long ContinuityErrors);
}
