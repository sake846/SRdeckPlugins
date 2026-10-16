using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeckPlugin.Meshtastic.Protocols;
using SRdeckPlugin.Meshtastic.Dsp;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Meshtastic.Services;

// Presentation state owned by the Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.ViewModels;

public partial class MeshtasticViewModel
{
    private void ApplyMeshtasticRadioTuning(IReadOnlyList<(int FrequencyHz, int BandwidthHz)> targetBands)
    {
        _tuningTargets = targetBands;
        if (_canRequestTuning) RequestMeshtasticRadioTuning();
    }

    private async void RequestMeshtasticRadioTuning()
    {
        if (_hostContext is null || _tuningTargets.Count == 0) return;
        MeshtasticLoRaProfile profile = MeshtasticJpLongFastProfile.GetProfile(SelectedMeshtasticModemPreset);
        long lowerEdgeHz = _tuningTargets.Min(band => band.FrequencyHz - band.BandwidthHz / 2L);
        long upperEdgeHz = _tuningTargets.Max(band => band.FrequencyHz + band.BandwidthHz / 2L);
        PluginTuningResult result = await _hostContext.Tuning.RequestAsync(new PluginTuningRequest(
            SelectedMeshtasticModemPreset.ToString().ToLowerInvariant(),
            profile.Name,
            _tuningTargets.Select(band => new TuningTarget(band.FrequencyHz, band.BandwidthHz)).ToArray(),
            lowerEdgeHz + (upperEdgeHz - lowerEdgeHz) / 2,
            1_000_000,
            profile.BandwidthHz / 2,
            true,
            IsMeshtasticDiscoveryMode,
            PluginGainPreference.Automatic));
        if (result.Outcome == PluginTuningOutcome.Rejected)
        {
            if (TryReduceMeshtasticSlotsToFitHostSampleRate(result.SampleRateHz, out string slotText))
            {
                MeshtasticChannelSettingStatus =
                    $"受信帯域に合わせて {slotText} に縮小して再同調しました。";
                return;
            }

            MeshtasticChannelSettingStatus = $"同調要求が拒否されました: {result.Message}";
        }
    }

    private bool TryReduceMeshtasticSlotsToFitHostSampleRate(int hostSampleRateHz, out string slotText)
    {
        slotText = string.Empty;
        if (hostSampleRateHz <= 0 ||
            !TryGetMeshtasticBandwidthSlots(out List<MeshtasticBandwidthSlots> selections, out _))
            return false;

        MeshtasticRegionProfile region = MeshtasticJpLongFastProfile.GetRegion(SelectedMeshtasticRegion);
        var targets = selections
            .SelectMany(selection => selection.RadioChannels.Select(slot => new MeshtasticTuningSlot(
                selection.BandwidthHz, slot,
                region.CalculateChannelFrequencyHz(slot, selection.BandwidthHz))))
            .OrderBy(target => target.FrequencyHz)
            .ThenByDescending(target => target.BandwidthHz)
            .ToArray();
        if (targets.Length < 2) return false;

        MeshtasticTuningSlot[]? best = null;
        for (int first = 0; first < targets.Length; first++)
        {
            for (int last = first; last < targets.Length; last++)
            {
                MeshtasticTuningSlot[] candidate = targets[first..(last + 1)];
                if (candidate.Select(target => target.BandwidthHz).Distinct().Count() != selections.Count)
                    continue;

                long lowerEdgeHz = candidate.Min(target => target.FrequencyHz - target.BandwidthHz / 2L);
                long upperEdgeHz = candidate.Max(target => target.FrequencyHz + target.BandwidthHz / 2L);
                int requiredSampleRateHz = checked((int)Math.Clamp(
                    Math.Ceiling((upperEdgeHz - lowerEdgeHz) / 0.9), 1_000_000, int.MaxValue));
                if (requiredSampleRateHz > hostSampleRateHz ||
                    (best is not null && candidate.Length <= best.Length))
                    continue;

                best = candidate;
            }
        }

        if (best is null || best.Length == targets.Length) return false;

        List<MeshtasticBandwidthSlots> reducedSelections = selections
            .Select(selection => new MeshtasticBandwidthSlots(
                selection.BandwidthHz,
                best.Where(target => target.BandwidthHz == selection.BandwidthHz)
                    .Select(target => target.Slot)
                    .Distinct()
                    .Order()
                    .ToArray()))
            .ToList();
        if (reducedSelections.Any(selection => selection.RadioChannels.Count == 0)) return false;

        _isUpdatingMeshtasticSlotSelection = true;
        try
        {
            foreach (MeshtasticBandwidthSlots selection in reducedSelections)
            {
                string slots = string.Join(',', selection.RadioChannels);
                if (selection.BandwidthHz == 250_000)
                {
                    MeshtasticRadioChannels250 = slots;
                    SyncMeshtasticSlotSelections(MeshtasticSlots250, slots);
                }
                else
                {
                    MeshtasticRadioChannels125 = slots;
                    SyncMeshtasticSlotSelections(MeshtasticSlots125, slots);
                }
            }
        }
        finally
        {
            _isUpdatingMeshtasticSlotSelection = false;
        }

        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        slotText = FormatMeshtasticBandwidthSlots(reducedSelections);
        ApplyMeshtasticChannelSettings();
        return true;
    }

    private readonly record struct MeshtasticTuningSlot(int BandwidthHz, int Slot, int FrequencyHz);

    private void RegisterMeshtasticReceiver()
    {
        _meshtasticReceiveService.MeshtasticDataReceived += HandleMeshtasticDataReceived;
        _meshtasticReceiveService.PreambleDetected += HandleMeshtasticPreambleDetected;
        _meshtasticReceiveService.FrameSynchronized += HandleMeshtasticFrameSynchronized;
        _meshtasticReceiveService.PayloadDecoded += HandleMeshtasticPayloadDecoded;
        _meshtasticReceiveService.MeshtasticPacketReceived += HandleMeshtasticPacketReceived;
        _meshtasticReceiveService.AcquisitionDiagnostic += HandleMeshtasticAcquisitionDiagnostic;
        _meshtasticReceiveService.WorkerFailed += HandleMeshtasticWorkerFailed;
    }

    private void UnregisterMeshtasticReceiver()
    {
        _meshtasticReceiveService.MeshtasticDataReceived -= HandleMeshtasticDataReceived;
        _meshtasticReceiveService.PreambleDetected -= HandleMeshtasticPreambleDetected;
        _meshtasticReceiveService.FrameSynchronized -= HandleMeshtasticFrameSynchronized;
        _meshtasticReceiveService.PayloadDecoded -= HandleMeshtasticPayloadDecoded;
        _meshtasticReceiveService.MeshtasticPacketReceived -= HandleMeshtasticPacketReceived;
        _meshtasticReceiveService.AcquisitionDiagnostic -= HandleMeshtasticAcquisitionDiagnostic;
        _meshtasticReceiveService.WorkerFailed -= HandleMeshtasticWorkerFailed;
    }

    private void HandleMeshtasticPreambleDetected(LoRaPreambleDetection detection)
    {
        lock (_diagnosticGate) { _pendingDetection = detection; _pendingSynchronization = null; }
    }

    private void HandleMeshtasticFrameSynchronized(LoRaFrameSynchronization synchronization)
    {
        lock (_diagnosticGate) _pendingSynchronization = synchronization;
    }

    private string FormatMeshtasticSignal(float? snr, float? rssiDbfs) =>
        MeshtasticLoRaReceptionQuality.FormatSignal(snr, rssiDbfs,
            MeshtasticLoRaReceptionQuality.CalibrateRssi(rssiDbfs, _hostContext?.ReceiverTelemetry));

    internal static string FormatMeshtasticFrequencyCorrection(LoRaFrameSynchronization synchronization) =>
        synchronization.CompensationApplied
            ? $"推定ずれ {synchronization.CarrierFrequencyOffsetHz:+0.0;-0.0;0.0} Hz / " +
              $"補正 {-synchronization.CarrierFrequencyOffsetHz:+0.0;-0.0;0.0} Hz / " +
              $"タイミング {synchronization.TimingCorrectionSamples:+#;-#;0} sample"
            : synchronization.CompensationRequired
                ? $"推定ずれ {synchronization.CarrierFrequencyOffsetHz:+0.0;-0.0;0.0} Hz / 安全範囲外のため未補正"
                : $"推定ずれ {synchronization.CarrierFrequencyOffsetHz:+0.0;-0.0;0.0} Hz / 許容範囲内（補正不要）";

    private void HandleMeshtasticPayloadDecoded(LoRaPayloadFrame frame)
    {
        if (frame.IsPayloadCrcValid is true)
        {
            Interlocked.Increment(ref _meshtasticCrcOkAccumulator);
            Interlocked.Exchange(ref _lastMeshtasticSuccessfulPayloadTicks, DateTimeOffset.UtcNow.Ticks);
        }
        else if (frame.IsPayloadCrcValid is false) Interlocked.Increment(ref _meshtasticCrcErrorAccumulator);
    }

    private void HandleMeshtasticPacketReceived(MeshtasticPacketReception reception)
    {
        int epoch = Volatile.Read(ref _presentationEpoch);
        Interlocked.Exchange(ref _lastMeshtasticSuccessfulPayloadTicks, DateTimeOffset.UtcNow.Ticks);
        if (BuzzerEnabled)
        {
            _hostContext?.Notifications.PlayReceptionAlarm(TimeSpan.FromMilliseconds(500));
        }
        if (reception.IsDataDecoded) return;

        reception = reception with { Quality = reception.Quality.WithReceiverCalibration(_hostContext?.ReceiverTelemetry) };

        _hostContext?.Dispatcher.Post(() =>
        {
            if (epoch != _presentationEpoch || Volatile.Read(ref _initialized) == 0) return;
            string transmission = reception.Packet.WasRelayed switch
            {
                false => "直接",
                true => $"中継 {reception.Packet.RelayNode:x2}",
                null => "経路不明"
            };
            var item = new MeshtasticDisplayItem(
                reception.Packet.ReceivedAt.ToLocalTime().ToString("HH:mm:ss"),
                $"!{reception.Packet.From:x8}",
                ResolveMeshtasticSenderName(reception.Packet.From),
                transmission,
                $"Hop {reception.Packet.HopLimit}/{reception.Packet.HopStart}",
                "未復号",
                reception.Quality.Summary,
                $"復号できないパケット / Channel 0x{reception.Packet.ChannelHash:X2}",
                $"Encrypted payload: {Convert.ToHexString(reception.Packet.EncryptedPayload)}",
                Convert.ToHexString(reception.Packet.EncryptedPayload))
            {
                Radio = reception.Radio.Summary,
                RegionName = reception.Radio.Region != default ? reception.Radio.Region.ToString() : SelectedMeshtasticRegion.ToString(),
                PacketId = reception.Packet.PacketId,
                IsDecoded = false,
                ModemPresetName = ResolveMeshtasticPresetName(reception.Radio),
                RadioSlot = reception.Radio.RadioChannel,
                ReceivedAt = reception.Packet.ReceivedAt,
                EstimatedSnrDb = reception.Quality.EstimatedSnrDb,
                EstimatedRssiDbfs = reception.Quality.EstimatedRssiDbfs,
                EstimatedRssiDbm = reception.Quality.EstimatedRssiDbm,
                PayloadCrcValid = reception.Quality.PayloadCrcValid,
                HopLimit = reception.Packet.HopLimit,
                HopStart = reception.Packet.HopStart,
                WasRelayed = reception.Packet.WasRelayed,
                RelayNode = reception.Packet.RelayNode
            };
            MeshtasticMessages.Insert(0, item);
            AppendMeshtasticHistory(item);
            while (MeshtasticMessages.Count > MeshtasticHistoryDisplayLimit)
                MeshtasticMessages.RemoveAt(MeshtasticMessages.Count - 1);
            SelectedTimelineMessage ??= item;
            ScheduleMeshtasticDerivedRefresh();

            MeshtasticNodeDisplayItem node = _nodeStore.GetOrAddNode(reception.Packet.From, out bool addedNode);
            if (addedNode) SelectedMeshtasticNode ??= node;
            node.Observe(reception, item.Summary, item.ModemPresetName);
            if (node.HasPosition) UpdateMeshtasticMapPoint(node);
            int nodeIndex = MeshtasticNodes.IndexOf(node);
            if (nodeIndex > 0) MeshtasticNodes.Move(nodeIndex, 0);

            SetMeshtasticReceiverStatus(
                $"受信 {MeshtasticMessages.Count}件 / Node {MeshtasticNodes.Count}",
                OverallStatusKind.Running);
            ScheduleMeshtasticSnapshotSave();
        });
    }

    private void HandleMeshtasticAcquisitionDiagnostic(LoRaAcquisitionDiagnostic diagnostic)
    {
        if (!diagnostic.IsFailure) return;
        Interlocked.Exchange(ref _lastMeshtasticFailureTicks, DateTimeOffset.UtcNow.Ticks);

        switch (diagnostic.Stage)
        {
            case "SYNC1":
            case "SYNC2":
                Interlocked.Increment(ref _meshtasticSyncFailureAccumulator);
                break;
            case "SFD":
                Interlocked.Increment(ref _meshtasticSfdFailureAccumulator);
                break;
            case "HEADER":
                Interlocked.Increment(ref _meshtasticHeaderFailureAccumulator);
                break;
            case "PAYLOAD":
                Interlocked.Increment(ref _meshtasticPayloadFailureAccumulator);
                break;
        }

        string status = $"直近の失敗: {diagnostic.Timestamp.ToLocalTime():HH:mm:ss} {diagnostic.Stage} / {diagnostic.Message}";
        lock (_diagnosticGate) _pendingFailure = status;
    }

    private long _meshtasticCrcOkAccumulator;
    private long _meshtasticCrcErrorAccumulator;
    private long _meshtasticSyncFailureAccumulator;
    private long _meshtasticSfdFailureAccumulator;
    private long _meshtasticHeaderFailureAccumulator;
    private long _meshtasticPayloadFailureAccumulator;

    [RelayCommand]
    private void ResetMeshtasticStatistics()
    {
        lock (_diagnosticGate)
        { _pendingDetection = null; _pendingSynchronization = null; _pendingFailure = null; }
        _meshtasticReceiveService.ResetStatistics();
        Interlocked.Exchange(ref _meshtasticCrcOkAccumulator, 0);
        Interlocked.Exchange(ref _meshtasticCrcErrorAccumulator, 0);
        Interlocked.Exchange(ref _meshtasticSyncFailureAccumulator, 0);
        Interlocked.Exchange(ref _meshtasticSfdFailureAccumulator, 0);
        Interlocked.Exchange(ref _meshtasticHeaderFailureAccumulator, 0);
        Interlocked.Exchange(ref _meshtasticPayloadFailureAccumulator, 0);
        MeshtasticLastSignalStatus = "最終信号: -";
        MeshtasticLastFailureStatus = "直近の失敗: -";
        MeshtasticFrequencyCorrectionText = "—";
        Interlocked.Exchange(ref _lastMeshtasticSuccessfulPayloadTicks, 0);
        Interlocked.Exchange(ref _lastMeshtasticFailureTicks, 0);
        ApplyPendingMeshtasticDiagnostics();
    }

    private void ApplyMeshtasticStatistics(MeshtasticReceiveSnapshot snapshot)
    {
        _lastWorkerFailure = snapshot.WorkerFailure;
        _diagnosticPerformanceState = MeshtasticPerformanceEvaluator.EvaluateState(new(
            snapshot.SubmittedBlocks, snapshot.ProcessedBlocks, snapshot.DroppedBlocks, snapshot.QueueDepth,
            snapshot.CurrentQueueDelayMs, snapshot.CurrentInputBlockTimeMs, snapshot.AverageProcessingLoadPercent,
            snapshot.OldestDeferredIqMs, snapshot.DeferredRetentionRemainingMs));
        MeshtasticPerformanceStatus = MeshtasticPerformanceEvaluator.Format(_diagnosticPerformanceState);
        _diagnosticInPassband = snapshot.IsTargetInPassband;
        MeshtasticPerformanceDetails = $"処理負荷 {snapshot.AverageProcessingLoadPercent:F1}% · 待ち {snapshot.QueueDepth}ブロック · 欠落 {snapshot.DroppedBlocks}";
        MeshtasticIqRetentionText = $"IQ保持 {snapshot.RetainedIqBytes / 1048576.0:F1} / 64 MiB · {snapshot.RetainedIqMilliseconds / 1000.0:F2} / 5秒 · 上限棄却 {snapshot.QueueLimitDroppedBlocks}";
        _meshtasticDiagnosticLastUpdated = DateTime.Now.ToString("HH:mm:ss");
        MeshtasticPreambleCount = snapshot.DetectedPreambles;
        MeshtasticSynchronizedCount = snapshot.SynchronizedFrames;
        MeshtasticHeaderCount = snapshot.DecodedHeaders;
        MeshtasticPayloadCount = snapshot.DecodedPayloads;
        MeshtasticPacketCount = snapshot.ParsedMeshtasticPackets;
        MeshtasticDuplicateCount = snapshot.DuplicateMeshtasticPackets;
        MeshtasticDataCount = snapshot.DecodedMeshtasticData;
        MeshtasticDroppedBlockCount = snapshot.DroppedBlocks;
        MeshtasticQueueDepth = snapshot.QueueDepth;
        MeshtasticMaximumQueueDepth = snapshot.MaximumQueueDepth;
        MeshtasticCurrentQueueDelayMs = snapshot.CurrentQueueDelayMs;
        MeshtasticAverageQueueDelayMs = snapshot.AverageQueueDelayMs;
        MeshtasticMaximumQueueDelayMs = snapshot.MaximumQueueDelayMs;
        MeshtasticCurrentProcessingTimeMs = snapshot.CurrentProcessingTimeMs;
        MeshtasticAverageProcessingTimeMs = snapshot.AverageProcessingTimeMs;
        MeshtasticMaximumProcessingTimeMs = snapshot.MaximumProcessingTimeMs;
        MeshtasticAverageChannelizationCpuMs = snapshot.AverageChannelizationCpuMs;
        MeshtasticAverageDetectionCpuMs = snapshot.AverageDetectionCpuMs;
        MeshtasticCurrentInputBlockTimeMs = snapshot.CurrentInputBlockTimeMs;
        MeshtasticCurrentProcessingLoadPercent = snapshot.CurrentProcessingLoadPercent;
        MeshtasticAverageProcessingLoadPercent = snapshot.AverageProcessingLoadPercent;
        MeshtasticMaximumProcessingLoadPercent = snapshot.MaximumProcessingLoadPercent;
        MeshtasticOldestDeferredIqMs = snapshot.OldestDeferredIqMs;
        MeshtasticDeferredRetentionRemainingMs = snapshot.DeferredRetentionRemainingMs;
        MeshtasticDeferredRecoveredBlocks = snapshot.DeferredRecoveredBlocks;
        MeshtasticExpiredHistoryBlocks = snapshot.ExpiredHistoryBlocks;
        MeshtasticPassbandStatus = snapshot.IsTargetInPassband ? "帯域内" : "帯域外（対象周波数外）";
        MeshtasticRateConversionText = snapshot.InputSampleRateHz <= 0
            ? "—"
            : SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatConversion(
                snapshot.InputSampleRateHz != MeshtasticJpLongFastProfile.DecoderSampleRateHz,
                "プラグイン内部");
        MeshtasticRatePathText = SRdeckPlugin.Wpf.DiagnosticRateDisplay.FormatPath(
            snapshot.InputSampleRateHz,
            snapshot.InputSampleRateHz > 0 ? (int)Math.Round(snapshot.InputSampleRateHz / (double)MeshtasticJpLongFastProfile.DecoderSampleRateHz) : 1,
            MeshtasticJpLongFastProfile.DecoderSampleRateHz);
        MeshtasticPayloadCrcOkCount = Interlocked.Read(ref _meshtasticCrcOkAccumulator);
        MeshtasticPayloadCrcErrorCount = Interlocked.Read(ref _meshtasticCrcErrorAccumulator);
        MeshtasticSyncFailureCount = Interlocked.Read(ref _meshtasticSyncFailureAccumulator);
        MeshtasticSfdFailureCount = Interlocked.Read(ref _meshtasticSfdFailureAccumulator);
        MeshtasticHeaderFailureCount = Interlocked.Read(ref _meshtasticHeaderFailureAccumulator);
        MeshtasticPayloadFailureCount = Interlocked.Read(ref _meshtasticPayloadFailureAccumulator);

        long lastFailureTicks = Interlocked.Read(ref _lastMeshtasticFailureTicks);
        if (lastFailureTicks <= 0) MeshtasticLastFailureStatus = "直近の失敗: -";
    }
}
