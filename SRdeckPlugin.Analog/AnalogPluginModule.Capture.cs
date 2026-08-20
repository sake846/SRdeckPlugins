using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Analog.Dsp;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.Analog.Views;
using SRdeckPlugin.Analog.ViewModels;

namespace SRdeckPlugin.Analog;

public sealed partial class AnalogPluginModule
{
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
            using (var capture = new AnalogIqCapture(path, historySnapshot.SampleRateHz, TimeSpan.FromSeconds(3)))
            {
                capture.WritePcm(historySnapshot.RawInterleaved);
            }
            var document = new
            {
                Format = "SRdeck Analog analysis capture v1",
                SavedAt = DateTimeOffset.Now,
                CaptureMode = "3-second rolling pre-trigger",
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
        float? calibratedDbm = _hostContext?.ReceiverTelemetry?.SignalLevelDbm;
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
            _demodulator.IsStereoDetected,
            Volatile.Read(ref _signalLevelDbfs),
            calibratedDbm ?? -150f,
            Volatile.Read(ref _isSquelchOpen),
            Volatile.Read(ref _tuningStatus),
            calibratedDbm is not null && float.IsFinite(calibratedDbm.Value),
            measuredTicks > 0 ? new DateTimeOffset(measuredTicks, TimeSpan.Zero) : default,
            audioTicks > 0 ? new DateTimeOffset(audioTicks, TimeSpan.Zero) : null,
            Volatile.Read(ref _lastAudioRms),
            Volatile.Read(ref _lastAudioPeak),
            _demodulator.AfcCorrectionHz,
            _demodulator.DemodulationSampleRateHz);
    }
}
