using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.HfFax.Dsp;
using SRdeckPlugin.HfFax.Models;
using SRdeckPlugin.HfFax.Storage;
using SRdeckPlugin.HfFax.ViewModels;
using SRdeckPlugin.HfFax.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.HfFax;

public sealed partial class HfFaxPluginModule
{
    private async Task RetuneAsync(IPluginHostContext context, string profileId, long frequencyHz)
    {
        try
        {
            PluginTuningResult result = await context.Tuning.RequestAsync(
                CreateTuningRequest(profileId, frequencyHz), CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred ||
                !ContainsFaxPassband(result, frequencyHz))
            {
                context.Dispatcher.Post(() => viewModel.Status = $"選局失敗 / {result.Message}");
                return;
            }
            lock (processingGate)
            {
                receiver.Stop(context.TimeProvider.GetUtcNow(), "周波数変更");
                receiver.Reset();
                ResetMonitorAudio();
            }
            context.Dispatcher.Post(() => viewModel.Status =
                $"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                $"{frequencyHz / 1_000.0:N1} kHz");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hffax.tuning.failed",
                "HF-FAX could not retune to the configured frequency.", exception);
            context.Dispatcher.Post(() => viewModel.Status = $"選局失敗 / {exception.Message}");
        }
    }

    private void StartImageManually()
    {
        if (State != PluginLifecycleState.Streaming)
        {
            viewModel.Status = "手動開始失敗 / 先に受信を開始してください";
            return;
        }
        lock (processingGate)
        {
            IqBlockMetadata metadata = lastSourceMetadata ?? new IqBlockMetadata(
                Guid.NewGuid(), 0, 0, 0, 0,
                HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow,
                HfFaxReceiver.OutputSampleRateHz, Volatile.Read(ref centerFrequencyHz), 0,
                IqInputSource.Sdr, IqDiscontinuity.StreamStarted);
            receiver.StartManually(metadata.UtcTimestamp, metadata.StreamId,
                metadata.AbsoluteSampleStart, Volatile.Read(ref centerFrequencyHz),
                DecodeOptions(settings));
        }
    }

    private void StopImageManually()
    {
        lock (processingGate)
            receiver.StopManually(HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow);
    }

    private void SaveCurrentImage()
    {
        if (Interlocked.CompareExchange(ref saveInProgress, 1, 0) != 0)
        {
            viewModel.SaveStatus = "画像保存中です";
            return;
        }
        IPluginHostContext? context = HostContext;
        HfFaxImageSnapshot? snapshot = SnapshotForSave("手動保存");
        if (context is null || snapshot is null)
        {
            viewModel.SaveStatus = "保存できる画像がありません";
            Interlocked.Exchange(ref saveInProgress, 0);
            return;
        }

        viewModel.SaveStatus = "PNG保存中…";
        _ = Task.Run(() =>
        {
            try
            {
                string directory = Path.Combine(context.Settings.DataDirectory, "images");
                string path = Path.Combine(directory,
                    $"hffax-{snapshot.StartedAt:yyyyMMdd-HHmmss}-{snapshot.FrequencyHz}.png");
                HfFaxPngWriter.Save(path, snapshot);
                context.Dispatcher.Post(() => viewModel.SaveStatus = $"保存済み: {path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Logger.Log(PluginLogLevel.Warning, "hffax.image.save-failed",
                    "HF-FAX image could not be saved.", exception);
                context.Dispatcher.Post(() => viewModel.SaveStatus = $"保存失敗: {exception.Message}");
            }
            finally { Interlocked.Exchange(ref saveInProgress, 0); }
        });
    }

    private HfFaxImageSnapshot? SnapshotForSave(string reason)
    {
        lock (processingGate)
        {
            HfFaxImageSnapshot? current = receiver.GetCurrentSnapshot(
                HostContext?.TimeProvider.GetUtcNow() ?? DateTimeOffset.UtcNow, reason);
            if (current is not null) return current;
        }
        lock (resultGate) return latestImage;
    }

    private void ClearResults()
    {
        lock (resultGate)
        {
            latestImage = null;
        }
        viewModel.ClearImages();
        DeleteHistoryFile();
    }

    private void OnImageStarted(HfFaxImageStarted image) =>
        HostContext?.Dispatcher.Post(() => viewModel.BeginImage(image));

    private void OnLineCompleted(HfFaxLine line) =>
        HostContext?.Dispatcher.Post(() => viewModel.AppendLine(line));

    private void OnImageRealigned(HfFaxImageSnapshot image) =>
        HostContext?.Dispatcher.Post(() => viewModel.RealignCurrentImage(
            image, settings.HorizontalShiftPixels, automaticallyAligned: true));

    private void OnImageCompleted(HfFaxImageSnapshot image)
    {
        if (settings.AutomaticHorizontalAlignmentEnabled)
            image = HfFaxImagePhaseAligner.AlignSnapshot(
                image, settings.HorizontalShiftPixels);
        lock (resultGate)
        {
            latestImage = image;
        }
        HostContext?.Notifications.PlayReceptionAlarm();
        HostContext?.Dispatcher.Post(() => viewModel.CompleteImage(image));
        AppendHistory(image);
        ResultPublished?.Invoke(this, new(new PluginResultSummary(
            image.Id,
            Descriptor.Id,
            image.CompletedAt,
            image.StreamId,
            "hffax.image",
            PluginResultSeverity.Information,
            $"HF-FAX画像 {image.SizeDisplay}",
            $"{image.FrequencyDisplay} / {image.FormatDisplay} / {image.CompletionReason}",
            image.FrequencyHz,
            image.SignalQuality,
            1,
            JsonSerializer.Serialize(new
            {
                image.Width,
                image.Height,
                image.Ioc,
                image.LinesPerMinute,
                image.SignalQuality,
                image.CompletionReason
            }))));
    }

    private void OnReceiverStateChanged(HfFaxReceiverState state) =>
        HostContext?.Dispatcher.Post(() => viewModel.SetReceiverState(state));

    private async Task SaveSettingsAsync(HfFaxSettings value)
    {
        IPluginHostContext? context = HostContext;
        if (context is null) return;
        try
        {
            await context.Settings.SaveAsync(new PluginSettingsDocument(1,
                JsonSerializer.Serialize(value))).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "hffax.settings.save-failed",
                "HF-FAX settings could not be saved.", exception);
        }
    }

    private void OnAppliedTuningChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome == PluginTuningOutcome.Rejected) return;
        long frequency = Volatile.Read(ref centerFrequencyHz);
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming &&
            !ContainsFaxPassband(result, frequency))
            PostStatus("HF-FAX信号が通過帯域外です");
    }

    private void SetCenterFrequency(long value)
    {
        Volatile.Write(ref centerFrequencyHz, value);
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        HostContext?.Dispatcher.Post(() => viewModel.SetCenterFrequency(value));
    }

    private void PostStatus(string value)
    {
        IPluginHostContext? context = HostContext;
        if (context is null || context.Dispatcher.CheckAccess()) viewModel.Status = value;
        else context.Dispatcher.Post(() => viewModel.Status = value);
    }

    private void SubmitMonitorAudio(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata,
        HfFaxSettings current,
        bool discontinuous)
    {
        IPluginHostContext? context = HostContext;
        if (context is null || !current.MonitorAudioEnabled || samples.IsEmpty) return;
        double oscillatorCos = monitorOscillatorCos;
        double oscillatorSin = monitorOscillatorSin;
        long oscillatorSamples = monitorOscillatorSamples;
        double power = monitorPower;
        double gain = monitorGain;
        double volume = current.MonitorAudioVolume / 100.0;
        byte[] pcm = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 sample = samples[index];
            // HF-FAX is conventionally monitored in USB. Re{(I+jQ) *
            // exp(+j*2*pi*1900*t)} puts a positive discriminator deviation
            // above the nominal subcarrier; the decoder continues to use the
            // raw IQ independently of this user-facing monitor path.
            double raw = sample.I * oscillatorCos - sample.Q * oscillatorSin;
            AdvanceMonitorOscillator(ref oscillatorCos, ref oscillatorSin,
                ++oscillatorSamples);
            if (!double.IsFinite(raw))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            double instantaneousPower = raw * raw;
            if (!double.IsFinite(instantaneousPower))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            double powerCoefficient = instantaneousPower > power
                ? MonitorPowerAttackCoefficient
                : MonitorPowerReleaseCoefficient;
            power += powerCoefficient * (instantaneousPower - power);
            if (!double.IsFinite(power))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            double targetGain = MonitorTargetRms /
                                Math.Sqrt(Math.Max(power, 1e-12));
            if (!double.IsFinite(targetGain))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }
            targetGain = Math.Clamp(targetGain, MonitorMinimumGain, MonitorMaximumGain);
            double gainCoefficient = targetGain < gain
                ? MonitorGainAttackCoefficient
                : MonitorGainReleaseCoefficient;
            gain += gainCoefficient * (targetGain - gain);
            if (!double.IsFinite(gain))
            {
                power = 0;
                gain = 1;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            // A soft limiter prevents a strong signal from making a click while
            // the slower power estimate and gain loop are catching up.
            double limited = MonitorPeakLimit *
                             Math.Tanh(raw * gain / MonitorPeakLimit);
            short value = (short)Math.Round(
                Math.Clamp(limited * volume, -1, 1) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2, 2), value);
        }
        monitorOscillatorCos = oscillatorCos;
        monitorOscillatorSin = oscillatorSin;
        monitorOscillatorSamples = oscillatorSamples;
        monitorPower = power;
        monitorGain = gain;
        context.Audio.TrySubmit(new PcmAudioFrame(context.PluginId, metadata.Source.StreamId,
            Interlocked.Increment(ref audioSequence), HfFaxReceiver.OutputSampleRateHz, 1,
            PcmSampleFormat.Signed16LittleEndian, pcm, discontinuous));
    }

    private void ResetMonitorAudio()
    {
        monitorOscillatorCos = 1;
        monitorOscillatorSin = 0;
        monitorOscillatorSamples = 0;
        ResetMonitorAudioLevel();
    }

    private void ResetMonitorAudioLevel()
    {
        monitorPower = 0;
        monitorGain = 1;
    }

    private static double MonitorTimeConstant(double seconds) =>
        1 - Math.Exp(-1 / (seconds * HfFaxReceiver.OutputSampleRateHz));

    internal static void AdvanceMonitorOscillator(ref double oscillatorCos,
        ref double oscillatorSin, long sampleIndex)
    {
        double nextCos = oscillatorCos * MonitorStepCos -
            oscillatorSin * MonitorStepSin;
        oscillatorSin = oscillatorSin * MonitorStepCos +
            oscillatorCos * MonitorStepSin;
        oscillatorCos = nextCos;
        if ((sampleIndex & 1023) != 0) return;
        double inverseMagnitude = 1 / Math.Sqrt(
            oscillatorCos * oscillatorCos + oscillatorSin * oscillatorSin);
        oscillatorCos *= inverseMagnitude;
        oscillatorSin *= inverseMagnitude;
    }

    private static HfFaxDecodeOptions DecodeOptions(HfFaxSettings value) => new(
        value.Ioc,
        value.LinesPerMinute,
        value.MaximumLines,
        value.HorizontalShiftPixels,
        value.SlantCorrectionPpm,
        value.AutomaticStartStop,
        value.InvertImage,
        value.VideoBandwidthHz,
        value.PerLineSynchronizationEnabled,
        value.AutomaticHorizontalAlignmentEnabled);

    private static long FrequencyFor(HfFaxSettings value)
    {
        Station station = Stations.First(item => item.Id == NormalizeStationId(value.SelectedStationId));
        return station.FrequencyHz > 0 ? station.FrequencyHz : value.CustomFrequencyHz;
    }

    private static PluginTuningRequest CreateTuningRequest(string profileId, long frequencyHz) => new(
        profileId,
        $"HF-FAX {frequencyHz / 1_000.0:N1} kHz",
        [new TuningTarget(frequencyHz, HfFaxReceiver.ReceiverBandwidthHz)],
        frequencyHz,
        24_000,
        10,
        true,
        false,
        PluginGainPreference.Automatic);

    private static bool ContainsFaxPassband(PluginTuningResult result, long frequencyHz)
    {
        long half = HfFaxReceiver.ReceiverBandwidthHz / 2;
        return result.PassbandLowerFrequencyHz > 0 &&
            result.PassbandUpperFrequencyHz > result.PassbandLowerFrequencyHz &&
            frequencyHz - half >= result.PassbandLowerFrequencyHz &&
            frequencyHz + half <= result.PassbandUpperFrequencyHz;
    }
}
