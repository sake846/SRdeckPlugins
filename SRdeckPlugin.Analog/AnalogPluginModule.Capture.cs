using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Analog;

public sealed partial class AnalogPluginModule
{
    private AnalogNarrowbandIqRecording? _narrowbandIqRecording;
    private int _lastNarrowbandStatusSecond;

    public bool IsNarrowbandIqRecording => _narrowbandIqRecording is not null;
    public string NarrowbandIqRecordingStatus { get; private set; } = "狭帯域IQ録音: 受信中に開始してください";

    public void StartNarrowbandIqRecording()
    {
        ThrowIfDisposed();
        _receiverGate.Wait();
        try
        {
            if (State != PluginLifecycleState.Streaming || _hostContext is null)
                throw new InvalidOperationException("受信開始後に録音してください。");
            if (_narrowbandIqRecording is not null)
                throw new InvalidOperationException("すでに狭帯域IQを録音中です。");
            AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
            if (!options.IsReceiverEnabled || options.FrequencyHz <= 0)
                throw new InvalidOperationException("受信周波数を設定してから録音してください。");

            _narrowbandIqRecording = new AnalogNarrowbandIqRecording(
                Path.Combine(_hostContext.Settings.DataDirectory, "captures"),
                _selectedProfileId, options.FrequencyHz, options.BandwidthHz);
            _lastNarrowbandStatusSecond = 0;
            string splitInterval = _selectedProfileId == "fm" && options.BandwidthHz > 50_000
                ? "1分ごと" : "10分ごと";
            UpdateNarrowbandIqRecordingStatus($"狭帯域IQ録音中: {splitInterval}に分割保存 / IQ入力待ち…");
        }
        finally
        {
            _receiverGate.Release();
        }
    }

    public void StopNarrowbandIqRecording()
    {
        ThrowIfDisposed();
        _receiverGate.Wait();
        try { FinishNarrowbandIqRecording("手動停止"); }
        finally { _receiverGate.Release(); }
    }

    private void CaptureNarrowbandIq(ReadOnlySpan<Complex32> samples, int sampleRateHz, IqBlockMetadata metadata)
    {
        if (_narrowbandIqRecording is not { } current) return;
        try
        {
            AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
            string? reason = current.Write(samples, sampleRateHz, metadata,
                _selectedProfileId, options.FrequencyHz, options.BandwidthHz,
                _hostContext?.ReceiverTelemetry?.CalibrationOffsetDb);
            if (reason is not null)
            {
                FinishNarrowbandIqRecording(reason);
                return;
            }

            int second = (int)current.Seconds;
            if (second > _lastNarrowbandStatusSecond)
            {
                _lastNarrowbandStatusSecond = second;
                UpdateNarrowbandIqRecordingStatus(
                    $"狭帯域IQ録音中: {current.Seconds:F0} 秒 / {current.SampleRateHz / 1_000.0:F1} kS/s / {current.SegmentCount}ファイル" +
                    (current.SourceDiscontinuityCount > 0
                        ? $" / 入力欠落・再開 {current.SourceDiscontinuityCount}回"
                        : string.Empty));
            }
        }
        catch (Exception exception)
        {
            _hostContext?.Logger.Log(PluginLogLevel.Error, "analog.recording.write", "Narrowband IQ recording failed.", exception);
            FinishNarrowbandIqRecording($"書き込み失敗: {exception.Message}");
        }
    }

    private void FinishNarrowbandIqRecording(string reason)
    {
        if (_narrowbandIqRecording is not { } current) return;
        _narrowbandIqRecording = null;
        try
        {
            string? path = current.Finish(reason);
            string staging = current.StagingPath is { } temporary ? $"\n未完了の一時ファイル: {temporary}" : string.Empty;
            UpdateNarrowbandIqRecordingStatus(path is null
                ? $"狭帯域IQ録音: {reason}（IQ未取得）{staging}"
                : $"狭帯域IQ録音: {reason}（{current.Seconds:F2} 秒、{current.SegmentCount}ファイル）\n最終ファイル: {path}{staging}");
        }
        catch (Exception exception)
        {
            UpdateNarrowbandIqRecordingStatus(
                $"狭帯域IQ保存失敗: {exception.Message}\n一時ファイルを確認してください: {current.StagingPath ?? current.LastPath ?? "(なし)"}");
            _hostContext?.Logger.Log(PluginLogLevel.Error, "analog.recording.finish", "Narrowband IQ recording finalization failed; retain staging files.", exception);
        }
    }

    private void UpdateNarrowbandIqRecordingStatus(string status)
    {
        NarrowbandIqRecordingStatus = status;
        CaptureStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StartIqCapture()
    {
        if (State != PluginLifecycleState.Streaming || _hostContext is null)
        {
            UpdateCaptureStatus("IQ録音: 受信中に開始してください");
            return;
        }
        if (Interlocked.CompareExchange(ref _captureSaveInProgress, 1, 0) != 0)
        {
            UpdateCaptureStatus("IQ録音: 保存処理中です");
            return;
        }
        IPluginHostContext context = _hostContext;
        IqBlockMetadata? metadata = _lastCaptureMetadata;
        AnalogReceiverSnapshot snapshot = GetReceiverSnapshot();
        UpdateCaptureStatus("IQ録音: 直前3秒を保存中…");
        _ = Task.Run(() => SavePretriggerCapture(context, metadata, snapshot));
    }

    private void SavePretriggerCapture(IPluginHostContext context, IqBlockMetadata? metadata, AnalogReceiverSnapshot snapshot)
    {
        try
        {
            PackedIqHistorySnapshot historySnapshot = _pretriggerBuffer.TakeSnapshot() ??
                throw new InvalidOperationException("まだ保存できるIQデータがありません。");
            string directory = Path.Combine(context.Settings.DataDirectory, "captures");
            Directory.CreateDirectory(directory);
            string basePath = Path.Combine(directory,
                $"analog-analysis-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{snapshot.FrequencyHz}Hz");
            string path = $"{basePath}.wav";
            using (var capture = new BoundedIqWavWriter(path, historySnapshot.SampleRateHz, TimeSpan.FromSeconds(3)))
            {
                capture.WritePcm(historySnapshot.RawInterleaved);
            }
            var document = new
            {
                Format = "SRdeck Analog analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = "3-second rolling pre-trigger",
                CaptureSignal = "Post-channel-filter complex IQ",
                Profile = SelectedProfileId,
                FrequencyHz = snapshot.FrequencyHz,
                BandwidthHz = snapshot.BandwidthHz,
                SampleRateHz = historySnapshot.SampleRateHz,
                DurationSeconds = historySnapshot.DurationSeconds,
                InputMetadata = metadata,
                Snapshot = snapshot
            };
            string diagnosticsPath = $"{basePath}-diagnostics.json";
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(document,
                new JsonSerializerOptions { WriteIndented = true }));
            UpdateCaptureStatus($"IQ録音: {Path.GetFileName(path)} を保存しました");
        }
        catch (Exception exception)
        {
            UpdateCaptureStatus($"IQ録音失敗: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _captureSaveInProgress, 0);
        }
    }

    private void UpdateCaptureStatus(string status)
    {
        _captureStatus = status;
        CaptureStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    internal AnalogReceiverSnapshot GetReceiverSnapshot()
    {
        AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
        long measuredTicks = Interlocked.Read(ref _lastInputMeasuredUtcTicks);
        long audioTicks = Interlocked.Read(ref _lastAudioOutputUtcTicks);
        return new AnalogReceiverSnapshot(
            options.FrequencyHz,
            Volatile.Read(ref _inputSampleRateHz),
            options.StepHz,
            options.BandwidthHz,
            options.IsReceiverEnabled,
            options.IsMuted,
            options.IsSquelchEnabled,
            options.SquelchThresholdDbm,
            options.IsAfcEnabled,
            options.IsLowerSideband,
            options.IsStereoEnabled,
            options.IsSsbCarrierFrequency,
            _demodulator.IsStereoDetected,
            Volatile.Read(ref _signalLevelDbfs),
            Volatile.Read(ref _signalLevelDbm),
            Volatile.Read(ref _isSquelchOpen),
            Volatile.Read(ref _tuningStatus),
            Volatile.Read(ref _hasCalibratedSignalLevelDbm),
            measuredTicks > 0 ? new DateTimeOffset(measuredTicks, TimeSpan.Zero) : default,
            audioTicks > 0 ? new DateTimeOffset(audioTicks, TimeSpan.Zero) : null,
            Volatile.Read(ref _lastAudioRms),
            Volatile.Read(ref _lastAudioPeak),
            _thirdOctaveAnalyzer.GetLevelsDb(),
            _demodulator.AfcCorrectionHz,
            _demodulator.DemodulationSampleRateHz);
    }
}
